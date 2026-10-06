using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// <c>GET /palette/search</c> at the HTTP layer: the 401 an anonymous fetch
/// gets instead of a redirect, the short-query short-circuit, and the JSON
/// shape the palette script in the browser is written against.
///
/// <para>The property names are asserted as literal strings on purpose. They
/// are a contract with a file that is not compiled against this one, so a
/// rename here would not break a build - it would quietly empty the palette.</para>
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class PaletteEndpointTests : IDisposable
{
    private const string UserEmail = "palette@cronus.test";
    private const string UserPassword = "correct-horse-battery-staple";

    private readonly TestDb _db = new();
    private readonly FakePaletteSource _solutions = new(
        "solutions", "Solutions", PaletteGroupOrder.Solutions,
        new PaletteCandidate("solution", "Contoso Coffee", "CONCOF - Microsoft cloud", "/solutions/12", "CONCOF"),
        new PaletteCandidate("solution", "Contoso Confectionery", null, "/solutions/13", "CONFEC"));
    private readonly FakePaletteSource _recipes = new(
        "recipes", "Recipes", PaletteGroupOrder.Recipes,
        new PaletteCandidate("recipe", "Contoso posting routine", null, "/cookbook/7"));
    private readonly EndpointFactory _factory;

    public PaletteEndpointTests()
    {
        _factory = new EndpointFactory(_db, services =>
        {
            services.AddScoped<IPaletteSource>(_ => _solutions);
            services.AddScoped<IPaletteSource>(_ => _recipes);
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    // ── Auth ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_anonymous_search_is_refused_with_401_not_a_redirect_to_login()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/palette/search?q=contoso");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the palette fetches this; a 302 would arrive at the script as a page of sign-in HTML");
        _solutions.SearchCallCount.Should().Be(0);
    }

    // ── The short-circuit ───────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("c")]
    public async Task A_query_under_two_characters_answers_empty_without_asking_a_source(string q)
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync($"/palette/search?q={q}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("groups").GetArrayLength().Should().Be(0);
        json.GetProperty("top").ValueKind.Should().Be(JsonValueKind.Null);
        _solutions.SearchCallCount.Should().Be(0, "no database work for a keystroke that cannot match");
    }

    [Fact]
    public async Task A_query_over_a_hundred_characters_answers_empty_without_asking_a_source()
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync(
            "/palette/search?q=" + new string('c', PaletteQuery.MaxLength + 1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("groups").GetArrayLength().Should().Be(0);
        _solutions.SearchCallCount.Should().Be(0);
    }

    // ── The wire contract ───────────────────────────────────────────────

    [Fact]
    public async Task Results_come_back_grouped_in_the_shape_the_script_expects()
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync("/palette/search?q=contoso");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);

        var groups = json.GetProperty("groups");
        groups.GetArrayLength().Should().Be(2);

        var solutions = groups[0];
        solutions.GetProperty("id").GetString().Should().Be("solutions");
        solutions.GetProperty("label").GetString().Should().Be("Solutions");

        var first = solutions.GetProperty("items")[0];
        first.GetProperty("kind").GetString().Should().Be("solution");
        first.GetProperty("title").GetString().Should().Be("Contoso Coffee");
        first.GetProperty("subtitle").GetString().Should().Be("CONCOF - Microsoft cloud");
        first.GetProperty("href").GetString().Should().Be("/solutions/12");

        groups[1].GetProperty("id").GetString().Should().Be("recipes",
            "groups arrive in display order, solutions before recipes");
        json.GetProperty("top").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_exact_short_name_arrives_as_the_top_hit_and_not_inside_its_group()
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync("/palette/search?q=concof");

        var json = await ReadJsonAsync(response);
        var top = json.GetProperty("top");
        top.ValueKind.Should().Be(JsonValueKind.Object);
        top.GetProperty("href").GetString().Should().Be("/solutions/12");

        var hrefs = json.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("href").GetString())
            .ToList();
        hrefs.Should().NotContain("/solutions/12", "the top hit is shown once, above the groups");
    }

    [Fact]
    public async Task A_source_whose_gate_refuses_the_caller_is_never_asked()
    {
        _recipes.Available = false;
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync("/palette/search?q=contoso");

        var json = await ReadJsonAsync(response);
        json.GetProperty("groups").EnumerateArray()
            .Select(g => g.GetProperty("id").GetString())
            .Should().Equal("solutions");
        _recipes.SearchCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Results_are_never_cached()
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync("/palette/search?q=contoso");

        response.Headers.CacheControl!.NoStore.Should().BeTrue(
            "a cached palette answer at a shared proxy would be another tenant's");
    }

    // ── GET /palette/context (#887) ─────────────────────────────────────

    [Fact]
    public async Task An_anonymous_context_request_is_refused_with_401_not_a_redirect_to_login()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/palette/context?at=solution:1&recent=/solutions/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_context_answer_has_the_shape_the_script_expects_and_is_never_cached()
    {
        using var client = await SignedInClientAsync();
        var projectId = await SeedSolutionAsync("CRONUS Coffee A/S");

        using var response = await client.GetAsync(
            $"/palette/context?at=solution:{projectId}"
            + $"&recent=/solutions/{projectId}&recent=https://evil.example/&recent=/nowhere&recent=/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue(
            "the answer names customers and is per-user; a shared proxy must never hold one");

        var json = await ReadJsonAsync(response);
        var context = json.GetProperty("context");
        context.GetProperty("label").GetString().Should().Be("CRONUS Coffee A/S");
        var first = context.GetProperty("items")[0];
        first.GetProperty("kind").GetString().Should().Be("tab");
        first.GetProperty("title").GetString().Should().Be("Customer");
        first.GetProperty("href").GetString().Should().Be($"/solutions/{projectId}");

        json.GetProperty("recents").EnumerateArray()
            .Select(r => r.GetProperty("href").GetString())
            .Should().Equal([$"/solutions/{projectId}", "/"],
                "an href the server does not recognise is dropped, never echoed back");
    }

    [Fact]
    public async Task A_context_the_caller_cannot_open_answers_null_rather_than_an_error()
    {
        using var client = await SignedInClientAsync();

        using var response = await client.GetAsync("/palette/context?at=solution:987654");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("context").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("recents").GetArrayLength().Should().Be(0);
    }

    private async Task<int> SeedSolutionAsync(string name)
    {
        await using var seed = _db.NewContext();
        var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            Visibility = ALDevToolbox.Domain.Entities.ObjectExplorer.ProjectVisibility.Public,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        seed.OeProjects.Add(project);
        await seed.SaveChangesAsync();
        return project.Id;
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = UserEmail,
                DisplayName = "Pat Palette",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                    .HashPassword(UserPassword),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        using var form = await client.GetAsync("/login");
        var html = await form.Content.ReadAsStringAsync();
        var token = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        token.Success.Should().BeTrue("the sign-in form must carry an antiforgery token");

        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", UserEmail),
            new KeyValuePair<string, string>("Password", UserPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", token.Groups[1].Value),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=",
            "the seeded user must actually sign in, or every case below would only prove the login guard works");

        return client;
    }
}
