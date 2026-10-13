using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Behavioural tests for the organisation identity surface added with the
/// org-identity rework: renaming, email-domain claim CRUD, and the
/// signup-time domain routing.
/// </summary>
public sealed class OrganizationIdentityTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Rename_rejects_names_shorter_than_two_characters()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationBrandingService(ctx);

        Func<Task> act = () => svc.RenameOrganizationAsync("A");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Rename_updates_the_cached_name_so_the_top_bar_picks_it_up()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        // The name cache lives on the config service; the branding service that
        // renames refreshes it, and both share this fixture's cache.
        var branding = _db.NewOrganizationBrandingService(ctx);

        // Prime the cache with the seeded "Default" name.
        var before = await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
        before.Should().Be("Default");

        await branding.RenameOrganizationAsync("Renamed Co");

        var after = await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
        after.Should().Be("Renamed Co",
            "the top-bar lookup is cached per-org and rename must refresh it");
    }

    [Fact]
    public async Task Rename_seeds_the_cache_rather_than_emptying_it()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        var branding = _db.NewOrganizationBrandingService(ctx);

        await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
        await branding.RenameOrganizationAsync("Renamed Co");

        // Change the row behind the service's back. A read that still went to
        // the database would come back "Out Of Band"; the cache must already
        // hold the name the rename just wrote. Emptying the entry instead is
        // what sent the next render to the database and triggered #551.
        await using (var oob = _db.NewContext())
        {
            var org = await oob.Organizations.IgnoreQueryFilters()
                .FirstAsync(o => o.Id == TestDb.DefaultOrgId);
            org.Name = "Out Of Band";
            await oob.SaveChangesAsync();
        }

        var after = await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
        after.Should().Be("Renamed Co",
            "rename already knows the new name, so it must not make the next reader fetch it");
    }

    [Fact]
    public async Task Name_lookup_does_not_borrow_the_callers_DbContext()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        // The shape behind #551: MainLayout reads the org name while the page
        // rendering below it is mid-query on the *same* request-scoped
        // AppDbContext, because Blazor does not wait for a layout's
        // OnInitializedAsync before starting its children's. Reproduce it by
        // keeping a command in flight on the caller's context and doing the
        // cache-miss read at the same time. If that read borrowed the caller's
        // context, EF's concurrency detector would throw "A second operation
        // was started on this context instance".
        var busy = ctx.Database.ExecuteSqlRawAsync("SELECT pg_sleep(1)");
        try
        {
            var name = await svc.GetOrganizationNameAsync(TestDb.DefaultOrgId);
            name.Should().Be("Default");
        }
        finally
        {
            await busy;
        }
    }

    [Fact]
    public async Task Rename_persists_trimmed_name_and_keeps_slug()
    {
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationBrandingService(ctx);

        await svc.RenameOrganizationAsync("  Acme Holdings  ");

        await using var read = _db.NewContext();
        var org = await read.Organizations.AsNoTracking().FirstAsync(o => o.Id == TestDb.DefaultOrgId);
        org.Name.Should().Be("Acme Holdings");
        org.Slug.Should().NotBeEmpty("renaming does not touch the slug");
    }

    [Fact]
    public async Task Add_normalises_case_and_strips_leading_at()
    {
        await SignInAdminAsync("mette@cronus.com");
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        await svc.AddEmailDomainAsync("@Cronus.COM");

        await using var read = _db.NewContext();
        var rows = await read.OrganizationEmailDomains.IgnoreQueryFilters().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Domain.Should().Be("cronus.com");
    }

    [Fact]
    public async Task Add_refuses_a_domain_other_than_the_admins_own()
    {
        // A claim routes every later signup from the domain here, so an Admin
        // may only claim the domain of their own email (#1211).
        await SignInAdminAsync("mette@cronus.com");
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.AddEmailDomainAsync("fabrikam.com");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Domain");
        ex.Which.Errors["Domain"].Should().Contain("your own email");

        await using var read = _db.NewContext();
        (await read.OrganizationEmailDomains.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Add_refuses_a_parent_domain_of_the_admins_own()
    {
        await SignInAdminAsync("mette@dk.cronus.com");
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        Func<Task> act = () => svc.AddEmailDomainAsync("cronus.com");
        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task Add_lets_a_site_admin_claim_any_domain()
    {
        await SignInAdminAsync("operator@cronus.com", siteAdmin: true);
        var svc = _db.NewOrganizationAdminService(_db.NewContext());

        await svc.AddEmailDomainAsync("fabrikam.com");

        await using var read = _db.NewContext();
        (await read.OrganizationEmailDomains.IgnoreQueryFilters().SingleAsync()).Domain.Should().Be("fabrikam.com");
    }

    [Fact]
    public async Task Add_rejects_a_domain_already_claimed_by_another_org()
    {
        // Seed an email-domain claim against the Other org.
        await using (var seed = _db.NewContext())
        {
            seed.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = TestDb.OtherOrgId,
                Domain = "shared.example",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await SignInAdminAsync("mette@shared.example");
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.AddEmailDomainAsync("shared.example");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Domain");
        ex.Which.Errors["Domain"].Should().Contain("another organisation");
    }

    [Fact]
    public async Task Add_rejects_garbage_domains_with_a_friendly_error()
    {
        await SignInAdminAsync("mette@cronus.com");
        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.AddEmailDomainAsync("not a domain");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Domain");
    }

    [Fact]
    public async Task Remove_cannot_delete_another_orgs_domain()
    {
        int otherDomainId;
        await using (var seed = _db.NewContext())
        {
            var row = new OrganizationEmailDomain
            {
                OrganizationId = TestDb.OtherOrgId,
                Domain = "untouchable.example",
                CreatedAt = DateTime.UtcNow,
            };
            seed.OrganizationEmailDomains.Add(row);
            await seed.SaveChangesAsync();
            otherDomainId = row.Id;
        }

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        // Service is scoped to the Default org (TestDb.OrgContext). The query
        // filter prevents it from seeing the row, so the call is a silent no-op.
        await svc.RemoveEmailDomainAsync(otherDomainId);

        await using var read = _db.NewContext();
        var stillThere = await read.OrganizationEmailDomains
            .IgnoreQueryFilters()
            .AnyAsync(d => d.Id == otherDomainId);
        stillThere.Should().BeTrue("the query filter must scope the delete to the calling org");
    }

    [Fact]
    public async Task SetMcpEnabled_refuses_when_site_wide_disabled()
    {
        _db.McpAvailability.Set(false);

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);

        Func<Task> act = () => svc.SetMcpEnabledAsync(true);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("McpEnabled");
    }

    [Fact]
    public async Task SetMcpEnabled_round_trips_when_site_wide_on()
    {
        _db.McpAvailability.Set(true);

        var ctx = _db.NewContext();
        var svc = _db.NewOrganizationAdminService(ctx);
        await svc.SetMcpEnabledAsync(false);

        await using var read = _db.NewContext();
        var org = await read.Organizations.AsNoTracking().FirstAsync(o => o.Id == TestDb.DefaultOrgId);
        org.McpEnabled.Should().BeFalse();
    }

    private async Task SignInAdminAsync(string email, bool siteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = "Mette", PasswordHash = "x",
            Role = UserRole.Admin, Status = UserStatus.Active, IsSiteAdmin = siteAdmin, CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = user.Id;
    }
}
