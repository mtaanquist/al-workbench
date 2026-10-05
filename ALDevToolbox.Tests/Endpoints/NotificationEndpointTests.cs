using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Opening a notification and "Mark all as read" (issue #1043), through the
/// real auth and antiforgery stack. The service rules have their own tests in
/// <c>InAppNotificationServiceTests</c>; these pin what the endpoints add on
/// top: who may call them, the antiforgery check, and where they redirect.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class NotificationEndpointTests : IDisposable
{
    private const string AlexEmail = "alex@cronus.example";
    private const string Password = "correct horse battery staple";
    private static readonly DateTime Monday = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Anonymous_open_redirects_to_login_and_leaves_the_notification_unread()
    {
        var alex = await SeedUserAsync(AlexEmail);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/notifications/{id}/open");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
        (await ReadAtAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Anonymous_read_all_is_refused_and_marks_nothing()
    {
        var alex = await SeedUserAsync(AlexEmail);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/notifications/read-all", Form(("upToId", id.ToString())));

        // Auth or antiforgery refuses it first; either way nothing is marked.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.BadRequest);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            response.Headers.Location!.AbsolutePath.Should().Be("/login");
        }
        (await ReadAtAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Opening_your_own_marks_it_read_and_goes_to_its_page()
    {
        var alex = await SeedUserAsync(AlexEmail, Password);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);

        using var response = await client.GetAsync($"/notifications/{id}/open");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/pipelines/1?build=7");
        (await ReadAtAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Opening_someone_elses_goes_back_to_the_list_and_leaves_theirs_unread()
    {
        await SeedUserAsync(AlexEmail, Password);
        var sam = await SeedUserAsync("sam@cronus.example");
        var samsId = await AddAsync(sam, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);

        using var response = await client.GetAsync($"/notifications/{samsId}/open");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/notifications");
        (await ReadAtAsync(samsId)).Should().BeNull();
    }

    [Fact]
    public async Task Opening_one_that_does_not_exist_goes_back_to_the_list()
    {
        await SeedUserAsync(AlexEmail, Password);
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);

        using var response = await client.GetAsync("/notifications/987654/open");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/notifications");
    }

    [Fact]
    public async Task Read_all_without_the_antiforgery_token_is_refused()
    {
        var alex = await SeedUserAsync(AlexEmail, Password);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);

        using var response = await client.PostAsync("/notifications/read-all", Form(("upToId", id.ToString())));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAtAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Read_all_marks_your_own_up_to_the_newest_shown_and_returns_to_the_list()
    {
        var alex = await SeedUserAsync(AlexEmail, Password);
        var sam = await SeedUserAsync("sam@cronus.example");
        var first = await AddAsync(alex, "/pipelines/1?build=7");
        var shown = await AddAsync(alex, "/pipelines/1?build=8");
        var samsId = await AddAsync(sam, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);
        var token = await TokenFromAsync(client, "/notifications");
        var later = await AddAsync(alex, "/pipelines/1?build=9");

        using var response = await client.PostAsync("/notifications/read-all", Form(
            ("upToId", shown.ToString()), ("__RequestVerificationToken", token)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/notifications");
        (await ReadAtAsync(first)).Should().NotBeNull();
        (await ReadAtAsync(shown)).Should().NotBeNull();
        (await ReadAtAsync(later)).Should().BeNull("it arrived after the page was shown");
        (await ReadAtAsync(samsId)).Should().BeNull();
    }

    [Theory]
    [InlineData("/pipelines/1?tab=builds", "/pipelines/1?tab=builds")]
    [InlineData("//evil.example/path", "/notifications")]
    [InlineData("https://evil.example/", "/notifications")]
    [InlineData("/\\evil.example", "/notifications")]
    [InlineData("", "/notifications")]
    [InlineData("/pipelines\n/1", "/notifications")]
    public async Task Read_all_from_the_flyout_goes_back_to_the_page_it_was_on_and_nowhere_else(
        string returnUrl, string expected)
    {
        var alex = await SeedUserAsync(AlexEmail, Password);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);
        var token = await TokenFromAsync(client, "/notifications");

        using var response = await client.PostAsync("/notifications/read-all", Form(
            ("upToId", id.ToString()), ("returnUrl", returnUrl), ("__RequestVerificationToken", token)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(expected);
        (await ReadAtAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Read_all_in_the_background_answers_no_content_and_marks_them_read()
    {
        var alex = await SeedUserAsync(AlexEmail, Password);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, AlexEmail);
        var token = await TokenFromAsync(client, "/notifications");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/notifications/read-all")
        {
            Content = Form(("upToId", id.ToString()), ("returnUrl", "/pipelines"), ("__RequestVerificationToken", token)),
        };
        request.Headers.Add(ALDevToolbox.Endpoints.NotificationEndpoints.BackgroundHeader, "1");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAtAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Read_all_in_the_background_when_signed_out_is_not_a_success()
    {
        var alex = await SeedUserAsync(AlexEmail);
        var id = await AddAsync(alex, "/pipelines/1?build=7");
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/notifications/read-all")
        {
            Content = Form(("upToId", id.ToString())),
        };
        request.Headers.Add(ALDevToolbox.Endpoints.NotificationEndpoints.BackgroundHeader, "1");

        using var response = await client.SendAsync(request);

        // The flyout's script takes only a 204 as done.
        response.StatusCode.Should().NotBe(HttpStatusCode.NoContent);
        (await ReadAtAsync(id)).Should().BeNull();
    }

    // ---- helpers -----------------------------------------------------------

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private async Task<DateTime?> ReadAtAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.UserNotifications.AsNoTracking().SingleAsync(n => n.Id == id)).ReadAt;
    }

    private async Task<int> AddAsync(int userId, string path)
    {
        await using var ctx = _db.NewContext();
        var row = new UserNotification
        {
            UserId = userId, OrganizationId = TestDb.DefaultOrgId, Category = NotificationCategory.Builds,
            Title = "Build failed: CRONUS Coffee - Main", Path = path, CreatedAt = Monday,
        };
        ctx.UserNotifications.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> SeedUserAsync(string email, string? password = null)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = "Alex Hansen",
            PasswordHash = password is null
                ? "x"
                : new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory, string email)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", Form(
            ("Email", email), ("Password", Password), ("__RequestVerificationToken", token)));
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
