namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// What the delivery worker needs from the connection layer to call BC for a project:
/// a valid S2S access token and the tenant its URLs must address. A seam (implemented
/// by <see cref="ProjectConnectionService"/>) so the delivery orchestration can be
/// unit-tested without the real OAuth round-trip or the Data Protection key ring — the
/// same testability reason the BC HTTP surfaces sit behind
/// <see cref="IBcAdminClient"/> and <see cref="IBcAppManagementClient"/>. The secret
/// never crosses this boundary; only the resulting bearer token does.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public interface IDeliveryTokenSource
{
    /// <summary>
    /// Returns the token and tenant for the project, or throws
    /// <see cref="BcApiException"/> with a clear, secret-free message when the
    /// connection isn't set up, the secret has expired, or Entra rejects the
    /// credentials.
    /// </summary>
    Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="AcquireDeliveryContextAsync(int, CancellationToken)"/>; with
    /// <paramref name="forceRefresh"/> a new token is fetched even when a cached one looks
    /// valid, for after Business Central refused the cached one with a 401 (#1113).
    /// </summary>
    Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, bool forceRefresh, CancellationToken ct = default) =>
        AcquireDeliveryContextAsync(projectId, ct);
}

/// <summary>
/// A project's resolved BC calling context: the bearer token the delivery run
/// authenticates with, and the tenant it was issued for.
/// <para>
/// <see cref="TenantId"/> is carried but not currently read by the publish flow. It was
/// required when the automation API addressed the tenant in the URL; the Admin Center
/// API is scoped by the token instead. It stays because it identifies <em>whose</em>
/// tenant a token is for, which is the thing to check first when a partner-managed
/// customer's delivery goes to the wrong place.
/// </para>
/// </summary>
public sealed record BcDeliveryContext(string AccessToken, Guid TenantId);
