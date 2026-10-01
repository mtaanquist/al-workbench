using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The "compare with" picker on a release page. A Microsoft release only offers
/// other Microsoft releases: with hundreds of customer and pipeline-build
/// releases imported, they buried the few BC versions a first-party compare is
/// made against. Other kinds keep the full list.
/// </summary>
public sealed class CompareCandidatesTests
{
    private static readonly ReleaseListItem[] Releases =
    [
        Item(1, "first_party"),
        Item(2, "first_party"),
        Item(3, "third_party"),
        Item(4, "project"),
        Item(5, "cal"),
        Item(6, "first_party", status: "importing"),
        Item(7, "first_party", deleted: true),
    ];

    [Fact]
    public void A_first_party_release_is_offered_only_other_ready_first_party_releases()
    {
        OeReleaseDetail.CompareCandidates(Releases, 1, "first_party")
            .Select(r => r.Id).Should().Equal(2);
    }

    [Theory]
    [InlineData("third_party")]
    [InlineData("project")]
    [InlineData("cal")]
    public void Other_kinds_are_offered_every_other_ready_release(string kind)
    {
        var self = Releases.Single(r => r.Kind == kind).Id;

        OeReleaseDetail.CompareCandidates(Releases, self, kind)
            .Select(r => r.Id).Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 }.Where(id => id != self));
    }

    private static ReleaseListItem Item(int id, string kind, string status = "ready", bool deleted = false) =>
        new(id, $"Release {id}", kind, status, BcVersion: null, ParentReleaseId: null, ParentLabel: null,
            Publisher: null, ProjectName: null, ImportedAt: DateTime.UtcNow, SourceFileCount: 0,
            SourceContentLength: 0, DeletedAt: deleted ? DateTime.UtcNow : null);
}
