using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Symbols tab of a solution (issue #901). Named user: a BC consultant whose
/// build failed because it could not find Continia Core, arriving from the failed
/// build to upload the package. Pinned: the three states, that only someone who
/// may manage the solution is offered a write, that an upload lands in the list,
/// and that removing a package asks first.
/// </summary>
public sealed class ProjectDetailSymbolsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private const int OwnerUserId = 9911;

    public ProjectDetailSymbolsTests()
    {
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectService>();
        _db.AddRepositoryReadProbe(_ctx.Services);
        _ctx.Services.AddScoped(sp => _db.NewOfflineGitHubAccessService(sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>()));
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = OwnerUserId, OrganizationId = TestDb.DefaultOrgId, Email = "owner@example.com", PasswordHash = "x",
            DisplayName = "Owner", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
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

    private int _seeded;

    private async Task<int> SeedAsync(params string[] storedFiles)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = $"CRONUS Denmark {++_seeded}", CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        foreach (var file in storedFiles)
        {
            ctx.OeProjectSymbols.Add(new OeProjectSymbol
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, FileName = file,
                Content = new byte[2048], ContentLength = 2048, CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private IRenderedComponent<ProjectDetailSymbols> Render(int id, bool canManage = true)
    {
        var cut = _ctx.Render<ProjectDetailSymbols>(p => p.Add(c => c.Id, id).Add(c => c.CanManage, canManage));
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
        return cut;
    }

    private static IEnumerable<string> Buttons(IRenderedComponent<ProjectDetailSymbols> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim());

    [Fact]
    public async Task Shows_a_loading_state_until_the_list_arrives()
    {
        var id = await SeedAsync("Continia Core.app");

        // Hold the table so the tab's read has to wait: the first render is then
        // certainly the loading state, not a race against the database.
        await using var blocker = new NpgsqlConnection(_db.ConnectionString);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE oe_project_symbols IN ACCESS EXCLUSIVE MODE", blocker, tx))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        var cut = _ctx.Render<ProjectDetailSymbols>(p => p.Add(c => c.Id, id).Add(c => c.CanManage, true));
        cut.Markup.Should().Contain("Loading symbols...");

        await tx.CommitAsync();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Continia Core.app"));
    }

    [Fact]
    public async Task With_nothing_stored_it_says_what_the_tab_is_for_and_offers_the_upload()
    {
        var id = await SeedAsync();

        var cut = Render(id);

        cut.Find(".empty-state__title").TextContent.Should().Be("No symbol packages yet");
        cut.Markup.Should().Contain("When a build fails because it can't find an app your extensions depend on");
        Buttons(cut).Should().Equal("Upload symbols");
    }

    [Fact]
    public async Task Lists_each_stored_package_with_its_size()
    {
        var id = await SeedAsync("Continia Core 12.1.app", "ForNAV Core 7.0.app");

        var cut = Render(id);

        cut.FindAll(".sub-row__name").Select(n => n.TextContent).Should()
            .BeEquivalentTo("Continia Core 12.1.app", "ForNAV Core 7.0.app");
        cut.Markup.Should().Contain("2 KB").And.Contain("2 symbol packages");
        Buttons(cut).Should().Equal("Upload symbols", "Remove", "Remove");
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_is_offered_no_upload_or_remove()
    {
        var empty = await SeedAsync();
        Render(empty, canManage: false).FindAll("button").Should().BeEmpty();

        var stored = await SeedAsync("Continia Core 12.1.app");
        var cut = Render(stored, canManage: false);
        cut.Markup.Should().Contain("Continia Core 12.1.app");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public async Task Uploading_a_package_stores_it_and_lists_it()
    {
        var id = await SeedAsync();
        var cut = Render(id);

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload symbols").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<InputFile>().UploadFiles(
                InputFileContent.CreateFromBinary(new byte[] { 1, 2, 3 }, "Continia Core 12.1.app"));
            cut.Markup.Should().Contain("Continia Core 12.1.app - 1 KB");
        });
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog button").Single(b => b.TextContent.Trim() == "Upload").Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".sub-row__name").Select(n => n.TextContent).Should().Equal("Continia Core 12.1.app");
            cut.Markup.Should().Contain("Stored Continia Core 12.1.app.");
        });
        await using var read = _db.NewContext();
        (await read.OeProjectSymbols.SingleAsync()).Content.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task A_file_that_is_not_an_app_cannot_be_uploaded()
    {
        var id = await SeedAsync();
        var cut = Render(id);

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload symbols").Click());
        cut.WaitForAssertion(() =>
        {
            cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("hello", "readme.txt"));
            cut.Markup.Should().Contain("readme.txt isn't a .app file");
        });
        cut.FindAll(".confirm-dialog button").Single(b => b.TextContent.Trim() == "Upload")
            .HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_package_asks_first_and_only_removes_on_yes()
    {
        var id = await SeedAsync("Continia Core 12.1.app");
        var cut = Render(id);

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__title").TextContent.Should().Be("Remove Continia Core 12.1.app?");
            cut.FindAll(".confirm-dialog button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        });
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog").Should().BeEmpty());
        await using (var read = _db.NewContext())
        {
            (await read.OeProjectSymbols.CountAsync()).Should().Be(1, "cancelling removes nothing");
        }

        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Remove").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog button").Single(b => b.TextContent.Trim() == "Remove").Click());

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Should().Be("No symbol packages yet"));
        await using (var read = _db.NewContext())
        {
            (await read.OeProjectSymbols.CountAsync()).Should().Be(0);
        }
    }
}
