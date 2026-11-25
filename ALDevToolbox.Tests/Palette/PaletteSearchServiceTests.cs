using System.Security.Claims;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// How the palette's one service fans a query out, merges and caps it: the
/// group order, the caps, the lifted top hit, the gate, and the short-circuit
/// that keeps a one-character query away from the database. See
/// <c>.design/command-palette.md</c>.
/// </summary>
public sealed class PaletteSearchServiceTests
{
    private static readonly ClaimsPrincipal Caller = new(new ClaimsIdentity("test"));

    private static PaletteSearchService Service(params IPaletteSource[] sources) =>
        new(sources, NullLogger<PaletteSearchService>.Instance);

    private static PaletteCandidate Solution(string title, string? shortName = null, string? href = null) =>
        new("solution", title, shortName, href ?? "/solutions/" + title.GetHashCode(), shortName);

    // ── Nothing registered, nothing to search ───────────────────────────

    [Fact]
    public async Task With_no_sources_registered_the_search_still_answers()
    {
        var result = await Service().SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Groups.Should().BeEmpty();
        result.Top.Should().BeNull();
    }

    // ── The short-circuit ───────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("c")]
    public async Task A_query_that_is_too_short_never_reaches_a_source(string q)
    {
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));

        var result = await Service(source).SearchAsync(Caller, q, CancellationToken.None);

        result.Groups.Should().BeEmpty();
        source.SearchCallCount.Should().Be(0, "a query this short must not touch the database");
    }

    [Fact]
    public async Task A_query_that_is_too_long_never_reaches_a_source()
    {
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));

        var result = await Service(source).SearchAsync(
            Caller, new string('c', PaletteQuery.MaxLength + 1), CancellationToken.None);

        result.Groups.Should().BeEmpty();
        source.SearchCallCount.Should().Be(0);
    }

    // ── The gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_whose_gate_refuses_the_caller_is_never_asked()
    {
        var allowed = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));
        var refused = new FakePaletteSource("releases", "Releases", PaletteGroupOrder.Releases,
            new PaletteCandidate("release", "Contoso 1.0", null, "/releases/1")) { Available = false };

        var result = await Service(allowed, refused).SearchAsync(Caller, "contoso", CancellationToken.None);

        refused.SearchCallCount.Should().Be(0,
            "a caller who fails the gate must not have the source's query run at all");
        result.Groups.Should().ContainSingle().Which.Id.Should().Be("solutions");
    }

    [Fact]
    public async Task The_gate_is_handed_the_calling_principal()
    {
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions);

        await Service(source).SearchAsync(Caller, "contoso", CancellationToken.None);

        source.LastGatedPrincipal.Should().BeSameAs(Caller);
    }

    // ── Grouping, order and caps ────────────────────────────────────────

    [Fact]
    public async Task Groups_come_back_in_the_sources_declared_order_not_their_registration_order()
    {
        var releases = new FakePaletteSource("releases", "Releases", PaletteGroupOrder.Releases,
            new PaletteCandidate("release", "Contoso 1.0", null, "/releases/1"));
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));

        // Registered releases-first on purpose.
        var result = await Service(releases, solutions).SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Groups.Select(g => g.Id).Should().Equal("solutions", "releases");
        result.Groups[0].Label.Should().Be("Solutions");
    }

    [Fact]
    public async Task A_group_with_nothing_matching_is_omitted()
    {
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));
        var recipes = new FakePaletteSource("recipes", "Recipes", PaletteGroupOrder.Recipes,
            new PaletteCandidate("recipe", "Something else entirely", null, "/cookbook/1"));

        var result = await Service(solutions, recipes).SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Groups.Select(g => g.Id).Should().Equal(
            new[] { "solutions" }, "an empty heading is noise; the script renders what it is given");
    }

    [Fact]
    public async Task One_matching_group_gets_eight_rows()
    {
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Enumerable.Range(1, 20).Select(i => Solution($"Contoso {i:00}")).ToArray());

        var result = await Service(solutions).SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Groups.Should().ContainSingle()
            .Which.Items.Should().HaveCount(PaletteSearchService.MaxWhenOnlyOneGroupMatches);
    }

    [Fact]
    public async Task Two_matching_groups_get_five_rows_each()
    {
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Enumerable.Range(1, 20).Select(i => Solution($"Contoso {i:00}")).ToArray());
        var recipes = new FakePaletteSource("recipes", "Recipes", PaletteGroupOrder.Recipes,
            Enumerable.Range(1, 20)
                .Select(i => new PaletteCandidate("recipe", $"Contoso recipe {i:00}", null, $"/cookbook/{i}"))
                .ToArray());

        var result = await Service(solutions, recipes).SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Groups.Should().HaveCount(2);
        result.Groups.Should().AllSatisfy(g => g.Items.Should().HaveCount(PaletteSearchService.MaxPerGroup));
    }

    [Fact]
    public async Task Sources_are_asked_for_more_candidates_than_the_cap()
    {
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions);

        await Service(source).SearchAsync(Caller, "contoso", CancellationToken.None);

        source.LastLimit.Should().Be(PaletteSearchService.CandidatesPerSource)
            .And.BeGreaterThan(PaletteSearchService.MaxWhenOnlyOneGroupMatches,
                "ranking drops rows, so a source that returned exactly the cap could show fewer");
    }

    // ── The lifted top hit ──────────────────────────────────────────────

    [Fact]
    public async Task An_exact_short_name_becomes_the_top_hit_and_leaves_its_group()
    {
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee", "CONCOF", "/solutions/12"),
            // Matches the same query on its title, so the group is not empty and
            // the assertion below is about the lift rather than about luck.
            Solution("CONCOF Holdings", "CONHLD", "/solutions/13"));

        var result = await Service(solutions).SearchAsync(Caller, "concof", CancellationToken.None);

        result.Top.Should().NotBeNull();
        result.Top!.Title.Should().Be("Contoso Coffee");
        result.Top.Href.Should().Be("/solutions/12");
        result.Groups.Should().ContainSingle()
            .Which.Items.Should().ContainSingle().Which.Href.Should().Be("/solutions/13",
                "the top hit is shown above the groups, not twice");
    }

    [Fact]
    public async Task Lifting_the_only_row_out_of_a_group_removes_the_group()
    {
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee", "CONCOF", "/solutions/12"));

        var result = await Service(solutions).SearchAsync(Caller, "concof", CancellationToken.None);

        result.Top.Should().NotBeNull();
        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_an_exact_short_name_there_is_no_top_hit()
    {
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee", "CONCOF", "/solutions/12"));

        var result = await Service(solutions).SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Top.Should().BeNull();
        result.Groups.Should().ContainSingle().Which.Items.Should().ContainSingle();
    }

    // ── Cancellation and logging ────────────────────────────────────────

    [Fact]
    public async Task A_superseded_query_is_cancelled_rather_than_finished()
    {
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions)
        {
            WaitForCancellation = true,
        };
        using var cts = new CancellationTokenSource();

        var search = Service(source).SearchAsync(Caller, "contoso", cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => search).Should().ThrowAsync<OperationCanceledException>(
            "the browser aborts the in-flight request on the next keystroke, and that has to stop the SQL");
    }

    [Fact]
    public async Task A_source_that_is_already_cancelled_is_not_asked_at_all()
    {
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => Service(source).SearchAsync(Caller, "contoso", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        source.SearchCallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_query_text_is_never_logged_at_information()
    {
        // A palette query is a customer's name somebody typed, and this line
        // lands in the container log where operations can read it.
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        var source = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));
        var service = new PaletteSearchService(
            new[] { source }, factory.CreateLogger<PaletteSearchService>());

        await service.SearchAsync(Caller, "contoso", CancellationToken.None);

        capture.Messages.Where(m => !m.StartsWith("Debug:", StringComparison.Ordinal)
                && !m.StartsWith("Trace:", StringComparison.Ordinal))
            .Should().AllSatisfy(m => m.Should().NotContainEquivalentOf("contoso"));
        capture.Messages.Should().Contain(m => m.StartsWith("Debug:", StringComparison.Ordinal)
            && m.Contains("contoso", StringComparison.OrdinalIgnoreCase),
            "Debug is where the query text is allowed to be, for a maintainer chasing a bad result");
    }

    [Fact]
    public async Task Every_source_that_answered_is_timed_on_a_line_that_does_not_carry_the_query()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        var solutions = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"));
        var recipes = new FakePaletteSource("recipes", "Recipes", PaletteGroupOrder.Recipes);
        var service = new PaletteSearchService(
            new[] { solutions, recipes }, factory.CreateLogger<PaletteSearchService>(), TimeSpan.FromSeconds(30));

        await service.SearchAsync(Caller, "contoso", CancellationToken.None);

        var timings = capture.Messages.Should().ContainSingle(
            m => m.StartsWith("Debug:", StringComparison.Ordinal) && m.Contains("timings", StringComparison.Ordinal),
            "a palette that feels slow has to say which source is").Subject;
        timings.Should().Contain("solutions=").And.Contain("recipes=")
            .And.Contain("ms", "the number is what makes the line worth reading");
        timings.Should().NotContainEquivalentOf("contoso",
            "this is the line somebody reads while chasing a slow palette, over a shoulder");
    }

    // ── The per-source slice ────────────────────────────────────────────

    [Fact]
    public async Task A_source_that_blows_its_slice_is_dropped_and_the_others_still_answer()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(capture);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        // Ordered first, so the assertion is about the stall being shed rather
        // than about the other source having already answered.
        var stalled = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions,
            Solution("Contoso Coffee"))
        {
            WaitForCancellation = true,
        };
        var recipes = new FakePaletteSource("recipes", "Recipes", PaletteGroupOrder.Recipes,
            new PaletteCandidate("recipe", "Contoso posting routine", null, "/cookbook/1"));
        var service = new PaletteSearchService(
            new[] { stalled, recipes }, factory.CreateLogger<PaletteSearchService>());

        var result = await service.SearchAsync(Caller, "contoso", CancellationToken.None);

        result.Groups.Should().ContainSingle(
            "a source that cannot answer in its slice is dropped from this response, not waited on")
            .Which.Id.Should().Be("recipes");
        stalled.ObservedCancellation.Should().BeTrue(
            "the slice has to cancel the source's token, or the SQL behind it runs on regardless");
        capture.Messages.Should().ContainSingle(m => m.StartsWith("Warning:", StringComparison.Ordinal))
            .Which.Should().Contain("solutions")
            .And.NotContainEquivalentOf("contoso", "a Warning lands in the container log; the query is not for it");
    }

    [Fact]
    public async Task A_request_the_browser_aborted_is_still_a_cancellation_not_a_dropped_source()
    {
        // The two look identical from inside the loop - a cancelled token - and
        // the difference matters: a superseded keystroke must not be answered
        // with a partial result the script would render.
        var stalled = new FakePaletteSource("solutions", "Solutions", PaletteGroupOrder.Solutions)
        {
            WaitForCancellation = true,
        };
        var recipes = new FakePaletteSource("recipes", "Recipes", PaletteGroupOrder.Recipes,
            new PaletteCandidate("recipe", "Contoso posting routine", null, "/cookbook/1"));
        using var cts = new CancellationTokenSource();

        // A budget nothing reaches, so only the request's own token can fire: with the
        // default slice a stall before the cancel would drop the source first (#1439).
        var service = new PaletteSearchService(
            [stalled, recipes], NullLogger<PaletteSearchService>.Instance, sourceBudget: TimeSpan.FromMinutes(1));

        var search = service.SearchAsync(Caller, "contoso", cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => search).Should().ThrowAsync<OperationCanceledException>();
        recipes.SearchCallCount.Should().Be(0, "nothing is owed to a request that is gone");
    }
}
