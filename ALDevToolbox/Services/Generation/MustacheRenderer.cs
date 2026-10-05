using System.Text.RegularExpressions;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Renders the small set of <c>{{name}}</c>-style placeholders the generator
/// supports against a <see cref="MustacheContext"/>. Pure: no DB access, no
/// org-context dependency. Decoupled from <see cref="GenerationService"/> so
/// the substitution table can be unit-tested in isolation (#86).
/// </summary>
/// <remarks>
/// Canonical names are snake_case to match the TOML schema. The old
/// camelCase names (<c>workspaceName</c>, <c>shortName</c>, <c>moduleName</c>)
/// are still resolved as aliases for backwards-compatibility — a single
/// warning per render lists any deprecated names encountered so admins can
/// rename them in their org files.
/// The full table is published by <see cref="Domain.ValueObjects.MustacheVariableCatalog"/>.
/// The <c>dependencies_array</c> and <c>id_ranges_array</c> variables
/// substitute as raw JSON fragments so the admin's <c>app.json</c> template
/// can embed them verbatim (e.g. <c>"dependencies": {{dependencies_array}}</c>).
/// </remarks>
public sealed class MustacheRenderer
{
    private static readonly Regex MustacheRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Legacy camelCase → canonical snake_case map. Kept here (rather than in
    /// <see cref="Domain.ValueObjects.MustacheVariableCatalog"/>) because it
    /// is purely a renderer concern: the catalogue describes the placeholders
    /// admins should be authoring against today, not the historical names.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspaceName"] = "workspace_name",
            ["shortName"] = "short_name",
            ["moduleName"] = "module_name",
        };

    private readonly ILogger<MustacheRenderer> _logger;

