using System.Reflection;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The shared fence harness over <see cref="PersonPaletteSource"/>, run as an
/// org Admin - the one role the source answers.
///
/// <para>People belong to an organisation, not to a solution, so the
/// Private-solution case reports as skipped. The cross-organisation case is the
/// one that matters here: an Admin's palette must never find a person in
/// another organisation by name. And the gate case names the two roles that
/// must get nothing: an ordinary member and an Editor.</para>
/// </summary>
public sealed class PersonPaletteSourceHarnessTests : PaletteSourceVisibilityTestBase
{
    protected override bool RowsBelongToSolutions => false;

    protected override UserRole CallerRole => UserRole.Admin;

    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new PersonPaletteSource(context.Db, context.OrgContext, new SingleTenantModeState(false));

    protected override Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        ctx.Users.Add(PersonPaletteSourceTests.NewUser(seed.OrganizationId, seed.Title));
        return Task.CompletedTask;
    }

    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        yield return ("an anonymous caller", new ClaimsPrincipal(new ClaimsIdentity()));
        yield return ("an ordinary member", CallerPrincipal(nameof(UserRole.User)));
        yield return ("an Editor", CallerPrincipal(nameof(UserRole.Editor)));
    }
}

/// <summary>
/// What the People source finds, what its rows say, and where they land.
/// </summary>
public sealed class PersonPaletteSourceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_person_is_found_by_name_and_lands_on_their_row_of_the_users_tab()
    {
        var id = await SeedAsync("Annette Møller", "annette@cronus.test", UserRole.Editor);

        var results = await SearchAsync(Admin(), "annette moller");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("person");
        row.Title.Should().Be("Annette Møller");
        row.Subtitle.Should().Be("Editor");
        row.Href.Should().Be($"/admin/administration/users?user={id}");
    }

    [Fact]
    public async Task The_email_address_is_neither_searched_nor_shown()
    {
        await SeedAsync("Annette Møller", "annette.private@cronus.test", UserRole.User);

        (await SearchAsync(Admin(), "annette.private")).Should().BeEmpty(
            "a person is found by name; an address is not something the palette matches on");

        var row = (await SearchAsync(Admin(), "annette")).Should().ContainSingle().Subject;
        foreach (var field in new[] { row.Title, row.Subtitle, row.Href, row.ShortName, row.SearchOnly })
        {
            (field ?? string.Empty).Should().NotContain("@");
        }
    }

    [Fact]
    public async Task A_disabled_person_is_found_and_says_so()
    {
        await SeedAsync("Annette Møller", "annette@cronus.test", UserRole.Admin, UserStatus.Disabled);

        var results = await SearchAsync(Admin(), "annette");

        results.Should().ContainSingle().Which.Subtitle.Should().Be("Admin - disabled");
    }

    [Fact]
    public async Task A_pending_signup_is_left_out()
    {
        // The Users tab lists pending signups as requests, under their own
        // heading, not as people with a role.
        await SeedAsync("Annette Møller", "annette@cronus.test", UserRole.User, UserStatus.Pending);

        var results = await SearchAsync(Admin(), "annette");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task A_siteadmin_who_is_not_an_org_admin_lands_on_the_site_user_list()
    {
        await SeedAsync("Annette Møller", "annette@cronus.test", UserRole.User);

        var results = await SearchAsync(Principal(nameof(UserRole.User), siteAdmin: true), "annette");

        results.Should().ContainSingle().Which.Href.Should()
            .Be("/site-admin/users?q=Annette%20M%C3%B8ller");
    }

    [Fact]
    public async Task In_the_system_organisation_an_admin_who_runs_the_site_lands_on_the_site_user_list()
    {
        // The sidebar hides Administration in the system organisation of a
        // multi-tenant site; people are administered from the site console.
        _db.OrgContext.CurrentOrganizationId = TestDb.SystemOrgId;
        await SeedAsync("Annette Møller", "annette@cronus.test", UserRole.User);

        var results = await SearchAsync(
            Principal(nameof(UserRole.Admin), siteAdmin: true, organizationId: TestDb.SystemOrgId), "annette");

        results.Should().ContainSingle().Which.Href.Should().StartWith("/site-admin/users?q=");
    }

    [Fact]
    public async Task A_search_the_gate_never_approved_finds_no_one()
    {
        await SeedAsync("Annette Møller", "annette@cronus.test", UserRole.User);

        await using var ctx = _db.NewContext();
        var source = new PersonPaletteSource(ctx, _db.OrgContext, new SingleTenantModeState(false));

        var results = await source.SearchAsync(PaletteQuery.Parse("annette"), 25, CancellationToken.None);

        results.Should().BeEmpty("the landing is decided by the gate, and without one there is nowhere to go");
    }

    [Theory]
    [InlineData(nameof(UserRole.User))]
    [InlineData(nameof(UserRole.Editor))]
    public async Task Only_admins_pass_the_gate(string role)
    {
        await using var ctx = _db.NewContext();
        var source = new PersonPaletteSource(ctx, _db.OrgContext, new SingleTenantModeState(false));

        (await source.IsAvailableAsync(Principal(role), CancellationToken.None)).Should().BeFalse();
        (await source.IsAvailableAsync(Admin(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public void Both_landing_pages_exist_and_the_users_tab_marks_each_row()
    {
        var routes = typeof(HttpOrganizationContext).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: true))
            .Select(r => r.Template)
            .ToList();

        routes.Should().Contain(PersonPaletteSource.OrganizationUsersHref);
        routes.Should().Contain(PersonPaletteSource.SiteUsersHref);

        // Each link's query parameter is only a landing if the page reads it.
        QueryParameters(typeof(ALDevToolbox.Components.Pages.Admin.Administration.AdminAdministrationUsers))
            .Should().Contain("user", "?user= is what marks and focuses the person's row");
        QueryParameters(typeof(ALDevToolbox.Components.Pages.SiteAdmin.SiteAdminUsers))
            .Should().Contain("q", "?q= is what filters the site user list to the name");
    }

    private static IEnumerable<string?> QueryParameters(Type page) =>
        page.GetProperties()
            .Select(p => p.GetCustomAttribute<SupplyParameterFromQueryAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name);

    // ── Plumbing ────────────────────────────────────────────────────────

    internal static User NewUser(int organizationId, string displayName, string? email = null,
        UserRole role = UserRole.User, UserStatus status = UserStatus.Active) => new()
    {
        OrganizationId = organizationId,
        Email = email ?? $"{Guid.NewGuid():N}@cronus.test",
        DisplayName = displayName,
        PasswordHash = "x",
        Role = role,
        Status = status,
        CreatedAt = DateTime.UtcNow,
    };

    private static ClaimsPrincipal Admin() => Principal(nameof(UserRole.Admin));

    private static ClaimsPrincipal Principal(
        string role, bool siteAdmin = false, int organizationId = TestDb.DefaultOrgId)
    {
        var claims = new List<Claim>
        {
            new(HttpOrganizationContext.UserIdClaim, "9400"),
            new(HttpOrganizationContext.OrganizationIdClaim, organizationId.ToString()),
            new(ClaimTypes.Role, role),
        };
        if (siteAdmin) claims.Add(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private async Task<int> SeedAsync(
        string displayName, string email, UserRole role, UserStatus status = UserStatus.Active)
    {
        await using var ctx = _db.NewContext();
        var user = NewUser(_db.OrgContext.CurrentOrganizationId!.Value, displayName, email, role, status);
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(ClaimsPrincipal caller, string rawQuery)
    {
        await using var ctx = _db.NewContext();
        var source = new PersonPaletteSource(ctx, _db.OrgContext, new SingleTenantModeState(false));

        (await source.IsAvailableAsync(caller, CancellationToken.None)).Should().BeTrue();

        var query = PaletteQuery.Parse(rawQuery);
        var candidates = await source.SearchAsync(query, 25, CancellationToken.None);
        return PaletteRanking.Rank(query, candidates).Select(m => m.Candidate).ToList();
    }
}
