using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using ALDevToolbox.Tests.GitHub;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Builds the gzipped tarballs GitHub serves for a commit, and wires a
/// <see cref="FakeGitHubApi"/> to serve one, for the repository-module tests (#1415).
/// </summary>
internal static class ModuleSourceTestKit
{
    public const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>One archive entry: a file with text or bytes, a directory, or a link.</summary>
    public sealed record Entry(string Path, byte[]? Content = null, TarEntryType Type = TarEntryType.RegularFile, string? LinkTarget = null)
    {
        public static Entry Text(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));
        public static Entry Bytes(string path, byte[] bytes) => new(path, bytes);
        public static Entry Directory(string path) => new(path, Type: TarEntryType.Directory);
        public static Entry Link(string path, string target) => new(path, Type: TarEntryType.SymbolicLink, LinkTarget: target);
    }

    /// <summary>
    /// A tarball in GitHub's shape: a global header naming the commit, then every
    /// entry under one top folder named after the repository and commit.
    /// <paramref name="rawNames"/> skips the top folder, for the path-escape tests.
    /// </summary>
    public static byte[] Tarball(IEnumerable<Entry> entries, string top = "cronus-dk-doc-aaaaaaa", bool rawNames = false)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = ShaA }));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/"));
            foreach (var entry in entries)
            {
                var name = rawNames ? entry.Path : $"{top}/{entry.Path}";
                var tar = new PaxTarEntry(entry.Type, name);
                if (entry.LinkTarget is not null) tar.LinkName = entry.LinkTarget;
                if (entry.Content is not null) tar.DataStream = new MemoryStream(entry.Content);
                writer.WriteEntry(tar);
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// Serves <paramref name="tarball"/> for <paramref name="fullName"/> at
    /// <paramref name="sha"/>, through the 302 to codeload GitHub answers with.
    /// </summary>
    public static FakeGitHubApi ServeTarball(this FakeGitHubApi api, string fullName, string sha, byte[] tarball) =>
        api.OnRedirect(HttpMethod.Get, $"/repos/{fullName}/tarball/{sha}", $"https://codeload.github.com/{fullName}/legacy.tar.gz/{sha}")
           .OnBytes(HttpMethod.Get, $"/{fullName}/legacy.tar.gz/{sha}", tarball);

    /// <summary>Answers a commit lookup of <paramref name="gitRef"/> with <paramref name="sha"/>.</summary>
    public static FakeGitHubApi ResolveRef(this FakeGitHubApi api, string fullName, string gitRef, string sha) =>
        api.On(HttpMethod.Get, $"/repos/{fullName}/commits/{gitRef}", HttpStatusCode.OK, sha);

    /// <summary>The repository read, public or private.</summary>
    public static FakeGitHubApi Repository(this FakeGitHubApi api, string fullName, bool isPrivate) =>
        api.On(HttpMethod.Get, $"/repos/{fullName}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(fullName, isPrivate: isPrivate));

    /// <summary>A small, ordinary AL extension.</summary>
    public static byte[] SimpleExtension(string codeunit = "codeunit 91000 \"{{affix}} Doc Adapter\" { }") => Tarball([
        Entry.Text("app.json", """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Document Capture",
              "idRanges": [ { "from": 91000, "to": 91099 } ],
              "dependencies": [
                { "id": "4b915d7e-c02a-435f-85ab-649086c1e002", "name": "Continia Core", "publisher": "Continia Software", "version": "1.0.0.0" }
              ]
            }
            """),
        Entry.Text("src/Codeunits/DocAdapter.Codeunit.al", codeunit),
        Entry.Text("README.md", "# Document Capture"),
        Entry.Text(".vscode/tasks.json", "{}"),
    ]);
}
