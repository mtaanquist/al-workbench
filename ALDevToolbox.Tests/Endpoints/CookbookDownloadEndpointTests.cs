using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The recipe ZIP download through the real host (issue #1212). The server
/// refuses synchronous writes to the response body, which is what
/// <c>ZipArchive</c> does on its own; these tests unzip what actually came
/// back, so a body that only starts out as a ZIP fails them.
/// </summary>
public sealed class CookbookDownloadEndpointTests : IDisposable
{
    private const string AlexEmail = "alex@cronus.example";
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Download_returns_a_zip_holding_every_file_of_the_recipe()
    {
        await SeedUserAsync();
        var recipeId = await SeedRecipeAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        using var response = await client.GetAsync($"/api/cookbook/{recipeId}/download");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("setup-table-pattern.zip");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = archive.Entries.ToDictionary(e => e.FullName, ReadEntry);
        entries.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["src/Setup/CronusSetup.Table.al"] = "table 50100 \"CRONUS Setup\" { }",
            ["src/Setup/CronusSetup.Page.al"] = "page 50100 \"CRONUS Setup\" { }",
        });
    }

    [Fact]
    public async Task Same_origin_download_records_one_attribution_row_for_the_customer()
    {
        await SeedUserAsync();
        var recipeId = await SeedRecipeAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/cookbook/{recipeId}/download?customer=CRONUS%20A%2FS");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            archive.Entries.Should().HaveCount(2);
        }

        await using var ctx = _db.NewContext();
        var rows = await ctx.RecipeDownloads.AsNoTracking().Where(d => d.RecipeId == recipeId).ToListAsync();
        rows.Should().ContainSingle().Which.CustomerName.Should().Be("CRONUS A/S");
    }

    /// <summary>
    /// #1278 (T25): the attribution is a write a GET cannot protect with an
    /// antiforgery token, so it is gated on fetch metadata and fails closed.
    /// An address-bar navigation ("none") records like same-origin does; a
    /// cross-site navigation, a same-site one and a request with no header at
    /// all still get the ZIP but record nothing.
    /// </summary>
    [Theory]
    [InlineData("none", true)]
    [InlineData("cross-site", false)]
    [InlineData("same-site", false)]
    [InlineData(null, false)]
    public async Task Only_a_navigation_from_this_origin_records_the_attribution(string? fetchSite, bool records)
    {
        await SeedUserAsync();
        var recipeId = await SeedRecipeAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/cookbook/{recipeId}/download?customer=CRONUS%20A%2FS");
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the ZIP itself is served either way");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        await using var ctx = _db.NewContext();
        (await ctx.RecipeDownloads.AsNoTracking().CountAsync(d => d.RecipeId == recipeId))
            .Should().Be(records ? 1 : 0);
    }

    /// <summary>#1278 (T25): another organisation's recipe id is not found, and nothing is recorded.</summary>
    [Fact]
    public async Task Another_organisations_recipe_is_not_found()
    {
        await SeedUserAsync();
        var foreignId = await SeedRecipeAsync(TestDb.OtherOrgId);
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/cookbook/{foreignId}/download");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var ctx = _db.NewContext();
        (await ctx.RecipeDownloads.IgnoreQueryFilters().AsNoTracking().CountAsync(d => d.RecipeId == foreignId))
            .Should().Be(0);
    }

    [Fact]
    public async Task A_deleted_recipe_is_not_found_rather_than_a_server_error()
    {
        // It used to build the ZIP and then fail recording the download, so a
        // stale tab got a 500 instead of being told the recipe is gone (#1251).
        await SeedUserAsync();
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Recipes.SingleAsync(r => r.Id == recipeId)).DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/cookbook/{recipeId}/download");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- helpers -----------------------------------------------------------

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private async Task<int> SeedRecipeAsync(int organizationId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var recipe = new Recipe
        {
            OrganizationId = organizationId,
            Title = "Setup table pattern",
            Description = "A setup table with its page.",
            Type = RecipeType.Pattern,
            CreatedAt = now,
            UpdatedAt = now,
            Files =
            [
                new RecipeFile
                {
                    OrganizationId = organizationId, Ordering = 0, RelativePath = "src/Setup",
                    FileName = "CronusSetup.Table.al", Content = "table 50100 \"CRONUS Setup\" { }",
                },
                new RecipeFile
                {
                    OrganizationId = organizationId, Ordering = 1, RelativePath = "src/Setup",
                    FileName = "CronusSetup.Page.al", Content = "page 50100 \"CRONUS Setup\" { }",
                },
            ],
        };
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = AlexEmail,
            DisplayName = "Alex Hansen",
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", AlexEmail), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var match = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }
}
