using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Procedures and Source text tabs say when their list was cut, and only
/// then (OEX2-5, OEX2-6). The Source text search caps files, not lines, so its
/// notice counts files; the Procedures tab says it was capped as the Objects
/// tab does.
/// </summary>
public sealed class OeSearchResultsNoticeTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public OeSearchResultsNoticeTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<ObjectExplorerLinks>();
    }

    public void Dispose() => _ctx.Dispose();

    private static List<ReleaseContentMatch> Lines(int files, int perFile) =>
        Enumerable.Range(0, files)
            .SelectMany(f => Enumerable.Range(1, perFile)
                .Select(l => new ReleaseContentMatch(f, $"src/File{f:D3}.al", 1, "Base Application", l, "Customer.Insert();")))
            .ToList();

    [Fact]
    public void A_complete_source_text_list_of_many_lines_is_not_called_cut()
    {
        // 70 files, three lines each: 210 lines, nothing left out.
        var cut = _ctx.Render<OeContentResults>(p => p
            .Add(c => c.Results, Lines(70, 3))
            .Add(c => c.Search, "Insert"));

        cut.Markup.Should().Contain("Showing 210 lines.")
            .And.NotContain("first");
    }

    [Fact]
    public void A_source_text_list_cut_at_the_file_cap_says_so_in_files()
    {
        var cut = _ctx.Render<OeContentResults>(p => p
            .Add(c => c.Results, Lines(200, 3))
            .Add(c => c.Search, "Insert")
            .Add(c => c.CappedAtFiles, 200));

        cut.Markup.Should().Contain("Showing 600 lines from the first 200 files.");
    }

    private static List<ReleaseProcedureMatch> Procedures(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ReleaseProcedureMatch(i, 1, "codeunit", "Sales-Post", "Base Application",
                "procedure", $"Post{i}", "()", null, 1, 10))
            .ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_procedures_tab_says_when_it_was_capped(bool capped)
    {
        var cut = _ctx.Render<OeProcedureResults>(p => p
            .Add(c => c.Results, Procedures(500))
            .Add(c => c.Capped, capped));

        cut.Markup.Contains("capped at 500").Should().Be(capped);
        cut.Markup.Should().Contain("Showing 500 procedures");
    }
}
