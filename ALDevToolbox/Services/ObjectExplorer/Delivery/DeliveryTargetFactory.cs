using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Picks the <see cref="IDeliveryTarget"/> for an environment from its
/// <see cref="OeProjectEnvironment.Kind"/> - the one place that knows about every kind, the
/// way <c>OffsiteStorageProviderFactory</c> is for off-site backends. Business Central
/// online gets <see cref="AdminCenterDeliveryTarget"/>. A linked on-premises service tier
/// has no implementation yet: deploying to it goes through the gateway (#1367), and until
/// that lands <see cref="Unavailable"/> says so and <see cref="Create"/> refuses. The
/// targets are short-lived and built per call, so they are never DI-registered; the
/// factory is scoped because the clients it hands them are.
/// See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed class DeliveryTargetFactory
{
    /// <summary>What a linked on-premises service tier can do once the gateway deploys to it: no deferred schedules.</summary>
    public static readonly DeliveryTargetCapabilities OnPremisesCapabilities = new(DeferredSchedules: false);

    private readonly IDeliveryTokenSource _tokens;
    private readonly IBcAppManagementClient _apps;
    private readonly IBcAdminClient _admin;

    public DeliveryTargetFactory(IDeliveryTokenSource tokens, IBcAppManagementClient apps, IBcAdminClient admin)
    {
        _tokens = tokens;
        _apps = apps;
        _admin = admin;
    }

    /// <summary>Why a deployment to a linked on-premises service tier is refused until the gateway can deploy (#1367).</summary>
    internal static string OnPremisesNotYet(string environmentName) =>
        $"{environmentName} is an on-premises service tier. Deploying to on-premises through the gateway isn't available yet, so nothing was sent.";

    /// <summary>
    /// Why nothing can be deployed to an environment of <paramref name="kind"/>, in a
    /// sentence for the person; null when <see cref="Create"/> has a target for it.
    /// </summary>
    public string? Unavailable(OeEnvironmentKind kind, string environmentName) => kind switch
    {
        OeEnvironmentKind.Online => null,
        _ => OnPremisesNotYet(environmentName),
    };

    /// <summary>
    /// What a target for <paramref name="kind"/> can do, without building one: for the
    /// checks made when a deployment is set up, before anything calls the environment.
    /// </summary>
    public DeliveryTargetCapabilities CapabilitiesFor(OeEnvironmentKind kind) => kind switch
    {
        OeEnvironmentKind.Online => AdminCenterDeliveryTarget.OnlineCapabilities,
        _ => OnPremisesCapabilities,
    };

    /// <summary>
    /// The target for <paramref name="environment"/>. Nothing is called yet; the caller
    /// connects. Throws <see cref="InvalidOperationException"/> for a kind
    /// <see cref="Unavailable"/> refuses: ask that first.
    /// </summary>
    public IDeliveryTarget Create(DeliveryTargetEnvironment environment) => environment.Kind switch
    {
        OeEnvironmentKind.Online => new AdminCenterDeliveryTarget(environment, _tokens, _apps, _admin),
        _ => throw new InvalidOperationException(Unavailable(environment.Kind, environment.Name)),
    };
}
