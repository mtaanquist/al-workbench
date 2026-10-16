using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Pure-text helpers shared across admin and site-admin Razor pages. Lives
/// here so the same copy of <c>PrettyJson</c>, <c>SoftDeleteStatus</c> and
/// <c>Capitalize</c> backs every call site (#80).
/// </summary>
public static class AdminPageHelpers
{
    /// <summary>
    /// Re-renders a JSON string with indentation for inline display in an
    /// audit snapshot. Returns the original string verbatim when the input
    /// isn't valid JSON — older audit rows or partial fragments shouldn't
    /// crash the page.
    /// </summary>
    public static string PrettyJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>
    /// Status label for a soft-deletable / deprecatable row. Deleted always
    /// wins over Deprecated; both fall through to Active.
    /// </summary>
    public static string SoftDeleteStatus(DateTime? deletedAt, bool deprecated) =>
        deletedAt is not null ? "Deleted"
        : deprecated ? "Deprecated"
        : "Active";

    /// <summary>
    /// The same three states as <see cref="SoftDeleteStatus"/>, lower-cased for
    /// <c>RowStateIcon</c>. An admin table shows state as the leading glyph plus
    /// the row's edge keyline, never as a word in a column of its own — see the
    /// component's own doc comment. Deleted still wins over Deprecated.
    /// </summary>
    public static string SoftDeleteState(DateTime? deletedAt, bool deprecated) =>
        deletedAt is not null ? "deleted"
        : deprecated ? "deprecated"
        : "active";

    /// <summary>
    /// Title-cases the first letter of a single word — used to turn the
    /// lower-case bulk-action verbs ("disable", "promote") into modal copy
    /// like "Disable 3 users?".
    /// </summary>
    public static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Past-tense verb for an audit row, so an entry reads as a sentence
    /// ("Mads changed this template") rather than a table cell. Shared by the
    /// per-entity history panel and the two global audit logs.
    /// </summary>
    public static string AuditVerb(AuditAction action) => action switch
    {
        AuditAction.Created => "created",
        AuditAction.Deleted => "deleted",
        _ => "changed",
    };

    /// <summary>
    /// The <c>.status-pill</c> variant for an audit action. Created is a good
    /// outcome, deleted is destructive, an update is neither — amber rather
    /// than green so a row of edits does not read as a row of successes.
    /// </summary>
    public static string AuditActionPill(AuditAction action) => action switch
    {
        AuditAction.Created => "success",
        AuditAction.Deleted => "danger",
        _ => "warn",
    };

    /// <summary>
    /// Display label for an audited entity type. The pre-unification values are
    /// still reachable: historical rows written before
    /// <c>20260514000000_UnifyExtensions</c> carry them.
    /// </summary>
    public static string FriendlyAuditType(AuditEntityType type) => type switch
    {
        AuditEntityType.RuntimeTemplate => "Template",
        AuditEntityType.WorkspaceExtension => "Extension",
        AuditEntityType.WorkspaceExtensionFolder => "Extension folder",
        AuditEntityType.WorkspaceExtensionFile => "Extension file",
        AuditEntityType.WorkspaceExtensionDependency => "Extension dependency",
        AuditEntityType.ModuleExtensionFolder => "Module folder",
        AuditEntityType.ModuleExtensionFile => "Module file",
        AuditEntityType.TemplateFolder => "Template folder",
        AuditEntityType.TemplateFile => "Template file",
        AuditEntityType.TemplateModuleFolder => "Template module folder",
        AuditEntityType.TemplateModuleFile => "Template module file",
        AuditEntityType.RuntimeTemplateDefaultModule => "Template default module",
        AuditEntityType.Module => "Module",
        AuditEntityType.ModuleDependency => "Module dependency",
        AuditEntityType.WellKnownDependency => "Catalogue entry",
        AuditEntityType.ReleasePipeline => "Deployment pipeline",
        AuditEntityType.Project => "Solution connection",
        // The multi-word enum names below used to fall through to ToString() and
        // reached the reader as "ApplicationVersion" / "PersonalAccessToken".
        // Single-word ones (User, Recipe, Invite, Backup, Passkey) still fall through
        // because the enum name already is the word a reader would use.
        AuditEntityType.ApplicationVersion => "Application version",
        AuditEntityType.SignupRequest => "Signup request",
        AuditEntityType.OrganizationSettings => "Organisation settings",
        AuditEntityType.OrganizationAsset => "Organisation asset",
        AuditEntityType.OrganizationFile => "Organisation file",
        AuditEntityType.SystemSettings => "System settings",
        AuditEntityType.RecipeFile => "Recipe file",
        AuditEntityType.RecipeSuggestion => "Recipe suggestion",
        AuditEntityType.RecipeSuggestionFile => "Recipe suggestion file",
        AuditEntityType.PersonalAccessToken => "Access token",
        // "Team" itself falls through — the enum name is already the word.
        AuditEntityType.TeamMember => "Team membership",
        // A team gaining or losing access to a project. "Assignment" rather than
        // "Project team" so the sentence reads "removed a team assignment".
        AuditEntityType.ProjectTeam => "Team assignment",
        // A setting changed on a customer's Business Central environment. "Environment
        // setting" rather than "Project environment" so the sentence reads "changed an
        // environment setting" - the row is only ever written for a deliberate change.
        AuditEntityType.ProjectEnvironment => "Environment setting",
        AuditEntityType.GitHubRepository => "GitHub repository",
        // "Gateway" itself falls through - the enum name is already the word.
        AuditEntityType.GatewayRegistrationCode => "Gateway registration code",
        _ => type.ToString(),
    };

