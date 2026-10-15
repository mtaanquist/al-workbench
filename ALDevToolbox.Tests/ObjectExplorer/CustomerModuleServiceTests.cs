using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Customer modules (.design/solution-customer-info.md, "Modules"): a short catalogue,
/// matched against what an online customer's environment reports and typed in for a
/// customer who isn't online.
/// </summary>
public sealed class CustomerModuleServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9900;
    private const int OtherUserId = 9901;
    private static readonly Guid CaptureAppId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    public CustomerModuleServiceTests()
    {
        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@example.com"), NewUser(OtherUserId, "other@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = email,
        Role = UserRole.Editor, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private CustomerModuleService Svc(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<CustomerModuleService>.Instance);

    private async Task<int> SeedSolutionAsync(string name, ProjectHostingType? hosting)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, HostingType = hosting, CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> SeedEnvironmentAsync(int projectId, string name, string type, params (Guid AppId, string Name, string Publisher, string Version)[] apps)
    {
        await using var ctx = _db.NewContext();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = type, FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        ctx.OeEnvironmentApps.AddRange(apps.Select(a => new OeEnvironmentApp
        {
            OrganizationId = TestDb.DefaultOrgId, EnvironmentId = env.Id, AppId = a.AppId, Name = a.Name,
            Publisher = a.Publisher, Version = a.Version, FetchedAt = DateTime.UtcNow,
        }));
        await ctx.SaveChangesAsync();
        return env.Id;
    }

    private async Task<int> AddToCatalogAsync(string name, string? publisher = null, Guid? appId = null)
    {
        await using var ctx = _db.NewContext();
        await Svc(ctx).SaveCatalogModuleAsync(null, new CatalogModuleInput(name, publisher, appId?.ToString()));
        return (await Svc(ctx).ListCatalogAsync()).Single(m => m.Name == name).Id;
    }

    [Fact]
    public async Task The_catalogue_refuses_a_second_module_with_the_same_name_or_app_id_and_a_bad_app_id()
    {
        await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var sameName = () => svc.SaveCatalogModuleAsync(null, new CatalogModuleInput("Continia Document Capture", null, null));
        var sameApp = () => svc.SaveCatalogModuleAsync(null, new CatalogModuleInput("Capture again", null, CaptureAppId.ToString()));
        var badApp = () => svc.SaveCatalogModuleAsync(null, new CatalogModuleInput("ForNAV", null, "not-a-guid"));
        var blank = () => svc.SaveCatalogModuleAsync(null, new CatalogModuleInput(" ", null, null));

        (await sameName.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
        (await sameApp.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("AppId");
        (await badApp.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("AppId");
        (await blank.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task An_online_solution_shows_the_catalogue_modules_its_production_environment_reports()
    {
        await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        await AddToCatalogAsync("ForNAV", "ForNAV");                       // no app id: matched by name and publisher
        await AddToCatalogAsync("Continia Payment Management", "Continia Software", Guid.NewGuid()); // not installed
        var id = await SeedSolutionAsync("CRONUS Denmark", ProjectHostingType.MicrosoftCloud);
        await SeedEnvironmentAsync(id, "Sandbox", "Sandbox", (CaptureAppId, "Document Capture", "Continia Software", "99.0.0.0"));
        var prod = await SeedEnvironmentAsync(id, "Production", "Production",
            (CaptureAppId, "Document Capture", "Continia Software", "25.1.0.0"),
            (Guid.NewGuid(), "fornav", "ForNAV", "8.1.0.0"),
            (Guid.NewGuid(), "Base Application", "Microsoft", "25.3.0.0"));

        await using var ctx = _db.NewContext();
        var modules = await Svc(ctx).GetSolutionModulesAsync(id);

        modules.ReadFromEnvironment.Should().BeTrue();
        modules.EnvironmentName.Should().Be("Production", "production is what the customer runs on");
        modules.EnvironmentId.Should().Be(prod);
        modules.FetchedAt.Should().NotBeNull();
        modules.Modules.Select(m => (m.Name, m.Version)).Should().Equal(
            ("Continia Document Capture", "25.1.0.0"), ("ForNAV", "8.1.0.0"));
    }

    [Fact]
    public async Task An_online_solution_nobody_has_read_yet_says_so_rather_than_claiming_it_has_nothing()
    {
        await AddToCatalogAsync("ForNAV", "ForNAV");
        var id = await SeedSolutionAsync("CRONUS Denmark", null);

        await using var ctx = _db.NewContext();
        var modules = await Svc(ctx).GetSolutionModulesAsync(id);

        modules.Should().BeEquivalentTo(new { ReadFromEnvironment = true, EnvironmentName = (string?)null, FetchedAt = (DateTime?)null, CatalogCount = 1 });
        modules.Modules.Should().BeEmpty();
    }

    [Fact]
    public async Task An_on_premises_solution_has_its_modules_typed_in_once_each()
    {
        var capture = await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        var id = await SeedSolutionAsync("CRONUS Norway", ProjectHostingType.CustomerHardware);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await svc.SaveSolutionModuleAsync(id, null, new SolutionModuleInput(capture, " 6.1.0.1 ", "OCR on their own server"));
        var twice = () => svc.SaveSolutionModuleAsync(id, null, new SolutionModuleInput(capture, "7.0", null));
        var unknown = () => svc.SaveSolutionModuleAsync(id, null, new SolutionModuleInput(424242, null, null));

        (await twice.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ModuleId");
        (await unknown.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ModuleId");
        var listed = await svc.GetSolutionModulesAsync(id);
        listed.ReadFromEnvironment.Should().BeFalse();
        var row = listed.Modules.Should().ContainSingle().Subject;
        row.Should().BeEquivalentTo(new { Name = "Continia Document Capture", Version = "6.1.0.1", Note = "OCR on their own server" });

        await svc.SaveSolutionModuleAsync(id, row.RowId, new SolutionModuleInput(capture, "7.0.0.0", null));
        (await svc.GetSolutionModulesAsync(id)).Modules.Single().Version.Should().Be("7.0.0.0");
        await svc.DeleteSolutionModuleAsync(id, row.RowId);
        (await svc.GetSolutionModulesAsync(id)).Modules.Should().BeEmpty();
    }

    [Fact]
    public async Task An_online_solutions_modules_are_business_centrals_to_say_and_cannot_be_typed()
    {
        var capture = await AddToCatalogAsync("Continia Document Capture", null, CaptureAppId);
        var id = await SeedSolutionAsync("CRONUS Denmark", ProjectHostingType.MicrosoftCloud);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveSolutionModuleAsync(id, null, new SolutionModuleInput(capture, "1.0", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Values.Single().Should().Contain("read from the environment");
    }

    [Fact]
    public async Task Anyone_who_can_see_a_public_solution_may_keep_its_typed_modules_up_to_date()
    {
        var capture = await AddToCatalogAsync("Continia Document Capture", null, CaptureAppId);
        var id = await SeedSolutionAsync("CRONUS Norway", ProjectHostingType.OurCloud);
        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var ctx = _db.NewContext();

        await Svc(ctx).SaveSolutionModuleAsync(id, null, new SolutionModuleInput(capture, "1.0", null));

        (await Svc(ctx).GetSolutionModulesAsync(id)).Modules.Should().ContainSingle().Which.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Who_has_a_module_counts_both_the_typed_and_the_installed_and_the_catalogue_says_how_many()
    {
        var capture = await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        var online = await SeedSolutionAsync("CRONUS Denmark", ProjectHostingType.MicrosoftCloud);
        await SeedEnvironmentAsync(online, "Production", "Production", (CaptureAppId, "Document Capture", "Continia Software", "25.1.0.0"));
        var onPrem = await SeedSolutionAsync("CRONUS Norway", ProjectHostingType.CustomerHardware);
        await SeedSolutionAsync("CRONUS Sweden", ProjectHostingType.CustomerHardware);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.SaveSolutionModuleAsync(onPrem, null, new SolutionModuleInput(capture, "6.1", null));

        (await svc.ListProjectIdsWithModuleAsync(capture)).Should().BeEquivalentTo([online, onPrem]);
        (await svc.ListCatalogAsync()).Single().Solutions.Should().Be(2);
    }

    /// <summary>Marks a seeded environment as one the customer deleted.</summary>
    private async Task SoftDeleteEnvironmentAsync(int environmentId)
    {
        await using var ctx = _db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == environmentId);
        env.Status = "SoftDeleted";
        env.SoftDeletedOn = DateTime.UtcNow.AddDays(-2);
        env.HardDeletePendingOn = DateTime.UtcNow.AddDays(12);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The card reads from the customer's production environment. A deleted one still has
    /// its app list in our mirror, but it lists what was installed on the day it was
    /// deleted - which is not what the customer has now, and would quietly become the
    /// answer the moment a production environment is deleted.
    /// </summary>
    [Fact]
    public async Task A_deleted_environment_is_never_the_one_the_modules_are_read_from()
    {
        await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        var fornav = Guid.NewGuid();
        await AddToCatalogAsync("ForNAV", "ForNAV", fornav);
        var id = await SeedSolutionAsync("CRONUS Denmark", ProjectHostingType.MicrosoftCloud);
        var deleted = await SeedEnvironmentAsync(id, "Production", "Production",
            (CaptureAppId, "Document Capture", "Continia Software", "25.1.0.0"));
        await SeedEnvironmentAsync(id, "Live", "Production", (fornav, "ForNAV", "ForNAV", "8.1.0.0"));
        await SoftDeleteEnvironmentAsync(deleted);

        await using var ctx = _db.NewContext();
        var modules = await Svc(ctx).GetSolutionModulesAsync(id);

        modules.EnvironmentName.Should().Be("Live");
        modules.Modules.Select(m => m.Name).Should().Equal("ForNAV");
    }

    /// <summary>
    /// And the Solutions list's filter reads the same environments, so a customer never
    /// appears under a module only their deleted environment had.
    /// </summary>
    [Fact]
    public async Task The_module_filter_ignores_apps_on_a_deleted_environment()
    {
        var capture = await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        var keeps = await SeedSolutionAsync("CRONUS Denmark", ProjectHostingType.MicrosoftCloud);
        await SeedEnvironmentAsync(keeps, "Production", "Production",
            (CaptureAppId, "Document Capture", "Continia Software", "25.1.0.0"));
        var lost = await SeedSolutionAsync("CRONUS Norway", ProjectHostingType.MicrosoftCloud);
        var deleted = await SeedEnvironmentAsync(lost, "Production", "Production",
            (CaptureAppId, "Document Capture", "Continia Software", "25.1.0.0"));
        await SoftDeleteEnvironmentAsync(deleted);

        await using var ctx = _db.NewContext();

        (await Svc(ctx).ListProjectIdsWithModuleAsync(capture)).Should().BeEquivalentTo([keeps]);
    }

    [Fact]
    public async Task Apps_to_pick_from_leave_out_microsofts_and_what_is_already_in_the_catalogue_most_widespread_first()
    {
        await AddToCatalogAsync("Continia Document Capture", "Continia Software", CaptureAppId);
        var fornav = Guid.NewGuid();
        var rare = Guid.NewGuid();
        var first = await SeedSolutionAsync("CRONUS Denmark", null);
        var second = await SeedSolutionAsync("CRONUS Norway", null);
        await SeedEnvironmentAsync(first, "Production", "Production",
            (CaptureAppId, "Document Capture", "Continia Software", "25.1"), (fornav, "ForNAV", "ForNAV", "8.1"),
            (rare, "CRONUS Warehouse Add-on", "CRONUS International", "1.0"), (Guid.NewGuid(), "Base Application", "Microsoft", "25.3"));
        await SeedEnvironmentAsync(second, "Production", "Production", (fornav, "ForNAV", "ForNAV", "8.0"));

        await using var ctx = _db.NewContext();
        var seen = await Svc(ctx).ListSeenAppsAsync();

        seen.Select(a => (a.Name, a.Solutions)).Should().Equal(("ForNAV", 2), ("CRONUS Warehouse Add-on", 1));
    }

    /// <summary>
    /// A deleted Solution's apps are not offered: the catalogue counts leave deleted
    /// Solutions out, and the list to pick from has to agree with them (#1342).
    /// </summary>
    [Fact]
    public async Task Apps_to_pick_from_leave_out_apps_only_a_deleted_solution_had()
    {
        var fornav = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var keeps = await SeedSolutionAsync("CRONUS Denmark", null);
        var gone = await SeedSolutionAsync("CRONUS Norway", null);
        await SeedEnvironmentAsync(keeps, "Production", "Production", (fornav, "ForNAV", "ForNAV", "8.1"));
        await SeedEnvironmentAsync(gone, "Production", "Production",
            (fornav, "ForNAV", "ForNAV", "8.1"), (orphan, "CRONUS Warehouse Add-on", "CRONUS International", "1.0"));
        await using (var del = _db.NewContext())
        {
            var project = await del.OeProjects.SingleAsync(p => p.Id == gone);
            project.DeletedAt = DateTime.UtcNow;
            await del.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var seen = await Svc(ctx).ListSeenAppsAsync();

        seen.Select(a => (a.Name, a.Solutions)).Should().Equal(("ForNAV", 1));
    }

    [Fact]
    public async Task Removing_a_module_from_the_catalogue_takes_the_typed_versions_with_it()
    {
        var capture = await AddToCatalogAsync("Continia Document Capture", null, CaptureAppId);
        var id = await SeedSolutionAsync("CRONUS Norway", ProjectHostingType.CustomerHardware);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.SaveSolutionModuleAsync(id, null, new SolutionModuleInput(capture, "6.1", null));

        await svc.DeleteCatalogModuleAsync(capture);

        (await svc.ListCatalogAsync()).Should().BeEmpty();
        (await svc.GetSolutionModulesAsync(id)).Modules.Should().BeEmpty();
    }
}
