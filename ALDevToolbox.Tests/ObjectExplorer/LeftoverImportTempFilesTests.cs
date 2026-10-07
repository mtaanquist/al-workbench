using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The startup sweep removes the build clones and downloads a crashed process left in
/// the temp folder, and nothing else (#1133).
/// </summary>
public sealed class LeftoverImportTempFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("leftover-sweep-").FullName;

    [Fact]
    public void Removes_leftover_build_folders_and_downloads()
    {
        var clone = Path.Combine(_root, "oe-build-0123abcd", "repo-0", ".git");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(_root, "oe-build-discover-4567"));
        foreach (var file in new[] { "oe-artifact-1.zip", "oe-dvd-2.zip", "oe-nested-3.zip", "oe-folder-4.zip", "oe-cal-5.txt" })
        {
            File.WriteAllText(Path.Combine(_root, file), "x");
        }

        var removed = LeftoverImportTempFiles.Sweep(_root, NullLogger.Instance);

        removed.Should().Be(7);
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
    }

    [Fact]
    public void Leaves_everything_else_in_the_temp_folder_alone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "aldt-bcquality"));
        File.WriteAllText(Path.Combine(_root, "someone-else.zip"), "x");
        File.WriteAllText(Path.Combine(_root, "notoe-build-1"), "x");

        LeftoverImportTempFiles.Sweep(_root, NullLogger.Instance).Should().Be(0);

        Directory.EnumerateFileSystemEntries(_root).Should().HaveCount(3);
    }

    [Fact]
    public void A_leftover_link_is_removed_without_touching_what_it_points_at()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Directory.CreateTempSubdirectory("leftover-sweep-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
            Directory.CreateSymbolicLink(Path.Combine(_root, "oe-build-link"), outside);
            var clone = Path.Combine(_root, "oe-build-real");
            Directory.CreateDirectory(clone);
            Directory.CreateSymbolicLink(Path.Combine(clone, "escape"), outside);

            LeftoverImportTempFiles.Sweep(_root, NullLogger.Instance).Should().Be(2);

            Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
            File.Exists(Path.Combine(outside, "keep.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_missing_temp_folder_is_not_an_error()
    {
        LeftoverImportTempFiles.Sweep(Path.Combine(_root, "missing"), NullLogger.Instance).Should().Be(0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
