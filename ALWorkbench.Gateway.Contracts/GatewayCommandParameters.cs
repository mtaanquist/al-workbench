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

    /// <summary>
    /// An app's id (a GUID), as its <c>app.json</c> says. <c>publish-install</c> needs it:
    /// the gateway reads the id from the <c>.app</c> itself and refuses a file that is
    /// not the app declared (#1375).
    /// </summary>
    public const string AppId = "id";

    /// <summary>
    /// An app's name, as its <c>app.json</c> says. Optional: left out, the gateway checks
    /// the id, publisher and version only; sent, it must be non-blank text (a blank one
    /// refuses the command) and the file's own name must match it. The cmdlets are given
    /// the name read from the file.
    /// </summary>
    public const string AppName = "name";

    /// <summary>
    /// An app's version (<c>1.4.0.0</c>), as its <c>app.json</c> says. <c>publish-install</c>
    /// needs it, and refuses a file whose own version differs.
    /// </summary>
    public const string AppVersion = "version";

    /// <summary><c>publish-install</c>: whether to unpublish each app's previous version afterwards.</summary>
    public const string UnpublishPrevious = "unpublishPrevious";

    /// <summary>
    /// <c>publish-install</c>: the schema sync mode, one of <see cref="GatewaySyncModes"/>
    /// (<c>Sync-NAVApp -Mode</c>). Left out means <see cref="GatewaySyncModes.Add"/>;
    /// any other word refuses the command, and <see cref="GatewaySyncModes.ForceSync"/>
    /// is refused unless the tier has <c>allow_force_sync</c> in the customer's policy file.
    /// </summary>
    public const string SyncMode = "syncMode";

    /// <summary>The tenant a single-tenant server has.</summary>
    public const string DefaultTenant = "default";
}

/// <summary>
/// The schema sync modes a <c>publish-install</c> may ask for, spelt as
/// <c>Sync-NAVApp -Mode</c> and AL Workbench's <c>BcSyncMode</c> spell them.
/// </summary>
public static class GatewaySyncModes
{
    /// <summary>Additive schema changes only. The default.</summary>
    public const string Add = "Add";

    /// <summary>Destructive schema changes allowed: data in removed fields is lost.</summary>
    public const string ForceSync = "ForceSync";

    /// <summary>Every mode the gateway accepts, compared exactly, case included.</summary>
    public static readonly IReadOnlyList<string> All = [Add, ForceSync];
}
