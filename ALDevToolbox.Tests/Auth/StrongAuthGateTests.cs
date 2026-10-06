using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The per-org strong-auth gate (<see cref="StrongAuthGate"/>) is a security
/// policy whose allow-list nothing checked: a new top-level route that
/// prefix-matches an allow-listed segment silently un-gates a strong-auth org
/// while the admin's toggle still reads "on", and over-gating is the mirror
/// risk that produced the #372 regression. These tests drive the middleware
/// directly with a hand-built pipeline. See issue #667.
/// </summary>
public sealed class StrongAuthGateTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;

    public StrongAuthGateTests()
    {
        var db = _db;
        _services = new ServiceCollection()
            .AddScoped(_ => db.NewContext())
            .AddScoped<IOrganizationContext>(_ => new AmbientOrganizationContext())
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private async Task<User> SeedUserAsync(int organizationId = TestDb.DefaultOrgId, string email = "alice@cronus.test")
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            DisplayName = "Alice",
            PasswordHash = "ignored",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    private async Task SetRequireStrongAuthAsync(int organizationId, bool required)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.OrganizationSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = organizationId };
            ctx.OrganizationSettings.Add(row);
        }
        row.RequireStrongAuth = required;
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Runs one request through the gate. Returns the context so the caller can
    /// assert on status / Location, plus whether the terminal middleware ran.
    /// </summary>
    private async Task<(HttpContext Context, bool ReachedEndpoint)> RunAsync(
        User? user,
        string path,
        string method = "GET",
        bool asPat = false)
    {
        var reached = false;
        var app = new ApplicationBuilder(_services);
        app.UseStrongAuthGate();
        app.Run(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var scope = _services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        if (user is not null)
        {
            var claims = new List<Claim>
            {
                new(HttpOrganizationContext.UserIdClaim, user.Id.ToString()),
                new(HttpOrganizationContext.OrganizationIdClaim, user.OrganizationId.ToString()),
            };
            if (asPat) claims.Add(new Claim("pat_id", "7"));
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
            var orgContext = (AmbientOrganizationContext)scope.ServiceProvider.GetRequiredService<IOrganizationContext>();
            orgContext.CurrentUserId = user.Id;
            orgContext.CurrentOrganizationId = user.OrganizationId;
        }

        await pipeline(ctx);
        return (ctx, reached);
    }

    [Fact]
    public async Task User_without_a_strong_method_is_redirected_on_a_get()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);

        var (ctx, reached) = await RunAsync(user, "/admin");

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        ctx.Response.Headers.Location.ToString().Should().Be("/account/security?required=1");
    }

    [Fact]
    public async Task User_without_a_strong_method_gets_403_on_a_post()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);

        var (ctx, reached) = await RunAsync(user, "/admin", method: "POST");

        reached.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [MemberData(nameof(AllowedPaths))]
    public async Task Allow_listed_paths_stay_reachable(string path)
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);

        var (ctx, reached) = await RunAsync(user, path);

        reached.Should().BeTrue($"{path} must stay reachable so the user can enrol or the probe can answer");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    public static TheoryData<string> AllowedPaths()
    {
        var data = new TheoryData<string>();
        foreach (var prefix in StrongAuthGate.AllowedPathPrefixes) data.Add(prefix);
        return data;
    }

    [Fact]
    public void The_allow_list_is_exactly_this_set()
    {
        // Pinned deliberately: adding an entry here widens a security policy,
        // so it should be a conscious edit reviewed alongside this list.
        StrongAuthGate.AllowedPathPrefixes.Should().Equal(
            "/account", "/auth", "/mcp", "/oauth", "/.well-known", "/login", "/signup",
            "/_blazor", "/_framework", "/_content", "/healthz", "/readyz", "/not-found",
            "/Error", "/css", "/js", "/favicon.ico");
    }

    [Theory]
    [InlineData("/account/security", true)]
    [InlineData("/accounts-payable", false)]
    [InlineData("/mcp", true)]
    [InlineData("/mcpx", false)]
    [InlineData("/admin", false)]
    [InlineData("/", false)]
    public void Allow_list_matching_is_by_segment_not_by_string_prefix(string path, bool allowed) =>
        StrongAuthGate.IsAllowed(path).Should().Be(allowed);

    [Fact]
    public async Task Enrolling_totp_lifts_the_gate()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == user.Id);
            row.TotpEnabled = true;
            await ctx.SaveChangesAsync();
        }

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Enrolling_email_mfa_lifts_the_gate()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == user.Id);
            row.EmailMfaEnabled = true;
            await ctx.SaveChangesAsync();
        }

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Registering_a_passkey_lifts_the_gate()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);
        await using (var ctx = _db.NewContext())
        {
            ctx.UserPasskeys.Add(new UserPasskey
            {
                UserId = user.Id,
                CredentialId = [1, 2, 3],
                PublicKey = [4, 5, 6],
                Nickname = "YubiKey",
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task A_linked_microsoft_account_lifts_the_gate()
    {
        // MFA is the Entra tenant's job for a federated account — see #552.
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);
        await using (var ctx = _db.NewContext())
        {
            ctx.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = user.Id,
                // The discriminator the app actually stamps. It matters since
                // issue #621 put GitHub links in this table: the gate now asks
                // for an Entra row specifically, because a GitHub link is
                // authorisation and not a way to sign in.
                Provider = EntraSignInService.ProviderName,
                Issuer = "https://login.microsoftonline.com/tid/v2.0",
                Subject = "sub-1",
                DisplayIdentity = "alice@cronus.test",
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Pat_requests_are_never_gated()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);

        var (_, reached) = await RunAsync(user, "/admin", asPat: true);

        reached.Should().BeTrue("a PAT is its own strong credential and the enrolment UI is browser-only (#372)");
    }

    [Fact]
    public async Task With_the_org_setting_off_nothing_is_gated()
    {
        var user = await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, false);

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task With_no_settings_row_at_all_nothing_is_gated()
    {
        var user = await SeedUserAsync(organizationId: TestDb.OtherOrgId, email: "bob@cronus.test");

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Anonymous_requests_pass_straight_through()
    {
        await SeedUserAsync();
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);

        var (_, reached) = await RunAsync(user: null, "/admin");

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task Another_orgs_setting_does_not_gate_this_user()
    {
        // The gate resolves the requirement from the *user's* org, so a
        // strong-auth Default org must not spill onto an Other-org member.
        await SetRequireStrongAuthAsync(TestDb.DefaultOrgId, true);
        var user = await SeedUserAsync(organizationId: TestDb.OtherOrgId, email: "bob@cronus.test");
        await SetRequireStrongAuthAsync(TestDb.OtherOrgId, false);

        var (_, reached) = await RunAsync(user, "/admin");

        reached.Should().BeTrue();
    }
}
