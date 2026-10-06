namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One curated entry in the application-version catalogue (Milestone P2.4).
/// Pairs a Business Central marketing name with the matching four-part
/// <c>application</c> version and the dotted <c>runtime</c> string. Picking
/// an entry on the New Workspace / New Extension forms fills both fields
/// atomically so users stop typing free-text versions by hand.
/// </summary>
public class ApplicationVersion
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>URL-safe unique key (e.g. <c>bc-2026-rw1</c>). Stable across renames.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Friendly label (e.g. <c>Business Central 2026 Release Wave 1</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Four-part application version (e.g. <c>28.0.0.0</c>). Goes into
    /// <c>app.json</c>'s <c>application</c> field verbatim.
    /// </summary>
    public string Application { get; set; } = string.Empty;

    /// <summary>
    /// Runtime string (e.g. <c>"28.0"</c>). BC's runtime field carries minor
    /// versions in real releases, so this stays a string rather than a number.
    /// </summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>
    /// Display order in the user-facing select. Newer releases sit above older
    /// ones by convention; the admin editor controls the actual values.
    /// </summary>
    public int Ordering { get; set; }

    /// <summary>Hides the entry from the user-facing select while keeping it referencable.</summary>
    public bool Deprecated { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. <c>null</c> means the row is active.</summary>
    public DateTime? DeletedAt { get; set; }
}
