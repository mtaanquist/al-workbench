using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// The <see cref="IDeliveryTarget"/> for a Business Central online environment: the Admin
/// Center API, called with the solution's S2S token from <see cref="IDeliveryTokenSource"/>
/// and the environment's application family. The code here is what
/// <see cref="DeliveryService"/> used to do inline, moved behind the seam unchanged (#1374).
/// Built per environment by <see cref="DeliveryTargetFactory"/>, never DI-registered.
/// </summary>
public sealed class AdminCenterDeliveryTarget : IDeliveryTarget
{
    /// <summary>Business Central online holds installs for its own updates.</summary>
    public static readonly DeliveryTargetCapabilities OnlineCapabilities = new(DeferredSchedules: true);

    private readonly DeliveryTargetEnvironment _environment;
    private readonly IDeliveryTokenSource _tokens;
    private readonly IBcAppManagementClient _apps;
    private readonly IBcAdminClient _admin;
    private string? _token;

    public AdminCenterDeliveryTarget(
        DeliveryTargetEnvironment environment,
        IDeliveryTokenSource tokens,
        IBcAppManagementClient apps,
        IBcAdminClient admin)
    {
        _environment = environment;
        _tokens = tokens;
        _apps = apps;
        _admin = admin;
    }

    public DeliveryTargetCapabilities Capabilities => OnlineCapabilities;

    /// <summary>
    /// Whatever the API called this environment's family, verbatim. Every App Management
    /// URL is built from it; the default stands in when the row is gone or never said.
    /// </summary>
    private string Family => _environment.ApplicationFamily ?? BcConstants.DefaultApplicationFamily;

    private string Token => _token
        ?? throw new InvalidOperationException("ConnectAsync must be called before the environment is.");

    public async Task ConnectAsync(CancellationToken ct) =>
        _token = (await _tokens.AcquireDeliveryContextAsync(_environment.ProjectId, ct)).AccessToken;

    /// <summary>
    /// Fetches the token again. The token source hands back its cached one while that
    /// still has time left.
    /// </summary>
    public Task RenewAsync(CancellationToken ct) => ConnectAsync(ct);

    public async Task<DeliveryTargetStatus> ReadStatusAsync(CancellationToken ct)
    {
        // The family as last read, null included: the Admin Center resolves it itself.
        var live = await _admin.GetEnvironmentAsync(Token, _environment.ApplicationFamily, _environment.Name, ct);
        if (live is null)
        {
            return DeliveryTargetStatus.Missing(
                $"Business Central no longer has an environment called '{_environment.Name}'. Refresh the environments on the solution's Business Central page.");
        }
        return new DeliveryTargetStatus(true, live.Type, live.Status,
            BcEnvironmentStatus.RefusalMessage(_environment.Name, live.Status));
    }

    public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(CancellationToken ct) =>
        _apps.ListInstalledAppsAsync(Token, Family, _environment.Name, ct);

    public Task<BcAppOperation> StartInstallAsync(
        byte[] package, string fileName, string schedule, string schemaSyncMode, CancellationToken ct) =>
        _apps.InstallPteAsync(
            Token, Family, _environment.Name,
            package, fileName,
            schedule, schemaSyncMode,
            // No language is sent. The workbench has no concept of one, and
            // guessing "en-US" would set the install locale wrong for (say) a
            // Danish customer; BC applies its own default until a deployment
            // pipeline can say what the language should be.
            languageId: string.Empty,
            installOrUpdateNeededDependencies: true,
            ct);

    /// <summary>
    /// Polls one install operation until it reports a terminal state or the per-app
    /// timeout elapses - the shared <see cref="BcAppOperationPoller"/>, which booked
    /// uploads use too, mapped onto a delivery's outcome shape.
    /// </summary>
    public async Task<DeliveryInstallOutcome> WaitForInstallAsync(
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout, CancellationToken ct)
    {
        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            _apps, Token,
            // Business Central refused the token in hand, so a cached one is no use.
            async token => (await _tokens.AcquireDeliveryContextAsync(_environment.ProjectId, forceRefresh: true, token)).AccessToken,
            Family, _environment.Name, started, pollDelay, timeout, ct);
        // A delivery needs a clean yes: an install it could not see finish (no id, a run
        // of failed polls, the wait ran out) is not one it reports as done. A missing app
        // id is the one caveat it has always carried as a success.
        var completed = result.Completed || (result.IsUnconfirmed && started.AppId is null);
        return new DeliveryInstallOutcome(completed, result.Message, result.Failure, result.Raw);
    }

    public Task<IReadOnlyList<BcSession>> ListSessionsAsync(CancellationToken ct) =>
        // The family as last read, null included.
        _admin.ListSessionsAsync(Token, _environment.ApplicationFamily, _environment.Name, ct);

    public Task<IReadOnlyList<BcScheduledPteOperation>> ListHeldInstallsAsync(CancellationToken ct) =>
        _apps.ListScheduledPteOperationsAsync(Token, Family, _environment.Name, ct);

    public Task CancelHeldInstallAsync(Guid appId, string version, string schedule, CancellationToken ct) =>
        _apps.RemoveScheduledPteVersionAsync(Token, Family, _environment.Name, appId, version, schedule, ct);
}
