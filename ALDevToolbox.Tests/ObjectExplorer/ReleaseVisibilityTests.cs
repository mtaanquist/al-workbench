using System.IO.Compression;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The release half of the project-visibility fence
/// (<c>.design/teams-and-visibility.md</c>, slice 3). A <c>Release</c> has no
/// project of its own, so it is hidden exactly when one of its two links — the
/// build that produced it, or the import job that brought it in — points at a
/// Private project the caller cannot view. An unlinked release stays governed by
/// the org filter alone.
///
/// <para>The matrix below is {non-member, member, org Admin, SiteAdmin} ×
/// {Public, Read-only, Private} across every release-keyed read surface. It is
/// the same shape for all of them on purpose: one rule, applied in one place, in
/// two shapes.</para>
/// </summary>
public sealed class ReleaseVisibilityTests : IDisposable
{
    private readonly TestDb _db = new();

    private const int OwnerUserId = 9400;
    private const int AdminUserId = 9401;
    private const int MemberUserId = 9402;
    private const int OutsiderUserId = 9403;

    private readonly ProjectDiscoveryQueue _discoveryQueue = new();

    public ReleaseVisibilityTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser(OwnerUserId, "owner@example.com", UserRole.Editor),
            NewUser(AdminUserId, "admin@example.com", UserRole.Admin),
            NewUser(MemberUserId, "mel@example.com", UserRole.User),
            NewUser(OutsiderUserId, "nils@example.com", UserRole.User));
        ctx.SaveChanges();
        ActAs(OwnerUserId);
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private void ActAs(int? userId, bool siteAdmin = false)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    // ── Service factories ───────────────────────────────────────────────

    private ProjectAccess Access(AppDbContext ctx) => new(ctx, _db.OrgContext);

    private ProjectService Projects(AppDbContext ctx)
    {
        var access = Access(ctx);
        var discovery = new ProjectDiscoveryService(
            ctx, _db.OrgContext, access, _discoveryQueue, NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectService(ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), _db.NewRepositoryReadProbe(ctx), NullLogger<ProjectService>.Instance);
    }

    private ReferenceQueryService References(AppDbContext ctx) =>
        new(ctx, Access(ctx), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance);

    private ObjectSearchService Search(AppDbContext ctx) => new(ctx, Access(ctx));

    private ObjectExplorerService Explorer(AppDbContext ctx) =>
        new(ctx, References(ctx), Access(ctx), NullLogger<ObjectExplorerService>.Instance);

    private SourceVisibility Visibility(AppDbContext ctx) => new(ctx, Access(ctx));

    private SourceViewerService Viewer(AppDbContext ctx) =>
        new(ctx, References(ctx), Visibility(ctx));

    private ExplorerTreeService Tree(AppDbContext ctx) => new(ctx, Visibility(ctx));

    private ReleaseComparisonService Comparison(AppDbContext ctx) =>
        new(ctx, Access(ctx), NullLogger<ReleaseComparisonService>.Instance);

    // ── Seeding ─────────────────────────────────────────────────────────

    /// <summary>A project, a team the member is on, and the project's visibility set as its owner.</summary>
    private async Task<(int ProjectId, int TeamId)> SeedProjectAsync(
        ProjectVisibility visibility, string name = "CRONUS Denmark")
    {
        int projectId, teamId;
        await using (var ctx = _db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = name,
                CreatedByUserId = OwnerUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);

            var team = new Team
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "Denmark engagement " + name,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();

            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId,
                TeamId = team.Id,
                UserId = MemberUserId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            projectId = project.Id;
            teamId = team.Id;
        }

        if (visibility != ProjectVisibility.Public)
        {
            var previous = _db.OrgContext.CurrentUserId;
            ActAs(OwnerUserId);
            await using (var ctx = _db.NewContext())
            {
                await Projects(ctx).SetAccessAsync(projectId, visibility, new[] { teamId });
            }
            ActAs(previous);
        }

        return (projectId, teamId);
    }

    /// <summary>A ready release with one module, one file, and one object in it.</summary>
    private async Task<SeededRelease> SeedReleaseAsync(string label, string kind = "project")
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = kind,
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        var module = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = label + " app",
            Publisher = "CRONUS",
            Version = "1.0.0.0",
            CreatedAt = DateTime.UtcNow,
            DependencyCount = 0,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();

        const string content = "table 50100 \"Loyalty Card\"\n{\n}\n";
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.OeFileContents.Add(new OeFileContent
        {
            ContentHash = hash,
            Content = content,
            ContentLength = content.Length,
            LineCount = 3,
        });
        var file = new OeModuleFile
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            Path = "src/LoyaltyCard.Table.al",
            ContentHash = hash,
            LineCount = 3,
        };
        ctx.OeModuleFiles.Add(file);
        await ctx.SaveChangesAsync();

        var obj = new OeModuleObject
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            Kind = "table",
            ObjectId = 50100,
            Name = "Loyalty Card",
            LineNumber = 1,
            SourceFileId = file.Id,
        };
        ctx.OeModuleObjects.Add(obj);
        await ctx.SaveChangesAsync();

        return new SeededRelease(release.Id, module.Id, file.Id, obj.Id);
    }

    private sealed record SeededRelease(int ReleaseId, long ModuleId, long FileId, long ObjectId);

    /// <summary>Links a release to a project the way a pipeline build does.</summary>
    private async Task LinkByBuildAsync(int projectId, int releaseId)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = "Production",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipeline.Id,
            ReleaseId = releaseId,
            Status = ProjectBuildStatus.Ready,
            StartedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Links a release to a project the other way: through the import job that produced it.</summary>
    private async Task LinkByImportJobAsync(int projectId, int releaseId)
    {
        await using var ctx = _db.NewContext();
        ctx.OeImportJobs.Add(new OeImportJob
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = releaseId,
            ProjectId = projectId,
            Kind = "project_build",
            Status = "done",
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    // ── IsReleaseVisibleAsync ───────────────────────────────────────────

    public static TheoryData<ProjectVisibility> AllLevels => new()
    {
        ProjectVisibility.Public, ProjectVisibility.ReadOnly, ProjectVisibility.Private,
    };

    public enum Linkage { Build, ImportJob }

    public static TheoryData<Linkage> BothLinkages => new() { Linkage.Build, Linkage.ImportJob };

    private async Task LinkAsync(Linkage linkage, int projectId, int releaseId)
    {
        if (linkage == Linkage.Build) await LinkByBuildAsync(projectId, releaseId);
        else await LinkByImportJobAsync(projectId, releaseId);
    }

    [Theory]
    [MemberData(nameof(BothLinkages))]
    public async Task A_private_projects_release_is_hidden_from_a_non_member_either_way_it_is_linked(Linkage linkage)
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var release = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkAsync(linkage, projectId, release.ReleaseId);

        (await IsVisibleAsAsync(release.ReleaseId, OutsiderUserId)).Should().BeFalse();
        (await IsVisibleAsAsync(release.ReleaseId, MemberUserId)).Should().BeTrue("they're on the assigned team");
        (await IsVisibleAsAsync(release.ReleaseId, OwnerUserId)).Should().BeTrue("they own the project");
        (await IsVisibleAsAsync(release.ReleaseId, AdminUserId)).Should().BeTrue("an org Admin bypasses visibility");
        (await IsVisibleAsAsync(release.ReleaseId, OutsiderUserId, siteAdmin: true))
            .Should().BeTrue("a SiteAdmin bypasses visibility");
    }

    [Theory]
    [MemberData(nameof(AllLevels))]
    public async Task Only_private_hides_a_linked_release(ProjectVisibility visibility)
    {
        var (projectId, _) = await SeedProjectAsync(visibility);
        var release = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, release.ReleaseId);

        (await IsVisibleAsAsync(release.ReleaseId, OutsiderUserId))
            .Should().Be(visibility != ProjectVisibility.Private);
    }

    [Fact]
    public async Task An_unlinked_release_stays_visible_to_everyone_in_the_org()
    {
        // A Microsoft DVD import belongs to no project — nothing to hide it behind.
        await SeedProjectAsync(ProjectVisibility.Private);
        var release = await SeedReleaseAsync("BC 26.0", kind: "first_party");

        (await IsVisibleAsAsync(release.ReleaseId, OutsiderUserId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_release_of_a_second_public_project_is_unaffected_by_a_private_one()
    {
        var (privateId, _) = await SeedProjectAsync(ProjectVisibility.Private, "CRONUS Denmark");
        var (publicId, _) = await SeedProjectAsync(ProjectVisibility.Public, "CRONUS Norway");
        var hidden = await SeedReleaseAsync("Denmark on BC 26.0");
        var open = await SeedReleaseAsync("Norway on BC 26.0");
        await LinkByBuildAsync(privateId, hidden.ReleaseId);
        await LinkByBuildAsync(publicId, open.ReleaseId);

        (await IsVisibleAsAsync(hidden.ReleaseId, OutsiderUserId)).Should().BeFalse();
        (await IsVisibleAsAsync(open.ReleaseId, OutsiderUserId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_release_with_no_signed_in_user_is_governed_by_the_link_alone()
    {
        // A background worker under an ambient org scope has no grants, so a
        // Private project's release is hidden from it — and must not throw.
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var release = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, release.ReleaseId);

        (await IsVisibleAsAsync(release.ReleaseId, null)).Should().BeFalse();
    }

    private async Task<bool> IsVisibleAsAsync(int releaseId, int? userId, bool siteAdmin = false)
    {
        ActAs(userId, siteAdmin);
        await using var ctx = _db.NewContext();
        return await Access(ctx).IsReleaseVisibleAsync(releaseId);
    }

    // ── The read surfaces ───────────────────────────────────────────────

    /// <summary>
    /// Every release-keyed read surface, run as a non-member and as a member of
    /// the assigned team. The two columns of the matrix that matter: the fence
    /// holds for the first and lets the second straight through. Anything that
    /// answers the same for both is either ungated or over-gated.
    /// </summary>
    [Fact]
    public async Task Every_release_keyed_read_hides_a_private_projects_release_from_a_non_member()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);
        var open = await SeedReleaseAsync("BC 26.0", kind: "first_party");

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Explorer(ctx).GetReleaseAsync(seeded.ReleaseId)).Should().BeNull();
            (await Explorer(ctx).ListModulesAsync(seeded.ReleaseId, new ModuleListFilter())).Should().BeEmpty();
            (await Explorer(ctx).ListModuleSummariesAsync(seeded.ReleaseId)).Should().BeEmpty();
            (await Explorer(ctx).ListObjectsAsync(seeded.ModuleId, new ObjectListFilter(), 0, 50)).Rows.Should().BeEmpty();
            (await Explorer(ctx).GetObjectAsync(seeded.ObjectId)).Should().BeNull();
            (await Explorer(ctx).GetObjectOutlineAsync(seeded.ReleaseId, "table", "Loyalty Card")).Should().BeNull();

            (await Search(ctx).SearchObjectsInReleaseAsync(
                seeded.ReleaseId, new ObjectListFilter(Search: "Loyalty"))).Should().BeEmpty();
            (await Search(ctx).ListObjectKindsInReleaseAsync(seeded.ReleaseId)).Should().BeEmpty();

            (await Viewer(ctx).GetFileAsync(seeded.FileId)).Should().BeNull();
            (await Viewer(ctx).GetFileHeaderAsync(seeded.FileId)).Should().BeNull();
            (await Tree(ctx).ListModuleFilesAsync(seeded.ModuleId)).Should().BeEmpty();
            (await Tree(ctx).SearchTreeAsync(seeded.ReleaseId, "Loyalty")).Should().BeEmpty();

            // Both sides of a comparison, in both directions: knowing one side
            // must not buy you the other's contents.
            (await Comparison(ctx).CompareReleaseObjectsAsync(open.ReleaseId, seeded.ReleaseId)).Rows.Should().BeEmpty();
            (await Comparison(ctx).CompareReleaseObjectsAsync(seeded.ReleaseId, open.ReleaseId)).Rows.Should().BeEmpty();
            (await Comparison(ctx).CompareReleasesAsync(open.ReleaseId, seeded.ReleaseId)).Should().BeNull();
            (await Comparison(ctx).FindObjectFileInReleaseAsync(seeded.ReleaseId, "table", 50100, "Loyalty Card"))
                .Should().BeNull();

            (await Explorer(ctx).ListLatestPipelineBuildReleasesAsync())
                .Should().NotContain(r => r.Id == seeded.ReleaseId);
        }
    }

    [Fact]
    public async Task Every_release_keyed_read_answers_a_member_of_the_assigned_team()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);
        var open = await SeedReleaseAsync("BC 26.0", kind: "first_party");

        // The same reads, in the same order, as the non-member test above: each empty
        // answer there is only proof of the fence if the same call answers here.
        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Explorer(ctx).GetReleaseAsync(seeded.ReleaseId)).Should().NotBeNull();
            (await Explorer(ctx).ListModulesAsync(seeded.ReleaseId, new ModuleListFilter())).Should().ContainSingle();
            (await Explorer(ctx).ListModuleSummariesAsync(seeded.ReleaseId)).Should().ContainSingle();
            (await Explorer(ctx).ListObjectsAsync(seeded.ModuleId, new ObjectListFilter(), 0, 50)).Rows.Should().ContainSingle();
            (await Explorer(ctx).GetObjectAsync(seeded.ObjectId)).Should().NotBeNull();
            (await Explorer(ctx).GetObjectOutlineAsync(seeded.ReleaseId, "table", "Loyalty Card")).Should().NotBeNull();

            (await Search(ctx).SearchObjectsInReleaseAsync(
                seeded.ReleaseId, new ObjectListFilter(Search: "Loyalty"))).Should().ContainSingle();
            (await Search(ctx).ListObjectKindsInReleaseAsync(seeded.ReleaseId)).Should().NotBeEmpty();

            (await Viewer(ctx).GetFileAsync(seeded.FileId)).Should().NotBeNull();
            (await Viewer(ctx).GetFileHeaderAsync(seeded.FileId)).Should().NotBeNull();
            (await Tree(ctx).ListModuleFilesAsync(seeded.ModuleId)).Should().NotBeEmpty();
            (await Tree(ctx).SearchTreeAsync(seeded.ReleaseId, "Loyalty")).Should().NotBeEmpty();

            (await Comparison(ctx).CompareReleaseObjectsAsync(open.ReleaseId, seeded.ReleaseId)).Rows.Should().NotBeEmpty();
            (await Comparison(ctx).CompareReleaseObjectsAsync(seeded.ReleaseId, open.ReleaseId)).Rows.Should().NotBeEmpty();
            (await Comparison(ctx).CompareReleasesAsync(open.ReleaseId, seeded.ReleaseId)).Should().NotBeNull();
            (await Comparison(ctx).FindObjectFileInReleaseAsync(seeded.ReleaseId, "table", 50100, "Loyalty Card"))
                .Should().NotBeNull();

            (await Explorer(ctx).ListLatestPipelineBuildReleasesAsync())
                .Should().ContainSingle(r => r.Id == seeded.ReleaseId);
        }
    }

    [Fact]
    public async Task A_private_builds_compare_candidates_answer_only_its_teams_members()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);
        var sibling = await SeedReleaseAsync("CRONUS on BC 26.1");
        await using (var ctx = _db.NewContext())
        {
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                ReleaseId = sibling.ReleaseId,
                Status = ProjectBuildStatus.Ready,
                StartedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Explorer(ctx).ListBuildCompareCandidatesAsync(seeded.ReleaseId)).Should().BeEmpty();
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Explorer(ctx).ListBuildCompareCandidatesAsync(seeded.ReleaseId))
                .Should().ContainSingle(r => r.Id == sibling.ReleaseId);
        }
    }

    [Theory]
    [MemberData(nameof(AllLevels))]
    public async Task The_releases_browser_list_follows_the_same_rule_as_the_single_id_check(ProjectVisibility visibility)
    {
        var (projectId, _) = await SeedProjectAsync(visibility);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);

        foreach (var (userId, siteAdmin, expected) in new (int?, bool, bool)[]
        {
            (OutsiderUserId, false, visibility != ProjectVisibility.Private),
            (MemberUserId, false, true),
            (AdminUserId, false, true),
            (OutsiderUserId, true, true),
        })
        {
            ActAs(userId, siteAdmin);
            await using var ctx = _db.NewContext();
            var rows = await Explorer(ctx).ListLatestPipelineBuildReleasesAsync();
            rows.Any(r => r.Id == seeded.ReleaseId).Should().Be(
                expected, $"user {userId} (siteAdmin={siteAdmin}) on a {visibility} project");
        }
    }

    /// <summary>
    /// A find-references session mints an empty result set for a hidden release —
    /// but the session's label carries the object's name, which is itself a fact
    /// about the project, so no session is minted at all.
    /// </summary>
    [Fact]
    public async Task No_reference_session_is_minted_on_a_hidden_release()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Sessions(ctx).CreateFromSymbolAsync(seeded.ObjectId, "outsider")).Should().BeNull();
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Sessions(ctx).CreateFromSymbolAsync(seeded.ObjectId, "member")).Should().NotBeNull();
        }
    }

    /// <summary>
    /// Seeding the session at a visible release (<c>?from=</c>) must not unlock a
    /// target that lives in a hidden one: the label names the target's kind,
    /// number, name and member signature (#1227). All three lookups by target id
    /// are covered.
    /// </summary>
    [Fact]
    public async Task No_reference_session_is_minted_for_a_hidden_target_seeded_from_a_visible_release()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var hidden = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, hidden.ReleaseId);
        var visible = await SeedReleaseAsync("BC 26.0 (DK)", kind: "first_party");
        long symbolId;
        await using (var ctx = _db.NewContext())
        {
            var symbol = new OeModuleSymbol
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = hidden.ModuleId,
                ObjectId = hidden.ObjectId,
                Kind = "procedure",
                Name = "GrantPoints",
                Signature = "(Points: Integer)",
            };
            ctx.OeModuleSymbols.Add(symbol);
            await ctx.SaveChangesAsync();
            symbolId = symbol.Id;
        }

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            var sessions = Sessions(ctx);
            (await sessions.CreateFromSymbolAsync(hidden.ObjectId, "outsider", visible.ReleaseId)).Should().BeNull();
            (await sessions.CreateSystemReferencesFromObjectAsync(hidden.ObjectId, "outsider", visible.ReleaseId)).Should().BeNull();
            (await sessions.CreateFromMemberSymbolAsync(symbolId, "outsider", visible.ReleaseId)).Should().BeNull();
            // The visible release's own objects still work from there.
            (await sessions.CreateFromSymbolAsync(visible.ObjectId, "outsider", visible.ReleaseId)).Should().NotBeNull();
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Sessions(ctx).CreateFromMemberSymbolAsync(symbolId, "member", visible.ReleaseId)).Should().NotBeNull();
        }
    }

    /// <summary>
    /// OEX-6: the two built-in calls paths #1531 added - a right-click in the
    /// code, and the method picker narrowing a list - refuse a Private
    /// solution's release the same way the paths above do. A click in a hidden
    /// file, or in a visible file viewed from a hidden release, mints nothing;
    /// and a list a member made stops answering once they can no longer see it.
    /// </summary>
    [Fact]
    public async Task Built_in_calls_from_a_click_or_the_method_picker_refuse_a_hidden_release()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var hidden = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, hidden.ReleaseId);
        var visible = await SeedReleaseAsync("BC 26.0 (DK)", kind: "first_party");
        // The object name in `table 50100 "Loyalty Card"`, line 1.
        const int line = 1, column = 15;

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            var sessions = Sessions(ctx);
            (await sessions.CreateSystemReferencesAtPositionAsync(hidden.FileId, line, column, "outsider"))
                .Should().BeNull("the file is in a release the outsider cannot see");
            (await sessions.CreateSystemReferencesAtPositionAsync(visible.FileId, line, column, "outsider", hidden.ReleaseId))
                .Should().BeNull("the click is viewed from a release the outsider cannot see");
            (await sessions.CreateSystemReferencesAtPositionAsync(visible.FileId, line, column, "outsider"))
                .Should().NotBeNull("the visible release's own file still works");
        }

        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        string token;
        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var session = await Sessions(ctx, cache).CreateSystemReferencesAtPositionAsync(hidden.FileId, line, column, "member");
            session.Should().NotBeNull("a member may list the calls in their own solution");
            token = session!.Token;
            (await Sessions(ctx, cache).NarrowSystemReferencesAsync(token, "member", "Insert")).Should().NotBeNull();
        }

        // The member loses access: the same browser, the same list, a fresh request.
        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Sessions(ctx, cache).NarrowSystemReferencesAsync(token, "member", "Insert")).Should().BeNull();
        }
    }

    /// <summary>
    /// OEX-6, the variable branches of a right-click: a global variable (its
    /// type read from the variable row, with a visibility check of its own) and
    /// a local one (its type read from the declaration in the file). Both are
    /// clicked in a visible file; viewed from a hidden release they mint
    /// nothing, and viewed from the file's own release they work.
    /// </summary>
    [Fact]
    public async Task Built_in_calls_on_a_variable_refuse_a_hidden_release_it_is_viewed_from()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var hidden = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, hidden.ReleaseId);
        var visible = await SeedReleaseAsync("BC 26.0 (DK)", kind: "first_party");
        const string code =
            "codeunit 50101 \"Loyalty Mgt.\"\n" +
            "{\n" +
            "    var\n" +
            "        Card: Record \"Loyalty Card\";\n" +
            "    procedure Grant()\n" +
            "    begin\n" +
            "        Card.Insert();\n" +
            "        Points.Modify();\n" +
            "    end;\n" +
            "}\n";
        var fileId = await SeedCodeunitFileAsync(visible, code, globalVariable: "Points");
        const int localLine = 7, globalLine = 8, column = 10;

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            var sessions = Sessions(ctx);
            (await sessions.CreateSystemReferencesAtPositionAsync(fileId, globalLine, column, "outsider", hidden.ReleaseId))
                .Should().BeNull("the global variable is viewed from a release the outsider cannot see");
            (await sessions.CreateSystemReferencesAtPositionAsync(fileId, localLine, column, "outsider", hidden.ReleaseId))
                .Should().BeNull("the local variable is viewed from a release the outsider cannot see");
        }
        await using (var ctx = _db.NewContext())
        {
            var sessions = Sessions(ctx);
            (await sessions.CreateSystemReferencesAtPositionAsync(fileId, globalLine, column, "outsider"))
                .Should().NotBeNull("the global-variable branch resolves from the file's own release");
            (await sessions.CreateSystemReferencesAtPositionAsync(fileId, localLine, column, "outsider"))
                .Should().NotBeNull("the local-variable branch resolves from the file's own release");
        }
    }

    // A codeunit file in the seeded release's module, with one global variable
    // row of type Record "Loyalty Card" (the seeded table) when one is named.
    private async Task<long> SeedCodeunitFileAsync(SeededRelease release, string content, string globalVariable)
    {
        await using var ctx = _db.NewContext();
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.OeFileContents.Add(new OeFileContent
        {
            ContentHash = hash,
            Content = content,
            ContentLength = content.Length,
            LineCount = content.Split('\n').Length,
        });
        var file = new OeModuleFile
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = release.ModuleId,
            Path = "src/LoyaltyMgt.Codeunit.al",
            ContentHash = hash,
            LineCount = content.Split('\n').Length,
        };
        ctx.OeModuleFiles.Add(file);
        await ctx.SaveChangesAsync();
        var codeunit = new OeModuleObject
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = release.ModuleId,
            Kind = "codeunit",
            ObjectId = 50101,
            Name = "Loyalty Mgt.",
            LineNumber = 1,
            SourceFileId = file.Id,
        };
        ctx.OeModuleObjects.Add(codeunit);
        await ctx.SaveChangesAsync();
        var appId = await ctx.OeModules.Where(m => m.Id == release.ModuleId).Select(m => m.AppId).SingleAsync();
        ctx.OeModuleVariables.Add(new OeModuleVariable
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = release.ModuleId,
            ObjectId = codeunit.Id,
            Name = globalVariable,
            TypeKeyword = "Record",
            TypeName = "\"Loyalty Card\"",
            TargetAppId = appId,
            TargetObjectKind = "table",
            TargetObjectId = 50100,
            TargetObjectName = "Loyalty Card",
        });
        await ctx.SaveChangesAsync();
        return file.Id;
    }

    /// <summary>
    /// What <c>Endpoints/ArtifactEndpoints.cs</c> turns into its existing 404: the
    /// byte getters refuse a non-member with <see cref="ProjectAccessDeniedException"/>
    /// rather than streaming a Private project's compiled <c>.app</c>.
    /// </summary>
    [Fact]
    public async Task Artifact_bytes_are_refused_to_a_non_member_and_served_to_a_member()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        int buildId, artifactId;
        await using (var ctx = _db.NewContext())
        {
            var build = new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                Status = ProjectBuildStatus.Ready,
                StartedAt = DateTime.UtcNow,
            };
            ctx.OeProjectBuilds.Add(build);
            await ctx.SaveChangesAsync();
            buildId = build.Id;

            var artifact = new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectBuildId = buildId,
                FileName = "CRONUS.app",
                AppName = "CRONUS",
                AppVersion = "1.0.0.0",
                SizeBytes = 1,
                Content = new byte[] { 42 },
                CreatedAt = DateTime.UtcNow,
            };
            ctx.OeProjectBuildArtifacts.Add(artifact);
            ctx.OeProjectBuildLogs.Add(new OeProjectBuildLog
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId,
                Section = "Build", Content = "ok", Ordering = 0, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            artifactId = artifact.Id;
        }

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            var artifacts = new ArtifactService(ctx, Access(ctx));
            await ((Func<Task>)(() => artifacts.GetArtifactBytesAsync(buildId, artifactId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => artifacts.ListArtifactRowsAsync(buildId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
            await ((Func<Task>)(() => artifacts.GetRawLogAsync(buildId)))
                .Should().ThrowAsync<ProjectAccessDeniedException>();
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var artifacts = new ArtifactService(ctx, Access(ctx));
            (await artifacts.GetArtifactBytesAsync(buildId, artifactId)).Should().NotBeNull();
        }
    }

    // ── Source viewer, tree, compare picker, sessions (#1273) ───────────

    /// <summary>
    /// The source viewer's per-file reads (outline, declarations, go to
    /// definition, underlined tokens, hover card, find in file, dependencies)
    /// each check visibility on their own. A non-member gets the empty answer
    /// from every one of them; a member of the assigned team gets the data.
    /// </summary>
    [Fact]
    public async Task Source_viewer_reads_hide_a_private_projects_file_from_a_non_member()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);
        long symbolId;
        await using (var ctx = _db.NewContext())
        {
            var symbol = new OeModuleSymbol
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = seeded.ModuleId,
                ObjectId = seeded.ObjectId,
                Kind = "procedure",
                Name = "GrantPoints",
                Signature = "(Points: Integer)",
                LineNumber = 2,
            };
            ctx.OeModuleSymbols.Add(symbol);
            // The header names its own table, so the line carries one
            // underlined token for the resolvables read to return.
            ctx.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = TestDb.DefaultOrgId,
                ModuleId = seeded.ModuleId,
                SourceObjectId = seeded.ObjectId,
                TargetObjectKind = "table",
                TargetObjectId = 50100,
                TargetObjectName = "Loyalty Card",
                ReferenceKind = "property_object",
                LineNumber = 1,
            });
            await ctx.SaveChangesAsync();
            symbolId = symbol.Id;
        }

        // Line 1 is `table 50100 "Loyalty Card"`; column 14 is the L.
        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            var viewer = Viewer(ctx);
            (await viewer.GetFileOutlineAsync(seeded.FileId)).Should().BeEmpty();
            (await viewer.ListDeclarationsInFileAsync(seeded.FileId)).Should().BeEmpty();
            (await viewer.GoToDefinitionAsync(seeded.FileId, 1, 14)).Should().BeNull();
            (await viewer.ListResolvablesInFileAsync(seeded.FileId)).Should().BeEmpty();
            (await viewer.DescribeSymbolAsync(symbolId)).Should().BeNull();
            (await viewer.FindInFileAsync(seeded.FileId, 1, 1)).Should().BeNull();
            (await References(ctx).GetFileDependenciesAsync(seeded.FileId)).Should().BeNull();
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var viewer = Viewer(ctx);
            (await viewer.GetFileOutlineAsync(seeded.FileId)).Should().NotBeEmpty();
            (await viewer.ListDeclarationsInFileAsync(seeded.FileId)).Should().NotBeEmpty();
            (await viewer.GoToDefinitionAsync(seeded.FileId, 1, 14)).Should().NotBeNull();
            (await viewer.ListResolvablesInFileAsync(seeded.FileId)).Should().NotBeEmpty();
            (await viewer.DescribeSymbolAsync(symbolId)).Should().NotBeNull();
            (await viewer.FindInFileAsync(seeded.FileId, 1, 1)).Should().NotBeNull();
            (await References(ctx).GetFileDependenciesAsync(seeded.FileId)).Should().NotBeNull();
        }
    }

    /// <summary>
    /// The explorer tree's module-keyed reads: expanding a folder and arranging
    /// one app's files. Both are reachable with a module id off the URL.
    /// </summary>
    [Fact]
    public async Task Explorer_tree_reads_hide_a_private_projects_module_from_a_non_member()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, seeded.ReleaseId);

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Tree(ctx).GetTreeChildrenAsync(seeded.ModuleId, "src/")).Should().BeEmpty();
            (await Tree(ctx).ListModuleTreeAsync(seeded.ModuleId, ExplorerTreeService.TreeGrouping.Folder))
                .Should().BeEmpty();
            (await Tree(ctx).ListModuleTreeAsync(seeded.ModuleId, ExplorerTreeService.TreeGrouping.Kind))
                .Should().BeEmpty();
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Tree(ctx).GetTreeChildrenAsync(seeded.ModuleId, "src/"))
                .Should().Contain(n => n.FileId == seeded.FileId);
            (await Tree(ctx).ListModuleTreeAsync(seeded.ModuleId, ExplorerTreeService.TreeGrouping.Folder))
                .Should().Contain(n => n.FileId == seeded.FileId);
            (await Tree(ctx).ListModuleTreeAsync(seeded.ModuleId, ExplorerTreeService.TreeGrouping.Kind))
                .Should().Contain(n => n.FileId == seeded.FileId);
        }
    }

    /// <summary>
    /// The "Compare with release" picker lists every other release holding the
    /// same file. Without its visibility filter it would name a Private
    /// project's release, and hand out its file id, to anyone viewing the same
    /// file in a public release. (A pipeline build is offered only from another
    /// build of its own Solution since #1570, so the private release here is one
    /// imported under the project.)
    /// </summary>
    [Fact]
    public async Task The_compare_target_picker_offers_a_private_release_only_to_its_teams_members()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var open = await SeedReleaseAsync("BC 26.0", kind: "first_party");
        var hidden = await SeedReleaseAsync("CRONUS 1.0", kind: "third_party");
        await LinkByImportJobAsync(projectId, hidden.ReleaseId);
        await using (var ctx = _db.NewContext())
        {
            // Same app and path in both releases: the shape the picker matches on.
            var appId = await ctx.OeModules.Where(m => m.Id == open.ModuleId).Select(m => m.AppId).SingleAsync();
            await ctx.OeModules.Where(m => m.Id == hidden.ModuleId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.AppId, appId));
        }

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Comparison(ctx).GetCompareTargetsAsync(open.FileId))
                .Should().NotContain(t => t.ReleaseId == hidden.ReleaseId);
        }

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Comparison(ctx).GetCompareTargetsAsync(open.FileId))
                .Should().ContainSingle(t => t.ReleaseId == hidden.ReleaseId);
        }
    }

    /// <summary>
    /// A reference session lives in a shared cache keyed by a token that
    /// travels in the URL. Only the user who minted it may read it back, or a
    /// shared link would show one person's results to another.
    /// </summary>
    [Fact]
    public async Task A_reference_session_is_returned_only_to_the_user_who_minted_it()
    {
        var seeded = await SeedReleaseAsync("BC 26.0", kind: "first_party");
        ActAs(MemberUserId);
        await using var ctx = _db.NewContext();
        var sessions = Sessions(ctx);

        var session = await sessions.CreateFromSymbolAsync(seeded.ObjectId, "user-a");

        session.Should().NotBeNull();
        (await sessions.GetAsync(session!.Token, "user-b")).Should().BeNull();
        (await sessions.GetAsync(session.Token, "user-a")).Should().NotBeNull();
    }

    /// <summary>
    /// OEX2-3: a Find references list holds code lines. Once its owner can no
    /// longer see the release - removed from a Private solution's team - the
    /// link in their open tab stops showing it on the next load, rather than
    /// for as long as they keep reloading inside the sliding expiry.
    /// </summary>
    [Fact]
    public async Task A_reference_session_stops_answering_once_its_owner_loses_access_to_the_release()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var hidden = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, hidden.ReleaseId);
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        string token;
        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            var session = await Sessions(ctx, cache).CreateFromSymbolAsync(hidden.ObjectId, "member");
            session.Should().NotBeNull();
            token = session!.Token;
        }
        await using (var ctx = _db.NewContext())
        {
            (await Sessions(ctx, cache).GetAsync(token, "member")).Should().NotBeNull("the member can still see it");
        }

        // The same browser, the same link, after the member left the team.
        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Sessions(ctx, cache).GetAsync(token, "member")).Should().BeNull();
        }
    }


    // ── The admin writes (#1430) ────────────────────────────────────────

    /// <summary>Every write keyed by a release id that the authoring endpoints reach.</summary>
    public enum ReleaseWrite
    {
        SoftDelete, Restore, UpdateMetadata, HardDelete, Amend, TranslationFile, TranslationZip, Backfill,
    }

    public static TheoryData<ReleaseWrite, Linkage> AllWritesBothLinkages
    {
        get
        {
            var data = new TheoryData<ReleaseWrite, Linkage>();
            foreach (var write in Enum.GetValues<ReleaseWrite>())
            {
                data.Add(write, Linkage.Build);
                data.Add(write, Linkage.ImportJob);
            }
            return data;
        }
    }

    public static TheoryData<ReleaseWrite, int> AllWritesForAllowedUsers
    {
        get
        {
            var data = new TheoryData<ReleaseWrite, int>();
            foreach (var write in Enum.GetValues<ReleaseWrite>())
            {
                data.Add(write, MemberUserId);
                data.Add(write, AdminUserId);
            }
            return data;
        }
    }

    /// <summary>An id no release has, distinctive enough to find again inside an error message.</summary>
    private const int UnknownReleaseId = 987_654_321;

    private const string WriteLabel = "CRONUS on BC 26.0";

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseManagementService Management(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, Access(ctx), NullLogger<ReleaseManagementService>.Instance);

    private TranslationImportService Translations(AppDbContext ctx) =>
        new(ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, Access(ctx));

    private ReleaseImportService Importer(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), Translations(ctx),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, Access(ctx));

    /// <summary>
    /// A Private solution's release, linked by <paramref name="linkage"/>. Seeded
    /// already soft-deleted for <see cref="ReleaseWrite.Restore"/> and
    /// <see cref="ReleaseWrite.HardDelete"/>, which only act on a soft-deleted
    /// release, so each write has something to change.
    /// </summary>
    private static bool SeedsSoftDeleted(ReleaseWrite write) =>
        write is ReleaseWrite.Restore or ReleaseWrite.HardDelete;

    private async Task<SeededRelease> SeedPrivateReleaseAsync(ReleaseWrite write, Linkage linkage = Linkage.Build)
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var seeded = await SeedReleaseAsync(WriteLabel);
        await LinkAsync(linkage, projectId, seeded.ReleaseId);
        if (SeedsSoftDeleted(write))
        {
            await using var ctx = _db.NewContext();
            var release = await ctx.OeReleases.SingleAsync(r => r.Id == seeded.ReleaseId);
            release.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        return seeded;
    }

    /// <summary>
    /// Runs one write as the current user and returns the refusal it got, or null
    /// when it went through. The backfill pre-check answers "absent" with false,
    /// which the endpoint turns into a 404; that comes back as a <c>Status</c> entry.
    /// </summary>
    private async Task<Dictionary<string, string>?> RunWriteAsync(ReleaseWrite write, int releaseId, long moduleId)
    {
        await using var ctx = _db.NewContext();
        try
        {
            switch (write)
            {
                case ReleaseWrite.SoftDelete: await Management(ctx).SoftDeleteAsync(releaseId); break;
                case ReleaseWrite.Restore: await Management(ctx).RestoreAsync(releaseId); break;
                case ReleaseWrite.UpdateMetadata: await Management(ctx).UpdateMetadataAsync(releaseId, "Renamed A/S", "Renamed"); break;
                case ReleaseWrite.HardDelete: await Management(ctx).HardDeleteAsync(releaseId, WriteLabel); break;
                case ReleaseWrite.Amend:
                {
                    await using var app = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
                    await Importer(ctx).AmendReleaseAsync(releaseId, new[] { new AppFileUpload("Microsoft_DK_Core.app", app, null) });
                    break;
                }
                case ReleaseWrite.TranslationFile:
                {
                    await using var xlf = File.OpenRead(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"));
                    await Translations(ctx).ImportSingleAsync(releaseId, moduleId, xlf, "OIOUBL.daDK.xlf");
                    break;
                }
                case ReleaseWrite.TranslationZip:
                {
                    using var zip = new MemoryStream();
                    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        archive.CreateEntryFromFile(Path.Combine(FixtureRoot, "OIOUBL.daDK.xlf"), "OIOUBL.daDK.xlf");
                    }
                    zip.Position = 0;
                    await Translations(ctx).ImportZipAsync(releaseId, zip);
                    break;
                }
                case ReleaseWrite.Backfill:
                    if (!await Management(ctx).EnsureBackfillableAsync(releaseId))
                    {
                        return new Dictionary<string, string> { ["Status"] = "404" };
                    }
                    break;
            }
            return null;
        }
        catch (PlanValidationException ex)
        {
            return new Dictionary<string, string>(ex.Errors);
        }
    }

    [Theory]
    [MemberData(nameof(AllWritesBothLinkages))]
    public async Task Every_release_write_answers_a_non_member_exactly_as_it_answers_an_unknown_id(
        ReleaseWrite write, Linkage linkage)
    {
        var seeded = await SeedPrivateReleaseAsync(write, linkage);

        ActAs(OutsiderUserId);
        var unknown = await RunWriteAsync(write, UnknownReleaseId, seeded.ModuleId);
        var hidden = await RunWriteAsync(write, seeded.ReleaseId, seeded.ModuleId);

        // Word for word, with only the id swapped, so the refusal confirms nothing.
        unknown.Should().NotBeNull();
        hidden.Should().Equal(unknown!.ToDictionary(
            e => e.Key, e => e.Value.Replace(UnknownReleaseId.ToString(), seeded.ReleaseId.ToString())));

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleOrDefaultAsync(r => r.Id == seeded.ReleaseId);
        release.Should().NotBeNull("a refused hard delete removes nothing");
        (release!.DeletedAt is not null).Should().Be(SeedsSoftDeleted(write), "the deleted flag is as seeded");
        release.Publisher.Should().BeNull("a refused metadata edit changes nothing");
        (await read.OeModules.AsNoTracking().CountAsync(m => m.ReleaseId == seeded.ReleaseId))
            .Should().Be(1, "a refused amend adds no module");
        (await read.OeModuleTranslations.AsNoTracking().AnyAsync(t => t.ModuleId == seeded.ModuleId))
            .Should().BeFalse("a refused translation upload stores nothing");
    }

    [Theory]
    [MemberData(nameof(AllWritesForAllowedUsers))]
    public async Task Every_release_write_goes_through_for_a_team_member_and_an_org_Admin(ReleaseWrite write, int userId)
    {
        var seeded = await SeedPrivateReleaseAsync(write);

        ActAs(userId);
        (await RunWriteAsync(write, seeded.ReleaseId, seeded.ModuleId)).Should().BeNull();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleOrDefaultAsync(r => r.Id == seeded.ReleaseId);
        switch (write)
        {
            case ReleaseWrite.SoftDelete: release!.DeletedAt.Should().NotBeNull(); break;
            case ReleaseWrite.Restore: release!.DeletedAt.Should().BeNull(); break;
            case ReleaseWrite.UpdateMetadata: release!.Publisher.Should().Be("Renamed A/S"); break;
            case ReleaseWrite.HardDelete: release.Should().BeNull(); break;
            case ReleaseWrite.Amend:
                (await read.OeModules.AsNoTracking().CountAsync(m => m.ReleaseId == seeded.ReleaseId)).Should().Be(2);
                break;
            case ReleaseWrite.TranslationFile:
                (await read.OeModuleTranslations.AsNoTracking().AnyAsync(t => t.ModuleId == seeded.ModuleId)).Should().BeTrue();
                break;
            // The ZIP's XLIFF names a module this release doesn't have, so it is
            // skipped; getting a summary rather than a refusal is the point.
            case ReleaseWrite.TranslationZip: break;
            case ReleaseWrite.Backfill: break;
        }
    }

    // ── Translation uploads and the org-wide memory (#1613) ─────────────

    /// <summary>A sentence from the OIOUBL fixture XLIFF, which it translates into Danish.</summary>
    private const string FixtureSource = "The total Line Discount Amount cannot be negative.";

    private TranslationMemoryService Memory(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TranslationMemoryService>.Instance);

    /// <summary>
    /// Names the seeded module after the fixture XLIFF's <c>original</c>, so the
    /// ZIP upload matches it rather than skipping the file.
    /// </summary>
    private async Task NameModuleForFixtureAsync(long moduleId)
    {
        await using var ctx = _db.NewContext();
        var module = await ctx.OeModules.SingleAsync(m => m.Id == moduleId);
        module.Name = "OIOUBL";
        await ctx.SaveChangesAsync();
    }

    public static TheoryData<ReleaseWrite> TranslationUploads =>
        new() { ReleaseWrite.TranslationFile, ReleaseWrite.TranslationZip };

    [Theory]
    [MemberData(nameof(TranslationUploads))]
    public async Task A_translation_upload_onto_a_Private_solution_build_does_not_teach_the_org_memory(ReleaseWrite upload)
    {
        var seeded = await SeedPrivateReleaseAsync(upload);
        await NameModuleForFixtureAsync(seeded.ModuleId);

        ActAs(MemberUserId);
        (await RunWriteAsync(upload, seeded.ReleaseId, seeded.ModuleId)).Should().BeNull();

        await using (var read = _db.NewContext())
        {
            (await read.OeModuleTranslations.AsNoTracking().CountAsync(t => t.ModuleId == seeded.ModuleId))
                .Should().Be(436, "the release still gets the uploaded translations");
            (await read.TranslationMemory.AsNoTracking().AnyAsync())
                .Should().BeFalse("the memory has no way to keep the pairs inside the solution");
        }

        ActAs(OutsiderUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Memory(ctx).SuggestAsync(FixtureSource, "en-US", "da-DK"))
                .Should().BeEmpty("someone outside the solution's team must not get its strings");
            (await Memory(ctx).SearchAsync(new MemorySearchQuery(TargetLanguage: "da-DK"))).Items
                .Should().BeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(TranslationUploads))]
    public async Task A_translation_upload_onto_a_Microsoft_release_still_teaches_the_org_memory(ReleaseWrite upload)
    {
        var seeded = await SeedReleaseAsync("BC 26.0", kind: "first_party");
        await NameModuleForFixtureAsync(seeded.ModuleId);

        ActAs(AdminUserId);
        (await RunWriteAsync(upload, seeded.ReleaseId, seeded.ModuleId)).Should().BeNull();

        ActAs(OutsiderUserId);
        await using var ctx = _db.NewContext();
        (await Memory(ctx).SuggestAsync(FixtureSource, "en-US", "da-DK"))
            .Should().Contain(h => h.TargetText.Contains("linjerabat"),
                "a Microsoft release's translations are for the whole organisation");
    }

    [Fact]
    public async Task The_bulk_backfill_queues_only_the_releases_the_caller_can_see()
    {
        var (privateId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var hidden = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(privateId, hidden.ReleaseId);
        var open = await SeedReleaseAsync("BC 26.0", kind: "first_party");

        async Task<List<int>> ListAsAsync(int userId)
        {
            ActAs(userId);
            await using var ctx = _db.NewContext();
            return await Management(ctx).ListBackfillableReleaseIdsAsync();
        }

        (await ListAsAsync(OutsiderUserId)).Should().Equal(open.ReleaseId);
        (await ListAsAsync(MemberUserId)).Should().Equal(hidden.ReleaseId, open.ReleaseId);
        (await ListAsAsync(AdminUserId)).Should().Equal(hidden.ReleaseId, open.ReleaseId);
    }

    // ── Retry (#1484) ───────────────────────────────────────────────────

    private const string RetryArtifactUrl = "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/27.1.0.0/dk";

    private ReleaseImportRequestService Retries(AppDbContext ctx, ReleaseImportQueue queue)
    {
        var importer = Importer(ctx);
        var management = Management(ctx);
        var persistedJobs = new PersistedImportJobs(ctx, TimeProvider.System);
        var projectBuilds = new ProjectBuildImporter(
            importer, management, new ProjectBuildQueue(), persistedJobs, ctx, _db.OrgContext, Access(ctx),
            new CloneCredentialResolver(
                new ALDevToolbox.Services.Account.UserRepositoryTokenService(
                    ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Account.UserRepositoryTokenService>.Instance,
                    _db.DataProtectionProvider),
                _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, new ALDevToolbox.Tests.GitHub.FakeGitHubApi())),
                _db.OrgContext, NullLogger<CloneCredentialResolver>.Instance),
            TimeProvider.System, NullLogger<ProjectBuildImporter>.Instance);
        var downloads = new DvdDownloadService(
            new NoDownloads(), _db.NewSystemSettingsService(ctx), NullLogger<DvdDownloadService>.Instance);
        return new ReleaseImportRequestService(
            importer, management, downloads, queue, persistedJobs, _db.OrgContext, projectBuilds, Access(ctx),
            new Microsoft.Extensions.Hosting.Internal.ApplicationLifetime(NullLogger<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>.Instance),
            NullLogger<ReleaseImportRequestService>.Instance);
    }

    /// <summary>How the release a retry acts on was last imported.</summary>
    public enum RetryOrigin
    {
        /// <summary>A Microsoft artifact import, retried with nothing to upload: the saved source is reused.</summary>
        Artifact,

        /// <summary>The same, retried with a C/AL file: the release is reopened and wiped.</summary>
        ArtifactWithFile,

        /// <summary>A solution's pipeline build, which a retry rebuilds.</summary>
        ProjectBuild,
    }

    /// <summary>
    /// A failed Private-solution release. For the artifact origins it is linked by
    /// its pipeline build and its latest import job is a Microsoft artifact import:
    /// the retry path that does not go through the build's manage check. For a
    /// project build, its latest job is the build's own import job.
    /// </summary>
    private async Task<SeededRelease> SeedFailedPrivateReleaseAsync(RetryOrigin origin = RetryOrigin.Artifact)
    {
        var seeded = await SeedPrivateReleaseAsync(ReleaseWrite.UpdateMetadata,
            origin == RetryOrigin.ProjectBuild ? Linkage.ImportJob : Linkage.Build);
        await using var ctx = _db.NewContext();
        var release = await ctx.OeReleases.SingleAsync(r => r.Id == seeded.ReleaseId);
        release.Status = "failed";
        await ctx.SaveChangesAsync();
        if (origin != RetryOrigin.ProjectBuild)
        {
            await new PersistedImportJobs(ctx, TimeProvider.System).CreateAsync(release.Id,
                new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, null, false, false),
                new ReleaseImportSource.BcArtifact(RetryArtifactUrl), storeSymbolReference: false);
        }
        return seeded;
    }

    private static ReleaseRetrySubmission RetryWith(bool calFile) => new(
        DvdUrl: string.Empty, CalEncoding: "850", StoreSymbolReference: false, FolderZip: null,
        CalTxtFile: calFile ? new UploadedFile("CRONUS.txt", 3, () => new MemoryStream("OBJ"u8.ToArray())) : null);

    private async Task<(Dictionary<string, string>? Error, ReleaseImportOutcome? Outcome)> RetryAsAsync(
        int releaseId, ReleaseRetrySubmission submission, ReleaseImportQueue queue)
    {
        await using var ctx = _db.NewContext();
        try
        {
            return (null, await Retries(ctx, queue).RetryAsync(releaseId, submission));
        }
        catch (PlanValidationException ex)
        {
            return (new Dictionary<string, string>(ex.Errors), null);
        }
        catch (ProjectAccessDeniedException)
        {
            // What a rebuild answers someone who cannot manage the solution:
            // an answer an unknown id never gets.
            return (new Dictionary<string, string> { ["Access"] = "denied" }, null);
        }
    }

    [Theory]
    [InlineData(RetryOrigin.Artifact)]
    [InlineData(RetryOrigin.ArtifactWithFile)]
    [InlineData(RetryOrigin.ProjectBuild)]
    public async Task Retry_answers_a_non_member_exactly_as_it_answers_an_unknown_id(RetryOrigin origin)
    {
        // Without a file the saved artifact source would be reused; with one the
        // release would be reopened and wiped; a build would be refused by the
        // manage check, which says the release is there. None of that may happen
        // for a release the caller cannot see.
        var seeded = await SeedFailedPrivateReleaseAsync(origin);
        var queue = new ReleaseImportQueue();
        var calFile = origin == RetryOrigin.ArtifactWithFile;

        ActAs(OutsiderUserId);
        var unknown = await RetryAsAsync(UnknownReleaseId, RetryWith(calFile), queue);
        var hidden = await RetryAsAsync(seeded.ReleaseId, RetryWith(calFile), queue);

        unknown.Error.Should().NotBeNull();
        hidden.Error.Should().Equal(unknown.Error!.ToDictionary(
            e => e.Key, e => e.Value.Replace(UnknownReleaseId.ToString(), seeded.ReleaseId.ToString())));
        queue.Reader.TryRead(out _).Should().BeFalse("a refused retry queues nothing");
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == seeded.ReleaseId)).Status.Should().Be("failed");
        (await read.OeModules.AsNoTracking().CountAsync(m => m.ReleaseId == seeded.ReleaseId))
            .Should().Be(1, "a refused retry wipes nothing");
    }

    [Theory]
    [InlineData(MemberUserId)]
    [InlineData(AdminUserId)]
    public async Task Retry_goes_through_for_a_team_member_and_an_org_Admin(int userId)
    {
        var seeded = await SeedFailedPrivateReleaseAsync();
        var queue = new ReleaseImportQueue();

        ActAs(userId);
        var result = await RetryAsAsync(seeded.ReleaseId, RetryWith(calFile: false), queue);

        result.Error.Should().BeNull();
        result.Outcome.Should().BeOfType<ReleaseImportOutcome.Queued>();
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.Source.Should().BeOfType<ReleaseImportSource.BcArtifact>()
            .Which.ApplicationUrl.Should().Be(RetryArtifactUrl);
    }

    // ── Manual-symbols recovery (#1484) ─────────────────────────────────

    [Fact]
    public async Task Symbol_recovery_answers_a_non_member_exactly_as_it_answers_an_unknown_id()
    {
        var seeded = await SeedFailedPrivateReleaseAsync(RetryOrigin.ProjectBuild);

        async Task<int?> FindAsAsync(int userId, int releaseId)
        {
            ActAs(userId);
            await using var ctx = _db.NewContext();
            return await Retries(ctx, new ReleaseImportQueue()).FindSymbolRecoveryProjectAsync(releaseId);
        }

        (await FindAsAsync(OutsiderUserId, UnknownReleaseId)).Should().BeNull();
        (await FindAsAsync(OutsiderUserId, seeded.ReleaseId))
            .Should().BeNull("a hidden build reads as no solution build at all");
        (await FindAsAsync(MemberUserId, seeded.ReleaseId)).Should().NotBeNull();
        (await FindAsAsync(AdminUserId, seeded.ReleaseId)).Should().NotBeNull();
    }

    // A retry here re-runs a saved artifact address, which is only queued, never fetched.
    private sealed class NoDownloads : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("A retry must not download anything.");
    }

    // Pass a cache to share one across requests, as the app's singleton memory
    // cache does; each call is otherwise one request's service.
    private ReferenceSessionService Sessions(AppDbContext ctx,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null) =>
        new(cache ?? new Microsoft.Extensions.Caching.Memory.MemoryCache(
                Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            References(ctx), ctx, new ReferenceResolver(ctx), Access(ctx),
            NullLogger<ReferenceSessionService>.Instance);
}
