using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Removes the temp files and folders that imports and builds leave behind when the
/// process dies mid-job (#1133). Each job deletes its own on the way out, but a crash or
/// an out-of-memory kill skips that, and a container restart (not a recreate) keeps
/// <c>/tmp</c>: build clones and downloaded artifact or DVD zips run to about 1 GB each.
///
/// <para>
/// <c>StartupTasks</c> calls it before the workers start, and it only removes entries
/// last written before this process started, so it can never take one from a job of this
/// process (or from another process sharing the temp folder, such as a test run booting
/// several hosts). Uploads staged by a request are included, because the startup
/// reconcile fails their jobs anyway.
/// </para>
/// </summary>
internal static class LeftoverImportTempFiles
{
    internal static readonly string[] Prefixes =
    [
        ProjectBuildService.TempPrefix,
        BcArtifactService.TempPrefix,
        DvdDownloadService.TempPrefix,
        ReleaseZipStaging.NestedTempPrefix,
        ReleaseImportRequestService.FolderZipTempPrefix,
        ReleaseImportRequestService.CalTxtTempPrefix,
    ];

    /// <summary>
    /// Deletes every leftover under <paramref name="tempRoot"/> last written before
    /// <paramref name="startedAtUtc"/>. Returns how many entries went. Never throws.
    /// </summary>
    public static int Sweep(string tempRoot, DateTime startedAtUtc, ILogger logger)
    {
        var removed = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(tempRoot).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list {TempRoot} to clean up leftover build and import files.", tempRoot);
            return 0;
        }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (!Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(path) >= startedAtUtc) continue;
                // A link is removed as itself, never followed: File.Delete unlinks it, and
                // Directory.Delete does not descend into linked folders.
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
                removed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the leftover build or import file {Path}.", path);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} leftover build and import file(s) from {TempRoot}.", removed, tempRoot);
        }
        return removed;
    }
}
