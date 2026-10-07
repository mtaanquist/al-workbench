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
/// CRUD + validation contract for <see cref="ProjectService"/>: a project and
/// its repositories round-trip, validation rejects blank names, duplicate names,
/// and provider/URL mismatches with field-keyed errors, update replaces the repo
/// set, soft-delete hides the row, the creator is stamped as owner, and the org
/// query filter keeps projects from other orgs invisible.
/// </summary>
public sealed class ProjectServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    /// <summary>The acting user — seeded so the owner FK holds and the creator owns what they create.</summary>
    private const int OwnerUserId = 9100;

    public ProjectServiceTests()
    {
        // Run the tests as a real, signed-in user so CreateProjectAsync stamps a
        // valid owner and the owner-or-admin gate on update/delete is satisfied.
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = OwnerUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "owner@example.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Shared discovery queue so a test can assert that a repo change warmed the cache (enqueued a discovery).</summary>
    private readonly ProjectDiscoveryQueue _discoveryQueue = new();

    /// <summary>A <see cref="ProjectService"/> wired with the shared org context, its access gate, and the discovery surface.</summary>
    private ProjectService Svc(ALDevToolbox.Data.AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var discovery = new ProjectDiscoveryService(
            ctx, _db.OrgContext, access, _discoveryQueue, NullLogger<ProjectDiscoveryService>.Instance);
        return new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance);
    }

    private static ProjectInput NewInput(
        string name = "Acme",
        string? country = "dk",
        string? shortName = null,
        params ProjectRepositoryInput[] repos)
        => new(name, shortName, country, repos.Length == 0
            ? new[] { new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core", "Core") }
            : repos);

    [Fact]
    public async Task Turning_on_automatic_update_pull_requests_makes_the_saver_the_person_they_are_opened_as()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S"));
        await ctx.OeProjects.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AutoUpdatePullRequestsBlocked, "stale"));

        await using (var save = _db.NewContext())
        {
            await Svc(save).UpdateProjectAsync(id, NewInput("CRONUS A/S") with { AutoUpdatePullRequests = true });
        }

        await using var read = _db.NewContext();
        var saved = await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == id);
        saved.AutoUpdatePullRequests.Should().BeTrue();
        saved.AutoUpdatePullRequestsByUserId.Should().Be(OwnerUserId);
        saved.AutoUpdatePullRequestsBlocked.Should().BeNull();

        // A save that does not mention it leaves it alone; turning it off forgets who.
        await Svc(read).UpdateProjectAsync(id, NewInput("CRONUS A/S"));
        (await _db.NewContext().OeProjects.AsNoTracking().SingleAsync(p => p.Id == id)).AutoUpdatePullRequests.Should().BeTrue();
        await Svc(_db.NewContext()).UpdateProjectAsync(id, NewInput("CRONUS A/S") with { AutoUpdatePullRequests = false });
        var off = await _db.NewContext().OeProjects.AsNoTracking().SingleAsync(p => p.Id == id);
        off.AutoUpdatePullRequests.Should().BeFalse();
        off.AutoUpdatePullRequestsByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Resuming_automatic_update_pull_requests_takes_them_over_and_clears_the_hold_up()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S"));
        await ctx.OeProjects.Where(p => p.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.AutoUpdatePullRequests, true)
            .SetProperty(p => p.AutoUpdatePullRequestsByUserId, (int?)null)
            .SetProperty(p => p.AutoUpdatePullRequestsBlocked, "the person they are opened as no longer has an active account."));

        await Svc(_db.NewContext()).ResumeAutoUpdatePullRequestsAsync(id);

        var saved = await _db.NewContext().OeProjects.AsNoTracking().SingleAsync(p => p.Id == id);
        saved.AutoUpdatePullRequestsByUserId.Should().Be(OwnerUserId);
        saved.AutoUpdatePullRequestsBlocked.Should().BeNull();
    }

    [Fact]
    public async Task Create_persists_project_and_repositories()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput(
            "Acme A/S", "dk", null,
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core", "Core"),
            new ProjectRepositoryInput(RepositoryProvider.AzureDevOps, "https://dev.azure.com/acme/bc/_git/exts", "Exts")));

        var loaded = await svc.GetProjectAsync(id);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Acme A/S");
        loaded.DefaultArtifactCountry.Should().Be("dk");
        loaded.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        loaded.Repositories.Should().HaveCount(2);
        loaded.Repositories.Should().Contain(r => r.Provider == RepositoryProvider.GitHub && r.DisplayName == "Core");
        loaded.Repositories.Should().OnlyContain(r => r.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task Create_defaults_display_name_to_repo_slug_when_blank()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("Acme", "dk", null,
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/core.git", "")));

        var loaded = await svc.GetProjectAsync(id);
        loaded!.Repositories.Single().DisplayName.Should().Be("core");
    }

    // --- Adding one repository (#759) --------------------------------------

    [Fact]
    public async Task Adding_a_repository_leaves_the_others_alone()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S", "dk", null,
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/cronus-dk/core", "core")));

        var name = await svc.AddRepositoryAsync(
            id, new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/cronus-dk/retail.git", ""));

        name.Should().Be("CRONUS A/S");
        var loaded = await svc.GetProjectAsync(id);
        loaded!.Repositories.Should().HaveCount(2);
        // The blank display name falls back the same way the editor's does.
        loaded.Repositories.Should().Contain(r => r.DisplayName == "retail");
    }

    [Fact]
    public async Task Adding_a_repository_the_solution_already_has_changes_nothing()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S", "dk", null,
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/cronus-dk/core", "core")));

        // Same repository, spelled with the .git suffix - identity is provider
        // plus normalised URL, so a retry settles instead of duplicating.
        await svc.AddRepositoryAsync(
            id, new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/cronus-dk/core.git", "core"));

        var loaded = await svc.GetProjectAsync(id);
        loaded!.Repositories.Should().ContainSingle();
    }

    [Fact]
    public async Task Adding_a_repository_refuses_a_url_the_provider_does_not_serve()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S"));

        var act = () => svc.AddRepositoryAsync(
            id, new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://gitlab.com/cronus-dk/core", "core"));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Url");
    }

    [Fact]
    public async Task Create_rejects_a_blank_country()
    {
        // The country is required: builds compile against its base symbols and
        // there's no org-wide fallback anymore.
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var act = () => svc.CreateProjectAsync(NewInput("Acme", country: null));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DefaultArtifactCountry");
    }

    [Fact]
    public async Task Create_rejects_blank_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var act = () => svc.CreateProjectAsync(NewInput(name: "   "));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Theory]
    [InlineData(RepositoryProvider.AzureDevOps, "https://github.com/acme/core")]
    [InlineData(RepositoryProvider.GitHub, "https://dev.azure.com/acme/bc/_git/core")]
    [InlineData(RepositoryProvider.GitHub, "http://github.com/acme/core")] // not https
    [InlineData(RepositoryProvider.GitHub, "not-a-url")]
    public async Task Create_rejects_provider_url_mismatch(RepositoryProvider provider, string url)
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var act = () => svc.CreateProjectAsync(NewInput("Acme", "dk", null,
            new ProjectRepositoryInput(provider, url, "Repo")));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Repositories[0].Url");
    }

    [Fact]
    public async Task Create_rejects_duplicate_active_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.CreateProjectAsync(NewInput("Acme"));

        var act = () => svc.CreateProjectAsync(NewInput("acme")); // case-insensitive clash

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Create_stamps_the_acting_user_as_owner()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S"));

        (await svc.GetProjectAsync(id))!.CreatedByUserId.Should().Be(OwnerUserId);
    }

    // ── Access chosen while creating ──────────────────────────────────────

    /// <summary>
    /// The level is part of making a solution, not a correction applied afterwards. A
    /// Public solution is managed by everyone in the organisation, so a create that
    /// landed Public and was narrowed a moment later would put a customer's Business
    /// Central connection in front of the whole company for that moment.
    /// </summary>
    [Fact]
    public async Task A_solution_is_created_at_the_level_and_teams_it_was_given()
    {
        var teamId = await SeedTeamAsync("Nordics");

        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(
            NewInput("CRONUS A/S"),
            new ProjectAccessSettings(ProjectVisibility.Private, new[] { teamId }));

        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().SingleAsync(p => p.Id == id))
            .Visibility.Should().Be(ProjectVisibility.Private);
        (await verify.OeProjectTeams.AsNoTracking().Where(t => t.ProjectId == id).Select(t => t.TeamId).ToListAsync())
            .Should().Equal(teamId);
    }

    /// <summary>
    /// The invariant holds at creation too, and it holds atomically: a refused level
    /// leaves no solution behind for somebody to find at the wrong one.
    /// </summary>
    [Fact]
    public async Task A_narrowed_level_with_no_team_creates_nothing_at_all()
    {
        await using var ctx = _db.NewContext();
        var act = () => Svc(ctx).CreateProjectAsync(
            NewInput("CRONUS A/S"),
            new ProjectAccessSettings(ProjectVisibility.ReadOnly, Array.Empty<int>()));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Teams");

        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().CountAsync(p => p.Name == "CRONUS A/S"))
            .Should().Be(0, "the level was refused, so there is no solution at the wrong one");
    }

    /// <summary>A caller that says nothing about the level gets the default, as before.</summary>
    [Fact]
    public async Task A_create_that_names_no_level_is_public_with_no_teams()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(NewInput("CRONUS A/S"));

        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().SingleAsync(p => p.Id == id))
            .Visibility.Should().Be(ProjectVisibility.Public);
        (await verify.OeProjectTeams.AsNoTracking().CountAsync(t => t.ProjectId == id)).Should().Be(0);
    }

    private async Task<int> SeedTeamAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        return team.Id;
    }

    [Fact]
    public async Task A_stranger_updates_a_public_project_but_never_deletes_one_and_neither_once_narrowed()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(NewInput("CRONUS A/S"));

        // A different signed-in user who is neither the owner nor an Admin.
        const int strangerId = 9200;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = strangerId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "stranger@example.com",
                PasswordHash = "x",
                DisplayName = "Stranger",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = strangerId;
        try
        {
            // The project is Public, which is open both ways: a stranger to it manages
            // it like anyone else in the organisation. Ending it is the carve-out.
            await using (var ctx2 = _db.NewContext())
            {
                await Svc(ctx2).UpdateProjectAsync(id, NewInput("CRONUS A/S"));

                var delete = () => Svc(ctx2).SoftDeleteProjectAsync(id);
                await delete.Should().ThrowAsync<ProjectAccessDeniedException>();
            }

            // Narrowed, both are refused: Read-only reserves writing for the teams.
            await using (var narrow = _db.NewContext())
            {
                (await narrow.OeProjects.SingleAsync(p => p.Id == id)).Visibility = ProjectVisibility.ReadOnly;
                await narrow.SaveChangesAsync();
            }

            await using (var ctx3 = _db.NewContext())
            {
                var svc = Svc(ctx3);

                var update = () => svc.UpdateProjectAsync(id, NewInput("CRONUS A/S"));
                await update.Should().ThrowAsync<ProjectAccessDeniedException>();

                var delete = () => svc.SoftDeleteProjectAsync(id);
                await delete.Should().ThrowAsync<ProjectAccessDeniedException>();
            }
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    [Fact]
    public async Task An_org_admin_can_manage_a_project_they_do_not_own()
    {
        await using var ctx = _db.NewContext();
        var id = await Svc(ctx).CreateProjectAsync(NewInput("CRONUS A/S"));

        const int adminId = 9300;
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                Id = adminId,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "admin@example.com",
                PasswordHash = "x",
                DisplayName = "Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = adminId;
        try
        {
            await using var ctx2 = _db.NewContext();
            var svc = Svc(ctx2);

            var update = () => svc.UpdateProjectAsync(id, NewInput("CRONUS Renamed"));
            await update.Should().NotThrowAsync();
            (await svc.CanManageAsync(id)).Should().BeTrue();
        }
        finally
        {
            _db.OrgContext.CurrentUserId = OwnerUserId;
        }
    }

    /// <summary>
    /// The short name is what the generator puts in extension names when the
    /// customer's own name is too long for one. It round-trips, and blanking it
    /// stores nothing rather than an empty string, so "no abbreviation" reads the
    /// same however it was reached. See <c>.design/customer-naming.md</c>.
    /// </summary>
    [Fact]
    public async Task Create_and_update_persist_the_short_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("CRONUS A/S", "dk", "CRO"));
        (await svc.GetProjectAsync(id))!.ShortName.Should().Be("CRO");

        await svc.UpdateProjectAsync(id, NewInput("CRONUS A/S", "dk", "  CRN  "));
        (await svc.GetProjectAsync(id))!.ShortName.Should().Be("CRN", "it is stored as typed, trimmed");

        await svc.UpdateProjectAsync(id, NewInput("CRONUS A/S", "dk", "   "));
        (await svc.GetProjectAsync(id))!.ShortName.Should().BeNull(
            "a blank abbreviation means the full name is used, and that is one state, not two");
    }

    [Fact]
    public async Task A_short_name_longer_than_the_generator_accepts_is_refused_inline()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var act = () => svc.CreateProjectAsync(NewInput("CRONUS A/S", "dk", new string('x', 51)));

        var ex = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
        ex.Errors.Should().ContainKey("ShortName");
    }

    [Fact]
    public async Task Update_replaces_repository_set()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme", "dk", null,
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/old", "Old")));

        await svc.UpdateProjectAsync(id, new ProjectInput("Acme Renamed", null, "w1", new[]
        {
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/new", "New"),
        }));

        var loaded = await svc.GetProjectAsync(id);
        loaded!.Name.Should().Be("Acme Renamed");
        loaded.DefaultArtifactCountry.Should().Be("w1");
        loaded.Repositories.Should().ContainSingle().Which.DisplayName.Should().Be("New");

        await using var verify = _db.NewContext();
        var orphanRepos = await verify.OeProjectRepositories.CountAsync(r => r.Url.Contains("old"));
        orphanRepos.Should().Be(0, "the replaced repo rows are removed, not left dangling");
    }

    [Fact]
    public async Task SoftDelete_hides_project_from_list_and_frees_the_name()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        await svc.SoftDeleteProjectAsync(id);

        (await svc.ListProjectsAsync()).Should().BeEmpty();
        (await svc.GetProjectAsync(id)).Should().BeNull();
        // The soft-delete filter on the unique index frees the name for reuse.
        var act = () => svc.CreateProjectAsync(NewInput("Acme"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListProjectReleases_returns_releases_linked_via_import_jobs()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var projectId = await svc.CreateProjectAsync(NewInput("Acme"));

        // A project release + the import job that links it back to the project.
        await using (var seed = _db.NewContext())
        {
            var rel = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "Acme on BC 26.0",
                Kind = "project",
                Status = "ready",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeReleases.Add(rel);
            await seed.SaveChangesAsync();
            seed.OeImportJobs.Add(new OeImportJob
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = rel.Id,
                ProjectId = projectId,
                Kind = "project_build",
                Status = "completed",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var releases = await svc.ListProjectReleasesAsync(projectId);
        releases.Should().ContainSingle().Which.Label.Should().Be("Acme on BC 26.0");
    }

    [Fact]
    public async Task AddSupplementalSymbols_persists_and_lists_with_size()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        await svc.AddSupplementalSymbolsAsync(id, new[]
        {
            new SupplementalSymbolUpload("Continia_Document Capture_12.0.0.0.app", new byte[] { 1, 2, 3, 4 }),
        });

        var rows = await svc.ListSupplementalSymbolsAsync(id);
        rows.Should().ContainSingle();
        rows[0].FileName.Should().Be("Continia_Document Capture_12.0.0.0.app");
        rows[0].ContentLength.Should().Be(4);
    }

    [Fact]
    public async Task AddSupplementalSymbols_replaces_same_named_package()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        await svc.AddSupplementalSymbolsAsync(id, new[] { new SupplementalSymbolUpload("Dep.app", new byte[] { 1 }) });
        await svc.AddSupplementalSymbolsAsync(id, new[] { new SupplementalSymbolUpload("Dep.app", new byte[] { 9, 9, 9 }) });

        var rows = await svc.ListSupplementalSymbolsAsync(id);
        rows.Should().ContainSingle("a re-upload of the same name replaces, not duplicates");
        rows[0].ContentLength.Should().Be(3, "the latest upload wins");
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("")]
    public async Task AddSupplementalSymbols_rejects_non_app(string fileName)
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        var act = () => svc.AddSupplementalSymbolsAsync(id, new[] { new SupplementalSymbolUpload(fileName, new byte[] { 1 }) });

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Symbols");
    }

    [Fact]
    public async Task AddSupplementalSymbols_rejects_empty_upload_list()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));

        var act = () => svc.AddSupplementalSymbolsAsync(id, Array.Empty<SupplementalSymbolUpload>());

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Symbols");
    }

    [Fact]
    public async Task DeleteSupplementalSymbol_removes_only_the_targeted_row()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));
        await svc.AddSupplementalSymbolsAsync(id, new[]
        {
            new SupplementalSymbolUpload("A.app", new byte[] { 1 }),
            new SupplementalSymbolUpload("B.app", new byte[] { 2 }),
        });
        var rows = await svc.ListSupplementalSymbolsAsync(id);

        await svc.DeleteSupplementalSymbolAsync(id, rows.Single(r => r.FileName == "A.app").Id);

        (await svc.ListSupplementalSymbolsAsync(id)).Should().ContainSingle().Which.FileName.Should().Be("B.app");
    }

    [Fact]
    public async Task Create_with_repos_warms_the_discovery_cache()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateProjectAsync(NewInput("Acme")); // NewInput seeds one repo

        _discoveryQueue.IsInFlight(id).Should().BeTrue("a project created with repositories warms its discovery cache in the background");
    }

    [Fact]
    public async Task Update_with_repos_warms_the_discovery_cache()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var id = await svc.CreateProjectAsync(NewInput("Acme"));
        // The create already enqueued; clear it so we observe the update's own warm.
        _discoveryQueue.Complete(id);

        await svc.UpdateProjectAsync(id, new ProjectInput("Acme", null, "dk", new[]
        {
            new ProjectRepositoryInput(RepositoryProvider.GitHub, "https://github.com/acme/new", "New"),
        }));

        _discoveryQueue.IsInFlight(id).Should().BeTrue("changing the repo set re-warms the discovery cache");
    }

    [Fact]
    public async Task Projects_from_another_org_are_invisible()
    {
        // Insert a project owned by the other org directly (write filters don't
        // apply); the org-scoped read must not surface it.
        await using (var seed = _db.NewContext())
        {
            seed.OeProjects.Add(new OeProject
            {
                OrganizationId = TestDb.OtherOrgId,
                Name = "Other Co",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        (await svc.ListProjectsAsync()).Should().BeEmpty("the query filter scopes to the acting org");
    }

    /// <summary>
    /// Issue #702: the name pre-check reads before the save writes. Held open on a
    /// repeatable-read snapshot from before the clashing project existed, the
    /// pre-check passes and the case-insensitive unique index catches the save —
    /// which must still surface as the inline Name error, not a 500.
    /// </summary>
    [Fact]
    public async Task Create_translates_a_lost_name_race_into_the_Name_field_error()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await using var tx = await ctx.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead);
        // Any read fixes the snapshot; from here this connection cannot see
        // rows another session commits.
        await ctx.OeProjects.AsNoTracking().AnyAsync(p => p.Name == "anything");

        // Another user wins the race on a separate connection.
        await using (var other = _db.NewContext())
        {
            await Svc(other).CreateProjectAsync(NewInput("CRONUS A/S"));
        }

        var act = () => svc.CreateProjectAsync(NewInput("CRONUS A/S"));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
    }
}
