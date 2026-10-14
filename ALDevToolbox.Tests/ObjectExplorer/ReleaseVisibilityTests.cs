using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;
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

        ActAs(MemberUserId);
        await using (var ctx = _db.NewContext())
        {
            (await Explorer(ctx).GetReleaseAsync(seeded.ReleaseId)).Should().NotBeNull();
            (await Explorer(ctx).ListModulesAsync(seeded.ReleaseId, new ModuleListFilter())).Should().ContainSingle();
            (await Explorer(ctx).GetObjectAsync(seeded.ObjectId)).Should().NotBeNull();
            (await Search(ctx).SearchObjectsInReleaseAsync(
                seeded.ReleaseId, new ObjectListFilter(Search: "Loyalty"))).Should().ContainSingle();
            (await Viewer(ctx).GetFileAsync(seeded.FileId)).Should().NotBeNull();
            (await Comparison(ctx).CompareReleaseObjectsAsync(open.ReleaseId, seeded.ReleaseId)).Rows.Should().NotBeEmpty();
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
    /// file in a public release.
    /// </summary>
    [Fact]
    public async Task The_compare_target_picker_offers_a_private_release_only_to_its_teams_members()
    {
        var (projectId, _) = await SeedProjectAsync(ProjectVisibility.Private);
        var open = await SeedReleaseAsync("BC 26.0", kind: "first_party");
        var hidden = await SeedReleaseAsync("CRONUS on BC 26.0");
        await LinkByBuildAsync(projectId, hidden.ReleaseId);
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
        sessions.Get(session!.Token, "user-b").Should().BeNull();
        sessions.Get(session.Token, "user-a").Should().NotBeNull();
    }

    private ReferenceSessionService Sessions(AppDbContext ctx) =>
        new(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                Microsoft.Extensions.Options.Options.Create(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            References(ctx), ctx, new ReferenceResolver(ctx), Access(ctx),
            NullLogger<ReferenceSessionService>.Instance);
}
