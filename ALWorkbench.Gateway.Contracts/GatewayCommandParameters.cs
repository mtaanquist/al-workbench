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
    /// <see cref="AppFile"/>, <see cref="AppPublisher"/> and <see cref="AppSigned"/>.
    /// Required and never empty, with exactly one entry for each of the command's
    /// files: the gateway refuses a <c>publish-install</c> whose list does not match
    /// its files one to one.
    /// </summary>
    public const string Apps = "apps";

    /// <summary>The name of the command's file that holds this app, as in <c>GatewayCommandFileMessage.Name</c>.</summary>
    public const string AppFile = "file";

    /// <summary>An app's publisher, as its <c>app.json</c> says.</summary>
    public const string AppPublisher = "publisher";

    /// <summary>
    /// Whether AL Workbench believes the app is signed. Left out means unsigned, which AL
    /// Workbench's builds are. Only a claim: the policy check uses it to refuse early, and
    /// the verb checks the file's own signature before publishing (#1375).
    /// </summary>
    public const string AppSigned = "signed";

    /// <summary><c>publish-install</c>: whether to unpublish each app's previous version afterwards.</summary>
    public const string UnpublishPrevious = "unpublishPrevious";

    /// <summary>The tenant a single-tenant server has.</summary>
    public const string DefaultTenant = "default";
}