    public MustacheRenderer(ILogger<MustacheRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Substitutes every supported placeholder in <paramref name="source"/>.
    /// Unknown variables are left as-is and logged at Warning. Deprecated
    /// camelCase names are resolved silently but accumulated; a single
    /// warning is logged per <see cref="Render"/> call listing them all.
    /// </summary>
    public string Render(string source, MustacheContext ctx)
    {
        HashSet<string>? deprecated = null;
        var result = MustacheRegex.Replace(source, match =>
        {
            var key = match.Groups[1].Value;
            if (Aliases.TryGetValue(key, out var canonical))
            {
                (deprecated ??= new(StringComparer.Ordinal)).Add(key);
                key = canonical;
            }
            return key switch
            {
                "name" => ctx.Name,
                "workspace_name" => ctx.WorkspaceName,
                // The word on the form is "customer", so templates can use that
                // word too. Same value as {{workspace_name}} - see
                // .design/customer-naming.md.
                "customer_name" => ctx.WorkspaceName,
                "short_name" => ctx.ShortName,
                "workspace_folder" => ctx.WorkspaceFolder,
                "module_name" => ctx.ModuleName,
                "publisher" => ctx.Publisher,
                "extension_prefix" => ctx.ExtensionPrefix,
                "affix" => ctx.Affix,
                "namespace" => ctx.FolderPath.Replace('/', '.'),
                // Deliberately non-deterministic: a fresh GUID per substitution.
                // Use {{extension_id}} for a value that's stable across a single
                // generation. The catalogue marks {{guid}} AvailableInAdminContent:
                // false so it's kept out of admin-edited files that must round-trip.
                "guid" => Guid.NewGuid().ToString(),
                "tenant_id" => ctx.TenantId,
                "extension_id" => ctx.ExtensionId,
                "extension_name" => ctx.ExtensionName,
                "brief" => ctx.Brief,
                "description" => ctx.Description,
                "url" => ctx.Url,
                "logo_path" => ctx.LogoPath,
                "platform_version" => ctx.PlatformVersion,
                "application_version" => ctx.ApplicationVersion,
                "application_version_major" => ctx.ApplicationVersionMajor,
                "application_version_minor" => ctx.ApplicationVersionMinor,
                "runtime" => ctx.Runtime,
                "dependencies_array" => ctx.DependenciesArrayJson,
                "id_ranges_array" => ctx.IdRangesArrayJson,
                _ => UnknownVariable(match.Value, key),
            };
        });
        if (deprecated is { Count: > 0 })
        {
            _logger.LogWarning(
                "Deprecated camelCase mustache placeholders encountered during generation: {Names}. " +
                "Rename them to their snake_case equivalents.",
                string.Join(", ", deprecated.OrderBy(n => n, StringComparer.Ordinal)));
        }
        return result;
    }

    private string UnknownVariable(string original, string key)
    {
        _logger.LogWarning("Unknown mustache variable {{{{{Key}}}}} encountered during generation; left as-is.", key);
        return original;
    }
}

/// <summary>
/// Bag of values consumed by <see cref="MustacheRenderer.Render"/>. Built per
/// extension or per file by the orchestrator so the same renderer can serve
/// every emit point with different substitution context.
/// </summary>
/// <remarks>
/// <see cref="TenantId"/> is captured on the New Workspace form and persisted
/// to <c>workspace.aldt.toml</c> so regeneration is reproducible. The
/// standalone extension flow leaves it empty.
/// </remarks>
public record MustacheContext(
    string Name,
    string WorkspaceName,
    /// <summary>
    /// What <c>{{short_name}}</c> renders to: the customer's abbreviated name
    /// with its fallback already applied by the caller. It is a display value,
    /// not a path - a file or folder name comes from
    /// <see cref="WorkspaceFolder"/>.
    /// </summary>
    string ShortName,
    string ModuleName,
    string Publisher,
    string ExtensionPrefix,
    string Affix,
    string FolderPath,
    string TenantId = "",
    // Per-extension app.json inputs (workspace-root contexts pass the
    // workspace's equivalents through so a root-scoped file can still embed
    // them without producing nonsense). Default to empty so the existing
    // `with`-clause call sites keep compiling without listing every field.
    string ExtensionId = "",
    string ExtensionName = "",
    string Brief = "",
    string Description = "",
    string Url = "",
    string LogoPath = "",
    string PlatformVersion = "",
    string ApplicationVersion = "",
    string Runtime = "",
    string DependenciesArrayJson = "[]",
    string IdRangesArrayJson = "[]",
    /// <summary>
    /// The organisation's folder naming style, which
    /// <see cref="WorkspaceFolder"/> applies. Passed in rather than read from
    /// the settings row here so this record stays a plain value - the emit
    /// points that build a context already hold the org config.
    /// </summary>
    NamingStyle FolderStyle = NamingStyle.PascalCase)
{
    /// <summary>
    /// The folder (and <c>.code-workspace</c> file) name derived from
    /// <see cref="WorkspaceName"/>. Computed rather than passed in so every
    /// emit point agrees on it - see
    /// <see cref="CustomerNaming"/> and <c>.design/customer-naming.md</c>.
    /// </summary>
    public string WorkspaceFolder => CustomerNaming.Apply(WorkspaceName, FolderStyle);

    /// <summary>
    /// First part of <see cref="ApplicationVersion"/> (<c>28</c> for
    /// <c>28.2.0.0</c>). Teams that version their apps after the Business
    /// Central release they target start <c>app.json</c>'s <c>version</c> with
    /// this and <see cref="ApplicationVersionMinor"/>; build versioning then
    /// fills in the build number. Empty when the version is empty.
    /// </summary>
    public string ApplicationVersionMajor => VersionPart(ApplicationVersion, 0);

    /// <summary>
    /// Second part of <see cref="ApplicationVersion"/> (<c>2</c> for
    /// <c>28.2.0.0</c>), or <c>0</c> when the version names only a major.
    /// Empty when the version is empty.
    /// </summary>
    public string ApplicationVersionMinor => VersionPart(ApplicationVersion, 1);

    private static string VersionPart(string version, int index)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        var parts = version.Trim().Split('.');
        return index < parts.Length && parts[index].Length > 0 ? parts[index] : "0";
    }
}
