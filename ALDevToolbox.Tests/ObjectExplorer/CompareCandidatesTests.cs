using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The "compare with" picker on a release page only offers releases of the same
/// kind. With hundreds of customer and pipeline-build releases imported, the
/// full list buried the few releases anyone compares against, and Microsoft
/// code and customisations live in separate apps, so a cross-kind compare is
/// not one anyone makes.
/// </summary>
public sealed class CompareCandidatesTests
{
    private static readonly ReleaseListItem[] Releases =
    [
        Item(1, "first_party"),
        Item(2, "first_party"),
        Item(3, "third_party"),
        Item(4, "third_party"),
        Item(5, "project"),
        Item(6, "project"),
        Item(7, "cal"),
        Item(8, "cal"),
        Item(9, "first_party", status: "importing"),
        Item(10, "first_party", deleted: true),
    ];

    [Theory]
    [InlineData(1, "first_party", 2)]
    [InlineData(3, "third_party", 4)]
    [InlineData(5, "project", 6)]
    [InlineData(7, "cal", 8)]
    public void A_release_is_offered_only_other_ready_releases_of_its_own_kind(int self, string kind, int expected)
    {
        OeReleaseDetail.CompareCandidates(Releases, self, kind)
            .Select(r => r.Id).Should().Equal(expected);
    }

    private static ReleaseListItem Item(int id, string kind, string status = "ready", bool deleted = false) =>
        new(id, $"Release {id}", kind, status, BcVersion: null, ParentReleaseId: null, ParentLabel: null,
            Publisher: null, ProjectName: null, ImportedAt: DateTime.UtcNow, SourceFileCount: 0,
            SourceContentLength: 0, DeletedAt: deleted ? DateTime.UtcNow : null);
}
