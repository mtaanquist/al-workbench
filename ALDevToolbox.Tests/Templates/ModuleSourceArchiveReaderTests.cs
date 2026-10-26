using System.Text;
using ALDevToolbox.Services.Templates;
using AwesomeAssertions;
using static ALDevToolbox.Tests.Templates.ModuleSourceTestKit;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// The address an Editor pastes, and what a repository's tarball turns into
/// (#1415). The tarball is untrusted content bound for customer workspaces, so
/// most of these are about what is left out and why.
/// </summary>
public sealed class ModuleSourceArchiveReaderTests
{
    // --- The address ---------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/cronus-dk/document-capture", "cronus-dk/document-capture", null, null)]
    [InlineData("https://github.com/cronus-dk/document-capture.git", "cronus-dk/document-capture", null, null)]
    [InlineData("https://github.com/cronus-dk/document-capture/", "cronus-dk/document-capture", null, null)]
    [InlineData("github.com/cronus-dk/document-capture", "cronus-dk/document-capture", null, null)]
    [InlineData("cronus-dk/document-capture", "cronus-dk/document-capture", null, null)]
    [InlineData("https://github.com/cronus-dk/apps/tree/main/DocumentCapture", "cronus-dk/apps", "main", "DocumentCapture")]
    [InlineData("https://github.com/cronus-dk/apps/tree/v1.2/src/Doc", "cronus-dk/apps", "v1.2", "src/Doc")]
    public void An_address_is_read_into_owner_repository_branch_and_folder(
        string address, string fullName, string? gitRef, string? path)
    {
        ModuleSourceAddress.TryParse(address, out var parsed, out var refFromLink, out var pathFromLink, out _)
            .Should().BeTrue();
        parsed.Should().Be(fullName);
        refFromLink.Should().Be(gitRef);
        pathFromLink.Should().Be(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://gitlab.com/cronus-dk/document-capture")]
    [InlineData("https://github.com.evil.example/cronus-dk/document-capture")]
    [InlineData("https://user:pass@github.com/cronus-dk/document-capture")]
    [InlineData("https://github.com:8443/cronus-dk/document-capture")]
    [InlineData("ftp://github.com/cronus-dk/document-capture")]
    [InlineData("evil.example/cronus-dk/document-capture")]
    [InlineData("cronus-dk")]
    [InlineData("cronus dk/document capture")]
    [InlineData("https://github.com/cronus-dk/document-capture/blob/main/app.json")]
    [InlineData("https://github.com/cronus-dk/..")]
    public void Anything_but_a_github_com_repository_is_refused(string address)
    {
        ModuleSourceAddress.TryParse(address, out _, out _, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("src/../..")]
    [InlineData("src\\Doc")]
    [InlineData("src//Doc")]
    public void A_folder_that_climbs_or_uses_backslashes_is_refused(string path)
    {
        ModuleSourceAddress.TryNormalisePath(path, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_folder_loses_its_outer_slashes_and_blank_means_the_root()
    {
        ModuleSourceAddress.TryNormalisePath("/src/Doc/", out var normalised, out _).Should().BeTrue();
        normalised.Should().Be("src/Doc");
        ModuleSourceAddress.TryNormalisePath("  ", out var root, out _).Should().BeTrue();
        root.Should().BeNull();
    }

    // --- The archive -------------------------------------------------------

    [Fact]
    public async Task An_extension_becomes_a_folder_tree_and_its_app_json_is_read_not_copied()
    {
        var snapshot = await ReadAsync(SimpleExtension(), path: null);

        var src = snapshot.Folders.Should().ContainSingle().Subject;
        src.Path.Should().Be("src");
        var codeunits = src.Folders.Should().ContainSingle().Subject;
        codeunits.Path.Should().Be("Codeunits");
        codeunits.Files.Should().ContainSingle(f => f.Path == "DocAdapter.Codeunit.al" && !f.IsExample);
        snapshot.FileCount.Should().Be(1);

        snapshot.Dependencies.Should().ContainSingle(d => d.DepName == "Continia Core" && d.DepVersion == "1.0.0.0");
        snapshot.IdRangeSize.Should().Be(100);
    }

    [Fact]
    public async Task Editor_settings_and_repository_plumbing_are_left_out_without_a_word()
    {
        var snapshot = await ReadAsync(Tarball([
            Entry.Text("src/a.al", "codeunit 1 A { }"),
            Entry.Text(".vscode/tasks.json", "{}"),
            Entry.Text("src/.vscode/settings.json", "{}"),
            Entry.Text(".github/workflows/ci.yml", "on: push"),
            Entry.Text("src/.gitignore", "*.app"),
            Entry.Text(".alpackages/Microsoft_System.app", "x"),
            Entry.Text("src/Built.app", "x"),
        ]), path: null);

        AllPaths(snapshot.Folders).Should().BeEquivalentTo(["src/a.al"]);
        snapshot.Skipped.Should().BeEmpty("these are left out on purpose, not by accident worth reporting");
    }

    [Fact]
    public async Task Files_directly_in_the_extension_folder_are_listed_as_left_out()
    {
        var snapshot = await ReadAsync(SimpleExtension(), path: null);

        snapshot.Skipped.Should().ContainSingle(s => s.Path == "README.md");
    }

    [Fact]
    public async Task Binary_oversized_and_linked_files_are_left_out_and_listed()
    {
        var snapshot = await ReadAsync(Tarball([
            Entry.Text("src/a.al", "codeunit 1 A { }"),
            Entry.Bytes("src/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]),
            Entry.Bytes("src/latin1.txt", [0x48, 0xE6, 0x6C]),
            Entry.Text("src/huge.al", new string('x', ModuleSourceLimits.MaxFileBytes + 1)),
            Entry.Link("src/secrets.al", "/etc/passwd"),
        ]), path: null);

        AllPaths(snapshot.Folders).Should().BeEquivalentTo(["src/a.al"]);
        snapshot.Skipped.Select(s => s.Path).Should().BeEquivalentTo(
            ["src/logo.png", "src/latin1.txt", "src/huge.al", "src/secrets.al"]);
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_archive_is_never_copied()
    {
        var snapshot = await ReadAsync(Tarball([
            Entry.Text("top/src/a.al", "codeunit 1 A { }"),
            Entry.Text("top/src/../../escape.al", "codeunit 2 B { }"),
        ], top: "ignored", rawNames: true), path: null);

        AllPaths(snapshot.Folders).Should().BeEquivalentTo(["src/a.al"]);
        snapshot.Skipped.Should().ContainSingle(s => s.Path.Contains(".."));
    }

    [Fact]
    public async Task A_folder_narrows_the_copy_and_its_own_app_json_is_the_one_read()
    {
        var snapshot = await ReadAsync(Tarball([
            Entry.Text("app.json", """{ "idRanges": [ { "from": 1, "to": 5 } ] }"""),
            Entry.Text("Other/src/x.al", "codeunit 1 X { }"),
            Entry.Text("DocumentCapture/app.json", """{ "idRanges": [ { "from": 91000, "to": 91199 } ] }"""),
            Entry.Text("DocumentCapture/src/a.al", "codeunit 91000 A { }"),
        ]), path: "DocumentCapture");

        AllPaths(snapshot.Folders).Should().BeEquivalentTo(["src/a.al"]);
        snapshot.IdRangeSize.Should().Be(200);
    }

    [Fact]
    public async Task A_folder_that_is_not_in_the_repository_is_refused_under_the_folder_field()
    {
        var act = () => ReadAsync(SimpleExtension(), path: "Missing");

        (await act.Should().ThrowAsync<ModuleSourceException>()).Which.IsAboutFolder.Should().BeTrue();
    }

    [Fact]
    public async Task Too_many_files_refuses_the_copy()
    {
        var entries = Enumerable.Range(0, ModuleSourceLimits.MaxFiles + 1)
            .Select(i => Entry.Text($"src/f{i}.al", "x"));

        var act = () => ReadAsync(Tarball(entries), path: null);

        (await act.Should().ThrowAsync<ModuleSourceException>()).WithMessage($"*more than {ModuleSourceLimits.MaxFiles} files*");
    }

    [Fact]
    public async Task Too_many_bytes_in_total_refuses_the_copy()
    {
        var chunk = new string('x', 400 * 1024);
        var entries = Enumerable.Range(0, 14).Select(i => Entry.Text($"src/f{i}.al", chunk));

        var act = () => ReadAsync(Tarball(entries), path: null);

        (await act.Should().ThrowAsync<ModuleSourceException>()).WithMessage("*more than 5 MB*");
    }

    [Fact]
    public async Task Two_files_differing_only_in_case_keep_the_first_and_list_the_second()
    {
        var snapshot = await ReadAsync(Tarball([
            Entry.Text("src/A.al", "codeunit 1 A { }"),
            Entry.Text("src/a.al", "codeunit 2 B { }"),
        ]), path: null);

        AllPaths(snapshot.Folders).Should().BeEquivalentTo(["src/A.al"]);
        snapshot.Skipped.Should().ContainSingle(s => s.Path == "src/a.al");
    }

    [Fact]
    public async Task A_long_list_of_left_out_files_is_cut_short_with_a_count()
    {
        var images = Enumerable.Range(0, ModuleSourceLimits.MaxListedSkipped + 50)
            .Select(i => Entry.Bytes($"src/Images/{i}.png", [0x89, 0x50, 0x00]));

        var snapshot = await ReadAsync(Tarball([Entry.Text("src/a.al", "codeunit 1 A { }"), .. images]), path: null);

        snapshot.Skipped.Should().HaveCount(ModuleSourceLimits.MaxListedSkipped + 1);
        snapshot.Skipped[^1].Path.Should().Be("and 50 more");
    }

    [Fact]
    public async Task A_byte_order_mark_is_dropped()
    {
        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("codeunit 1 A { }")).ToArray();
        var snapshot = await ReadAsync(Tarball([Entry.Bytes("src/a.al", bom)]), path: null);

        snapshot.Folders.Single().Files.Single().Content.Should().Be("codeunit 1 A { }");
    }

    [Fact]
    public async Task A_malformed_app_json_contributes_nothing_and_does_not_fail_the_copy()
    {
        var snapshot = await ReadAsync(Tarball([
            Entry.Text("app.json", "{ not json"),
            Entry.Text("src/a.al", "codeunit 1 A { }"),
        ]), path: null);

        snapshot.Dependencies.Should().BeEmpty();
        snapshot.IdRangeSize.Should().BeNull();
        snapshot.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task Garbage_instead_of_a_tarball_is_refused_with_a_message()
    {
        var act = () => ReadAsync(Encoding.UTF8.GetBytes("<html>not a tarball</html>"), path: null);

        await act.Should().ThrowAsync<ModuleSourceException>();
    }

    private static Task<ModuleSourceSnapshot> ReadAsync(byte[] tarball, string? path) =>
        ModuleSourceArchiveReader.ReadAsync(new MemoryStream(tarball), path, CancellationToken.None);

    private static IEnumerable<string> AllPaths(IReadOnlyList<FolderAuthoring> folders, string prefix = "")
    {
        foreach (var folder in folders)
        {
            var here = prefix.Length == 0 ? folder.Path : $"{prefix}/{folder.Path}";
            foreach (var file in folder.Files) yield return $"{here}/{file.Path}";
            foreach (var path in AllPaths(folder.Folders, here)) yield return path;
        }
    }
}
