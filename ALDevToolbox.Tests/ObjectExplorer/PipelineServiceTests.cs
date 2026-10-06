using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// CRUD + validation for <see cref="PipelineService"/> against the shared
/// <see cref="TestDb"/> fixture: name required and unique per project (but free to
/// repeat across projects), the selection serialised to JSON (null = build all),
/// update, and soft-delete. See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class PipelineServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public PipelineServiceTests()
    {
        // Manage rights come from the parent project's owner; act as SiteAdmin so the
        // access gate passes without seeding a user.
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreatePipelineAsync_persists_name_and_selection()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var selection = new[] { "11111111-1111-1111-1111-111111111111" };

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", selection));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.Name.Should().Be("Production");
        pipeline.ProjectId.Should().Be(projectId);
        JsonSerializer.Deserialize<List<string>>(pipeline.RequestedAppIdsJson!).Should().Equal(selection);
    }

    [Fact]
    public async Task CreatePipelineAsync_stores_null_selection_for_build_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "All", SelectedAppIds: null));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).RequestedAppIdsJson.Should().BeNull();
    }

    [Fact]
    public async Task A_pipeline_is_named_after_its_branch_and_the_extensions_it_builds()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx, """
            [{"AppId":"11111111-1111-1111-1111-111111111111","Name":"CRONUS Sales","Publisher":"CRONUS","Version":"1.0.0.0","RepoUrl":"","RepoDisplayName":""},
             {"AppId":"22222222-2222-2222-2222-222222222222","Name":"CRONUS Base","Publisher":"CRONUS","Version":"1.0.0.0","RepoUrl":"","RepoDisplayName":""}]
            """);
        var svc = NewService(ctx);

        var all = await svc.CreatePipelineAsync(new PipelineInput(projectId, null, null));
        var one = await svc.CreatePipelineAsync(new PipelineInput(projectId, null, ["{11111111-1111-1111-1111-111111111111}"], Branch: "main"));
        var two = await svc.CreatePipelineAsync(new PipelineInput(projectId, "  ",
            ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"], Branch: " release/25.0 "));
        var unknown = await svc.CreatePipelineAsync(new PipelineInput(projectId, null, ["33333333-3333-3333-3333-333333333333"], Branch: "main"));

        await using var read = _db.NewContext();
        var names = await read.OePipelines.Where(p => p.ProjectId == projectId).ToDictionaryAsync(p => p.Id, p => p.Name);
        names[all].Should().Be("Default branch");
        names[one].Should().Be("main (CRONUS Sales)");
        names[two].Should().Be("release/25.0 (2 extensions)");
        names[unknown].Should().Be("main (1 extension)");
    }

    [Fact]
    public async Task A_taken_generated_name_asks_for_a_name_of_its_own()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectId, null, null, Branch: "main"));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectId, null, null, Branch: "main", PreviewCheck: true));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name")
            .WhoseValue.Should().Contain("'main'");
    }

    [Fact]
    public async Task A_typed_name_is_kept_on_later_saves_until_it_is_cleared()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "main - nightly", null, Branch: "main"));

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "main - nightly", null, Branch: "main", AutoVersion: false));
        await using (var read = _db.NewContext())
        {
            var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
            pipeline.Name.Should().Be("main - nightly");
            pipeline.NameIsCustom.Should().BeTrue();
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, null, null, Branch: "main"));
        await using (var read = _db.NewContext())
        {
            var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
            pipeline.Name.Should().Be("main");
            pipeline.NameIsCustom.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Typing_the_generated_name_is_not_a_name_of_your_own()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "main", null, Branch: "main"));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).NameIsCustom.Should().BeFalse();
    }

    [Fact]
    public async Task Renaming_a_build_pipeline_renames_the_deployment_pipelines_named_after_it()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var buildId = await svc.CreatePipelineAsync(new PipelineInput(projectId, null, null, Branch: "main"));
        var prod = await SeedEnvironmentAsync(ctx, projectId, "Production");
        var test = await SeedEnvironmentAsync(ctx, projectId, "Test");
        var deployments = new ReleasePipelineService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleasePipelineService>.Instance);
        var generated = await deployments.CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, null, buildId, prod, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        var typed = await deployments.CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Hotfixes to Test", buildId, test, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await svc.UpdatePipelineAsync(buildId, new PipelineInput(projectId, null, null, Branch: "release/25.0"));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == buildId)).Name.Should().Be("release/25.0");
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == generated)).Name.Should().Be("release/25.0 to Production");
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == typed)).Name.Should().Be("Hotfixes to Test");
    }

    [Fact]
    public async Task CreatePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectId, "production", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreatePipelineAsync_allows_the_same_name_in_a_different_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectA, "Production", null));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectB, "Production", null));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreatePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(424242, "Production", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Project");
    }

    [Fact]
    public async Task UpdatePipelineAsync_changes_name_and_selection()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Test", new[] { "aaa" }));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.Name.Should().Be("Test");
        JsonSerializer.Deserialize<List<string>>(pipeline.RequestedAppIdsJson!).Should().Equal("aaa");
    }

    [Fact]
    public async Task SoftDeletePipelineAsync_hides_the_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await svc.SoftDeletePipelineAsync(id);

        await using var read = _db.NewContext();
        (await NewService(read).GetPipelineAsync(id)).Should().BeNull();
        (await read.OePipelines.IgnoreQueryFilters().SingleAsync(p => p.Id == id)).DeletedAt.Should().NotBeNull();
    }

    // --- Build numbers in app versions --------------------------------------

    [Fact]
    public async Task A_new_pipeline_numbers_its_builds_unless_turned_off()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).AutoVersion.Should().BeTrue();
            (await ProjectBuildService.PipelineNumbersAppsAsync(read, id, CancellationToken.None)).Should().BeTrue();
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, AutoVersion: false));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).AutoVersion.Should().BeFalse();
            // What the build reads when it decides whether to number its apps.
            (await ProjectBuildService.PipelineNumbersAppsAsync(read, id, CancellationToken.None)).Should().BeFalse();
        }
    }

    // --- Publishing only what changed (#1094) -------------------------------

    [Fact]
    public async Task A_new_pipeline_publishes_only_changed_extensions_unless_turned_off()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).ChangedAppsOnly.Should().BeTrue();
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, ChangedAppsOnly: false));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).ChangedAppsOnly.Should().BeFalse();
        }

        var other = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Test", null, ChangedAppsOnly: false));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == other)).ChangedAppsOnly.Should().BeFalse("turning it off on a new pipeline sticks");
        }
    }

    // --- Building on push (#1079) ------------------------------------------

    [Fact]
    public async Task Building_on_push_is_off_unless_asked_and_runs_as_whoever_last_saved_it()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var alice = await SeedUserAsync(ctx, "alice@cronus.test");
        var bob = await SeedUserAsync(ctx, "bob@cronus.test");
        _db.OrgContext.CurrentUserId = alice;
        var svc = NewService(ctx);

        var off = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Test", null));
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null, BuildOnPush: true));
        _db.OrgContext.CurrentUserId = bob;
        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production line", null, BuildOnPush: true));

        await using (var read = _db.NewContext())
        {
            var plain = await read.OePipelines.SingleAsync(p => p.Id == off);
            plain.BuildOnPush.Should().BeFalse();
            plain.BuildOnPushByUserId.Should().BeNull();
            var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
            pipeline.BuildOnPush.Should().BeTrue();
            pipeline.BuildOnPushByUserId.Should().Be(bob, "saving the pipeline makes the saver the person its builds run as");
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production line", null, BuildOnPush: false));
        await using (var read = _db.NewContext())
        {
            var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
            pipeline.BuildOnPush.Should().BeFalse();
            pipeline.BuildOnPushByUserId.Should().BeNull();
        }
    }

    [Fact]
    public async Task Resume_with_my_access_resumes_paused_building_on_push()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var alice = await SeedUserAsync(ctx, "alice@cronus.test");
        var bob = await SeedUserAsync(ctx, "bob@cronus.test");
        _db.OrgContext.CurrentUserId = alice;
        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null, BuildOnPush: true));
        var plain = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Test", null));
        await using (var paused = _db.NewContext())
        {
            await paused.OePipelines.Where(p => p.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.BuildOnPushBlocked, "the person who turned it on can no longer manage this solution."));
        }

        _db.OrgContext.CurrentUserId = bob;
        await using (var act = _db.NewContext())
        {
            await NewService(act).TakeOverBuildOnPushAsync(id);
            var refused = () => NewService(act).TakeOverBuildOnPushAsync(plain);
            await refused.Should().ThrowAsync<PlanValidationException>();
        }

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.BuildOnPushByUserId.Should().Be(bob);
        pipeline.BuildOnPushBlocked.Should().BeNull();
    }

    // --- Nightly preview check (#994) ---------------------------------------

    [Fact]
    public async Task A_new_pipeline_has_the_preview_check_off_unless_asked()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.PreviewCheck.Should().BeFalse();
        pipeline.PreviewCheckByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Turning_the_check_on_runs_it_as_you_and_turning_it_off_forgets_you()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var userId = await SeedUserAsync(ctx, "alice@cronus.test");
        _db.OrgContext.CurrentUserId = userId;
        var svc = NewService(ctx);

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null, PreviewCheck: true));
        await using (var read = _db.NewContext())
        {
            var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
            pipeline.PreviewCheck.Should().BeTrue();
            pipeline.PreviewCheckByUserId.Should().Be(userId);
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, PreviewCheck: false));
        await using (var read = _db.NewContext())
        {
            var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
            pipeline.PreviewCheck.Should().BeFalse();
            pipeline.PreviewCheckByUserId.Should().BeNull();
        }
    }

    [Fact]
    public async Task Someone_else_saving_the_pipeline_runs_the_check_as_them()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var alice = await SeedUserAsync(ctx, "alice@cronus.test");
        var bob = await SeedUserAsync(ctx, "bob@cronus.test");
        var svc = NewService(ctx);
        _db.OrgContext.CurrentUserId = alice;
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null, PreviewCheck: true));

        _db.OrgContext.CurrentUserId = bob;
        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production line", null, PreviewCheck: true));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).PreviewCheckByUserId.Should().Be(bob);
    }

    [Fact]
    public async Task Saving_a_paused_check_takes_it_over()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var alice = await SeedUserAsync(ctx, "alice@cronus.test");
        var bob = await SeedUserAsync(ctx, "bob@cronus.test");
        var svc = NewService(ctx);
        _db.OrgContext.CurrentUserId = alice;
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null, PreviewCheck: true));
        await using (var paused = _db.NewContext())
        {
            await paused.OePipelines.Where(p => p.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PreviewCheckBlocked, "Alice can no longer manage this solution."));
        }

        _db.OrgContext.CurrentUserId = bob;
        await using var edit = _db.NewContext();
        await NewService(edit).UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, PreviewCheck: true));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.PreviewCheckByUserId.Should().Be(bob);
        pipeline.PreviewCheckBlocked.Should().BeNull();
    }

    [Fact]
    public async Task Run_the_check_as_me_resumes_a_paused_check()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var alice = await SeedUserAsync(ctx, "alice@cronus.test");
        var bob = await SeedUserAsync(ctx, "bob@cronus.test");
        _db.OrgContext.CurrentUserId = alice;
        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null, PreviewCheck: true));
        await using (var paused = _db.NewContext())
        {
            await paused.OePipelines.Where(p => p.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PreviewCheckBlocked, "Alice can no longer manage this solution."));
        }

        _db.OrgContext.CurrentUserId = bob;
        await using (var act = _db.NewContext())
        {
            await NewService(act).TakeOverPreviewCheckAsync(id);
        }

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.PreviewCheckByUserId.Should().Be(bob);
        pipeline.PreviewCheckBlocked.Should().BeNull();
    }

    [Fact]
    public async Task Run_the_check_as_me_refuses_a_pipeline_without_the_check()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        var act = () => NewService(ctx).TakeOverPreviewCheckAsync(id);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("PreviewCheck");
    }

    // --- The watched branch (#963) ------------------------------------------

    [Fact]
    public async Task A_pipeline_keeps_the_branch_it_was_given_and_a_blank_one_means_the_default()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);

        var named = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Release", null, Branch: " release/25.0 "));
        var blank = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Main", null, Branch: "   "));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == named)).Branch.Should().Be("release/25.0");
        (await read.OePipelines.SingleAsync(p => p.Id == blank)).Branch.Should().BeNull(
            "blank means each repository's default branch, stored as no branch at all");
    }

    [Fact]
    public async Task Editing_a_pipeline_changes_and_clears_its_branch()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null, Branch: "main"));

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, Branch: "develop"));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).Branch.Should().Be("develop");
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, Branch: null));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).Branch.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("-main")]
    [InlineData("feature..vat")]
    [InlineData("feature/vat/")]
    [InlineData("/feature")]
    [InlineData("feature//vat")]
    [InlineData("feature/.hidden")]
    [InlineData("main.lock")]
    [InlineData("main.")]
    [InlineData("feature vat")]
    [InlineData("feature~1")]
    [InlineData("feature:vat")]
    [InlineData("feat^")]
    [InlineData("feat*")]
    [InlineData("feat?")]
    [InlineData("feat[1]")]
    [InlineData("feat\\vat")]
    [InlineData("feat@{1}")]
    public async Task A_branch_name_git_would_refuse_is_rejected_against_the_branch_field(string branch)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null, Branch: branch));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Branch");
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/25.0", true)]
    [InlineData("feature/CRONUS-123_vat-fix", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("-main", false)]
    [InlineData(".main", false)]
    [InlineData("feature..vat", false)]
    [InlineData("feature/vat/", false)]
    [InlineData("/feature", false)]
    [InlineData("feature//vat", false)]
    [InlineData("feature/.hidden", false)]
    [InlineData("main.lock", false)]
    [InlineData("main.lock/x", false)]
    [InlineData("main.", false)]
    [InlineData("feature vat", false)]
    [InlineData("feature~1", false)]
    [InlineData("feat\\vat", false)]
    [InlineData("æøå", false)]
    public void The_browser_pattern_and_the_server_rule_agree(string branch, bool valid)
    {
        // The pattern= on the editor field mirrors the server rule; a browser
        // anchors the pattern at both ends, so the test does too.
        ALDevToolbox.Services.ObjectExplorer.Projects.GitBranchName.IsValid(branch).Should().Be(valid);
        System.Text.RegularExpressions.Regex
            .IsMatch(branch, "^(?:" + ALDevToolbox.Services.ObjectExplorer.Projects.GitBranchName.HtmlPattern + ")$")
            .Should().Be(valid);
    }

    private PipelineService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<PipelineService>.Instance);

    private static async Task<int> SeedUserAsync(AppDbContext ctx, string email)
    {
        var user = new ALDevToolbox.Domain.Entities.User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = email, PasswordHash = "x",
            Role = ALDevToolbox.Domain.Entities.UserRole.Admin, Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<int> SeedEnvironmentAsync(AppDbContext ctx, int projectId, string name)
    {
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = "Sandbox", FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }

    private static async Task<int> SeedProjectAsync(AppDbContext ctx, string? discoveredExtensionsJson = null)
    {
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            DiscoveredExtensionsJson = discoveredExtensionsJson,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }
}
