namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The names, in <see cref="GatewayCommandMessage.Parameters"/>, that the gateway's
/// local policy check reads before any verb runs. Every verb that acts on a service
/// tier names it with <see cref="Machine"/> and <see cref="Instance"/>; the verbs
/// themselves (#1365 onward) add their own parameters beside these. See
/// <c>.design/on-prem-gateway.md</c>, "The local policy file".
/// </summary>
public static class GatewayCommandParameters
{
    /// <summary>The computer the target tier runs on, e.g. <c>CRONUS-BC01</c>.</summary>
    public const string Machine = "machine";

    /// <summary>The target server instance, e.g. <c>BC140</c>.</summary>
    public const string Instance = "instance";

    /// <summary>The tenant to act on. Left out on a single-tenant server, which is tenant <c>default</c>.</summary>
    public const string Tenant = "tenant";

    /// <summary>
    /// <c>publish-install</c>: the apps to publish, each an object with
    /// <see cref="AppPublisher"/> and <see cref="AppSigned"/>.
    /// </summary>
    public const string Apps = "apps";

    /// <summary>An app's publisher, as its <c>app.json</c> says.</summary>
    public const string AppPublisher = "publisher";

    /// <summary>Whether the app is signed. Left out means unsigned, which AL Workbench's builds are.</summary>
    public const string AppSigned = "signed";

    /// <summary><c>publish-install</c>: whether to unpublish each app's previous version afterwards.</summary>
    public const string UnpublishPrevious = "unpublishPrevious";

    /// <summary>The tenant a single-tenant server has.</summary>
    public const string DefaultTenant = "default";
}
