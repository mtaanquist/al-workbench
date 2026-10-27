using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/admin/customer-modules</c>: removing a catalogue module also takes it off the
/// Customer tab of every solution that has it, with any hand-typed versions, so the
/// page asks first and says how many solutions that is (#1438).
/// </summary>
public sealed class AdminCustomerModulesTests : IAsyncDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int EditorUserId = 9320;

    public AdminCustomerModulesTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = EditorUserId, OrganizationId = TestDb.DefaultOrgId, Email = "editor@cronus.test",
                PasswordHash = "x", DisplayName = "Editor", Role = UserRole.Editor,
                Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }
        _db.OrgContext.CurrentUserId = EditorUserId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("editor@cronus.test");
        auth.SetRoles("Editor");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<CustomerModuleService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    /// <summary>One catalogue module typed onto two on-premises solutions.</summary>
    private async Task<int> SeedModuleOnTwoSolutionsAsync()
    {
        await using var seed = _db.NewContext();
        var module = new CustomerModule
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "ForNAV", Publisher = "ForNAV", CreatedAt = DateTime.UtcNow,
        };
        seed.CustomerModules.Add(module);
        var solutions = new[] { "CRONUS Denmark", "CRONUS Sweden" }.Select(name => new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, HostingType = ProjectHostingType.OurCloud,
            CreatedByUserId = EditorUserId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        }).ToList();
        seed.OeProjects.AddRange(solutions);
        await seed.SaveChangesAsync();
        seed.OeProjectModules.AddRange(solutions.Select(p => new OeProjectModule
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = p.Id, ModuleId = module.Id,
            Version = "8.1.0.0", CreatedAt = DateTime.UtcNow,
        }));
        await seed.SaveChangesAsync();
        return module.Id;
    }

    private (bool Module, int SolutionRows) Stored(int moduleId)
    {
        using var ctx = _db.NewContext();
        return (ctx.CustomerModules.AsNoTracking().Any(m => m.Id == moduleId),
            ctx.OeProjectModules.AsNoTracking().Count(m => m.ModuleId == moduleId));
    }

    private IRenderedComponent<AdminCustomerModules> RenderAndAskToRemove()
    {
        var cut = _ctx.Render<AdminCustomerModules>();
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Remove ForNAV']").Click());
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__body").TextContent.Should().Contain(
            "the Customer tab of the 2 solutions that have it",
            "the question has to say how far the removal reaches"));
        return cut;
    }

    [Fact]
    public async Task Dismissing_the_question_keeps_the_module_and_every_solutions_copy_of_it()
    {
        var moduleId = await SeedModuleOnTwoSolutionsAsync();
        var cut = RenderAndAskToRemove();

        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions").Should().BeEmpty());
        StillTrue.For(() => Stored(moduleId) == (true, 2), "saying no must leave the catalogue and both solutions as they were");
    }

    [Fact]
    public async Task Confirming_removes_the_module_from_the_catalogue_and_from_both_solutions()
    {
        var moduleId = await SeedModuleOnTwoSolutionsAsync();
        var cut = RenderAndAskToRemove();

        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Remove module").Click();

        cut.WaitForAssertion(() => cut.FindAll("button[aria-label='Remove ForNAV']").Should().BeEmpty());
        Stored(moduleId).Should().Be((false, 0));
    }
}
