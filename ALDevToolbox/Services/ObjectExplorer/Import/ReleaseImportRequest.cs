namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// One Release-import operation in its entirety: the label / kind / parent the
/// admin chose on the upload form, plus the list of <c>.app</c> files to ingest
/// into the new <see cref="Domain.Entities.ObjectExplorer.OeRelease"/>. Each
/// upload optionally carries a paired <c>.Source.zip</c> for when the <c>.app</c>
/// doesn't embed source.
/// </summary>
public sealed record ReleaseImportRequest(
    string Label,
    string Kind,
    int? ParentReleaseId,
    int? ApplicationVersionId,
    IReadOnlyList<AppFileUpload> Uploads,
    string? Publisher = null,
    string? ProjectName = null,
    bool StoreSymbolReference = false);

/// <summary>
/// The Release-identity half of an import (everything except the uploads). The
/// queued DVD-scale paths create the Release row from this synchronously, then
/// hand the uploads to the background worker — which materialises them from a
/// downloaded/staged ZIP rather than request-scoped streams.
/// </summary>
public sealed record ReleaseImportMetadata(
    string Label,
    string Kind,
    int? ParentReleaseId,
    int? ApplicationVersionId,
    string? Publisher = null,
    string? ProjectName = null,
    string? DedupKey = null,
    bool IsPrerelease = false)
{
    public static ReleaseImportMetadata From(ReleaseImportRequest request) => new(
        Label: request.Label,
        Kind: request.Kind,
        ParentReleaseId: request.ParentReleaseId,
        ApplicationVersionId: request.ApplicationVersionId,
        Publisher: request.Publisher,
        ProjectName: request.ProjectName);
}

/// <summary>
/// One <c>.app</c> file + optional paired source zip. Streams are owned by the
/// caller; the service reads them to completion but does not close them.
///
/// The three flag fields default to <c>false</c> so the per-file upload path
/// (where admins pick individual .apps without any path context) stays
/// backward compatible. The folder-ZIP upload path infers them from the
/// surrounding folder + filename conventions before construction.
/// </summary>
public sealed record AppFileUpload(
    string FileName,
    Stream AppStream,
    Stream? SourceZipStream,
    bool IsTest = false,
    bool IsInternal = false,
    bool IsLanguagePack = false);

/// <summary>
/// Summary statistics from an import run — surfaced on the admin UI's
/// completion screen (PR 5) and exposed to tests so they can pin the exact
/// counts without re-querying the database.
/// </summary>
public sealed record ReleaseImportSummary(
    int ReleaseId,
    int ModulesImported,
    int ModulesSkipped,
    int ObjectsImported,
    int ReferencesImported,
    int SourceFilesImported,
    int TranslationsImported);
