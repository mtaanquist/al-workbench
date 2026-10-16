using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Picks the <see cref="IDeliveryTarget"/> for an environment from its
/// <see cref="OeProjectEnvironment.Kind"/> - the one place that knows about every kind, the
/// way <c>OffsiteStorageProviderFactory</c> is for off-site backends. Business Central
/// online gets <see cref="AdminCenterDeliveryTarget"/>; a linked on-premises service tier
/// gets <see cref="GatewayDeliveryTarget"/> (#1367). The targets are short-lived and built
/// per call, so they are never DI-registered; the factory is scoped because the clients
/// and the database context it hands them are.
/// See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed class DeliveryTargetFactory
{
    /// <summary>What a linked on-premises service tier can do: no deferred schedules.</summary>
    public static readonly DeliveryTargetCapabilities OnPremisesCapabilities = new(DeferredSchedules: false);

    private readonly IDeliveryTokenSource _tokens;
    private readonly IBcAppManagementClient _apps;
    private readonly IBcAdminClient _admin;
    private readonly GatewayTargetServices? _gateway;

    public DeliveryTargetFactory(
        IDeliveryTokenSource tokens,
        IBcAppManagementClient apps,
        IBcAdminClient admin,
        GatewayTargetServices? gateway = null)
    {
        _tokens = tokens;
        _apps = apps;
        _admin = admin;
        _gateway = gateway;
    }

    /// <summary>
    /// The gateway command queue, for following an on-premises deployment once its run
    /// has handed it off (#1367); null where on-premises deploying isn't set up.
    /// </summary>
    internal GatewayCommandService? GatewayCommands => _gateway?.Commands;

    /// <summary>
    /// Why nothing can be deployed to an environment of <paramref name="kind"/>, in a
    /// sentence for the person; null when <see cref="Create"/> has a target for it. Every
    /// kind has one now; this stays the place a kind without one would say so.
    /// </summary>
    public string? Unavailable(OeEnvironmentKind kind, string environmentName) => kind switch
    {
        OeEnvironmentKind.Online => null,
        OeEnvironmentKind.OnPremises when _gateway is not null => null,
        _ => $"{environmentName} is an on-premises service tier, and deploying through its gateway isn't set up here, so nothing was sent.",
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
        OeEnvironmentKind.OnPremises when _gateway is not null =>
            new GatewayDeliveryTarget(environment, _gateway.Db, _gateway.Commands, _gateway.Clock, _gateway.Logger),
        _ => throw new InvalidOperationException(Unavailable(environment.Kind, environment.Name)),
    };
}

/// <summary>
/// What <see cref="GatewayDeliveryTarget"/> is built with: the request's database context
/// (for the inventory it reads), the gateway command queue, and the clock. Scoped, like
/// the context. One parameter object rather than four on the factory, so the factory
/// keeps one optional dependency for the on-premises kind.
/// </summary>
public sealed class GatewayTargetServices
{
    public GatewayTargetServices(AppDbContext db, GatewayCommandService commands, TimeProvider clock, ILogger<GatewayDeliveryTarget> logger)
    {
        Db = db;
        Commands = commands;
        Clock = clock;
        Logger = logger;
    }

    public AppDbContext Db { get; }
    public GatewayCommandService Commands { get; }
    public TimeProvider Clock { get; }
    public ILogger<GatewayDeliveryTarget> Logger { get; }
}
