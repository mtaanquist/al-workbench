using ALDevToolbox.Services.ObjectExplorer.Projects;
namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One imported snapshot of a Business Central application surface — a DVD-style upload that
/// holds many <see cref="OeModule"/> rows (one per <c>.app</c> file). The Object Explorer's
/// version picker shows Releases; <c>ParentReleaseId</c> lets a third-party Release sit on
/// top of a first-party one so its references resolve up the chain. See
/// <c>.design/object-explorer.md</c> for the full model.
/// </summary>
public class OeRelease
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>User-visible label, e.g. "BC 25.18" or "Continia DC 6.5 on BC 25.18".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// BC platform/application version stamped from a contained Module's manifest at ingest
    /// time (Base Application's <c>Application</c> field is canonical when present).
    /// Nullable because pre-ingest rows and third-party-only Releases without a Base App
    /// in the same upload may not have a clear platform version.
    /// </summary>
    public string? BcVersion { get; set; }

    /// <summary>
    /// One of <c>first_party</c> (Microsoft), <c>third_party</c> (everything
    /// published by someone else — ISV apps and per-customer bundles alike,
    /// differentiated by <see cref="Publisher"/>), <c>cal</c> (a legacy C/AL TXT
    /// export), or <c>project</c> (reserved for pipeline-build Releases stamped by
    /// <c>ProjectBuildImporter</c> — never set from the import forms).
    /// </summary>
    public string Kind { get; set; } = "first_party";

    /// <summary>
    /// The <see cref="Kind"/> of a Release a pipeline build produced. The stored value
    /// says <c>project</c> and stays that way (see CLAUDE.md, "Solutions in the product,
    /// Project in the code"): the Solutions rename once rewrote the literal in three
    /// pages to <c>solution</c>, which matches no row, and every pipeline-build feature
    /// on them went quiet. Compare against this, never against a literal.
    /// </summary>
    public const string ProjectBuildKind = "project";

    /// <summary>
    /// The <see cref="DedupKey"/> prefix of a vendor symbols release a pipeline build
    /// ingested from the public symbol feeds (<c>symbols:{appId}:{version}</c>).
    /// Most of those packages carry no source, and one with no files is left off the
    /// release list, the compare picker, the command palette and the launcher count
    /// (#1092): it exists to resolve references, not to be browsed.
    /// </summary>
    public const string SymbolFeedDedupPrefix = "symbols:";

    /// <summary>
    /// Explicit, source-derived identity for releases that must not import twice —
    /// first-party OnPrem artifacts (<c>bc-onprem:{Maj}.{Min}:{cc}</c>) and the vendor
    /// symbols a pipeline build ingests from the public feeds
    /// (<c>symbols:{appId}:{version}</c>, #901). Null when a release isn't deduped
    /// (manual uploads, manual third-party imports, project builds),
    /// which is why the unique index is filtered to non-null keys. This is what lets
    /// the <see cref="Label"/> be a pure display string. See
    /// <c>.design/roadmap.md</c> ("Harden first-party dedup, then free the label").
    /// </summary>
    public string? DedupKey { get; set; }

    /// <summary>
    /// True for a Microsoft pre-release build imported off the insider channel
    /// (dedup key <c>bc-insider:...</c>), so every list and the MCP surface can
    /// mark it "Preview" and the Releases hero never features one over the newest
    /// shipped release. The auto-import sweep retires such a row once the same
    /// Major.Minor ships. Always false for manual uploads and third-party imports.
    /// </summary>
    public bool IsPrerelease { get; set; }

    /// <summary>
    /// Free-text publisher name for third-party / project Releases. Stored on the
    /// Release rather than derived from module manifests because we sometimes take
    /// over an extension someone else built — the manifest publisher no longer
    /// reflects who owns it now. Null for first-party (Microsoft) Releases.
    /// </summary>
    public string? Publisher { get; set; }

    /// <summary>
    /// Name of the project a <c>kind = project</c> (pipeline-build) Release belongs
    /// to. Null for first-party / third-party Releases. Legacy manual imports that
    /// were merged into <c>third_party</c> (migration <c>MergeProjectKindAndAddCal</c>)
    /// may still carry a value; it's kept but no longer shown or editable for them.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Parent Release this one sits on top of. Null for first-party DVDs. Reference resolution
    /// walks the chain via recursive CTE; same-AppId modules at different versions are
    /// shadowed by the closest-to-current copy. Restricted on delete — a parent can't be
    /// removed while a child still references it.
    /// </summary>
    public int? ParentReleaseId { get; set; }
    public OeRelease? ParentRelease { get; set; }

    /// <summary>
    /// Optional link to the <c>ApplicationVersion</c> catalogue row matching this BC release
    /// wave, for label/UI affordance. Nullable.
    /// </summary>
    public int? ApplicationVersionId { get; set; }
    public ApplicationVersion? ApplicationVersion { get; set; }

    /// <summary>
    /// One of <c>ingesting</c>, <c>ready</c>, <c>failed</c>. Releases stay hidden from the
    /// picker until <c>ready</c>; <c>failed</c> rows are tombstones SiteAdmins can clear.
    /// </summary>
    public string Status { get; set; } = "ingesting";

    /// <summary>Free-text error context for <c>status = 'failed'</c>; null otherwise.</summary>
    public string? StatusMessage { get; set; }

    public DateTime ImportedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Denormalised total <c>oe_module_files</c> count across every module in this
    /// Release, stamped when ingest flips to <c>ready</c>. Cached so the Releases
    /// picker doesn't have to fan out a correlated subquery over multi-thousand-row
    /// file tables for every page load. Updated only by import / management code —
    /// nothing else mutates the file set after a Release goes ready.
    /// </summary>
    public int SourceFileCount { get; set; }

    /// <summary>
    /// Denormalised sum of <c>LENGTH(content)</c> across this Release's source
    /// files. Counterpart to <see cref="SourceFileCount"/> for the "Size" column on
    /// the Releases picker.
    /// </summary>
    public long SourceContentLength { get; set; }

    /// <summary>Soft-delete marker. Null = active.</summary>
    public DateTime? DeletedAt { get; set; }

    public ICollection<OeModule> Modules { get; set; } = new List<OeModule>();
}
