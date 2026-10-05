namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Single source of truth for the mustache placeholders the generator
/// understands. The generator reads <see cref="Names"/> when matching tokens
/// during substitution; the admin UI reads <see cref="AvailableInAdminContent"/>
/// to render the hint shown above mustache-enabled editors. Keeping the list in
/// one place stops the doc-strings and the UI hint from drifting away from the
/// generator's actual switch.
/// </summary>
/// <remarks>
/// All canonical names are snake_case to match the TOML schema. The renderer
/// keeps a separate map of legacy camelCase aliases
/// (<c>workspaceName</c>, <c>shortName</c>, <c>moduleName</c>) for
/// backwards-compatibility but they are intentionally absent from this
/// catalogue — the admin hint should advertise only the current names.
/// </remarks>
public static class MustacheVariableCatalog
{
    /// <summary>
    /// The full table of variables recognised by
    /// <see cref="Services.Generation.MustacheRenderer"/>'s substituter. Order
    /// is the order admins see in the hint — most-useful first, scoped/contextual
    /// last.
    /// </summary>
    public static readonly IReadOnlyList<MustacheVariable> All = new MustacheVariable[]
    {
        new("workspace_name", "Workspace display name as the user typed it (e.g. \"CRONUS Customer\").", AvailableInAdminContent: true),
        new("customer_name", "The same value as {{workspace_name}}, under the word the form uses (e.g. \"CRONUS Customer\").", AvailableInAdminContent: true),
        new("short_name", "The short name typed on the form, or the customer's name when it was left blank (e.g. \"CRO\"). Used in extension names.", AvailableInAdminContent: true),
        new("workspace_folder", "Folder the workspace unpacks into, derived from the workspace name (e.g. \"CRONUSCustomer\"). Use it wherever a path is needed.", AvailableInAdminContent: true),
        new("publisher", "Organisation publisher from the configuration defaults.", AvailableInAdminContent: true),
        new("extension_prefix", "Extension prefix from the New Workspace form.", AvailableInAdminContent: true),
        new("affix", "Template affix when the template's affix type is not 'None'; empty otherwise.", AvailableInAdminContent: true),
        new("tenant_id", "Tenant GUID captured on the New Workspace form (empty for standalone extensions).", AvailableInAdminContent: true),
        // The block below carries the per-extension app.json inputs. Surfaced
        // to admin content because the canonical app.json template is now an
        // OrganizationFile authored from /admin/templates/files; the variables
        // resolve per-extension when the file is emitted with Scope =
        // EveryExtension.
        new("extension_name", "Resolved name of the current extension (e.g. \"CRONUS Core\"). Per-extension scope only.", AvailableInAdminContent: true),
        new("extension_id", "Fresh GUID assigned to the current extension. Stable for a single generation.", AvailableInAdminContent: true),
        new("brief", "Brief from the New Workspace / New Extension form.", AvailableInAdminContent: true),
        new("description", "Description from the New Workspace / New Extension form.", AvailableInAdminContent: true),
        new("url", "Organisation URL from the configuration defaults.", AvailableInAdminContent: true),
        new("logo_path", "Workspace-relative path to the embedded org logo (e.g. ../.assets/images/logo.png).", AvailableInAdminContent: true),
        new("platform_version", "Platform version from the template defaults.", AvailableInAdminContent: true),
        new("application_version", "Application version resolved for the current extension (after \"Latest\" substitution).", AvailableInAdminContent: true),
        new("application_version_major", "Major part of the application version (e.g. \"28\" for \"28.2.0.0\"). Use with {{application_version_minor}} to start an app version, e.g. \"{{application_version_major}}.{{application_version_minor}}.0.0\". Per-extension scope only.", AvailableInAdminContent: true),
        new("application_version_minor", "Minor part of the application version (e.g. \"2\" for \"28.2.0.0\"), or \"0\" when the version has no minor part. Per-extension scope only.", AvailableInAdminContent: true),
        new("runtime", "AL runtime version resolved for the current extension.", AvailableInAdminContent: true),
        new("dependencies_array", "Raw JSON array of resolved dependency objects, e.g. [{\"id\":\"...\",\"name\":\"...\"}]. Embed verbatim where a JSON array is expected.", AvailableInAdminContent: true),
        new("id_ranges_array", "Raw JSON array of resolved id range objects, e.g. [{\"from\":50000,\"to\":50099}].", AvailableInAdminContent: true),
        new("name", "Resolved name of the current extension (per-file context only — prefer {{extension_name}} in admin-edited files).", AvailableInAdminContent: false),
        new("module_name", "Module name when generating from a catalogue module clone.", AvailableInAdminContent: false),
        new("namespace", "Folder path of the current AL file, dots-separated (per-file context only).", AvailableInAdminContent: false),
        new("guid", "Fresh GUID generated on every substitution — avoid in admin-edited files; the file would change on every generation.", AvailableInAdminContent: false),
    };

    /// <summary>Subset surfaced in the admin-facing hint above mustache-enabled editors.</summary>
    public static IEnumerable<MustacheVariable> ForAdminContent =>
        All.Where(v => v.AvailableInAdminContent);

    /// <summary>All recognised names — used by the generator and tested for parity with the catalogue.</summary>
    public static IReadOnlySet<string> Names { get; } =
        All.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
}

/// <summary>One entry in <see cref="MustacheVariableCatalog"/>.</summary>
/// <param name="Name">The placeholder name (without the surrounding braces).</param>
/// <param name="Caption">One-line description shown in the admin hint.</param>
/// <param name="AvailableInAdminContent">
/// True when the variable resolves to a stable, useful value in
/// admin-edited org-wide files. False for per-file or volatile substitutions
/// (e.g. <c>{{guid}}</c>) that would produce churn if embedded in
/// always-included content or the workspace settings JSON.
/// </param>
public sealed record MustacheVariable(string Name, string Caption, bool AvailableInAdminContent);
