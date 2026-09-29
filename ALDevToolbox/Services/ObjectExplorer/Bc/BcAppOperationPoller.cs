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

    public static async Task<BcAppOperationResult> PollUntilTerminalAsync(
        IBcAppManagementClient apps, string accessToken, string applicationFamily, string environmentName,
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout, CancellationToken ct)
    {
        if (started.AppId is not { } appId)
        {
            // Without an app id there's nothing to poll. The upload was accepted, so
            // don't call it a failure — say what's unverified and let the consultant look.
            return new BcAppOperationResult(true,
                "Business Central accepted the upload but didn't say which app it was, so the install wasn't confirmed here.");
        }

        var deadline = DateTime.UtcNow + timeout;
        var consecutiveErrors = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            BcAppOperation? operation;
            try
            {
                operation = await apps.GetAppOperationAsync(
                    accessToken, applicationFamily, environmentName, appId, started.Id, ct).ConfigureAwait(false);
                consecutiveErrors = 0;
            }
            catch (BcApiException) when (++consecutiveErrors < MaxConsecutivePollErrors)
            {
                // A throttled or briefly unavailable read says nothing about the install,
                // which Business Central is running regardless. One blip must not turn an
                // accepted install into a failure - and, for a batch, stop the apps after it.
                operation = null;
            }
            catch (BcApiException ex)
            {
                return new BcAppOperationResult(false,
                    "Business Central accepted the app, but kept answering with an error when asked whether the install had finished, so it wasn't confirmed here. "
                    + ex.Message);
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
                return new BcAppOperationResult(false, "Timed out waiting for the install to finish.");
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

/// <summary>How one install operation ended.</summary>
/// <param name="Completed">True when Business Central reported the install done (or accepted it without an id to confirm by).</param>
/// <param name="Message">The sentence a history shows: null for a plain success, otherwise the reason or the caveat.</param>
/// <param name="Failure">Set when Business Central reported the install as failed, so a line can be built from its code.</param>
/// <param name="Raw">Business Central's text as it came, for the log.</param>
public sealed record BcAppOperationResult(bool Completed, string? Message, BcFailureDetail? Failure = null, string? Raw = null);
