namespace ALDevToolbox.Domain.Seed;

/// <summary>
/// In-memory representation of <c>organization-config.toml</c> in an export
/// archive (Milestone P3.14). Round-trips an organisation's defaults block,
/// always-included files, and logo so a snapshot import restores the full
/// admin-visible configuration.
/// </summary>
public class OrganizationConfigSeedFile
{
    public OrganizationSettingsSeed Settings { get; set; } = new();
    public OrganizationLogoSeed? Logo { get; set; }
    public List<OrganizationFileSeed> File { get; set; } = new();
}

public class OrganizationSettingsSeed
{
    public string DefaultPublisher { get; set; } = string.Empty;
    public int DefaultIdRangeFrom { get; set; }
    public int DefaultIdRangeTo { get; set; }
    public string DefaultBrief { get; set; } = string.Empty;
    public string DefaultCoreDescription { get; set; } = string.Empty;

    /// <summary>
    /// JSON template for the workspace's <c>{{workspace_folder}}.code-workspace</c>
    /// file. Empty in pre-Issue-#61 exports — readers default to the in-app
    /// fallback (<see cref="ValueObjects.OrganizationDefaults.CodeWorkspaceJson"/>).
    /// </summary>
    public string CodeWorkspaceJson { get; set; } = string.Empty;

    /// <summary>
    /// Default URL. Null when the export predates the field, in which case the
    /// import keeps the current value rather than clearing it; an empty string
    /// means "no URL".
    /// </summary>
    public string? DefaultUrl { get; set; }

    /// <summary>
    /// Default logo path written into app.json. Same null-means-absent rule as
    /// <see cref="DefaultUrl"/>.
    /// </summary>
    public string? DefaultLogo { get; set; }
}

public class OrganizationLogoSeed
{
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Logo bytes encoded as base64. Capped at 256 KB upstream.</summary>
    public string ContentBase64 { get; set; } = string.Empty;
}

public class OrganizationFileSeed
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool MustacheEnabled { get; set; }
    /// <summary>
    /// Scope round-trip via TOML. Stored as the enum name so exports stay
    /// readable. Defaults to <c>WorkspaceRoot</c> so seeds authored before
    /// the column landed parse cleanly.
    /// </summary>
    public string Scope { get; set; } = nameof(ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.WorkspaceRoot);
}
