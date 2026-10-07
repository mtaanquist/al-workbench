using ALDevToolbox.Services.ObjectExplorer.Delivery;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Waits for one app install operation to finish. The upload endpoint returns as soon as
/// Business Central has the package, with the operation still <c>running</c>; the
/// install itself takes minutes, and two installs started back to back can deadlock on
/// Business Central's own bookkeeping table. So every path that sends more than one app
/// polls the operation to a terminal state before sending the next: deliveries always
/// have, and booked uploads do the same. Keyed on the operation and app ids Business
/// Central returned from the upload, so two extensions that share a display name can't
/// be mistaken for each other.
/// </summary>
public static class BcAppOperationPoller
{
    /// <summary>How many polls in a row may fail before the install is given up as unconfirmed.</summary>
    internal const int MaxConsecutivePollErrors = 4;

    /// <param name="onPoll">Called before each read, so a long install can keep a worker's heartbeat alive.</param>
    public static Task<BcAppOperationResult> PollUntilTerminalAsync(
        IBcAppManagementClient apps, string accessToken, string applicationFamily, string environmentName,
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout, CancellationToken ct, Action? onPoll = null) =>
        PollUntilTerminalAsync(apps, accessToken, refreshToken: null, applicationFamily, environmentName,
            started, pollDelay, timeout, ct, onPoll);

    /// <param name="refreshToken">
    /// Fetches a token again when Business Central answers a poll with 401. A cached token
    /// is handed out while it has a few minutes left, and one install can be polled for
    /// longer than that, so the token can run out mid-wait (#1113). Retried once per run of
    /// errors; null keeps <paramref name="accessToken"/> throughout.
    /// </param>
    /// <param name="onPoll">Called before each read, so a long install can keep a worker's heartbeat alive.</param>
    public static async Task<BcAppOperationResult> PollUntilTerminalAsync(
        IBcAppManagementClient apps, string accessToken, Func<CancellationToken, Task<string>>? refreshToken,
        string applicationFamily, string environmentName,
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout, CancellationToken ct, Action? onPoll = null)
    {
        if (started.AppId is not { } appId)
        {
            // Without an app id there's nothing to poll. The upload was accepted, so
            // don't call it a failure — say what's unverified and let the consultant look.
            return BcAppOperationResult.Unconfirmed(
                "Business Central accepted the upload but didn't say which app it was, so the install wasn't confirmed here.");
        }

        var deadline = DateTime.UtcNow + timeout;
        var consecutiveErrors = 0;
        var refreshed = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            onPoll?.Invoke();

            BcAppOperation? operation;
            try
            {
                operation = await apps.GetAppOperationAsync(
                    accessToken, applicationFamily, environmentName, appId, started.Id, ct).ConfigureAwait(false);
                consecutiveErrors = 0;
                refreshed = false;
            }
            catch (BcApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                            && refreshToken is not null && !refreshed)
            {
                // The token ran out while Business Central was installing: get a new one
                // and ask again straight away. Not counted as a failed poll.
                refreshed = true;
                try
                {
                    accessToken = await refreshToken(ct).ConfigureAwait(false);
                }
                catch (BcApiException signInFailed)
                {
                    // The sign-in's own reason (an expired secret, say) is what to act on.
                    return BcAppOperationResult.Unconfirmed(
                        "Business Central accepted the app, but signing in again to ask whether the install had finished failed, so it wasn't confirmed here. " + signInFailed.Message);
                }
                continue;
            }
            catch (BcApiException) when (++consecutiveErrors < MaxConsecutivePollErrors)
            {
                // A throttled or briefly unavailable read says nothing about the install,
                // which Business Central is running regardless. One blip must not turn an
                // accepted install into a failure - and, for a batch, stop the apps after it.
                operation = null;
            }
            catch (BcApiException)
            {
                // The install is Business Central's now; only the answer is missing.
                return BcAppOperationResult.Unconfirmed(
                    "Business Central accepted the app, but kept answering with an error when asked whether the install had finished, so it wasn't confirmed here.");
            }

            switch (operation?.Status)
            {
                case BcAppOperationStatus.Succeeded:
                    return new BcAppOperationResult(true, null);
                case BcAppOperationStatus.Failed:
                    return DescribeFailure(operation);
                case BcAppOperationStatus.Canceled:
                    return new BcAppOperationResult(false, "The install was cancelled in Business Central.");
                case BcAppOperationStatus.Skipped:
                    return new BcAppOperationResult(false, "Business Central skipped the install.");
                // Scheduled / Running / Unknown, and a not-yet-visible operation → keep polling.
            }

            if (DateTime.UtcNow > deadline)
            {
                return BcAppOperationResult.Unconfirmed("Business Central was still installing the app when the workbench stopped waiting, so the install wasn't confirmed here.");
            }
            await Task.Delay(pollDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Turns a failed operation into what a history stores (#930): the app's message is
    /// the code's sentence followed by Business Central's own message, verbatim, and the raw
    /// text goes to the log. The codes lead because <see cref="BcAppOperation.ErrorMessage"/>
    /// comes back in the <em>environment's</em> language - shown, never branched on. The
    /// codes the client already read win over the ones parsed from the text.
    /// </summary>
    private static BcAppOperationResult DescribeFailure(BcAppOperation operation)
    {
        var parsed = BcFailureText.Parse(operation.ErrorMessage);
        var detail = parsed with
        {
            Code = string.IsNullOrEmpty(operation.ErrorCode) ? parsed.Code : operation.ErrorCode,
            InnerCode = string.IsNullOrEmpty(operation.InnerErrorCode) ? parsed.InnerCode : operation.InnerErrorCode,
        };
        var raw = string.IsNullOrWhiteSpace(operation.ErrorMessage) ? null : operation.ErrorMessage;
        return new BcAppOperationResult(false, BcFailureText.AppMessage(detail), detail, raw);
    }
}

/// <summary>
/// How one install operation ended. Three shapes: done (<see cref="Completed"/>), refused
/// by Business Central (not completed, with its reason), and <see cref="Unconfirmed"/> -
/// Business Central has the app and was installing it, but the workbench could not see
/// it through to the end (no id to poll, a run of failed polls, or the wait ran out).
/// A caller that must not claim what it did not see treats the third as "sent, not
/// confirmed"; a caller that needs a clean yes treats it as not completed.
/// </summary>
/// <param name="Completed">True when Business Central reported the install done.</param>
/// <param name="Message">The sentence a history shows: null for a plain success, otherwise the reason or the caveat.</param>
/// <param name="Failure">Set when Business Central reported the install as failed, so a line can be built from its code.</param>
/// <param name="Raw">Business Central's text as it came, for the log.</param>
/// <param name="IsUnconfirmed">True when the app is with Business Central but the install was not seen to finish.</param>
public sealed record BcAppOperationResult(bool Completed, string? Message, BcFailureDetail? Failure = null, string? Raw = null, bool IsUnconfirmed = false)
{
    public static BcAppOperationResult Unconfirmed(string message) => new(false, message, IsUnconfirmed: true);
}