    /// <summary>
    /// The subject of an audit sentence: what changed, named when we know its
    /// name. <c>("the module", "Sales Extensions")</c> renders as
    /// <i>changed the module <b>Sales Extensions</b></i>; <c>("a catalogue
    /// entry", null)</c> as <i>deleted a catalogue entry</i>.
    ///
    /// <para>Split rather than pre-joined so the caller can emphasise the name,
    /// which is the half a reader is scanning for.</para>
    /// </summary>
    public readonly record struct AuditSubject(string Lead, string? Name);

    /// <summary>
    /// Types there is only ever one of, which take "the" and never a name.
    /// "changed a system settings" is wrong twice over.
    /// </summary>
    private static readonly HashSet<AuditEntityType> SingletonTypes = new()
    {
        AuditEntityType.OrganizationSettings,
        AuditEntityType.SystemSettings,
    };

    /// <summary>
    /// Turns an audit row into the subject of a sentence — see issue #554.
    /// Every row used to read <c>changed Module #4</c>, and <c>#4</c> is the
    /// primary key of a row in <c>modules</c>: an admin has never seen it,
    /// cannot map it to anything, and two rows about two different modules were
    /// indistinguishable at a glance.
    ///
    /// <para>The name comes from <see cref="AuditLogEntry.EntityName"/>, stamped
    /// when the change happened, and falls back to the row's own snapshot for
    /// entries written before that column existed. When neither has one the
    /// sentence says so plainly rather than reaching for the id again — an
    /// unnamed thing is still better described as "a catalogue entry" than as a
    /// number the reader cannot use.</para>
    /// </summary>
    public static AuditSubject AuditSubjectOf(AuditLogEntry entry) =>
        AuditSubjectOf(entry.EntityType, entry.EntityName, entry.SnapshotJson);

    /// <summary>
    /// The same wording from the loose parts, for the SiteAdmin audit page —
    /// it reads cross-org rows through a projection rather than the entity, and
    /// the two pages have to describe one change identically.
    /// </summary>
    public static AuditSubject AuditSubjectOf(
        AuditEntityType entityType, string? entityName, string? snapshotJson)
    {
        var label = FriendlyAuditType(entityType);
        var lower = char.ToLowerInvariant(label[0]) + label[1..];

        if (SingletonTypes.Contains(entityType))
        {
            return new AuditSubject($"the {lower}", null);
        }

        var name = entityName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = AuditEntityName.FromSnapshot(snapshotJson);
        }

        return string.IsNullOrWhiteSpace(name)
            ? new AuditSubject($"{IndefiniteArticle(lower)} {lower}", null)
            : new AuditSubject($"the {lower}", name.Trim());
    }

    /// <summary>
    /// "a" or "an" for a type label. First-letter vowels, with <c>u</c>
    /// excluded: it is the one vowel that usually opens on a consonant sound,
    /// and "user" — <i>a user</i>, not <i>an user</i> — is the only label in the
    /// enum that starts with one.
    /// </summary>
    private static string IndefiniteArticle(string word) =>
        word.Length > 0 && "aeio".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";
}
