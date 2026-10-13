using ALDevToolbox.Components.Pages.ObjectExplorer;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The release page's "Include inherited" toggle has to survive the URL round
/// trip, so Back and a shared link come back with the search the user ran. The
/// writer and the reader once disagreed on the default, and turning it on was
/// never kept (#1226).
/// </summary>
public sealed class ReleaseDetailInheritedToggleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_toggle_reads_back_what_was_written(bool includeInherited)
    {
        var part = OeReleaseDetail.InheritedQueryPart(includeInherited);
        var value = part?.Split('=', 2)[1];

        OeReleaseDetail.InheritedFromQuery(value).Should().Be(includeInherited);
    }

    [Fact]
    public void Turning_it_on_is_written_to_the_url()
    {
        OeReleaseDetail.InheritedQueryPart(true).Should().Be("inherited=1");
        OeReleaseDetail.InheritedQueryPart(false).Should().BeNull("off is the default, so it is left out");
    }
}
