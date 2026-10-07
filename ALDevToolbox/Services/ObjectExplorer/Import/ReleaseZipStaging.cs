using System.IO.Compression;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Turns a ZIP staged on disk (an uploaded <c>applications/</c> folder, or a
/// downloaded BC DVD) into the <see cref="AppFileUpload"/> list the importer
/// consumes. Shared by the background <see cref="ReleaseImportWorker"/> and the
/// synchronous amend endpoint so both pair <c>.app</c> + <c>.Source.zip</c> the
/// same way.
/// </summary>
public static class ReleaseZipStaging
{
    /// <summary>Temp-file prefix for a nested DVD zip extracted to disk.</summary>
    public const string NestedTempPrefix = "oe-nested-";

    /// <summary>
    /// Opens <paramref name="tempZipPath"/> and builds one upload per app.
    /// <paramref name="isDvd"/> picks the DVD-subset walk (Applications/ +
    /// System.app, test apps dropped) over the whole-archive walk. The returned
    /// <see cref="ZipArchive"/> owns the entry streams added to
    /// <paramref name="openedStreams"/>; the caller disposes both and deletes
    /// the temp file once the importer has finished reading.
    /// </summary>
    public static (List<AppFileUpload> Uploads, ZipArchive Archive) OpenStagedZip(
        string tempZipPath, bool isDvd, List<Stream> openedStreams)
    {
        var archive = new ZipArchive(File.OpenRead(tempZipPath), ZipArchiveMode.Read);
        GuardEntryCount(archive);
        var entries = WalkArchive(archive, isDvd);

        // Nested-DVD wrapper: some older BC downloads (e.g. "Update 15.1
        // Dynamics 365 Business Central 2019 Release Wave 2 DK") wrap the real
        // DVD in an outer ZIP whose only payload is a single nested
        // <Name>.DVD.zip. The outer walk then finds no apps, so descend into
        // the lone nested zip and walk that instead. Bounded so a pathological
        // wrapper-of-wrapper can't loop forever; in practice one level is all
        // Microsoft ships. See GitHub issue #303.
        var descend = 0;
        while (entries.Count == 0 && descend < MaxNestedZipDescend
            && TryFindSoleNestedZip(archive) is { } nested)
        {
            var inner = ExtractNestedZipToSelfDeletingArchive(nested);
            // Done with the wrapper. Disposing it early releases the outer
            // temp file's read handle; the caller still deletes that path in
            // its own cleanup. The inner archive owns a DeleteOnClose temp, so
            // the caller's existing `archive.Dispose()` reclaims it too — no
            // new cleanup plumbing needed at any call site.
            archive.Dispose();
            archive = inner;
            entries = WalkArchive(archive, isDvd);
            descend++;
        }

        return (BuildUploads(entries, openedStreams), archive);
    }

    /// <summary>
    /// Opens a downloaded Microsoft BC artifact zip and builds its uploads.
    /// <paramref name="isPlatform"/> selects the platform walk (System.app only —
    /// see <see cref="FolderZipWalker.WalkBcArtifactPlatform"/>) over the
    /// application walk (localized <c>Applications.&lt;country&gt;</c> /
    /// <c>Extensions</c> apps, tests dropped). Artifacts aren't nested-zipped, so
    /// there's no descent step. The returned <see cref="ZipArchive"/> owns the
    /// entry streams added to <paramref name="openedStreams"/>; the caller
    /// disposes both and deletes the temp file.
    /// </summary>
    public static (List<AppFileUpload> Uploads, ZipArchive Archive) OpenBcArtifactZip(
        string tempZipPath, bool isPlatform, List<Stream> openedStreams)
    {
        var archive = new ZipArchive(File.OpenRead(tempZipPath), ZipArchiveMode.Read);
        GuardEntryCount(archive);
        var entries = isPlatform
            ? FolderZipWalker.WalkBcArtifactPlatform(archive)
            : FolderZipWalker.WalkBcArtifactApplication(archive);
        return (BuildUploads(entries, openedStreams), archive);
    }

    /// <summary>
    /// Upper bound on the number of entries we'll enumerate in an uploaded
    /// archive before refusing it. A real BC DVD or artifact zip holds at most a
    /// few thousand files; a central directory with hundreds of thousands of
    /// entries is a malformed/hostile upload (the classic many-tiny-entries
    /// flavour of zip bomb) and would otherwise drive the walkers' enumeration
    /// and per-entry work unboundedly. See issue #361.
    /// </summary>
    private const int MaxArchiveEntries = 200_000;

    /// <summary>
    /// Rejects an archive whose central directory advertises an implausible
    /// number of entries before any walker enumerates them. <see cref="ZipArchive.Entries"/>
    /// is materialised lazily from the central directory, so reading
    /// <see cref="ICollection{T}.Count"/> is cheap relative to opening each entry.
    /// </summary>
    private static void GuardEntryCount(ZipArchive archive)
    {
        var count = archive.Entries.Count;
        if (count > MaxArchiveEntries)
        {
            throw new InvalidDataException(
                $"The uploaded archive declares {count:N0} entries, over the {MaxArchiveEntries:N0} limit; "
                + "it doesn't look like a Business Central DVD or artifact.");
        }
    }

    /// <summary>Opens each walked entry's app (and paired source) stream into one upload list.</summary>
    private static List<AppFileUpload> BuildUploads(
        IReadOnlyList<FolderZipEntry> entries, List<Stream> openedStreams)
    {
        var uploads = new List<AppFileUpload>(entries.Count);
        foreach (var entry in entries)
        {
            // Wrap the outer archive's entry streams in the same decompression
            // tripwire AppPackageReader uses for inner entries. Without this the
            // outer DVD/.app layer is uncapped, so a single malicious deflate
            // entry could exhaust the shared Blazor Server heap. See issue #361.
            var appStream = AppPackageReader.OpenCapped(entry.AppEntry);
            openedStreams.Add(appStream);

            Stream? sourceStream = null;
            if (entry.SourceZipEntry is not null)
            {
                sourceStream = AppPackageReader.OpenCapped(entry.SourceZipEntry);
                openedStreams.Add(sourceStream);
            }

            uploads.Add(new AppFileUpload(
                FileName: entry.FileName,
                AppStream: appStream,
                SourceZipStream: sourceStream,
                IsTest: entry.IsTest,
                IsInternal: entry.IsInternal,
                IsLanguagePack: entry.IsLanguagePack));
        }
        return uploads;
    }

    /// <summary>
    /// How many times <see cref="OpenStagedZip"/> will descend into a sole
    /// nested zip before giving up. Microsoft only ever wraps the DVD one level
    /// deep; the small ceiling is purely a guard against a pathological
    /// wrapper-of-wrapper chain looping forever.
    /// </summary>
    private const int MaxNestedZipDescend = 4;

    /// <summary>
    /// Hard cap on the bytes written while unwrapping a nested DVD zip. A real
    /// BC DVD is 1–3 GB; 15 GB leaves generous margin for an unusually large
    /// release while still refusing a malicious/decompression-bomb entry before
    /// it fills the container's ephemeral scratch disk. The cap is enforced
    /// during the copy rather than trusting the entry's declared
    /// <see cref="ZipArchiveEntry.Length"/>, which an attacker controls.
    /// </summary>
    private const long MaxNestedZipBytes = 15L * 1024 * 1024 * 1024;

    /// <summary>
    /// Picks the walk strategy for an open archive. The DVD path is explicit;
    /// otherwise auto-detect a VS Code AL workspace (folders each holding an
    /// <c>app.json</c>) and scope to each app's own build output, so an admin
    /// can zip a multi-root workspace and upload it through the same box. Falls
    /// back to the flat whole-archive walk for a plain <c>applications/</c>
    /// folder zip (no <c>app.json</c>).
    /// </summary>
    private static IReadOnlyList<FolderZipEntry> WalkArchive(ZipArchive archive, bool isDvd) =>
        isDvd
            ? FolderZipWalker.WalkDvd(archive)
            : FolderZipWalker.LooksLikeWorkspace(archive)
                ? FolderZipWalker.WalkWorkspace(archive)
                : FolderZipWalker.Walk(archive);

    /// <summary>
    /// Finds the single nested <c>.zip</c> to descend into when the outer
    /// archive holds no apps, or <see langword="null"/> when the choice is
    /// ambiguous. Prefers an unambiguous sole zip; failing that, a sole
    /// <c>*.dvd.zip</c> (Microsoft's naming for the wrapped DVD) so a stray
    /// sibling zip alongside it doesn't block the descent. Multiple plausible
    /// candidates stay ambiguous on purpose — the caller's "no apps" diagnostic
    /// then names them so the admin can re-zip with just the DVD inside.
    /// Directory entries and zero-length placeholders are ignored.
    /// </summary>
    private static ZipArchiveEntry? TryFindSoleNestedZip(ZipArchive archive)
    {
        ZipArchiveEntry? soleZip = null;
        ZipArchiveEntry? soleDvdZip = null;
        var zipCount = 0;
        var dvdZipCount = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length == 0) continue;
            zipCount++;
            soleZip = entry;
            if (name.EndsWith(".dvd.zip", StringComparison.OrdinalIgnoreCase))
            {
                dvdZipCount++;
                soleDvdZip = entry;
            }
        }
        if (zipCount == 1) return soleZip;
        if (dvdZipCount == 1) return soleDvdZip;
        return null;
    }

    /// <summary>
    /// Extracts a nested zip entry to a temp file and returns a read-only
    /// <see cref="ZipArchive"/> over it. <see cref="ZipArchive"/> in read mode
    /// needs a seekable stream, which a compressed entry's
    /// <see cref="ZipArchiveEntry.Open"/> stream isn't, so the inner zip is
    /// spilled to disk first (the DVD can be a GB-plus — too large to buffer in
    /// memory). The temp file is opened <see cref="FileOptions.DeleteOnClose"/>
    /// so the OS reclaims it when the returned archive's stream is disposed,
    /// tying its lifetime to the archive the caller already disposes.
    /// </summary>
    private static ZipArchive ExtractNestedZipToSelfDeletingArchive(ZipArchiveEntry nested)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), NestedTempPrefix + Guid.NewGuid().ToString("N") + ".zip");
        var fs = new FileStream(tempPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose,
        });
        try
        {
            using (var source = nested.Open())
            {
                CopyWithCap(source, fs, MaxNestedZipBytes);
            }
            fs.Position = 0;
            // ZipArchive owns fs (leaveOpen defaults to false), so disposing the
            // archive disposes fs, which deletes the temp file.
            return new ZipArchive(fs, ZipArchiveMode.Read);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Streams <paramref name="source"/> into <paramref name="dest"/>, throwing
    /// <see cref="InvalidDataException"/> once more than <paramref name="maxBytes"/>
    /// have been written. Counting the bytes actually inflated (rather than
    /// trusting the zip entry's declared length) is what makes this a real guard
    /// against a decompression bomb.
    /// </summary>
    private static void CopyWithCap(Stream source, Stream dest, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"The nested DVD zip expands past the {maxBytes / (1024 * 1024 * 1024)} GB limit; "
                    + "it doesn't look like a Business Central DVD.");
            }
            dest.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// Builds a short human description of where <c>.app</c> files actually sit
    /// in the archive, for the "no apps found" failure message — so an
    /// unrecognised DVD layout tells us the folder name to add to
    /// <c>FolderZipWalker.DvdAppFolderNames</c> instead of failing opaquely.
    /// Reports the distinct top-level path segments of every <c>.app</c> entry.
    /// </summary>
    public static string DescribeAppLocations(ZipArchive archive)
    {
        var topFolders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var nestedZips = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawApp = false;
        foreach (var entry in archive.Entries)
        {
            // Normalise backslash separators (some DVD ZIPs use them) before
            // taking the top segment.
            var full = entry.FullName.Replace('\\', '/');
            if (full.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                sawApp = true;
                var slash = full.IndexOf('/');
                topFolders.Add(slash > 0 ? full[..slash] : "(archive root)");
            }
            else if (!full.EndsWith('/') && full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                nestedZips.Add(full);
            }
        }

        if (sawApp) return "the .app files are under: " + string.Join(", ", topFolders);

        // A single nested zip is descended into automatically; reaching here
        // with several means we couldn't pick which one is the DVD, so name
        // them — the admin can re-zip with just the DVD inside.
        if (nestedZips.Count > 0)
        {
            return "the archive contains no .app files, only nested zip(s): "
                + string.Join(", ", nestedZips)
                + " — a single nested zip is opened automatically, but several are ambiguous; "
                + "re-zip with just the DVD inside";
        }
        return "the archive contains no .app files at all";
    }
}
