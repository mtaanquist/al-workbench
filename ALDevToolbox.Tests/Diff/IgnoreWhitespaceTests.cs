using System.Net.Http.Json;
using System.Text.Json;
using ALDevToolbox.Tests.Infrastructure;
using DiffPlex.DiffBuilder;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Diff;

/// <summary>
/// Pins what "ignore whitespace" means on the two compare screens (#577), and
/// the fact the issue turned out to have backwards: <b>both screens have always
/// ignored whitespace</b>. DiffPlex's <c>ignoreWhiteSpace</c> parameter defaults
/// to <c>true</c> and nothing here ever passed it, so a reindent has never been
/// reported as a change — the control added for #577 exists to turn that OFF.
///
/// That default was invisible and it made the file diff contradict itself. The
/// change rail lists a file as <c>modified</c> from its <c>content_hash</c>, so
/// any byte counts; the diff beside it said the two files were identical. Both
/// statements were true and nothing on the page reconciled them.
///
/// These tests are the reason a DiffPlex upgrade that flips the default cannot
/// land quietly: it would change every count on both screens.
/// </summary>
public sealed class IgnoreWhitespaceTests
{
    /// <summary>
    /// The exact shape of what gets ignored, measured rather than assumed —
    /// DiffPlex trims each line, so this covers far more than indentation.
    /// A blank line added is NOT whitespace to it: the line count changed.
    /// </summary>
    [Theory]
    [InlineData("begin\n    Foo();\nend;", "begin\n        Foo();\nend;", true, "leading indent")]
    [InlineData("Foo();", "Foo();   ", true, "trailing spaces")]
    [InlineData("a := 1;", "a  :=  1;", true, "spacing inside the line")]
    [InlineData("\tFoo();", "    Foo();", true, "a tab against four spaces")]
    [InlineData("a\r\nb", "a\nb", true, "the line ending")]
    [InlineData("a\nb", "a\n\nb", false, "a blank line is a line, not whitespace")]
    [InlineData("a := 1;", "a := 2;", false, "a real change")]
    public void Ignoring_whitespace_covers_more_than_indentation(
        string left, string right, bool expectedIdentical, string what)
    {
        var ignoring = SideBySideDiffSerializer_Summarize(left, right, ignoreWhitespace: true);
        ignoring.Identical.Should().Be(expectedIdentical, because: what);
    }

    /// <summary>
    /// With the toggle off, every one of those is a change again — which is
    /// the whole point of the control, and the only way to see what a rail row
    /// marked "modified" actually contains when the diff says nothing.
    /// </summary>
    [Theory]
    [InlineData("begin\n    Foo();\nend;", "begin\n        Foo();\nend;")]
    [InlineData("a := 1;", "a  :=  1;")]
    public void Turning_it_off_makes_a_whitespace_change_visible(string left, string right)
    {
        SideBySideDiffSerializer_Summarize(left, right, ignoreWhitespace: false)
            .Identical.Should().BeFalse();
    }

    private static ALDevToolbox.Services.Diff.SideBySideDiffSerializer.DiffSummary
        SideBySideDiffSerializer_Summarize(string left, string right, bool ignoreWhitespace) =>
        ALDevToolbox.Services.Diff.SideBySideDiffSerializer.Summarize(
            SideBySideDiffBuilder.Diff(left, right, ignoreWhitespace));
}

/// <summary>
/// The endpoint half: the Diff tool sends the flag with every re-diff, and a
/// request that omits it has to keep the behaviour it has always had.
/// </summary>
public sealed class IgnoreWhitespaceEndpointTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public IgnoreWhitespaceEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    private const string Indented = "begin\n    Foo();\nend;";
    private const string Reindented = "begin\n        Foo();\nend;";

    [Fact]
    public async Task An_omitted_flag_keeps_the_behaviour_the_endpoint_always_had()
    {
        (await IdenticalAsync(new { left = Indented, right = Reindented }))
            .Should().BeTrue(because: "a client written before the toggle must not start seeing reindents");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task The_flag_decides_whether_a_reindent_is_a_change(bool ignoreWhitespace, bool identical)
    {
        (await IdenticalAsync(new { left = Indented, right = Reindented, ignoreWhitespace }))
            .Should().Be(identical);
    }

    private async Task<bool> IdenticalAsync(object body)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff", body);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("summary").GetProperty("identical").GetBoolean();
    }
}
