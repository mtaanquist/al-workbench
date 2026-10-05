using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The connection form on a solution's Business Central tab. The named user is a
/// consultant wiring up a new customer: with an organisation-wide app registration in
/// place that takes a tenant ID and nothing else, and the three credential fields are
/// for the customer who has a registration of their own.
/// </summary>
public sealed class ProjectDetailBcTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9880;
    private const string OrgClientId = "33333333-3333-3333-3333-333333333333";

    public ProjectDetailBcTests()
    {
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IBcAdminClient>(new UnreachableAdminClient());
        _ctx.Services.AddSingleton<IBcAppManagementClient>(new UnreachableAppManagementClient());
        _ctx.Services.AddSingleton(new BcTokenService(
            new UnreachableHttpClientFactory(), NullLogger<BcTokenService>.Instance));
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton(new BcPanelCache(TimeProvider.System));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId, OrganizationId = TestDb.DefaultOrgId, Email = "owner@example.com", PasswordHash = "x",
            DisplayName = "Owner", Role = UserRole.Editor, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<int> SeedSolutionAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Denmark", CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedOrganisationRegistrationAsync()
    {
        await using var ctx = _db.NewContext();
        var settings = await ctx.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
            ctx.OrganizationSettings.Add(settings);
        }
        settings.BcClientId = OrgClientId;
        settings.BcClientSecretEncrypted = "cipher";
        settings.BcClientSecretExpiresAt = DateTime.UtcNow.AddYears(1);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task With_an_organisation_registration_a_new_solution_asks_for_a_tenant_and_nothing_else()
    {
        await SeedOrganisationRegistrationAsync();
        var id = await SeedSolutionAsync();

        var cut = _ctx.Render<ProjectDetailBc>(p => p.Add(c => c.Id, id));

        // All inside one wait: the choice can render a moment before the page settles on
        // the organisation's registration, and on a slow machine the fields are still there.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("input[name=bc-reg]").Should().HaveCount(2);
            cut.FindAll("#bc-tenant").Should().HaveCount(1);
            cut.FindAll("#bc-client, #bc-secret, #bc-expiry").Should().BeEmpty();
            cut.Markup.Should().Contain(OrgClientId, "it is what the customer has to authorise in their admin centre");
        });
    }

    [Fact]
    public async Task Choosing_a_different_registration_brings_the_three_fields_back()
    {
        await SeedOrganisationRegistrationAsync();
        var id = await SeedSolutionAsync();
        var cut = _ctx.Render<ProjectDetailBc>(p => p.Add(c => c.Id, id));
        cut.WaitForAssertion(() => cut.FindAll("input[name=bc-reg]").Should().HaveCount(2));

        // Found and changed inside the wait, and asserted inside one: on a slow machine the
        // page is still settling when the first render returns, and a re-render between
        // the find and the change, or the change and the assert, loses the race.
        cut.WaitForAssertion(() => cut.FindAll("input[name=bc-reg]")[1].Change(true));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#bc-client, #bc-secret, #bc-expiry").Should().HaveCount(3);
            cut.Find(".pd-before").TextContent.Should().NotContain(OrgClientId,
                "the customer authorises their own registration now, not the organisation's");
        });
    }

    [Fact]
    public async Task Without_an_organisation_registration_the_form_is_the_three_fields_and_says_there_is_another_way()
    {
        var id = await SeedSolutionAsync();

        var cut = _ctx.Render<ProjectDetailBc>(p => p.Add(c => c.Id, id));

        cut.WaitForAssertion(() => cut.FindAll("#bc-client, #bc-secret, #bc-expiry").Should().HaveCount(3));
        cut.FindAll("input[name=bc-reg]").Should().BeEmpty("a choice with one option is not a choice");
        cut.Markup.Should().Contain("Administration → Business Central");
    }
}
