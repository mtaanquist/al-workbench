using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Pins the anonymous <c>POST /api/compare/diff</c> the editable Diff tool
/// hits from source-viewer.js: it must serve without auth, return real JSON
/// arrays (not double-encoded strings) so the client can apply them directly,
/// report the identical case, and refuse an over-cap paste with a friendly
/// error rather than a stack trace.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class CompareEndpointTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public CompareEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Diff_of_two_texts_serves_anonymously_with_arrays_and_summary()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "a\nb\nc", right = "a\nx\nc\nd" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Raw JSON arrays, not re-encoded strings.
        root.GetProperty("right").GetProperty("diff").ValueKind.Should().Be(JsonValueKind.Array);
        var summary = root.GetProperty("summary");
        summary.GetProperty("modified").GetInt32().Should().Be(1);
        summary.GetProperty("added").GetInt32().Should().Be(1);
        summary.GetProperty("identical").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// The inline layout's document rides every response (#581). It has to,
    /// on this tool: the two panes ARE the input, so the unified text is a
    /// function of what the reader typed a keystroke ago — unlike the Object
    /// Explorer's file diff, where it can be baked into the page once.
    ///
    /// Shipped as one block beside the two sides rather than fetched on the
    /// switch, so the layout tabs never wait on a round-trip and the two
    /// renderings can never be of different text.
    /// </summary>
    [Fact]
    public async Task The_response_carries_the_inline_document_for_the_same_texts()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "a\nOLD\nc", right = "a\nNEW\nc" });

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var inline = doc.RootElement.GetProperty("inline");

        // Old above new — the one case where an aligned row becomes two.
        inline.GetProperty("content").GetString()!.Split('\n')
            .Should().Equal("a", "OLD", "NEW", "c");

        foreach (var name in new[] { "rows", "gutters", "collapse", "wordDiff" })
        {
            inline.GetProperty(name).ValueKind.Should().Be(JsonValueKind.Array,
                because: $"the client applies {name} directly, so it must not arrive double-encoded");
        }
    }

    /// <summary>
    /// The document carries the unchanged runs even when they are far outside
    /// the context window, because the bands hide them client-side and put them
    /// back on a click. Dropping them server-side is what left the Object
    /// Explorer's inline bands inert before #585.
    /// </summary>
    [Fact]
    public async Task The_inline_document_keeps_the_lines_its_bands_hide()
    {
        var left = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line{i}"));
        var right = left.Replace("line15", "CHANGED");

        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left, right });

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var inline = doc.RootElement.GetProperty("inline");

        var content = inline.GetProperty("content").GetString()!;
        content.Should().Contain("line1\n", "a hidden line is still in the document");
        content.Should().Contain("line30");

        var bands = inline.GetProperty("collapse");
        bands.GetArrayLength().Should().Be(2, "one run hidden above the change and one below");
        foreach (var band in bands.EnumerateArray())
        {
            band.TryGetProperty("from", out var from).Should().BeTrue(
                because: "a band with nothing behind it is not a control, and both of these hide a run");
            from.GetInt32().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Identical_texts_report_identical()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "same\ntext", right = "same\ntext" });

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = doc.RootElement.GetProperty("summary");
        summary.GetProperty("identical").GetBoolean().Should().BeTrue();
        summary.GetProperty("modified").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// The per-side cap is this endpoint's own (#673), not Piper's 50 MB one:
    /// the route is anonymous and does a full diff plus six serialisations and
    /// a unified rebuild per request, so it is bounded by what a person pastes
    /// rather than by what DiffPlex survives. Pinned here so nobody quietly points it back at
    /// <see cref="PiperTransform.MaxInputLength"/>.
    /// </summary>
    [Fact]
    public void The_per_side_cap_is_far_below_Pipers()
    {
        CompareEndpoints.MaxInputLength.Should().BeLessThan(PiperTransform.MaxInputLength / 10);
    }

    [Fact]
    public async Task Oversize_input_returns_a_friendly_error()
    {
        using var client = _factory.CreateClient();
        var huge = new string('a', CompareEndpoints.MaxInputLength + 1);
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = huge, right = "b" });

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().Contain("too large");
        // The number the reader is asked to get under is the endpoint's, and
        // the message has to name it or "trim it" is not actionable.
        error.GetString().Should().Contain(CompareEndpoints.MaxInputLength.ToString("N0"));
    }

    /// <summary>
    /// The character cap alone does not bound the diff: its cost grows with
    /// the square of the line count, so a text of many short lines is refused
    /// before any diffing starts. Counted on either side, and with every line
    /// ending DiffPlex splits on.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Too_many_lines_on_either_side_is_refused(string newline)
    {
        var many = string.Join(newline, Enumerable.Range(0, CompareEndpoints.MaxLines + 1).Select(i => $"l{i}"));

        using var client = _factory.CreateClient();
        foreach (var body in new[] { new { left = many, right = "b" }, new { left = "a", right = many } })
        {
            using var response = await client.PostAsJsonAsync("/api/compare/diff", body);

            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = doc.RootElement.GetProperty("error").GetString();
            error.Should().Contain("too many lines");
            error.Should().Contain(CompareEndpoints.MaxLines.ToString("N0"));
        }
    }

    [Fact]
    public async Task Exactly_the_line_cap_is_still_compared()
    {
        var atCap = string.Join("\r\n", Enumerable.Repeat("same", CompareEndpoints.MaxLines));

        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = atCap, right = atCap + "x" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A changed line is word-diffed against its counterpart, at a cost that
    /// grows with the square of its length, so one very long line is refused
    /// even when the text as a whole is well under the character cap.
    /// </summary>
    [Fact]
    public async Task A_line_over_the_length_cap_is_refused()
    {
        var longLine = "short\n" + string.Join(' ', Enumerable.Repeat("w", CompareEndpoints.MaxLineLength / 2 + 1)) + "\nshort";
        longLine.Length.Should().BeLessThan(CompareEndpoints.MaxInputLength);

        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "a", right = longLine });

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error").GetString();
        error.Should().Contain("line that is too long");
        error.Should().Contain(CompareEndpoints.MaxLineLength.ToString("N0"));
    }

    /// <summary>
    /// A diff cannot be stopped once it starts, so the number running at once
    /// is capped app-wide whoever is asking. With every slot taken, a request
    /// waits briefly and is then told the server is busy rather than queuing
    /// behind work it cannot see.
    /// </summary>
    [Fact]
    public async Task With_every_diff_slot_taken_a_request_is_answered_busy()
    {
        using var client = _factory.CreateClient();
        var held = 0;
        try
        {
            for (; held < CompareEndpoints.MaxConcurrentDiffs; held++)
            {
                (await CompareEndpoints.DiffSlots.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            }

            using var response = await client.PostAsJsonAsync("/api/compare/diff",
                new { left = "a", right = "b" });

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("error").GetString().Should().Contain("busy");
        }
        finally
        {
            if (held > 0) CompareEndpoints.DiffSlots.Release(held);
        }

        using var after = await client.PostAsJsonAsync("/api/compare/diff",
            new { left = "a", right = "b" });
        after.StatusCode.Should().Be(HttpStatusCode.OK, "the slots are released once the work is done");
    }

    /// <summary>
    /// The route is anonymous by design, so the per-IP token bucket is the only
    /// thing keeping a script from spending the whole container's CPU on
    /// diffs (#673). A burst well past the bucket must start getting 429s —
    /// and the burst size here is far more than the Diff tool's 300 ms debounce
    /// can produce, so an actual reader never sees one.
    /// </summary>
    [Fact]
    public async Task A_sustained_burst_is_throttled_with_429()
    {
        using var client = _factory.CreateClient();
        var sawTooManyRequests = false;

        for (var i = 0; i < 120 && !sawTooManyRequests; i++)
        {
            using var response = await client.PostAsJsonAsync("/api/compare/diff",
                new { left = "a", right = "b" });
            sawTooManyRequests = response.StatusCode == HttpStatusCode.TooManyRequests;
        }

        sawTooManyRequests.Should().BeTrue(
            because: "the anonymous diff route is rate limited per caller");
    }
}
