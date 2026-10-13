using ALDevToolbox.Services.ObjectExplorer.Import;
namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Three-bucket module-level summary for a Release-vs-Release comparison.
/// Modules are keyed by <c>AppId</c>: <see cref="Added"/> appears on the right
/// only, <see cref="Removed"/> on the left only, <see cref="Changed"/> on both
/// sides with at least one file pair whose <c>content_hash</c> differs (or one
/// file present on only one side).
/// </summary>
public sealed record ReleaseCompareSummary(
    int LeftReleaseId,
    string LeftReleaseLabel,
    int RightReleaseId,
    string RightReleaseLabel,
    IReadOnlyList<ModuleCompareEntry> Added,
    IReadOnlyList<ModuleCompareEntry> Removed,
    IReadOnlyList<ModuleCompareEntry> Changed);

/// <summary>
/// One module bucketed by the compare. Left/right ids and versions are
/// nullable because the row is half-populated for the Added/Removed buckets.
/// File counts are zero for Added/Removed (the whole module is the diff);
/// for Changed they break down which file pairs differ how.
/// </summary>
public sealed record ModuleCompareEntry(
    Guid AppId,
    string Name,
    string Publisher,
    long? LeftModuleId,
    string? LeftVersion,
    long? RightModuleId,
    string? RightVersion,
    int AddedFileCount,
    int RemovedFileCount,
    int ChangedFileCount);

/// <summary>
/// File-pair diff for a single Changed module. Files are paired by canonical
/// <c>Path</c> (set during ingest by <c>AppPackageReader.CanonicalizeSourcePath</c>),
/// so the same conceptual file across two releases of the same AppId joins
/// correctly even when the on-disk layout shifted.
/// </summary>
public sealed record ModuleFileCompareResult(
    long LeftModuleId,
    long RightModuleId,
    string ModuleName,
    IReadOnlyList<FileCompareEntry> Added,
    IReadOnlyList<FileCompareEntry> Removed,
    IReadOnlyList<FileCompareEntry> Changed);

/// <summary>
/// One file in the per-module diff. Both ids null is impossible; LeftFileId
/// only means Removed, RightFileId only means Added, both populated means
/// Changed (<c>content_hash</c> differed).
/// </summary>
public sealed record FileCompareEntry(
    string Path,
    long? LeftFileId,
    long? RightFileId,
    int LeftLineCount,
    int RightLineCount);

/// <summary>
/// One file-pair row in the flattened release-compare result, ready for the
/// search table on the Release page. Combines a <see cref="ModuleCompareEntry"/>
/// header with a single <see cref="FileCompareEntry"/>.
/// </summary>
public sealed record ReleaseCompareFileRow(
    Guid AppId,
    string ModuleName,
    string Path,
    string Status,            // "added" | "removed" | "modified"
    long? LeftFileId,
    long? RightFileId);

/// <summary>
/// One option in the "Compare with release" picker on the source-file viewer.
/// Only releases that contain a file with the same <c>(AppId, Path)</c> pair
/// as the currently-viewed file are returned, so the picker is dead-link-free.
/// <see cref="TargetIsOlder"/> tells the caller which way round to build the
/// diff link: the older release goes on the LEFT so added lines always read
/// green in the newer version.
/// </summary>
public sealed record CompareTargetOption(
    int ReleaseId,
    string ReleaseLabel,
    long TargetFileId,
    bool TargetIsOlder = false);

/// <summary>
/// One object in an <b>object-level</b> Release-vs-Release comparison, matched
/// by <c>(Kind, ObjectId)</c> (falling back to <c>(Kind, Name)</c> for id-less
/// objects). Unlike the module/AppId-keyed file compare, this lines two
/// independent releases up by object identity — the shape a legacy C/AL
/// Base-vs-Customer diff needs, since each C/AL release is its own synthetic
/// module with a distinct AppId. <see cref="Status"/> is decided by the
/// objects' source-slice <c>content_hash</c>. For <c>modified</c> rows both
/// file ids are set, so the row links straight into the side-by-side source
/// diff at <c>/object-explorer/compare/file</c>.
/// </summary>
public sealed record ObjectCompareRow(
    string Kind,
    int? ObjectId,
    string Name,
    string Status,            // "added" | "removed" | "modified" | "unchanged"
    long? LeftFileId,
    long? RightFileId);

/// <summary>
/// One object-level diff: the rows, capped, plus what the caller needs to say
/// so. <see cref="ObjectCount"/> is how many objects matched the filters across
/// both releases, unchanged ones included, so a page can still say "all N are
/// identical" when <see cref="Rows"/> leaves them out. <see cref="Truncated"/> is
/// true when more rows matched than <see cref="Rows"/> holds (#1228).
/// </summary>
public sealed record ObjectCompareSet(
    IReadOnlyList<ObjectCompareRow> Rows,
    int ObjectCount,
    bool Truncated);
