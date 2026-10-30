using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Tests.Infrastructure;

// Business Central doubles for page tests that must never reach it: a render, or a
// read answered from our own mirror or the panel cache, has no business making a
// call, and one that tries fails the test instead of quietly going nowhere.

internal sealed class UnreachableHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new NotSupportedException();
}

/// <summary>
/// An Admin Center that refuses everything: a page or a service that reaches for the
/// customer's tenant when it shouldn't says so by throwing rather than by passing.
/// <para>
/// Open for inheritance so a test that needs <em>one</em> live call can override that
/// one and keep the refusal for the rest - which is the only way a "this tab reads
/// nothing else" assertion stays true as the interface grows.
/// </para>
/// </summary>
internal class UnreachableAdminClient : IBcAdminClient
{
    public virtual Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<BcEnvironmentCopy> CopyEnvironmentAsync(string accessToken, string? applicationFamily, string sourceEnvironmentName, string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<IReadOnlyList<BcSession>> ListSessionsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task CancelSessionAsync(string accessToken, string? applicationFamily, string environmentName, int sessionId, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal class UnreachableAppManagementClient : IBcAppManagementClient
{
    public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        => throw new NotSupportedException();
    public virtual Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
        => throw new NotSupportedException();
}
