using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// GitHub Releases in both directions (issue #632).
///
/// <para>Publishing: the tag a build is published at, the credential that publishes it,
/// what re-publishing the same version does to the Release that is already there, and
/// every refusal - because the rule that matters most is that none of them can turn a
/// successful build into a failed one.</para>
///
/// <para>Deploying: what a Release has to carry before it can be installed, the
/// hand-followed redirect that fetches an asset's bytes, and the fact that the
/// installation token is not handed to the storage host on the way.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note. The
/// request shapes are GitHub's documented ones - they have not been exercised against
/// api.github.com from this environment.</para>
/// </summary>
public sealed class GitHubReleaseServiceTests : IDisposable
{
    private const int UserId = 921;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "cronus-customer";
    private const string Repo = $"{OrgLogin}/{RepoName}";
    private const string RepoUrl = $"https://github.com/{Repo}.git";
    private const string InstallationToken = "ghs_installation";
    private const long ReleaseId = 900;
    private const long AssetId = 5501;

    private readonly TestDb _db = new();

    public GitHubReleaseServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "Dev Eloper",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
        _db.OrgContext.IsSiteAdmin = true; // manage rights on an ownerless solution
    }

    public void Dispose() => _db.Dispose();

    // ── Publishing a build ──────────────────────────────────────────────────

    [Fact]
    public async Task A_build_is_published_at_the_apps_version_with_every_app_attached()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.2.3.0"), ("CRONUS Sales", "1.2.3.0")]);
        var api = PublishableApi(existingRelease: false, tag: "v1.2.3.0");

        await using var ctx = _db.NewContext();
        var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeTrue();
        result.Tag.Should().Be("v1.2.3.0");
        result.Url.Should().Be($"https://github.com/{Repo}/releases/tag/v1.2.3.0");

        var created = CreateReleaseBody(api);
        created.Should().Contain("\"tag_name\":\"v1.2.3.0\"");
        // The tag goes on the commit that was built, not on the default branch's head.
        created.Should().Contain($"\"target_commitish\":\"{BuiltCommit}\"");
        created.Should().Contain("CRONUS Core 1.2.3.0");
        // Generated notes would describe commits nobody chose; the body names the apps.
        created.Should().Contain("\"generate_release_notes\":false");
        api.Calls.Where(c => c.Contains("uploads.github.com")).Should().HaveCount(2);
        api.Calls.Should().Contain(c => c.Contains("name=CRONUS Core_1.2.3.0.app"));
    }

    [Fact]
    public async Task Publishing_is_an_act_of_the_organisation_so_it_rides_the_installation_token()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0")]);
        var api = PublishableApi(existingRelease: false);

        await using var ctx = _db.NewContext();
        await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        // There may be no user at all behind a build, so nothing here may need one.
        api.Credentials
            .Where(c => !c.Call.Contains("access_tokens"))
            .Should().OnlyContain(c => c.Token == InstallationToken);
    }

    [Fact]
    public async Task Publishing_the_same_version_again_replaces_the_assets_rather_than_making_a_second_release()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0")]);
        var api = PublishableApi(existingRelease: true);

        await using var ctx = _db.NewContext();
        var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeTrue();
        result.Tag.Should().Be("v1.0.0.0");
        // The tag is never moved, and no second release is created.
        api.Calls.Should().NotContain(c => c.StartsWith("POST", StringComparison.Ordinal) && c.EndsWith("/releases", StringComparison.Ordinal));
        api.Calls.Should().Contain(c => c.StartsWith($"DELETE https://api.github.com/repos/{Repo}/releases/assets/{AssetId}", StringComparison.Ordinal));
        api.Calls.Should().Contain(c => c.StartsWith($"PATCH https://api.github.com/repos/{Repo}/releases/{ReleaseId}", StringComparison.Ordinal));
        api.Calls.Where(c => c.Contains("uploads.github.com")).Should().ContainSingle();
    }

    [Fact]
    public async Task Apps_at_different_versions_are_published_under_the_build_number()
    {
        // Apps that each keep their own Major.Minor share only the build number, so the
        // Release is named after the build rather than after one app's version.
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0"), ("CRONUS Sales", "2.0.0.0")]);
        var tag = $"build-{seed.BuildId}";
        var api = PublishableApi(existingRelease: false, tag: tag);

        await using (var ctx = _db.NewContext())
        {
            var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);
            result.Published.Should().BeTrue();
            result.Tag.Should().Be(tag);
        }

        CreateReleaseBody(api).Should().Contain($"\"tag_name\":\"{tag}\"");
        api.Calls.Where(c => c.Contains("uploads.github.com")).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_app_carried_over_unchanged_is_left_out_of_the_release()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.500.0"), ("CRONUS Sales", "1.0.612.0")]);
        await CarryAsync(seed.BuildId, "CRONUS Core");
        var api = PublishableApi(existingRelease: false, tag: "v1.0.612.0");

        await using var ctx = _db.NewContext();
        var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeTrue();
        result.Tag.Should().Be("v1.0.612.0", "the tag follows the apps this build published");
        api.Calls.Where(c => c.Contains("uploads.github.com")).Should().ContainSingle()
            .Which.Should().Contain("name=CRONUS Sales_1.0.612.0.app");
        CreateReleaseBody(api).Should().NotContain("CRONUS Core");
    }

    [Fact]
    public async Task A_build_where_nothing_changed_makes_no_release_and_says_so()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.500.0")]);
        await CarryAsync(seed.BuildId, "CRONUS Core");
        var api = PublishableApi(existingRelease: false);

        await using (var ctx = _db.NewContext())
        {
            var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);
            result.Published.Should().BeFalse();
            result.Error.Should().BeNull("nothing went wrong");
        }

        api.Calls.Should().NotContain(c => c.Contains("/releases"));
        await using var verify = _db.NewContext();
        (await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == seed.BuildId)).GithubReleaseError.Should().BeNull();
        (await verify.OeProjectBuildLogs.AsNoTracking()
                .SingleAsync(l => l.ProjectBuildId == seed.BuildId && l.Section == "GitHub Release"))
            .Content.Should().Contain("No extension changed");
    }

    [Fact]
    public async Task A_partial_release_whose_version_tag_is_taken_goes_under_the_build_number()
    {
        // Numbering off: Sales changed but kept 1.0.0.0, the tag an earlier release with
        // both apps already has. Rewriting it would drop Core's file from it.
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0"), ("CRONUS Sales", "1.0.0.0")]);
        await CarryAsync(seed.BuildId, "CRONUS Core");
        var tag = $"build-{seed.BuildId}";
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Post, $"/repos/{Repo}/releases/{ReleaseId}/assets", HttpStatusCode.Created,
                "{\"id\":77,\"name\":\"uploaded.app\",\"size\":3}")
            .On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/v1.0.0.0", HttpStatusCode.OK,
                FakeGitHubApi.ReleaseJson(Repo, "v1.0.0.0", ReleaseId, assets: (AssetId, "CRONUS Core_1.0.0.0.app")))
            .On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/{tag}", HttpStatusCode.NotFound)
            .On(HttpMethod.Post, $"/repos/{Repo}/releases", HttpStatusCode.Created,
                FakeGitHubApi.ReleaseJson(Repo, tag, ReleaseId));

        await using var ctx = _db.NewContext();
        var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeTrue();
        result.Tag.Should().Be(tag);
        api.Calls.Should().NotContain(c => c.StartsWith("DELETE", StringComparison.Ordinal));
    }

    private async Task CarryAsync(int buildId, string appName)
    {
        await using var ctx = _db.NewContext();
        var artifact = await ctx.OeProjectBuildArtifacts.SingleAsync(a => a.ProjectBuildId == buildId && a.AppName == appName);
        artifact.CarriedFromBuildId = buildId - 1;
        await ctx.SaveChangesAsync();
    }

    [Theory]
    [InlineData(new[] { "28.2.4812.0" }, "v28.2.4812.0")]
    [InlineData(new[] { "28.2.4812.0", "28.2.4812.0" }, "v28.2.4812.0")]
    [InlineData(new[] { "28.2.4812.0", "1.3.4812.0" }, "build-4812")]
    public void The_tag_is_the_shared_version_or_else_the_build_number(string[] versions, string expected) =>
        GitHubReleaseService.ReleaseTag(versions, 4812).Should().Be(expected);

    [Fact]
    public async Task A_refusal_from_github_lands_on_the_build_rather_than_failing_it()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0")]);
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/", HttpStatusCode.NotFound)
            // What a "restrict tag creation" rule looks like from outside.
            .On(HttpMethod.Post, $"/repos/{Repo}/releases", HttpStatusCode.Forbidden,
                "{\"message\":\"Tag creation is restricted on this repository.\"}");

        await using (var ctx = _db.NewContext())
        {
            var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);
            result.Published.Should().BeFalse();
            // GitHub's own words: an admin can act on them, "the publish failed" cannot.
            result.Error.Should().Contain("Tag creation is restricted");
        }

        await AssertBuildStillReadyAsync(seed.BuildId, expectError: "Tag creation is restricted");
    }

    [Fact]
    public async Task A_repository_outside_the_connected_organisation_is_refused_in_words()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0")],
            repoUrl: "https://github.com/someone-else/cronus-customer.git");
        var api = PublishableApi(existingRelease: false);

        await using var ctx = _db.NewContext();
        var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeFalse();
        result.Error.Should().Contain("outside the connected GitHub organisation");
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pipeline_that_publishes_nowhere_asks_nothing_of_github()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")]);
        var api = PublishableApi(existingRelease: false);

        await using var ctx = _db.NewContext();
        var result = await NewService(ctx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeFalse();
        result.Error.Should().BeNull();
        api.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProjectBuildTarget.NextMinor)]
    [InlineData(ProjectBuildTarget.NextMajor)]
    public async Task A_preview_build_is_never_tagged_even_when_the_pipeline_publishes(string target)
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0")]);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectBuilds.Where(b => b.Id == seed.BuildId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BcTarget, target));
        }
        var api = PublishableApi(existingRelease: false);

        await using var svcCtx = _db.NewContext();
        var result = await NewService(svcCtx, api).PublishBuildAsync(seed.BuildId);

        result.Published.Should().BeFalse();
        result.Error.Should().BeNull("a preview build was never asked to be published, so nothing failed");
        api.Calls.Should().BeEmpty();
        await using var verify = _db.NewContext();
        var build = await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == seed.BuildId);
        build.GithubReleaseTag.Should().BeNull();
        build.Status.Should().Be(ProjectBuildStatus.Ready);
    }

    [Fact]
    public async Task The_outcome_is_written_onto_the_build_and_into_its_log()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: true, apps: [("CRONUS Core", "1.0.0.0")]);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx, PublishableApi(existingRelease: false)).PublishBuildAsync(seed.BuildId);
        }

        await using var verify = _db.NewContext();
        var build = await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == seed.BuildId);
        build.GithubReleaseTag.Should().Be("v1.0.0.0");
        build.GithubReleaseUrl.Should().Contain("/releases/tag/v1.0.0.0");
        build.GithubReleaseError.Should().BeNull();

        var log = await verify.OeProjectBuildLogs.AsNoTracking()
            .SingleAsync(l => l.ProjectBuildId == seed.BuildId && l.Section == "GitHub Release");
        log.Content.Should().Contain("v1.0.0.0");
    }

    // ── Deploying from a Release ────────────────────────────────────────────

    [Fact]
    public async Task Only_releases_with_app_files_are_offered_to_deploy()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();

        await using var ctx = _db.NewContext();
        var releases = await NewService(ctx, api).ListReleasesAsync(seed.ReleasePipelineId);

        releases.Should().HaveCount(2);
        releases[0].Tag.Should().Be("v1.0.0.0");
        releases[0].AppFileNames.Should().ContainSingle().Which.Should().Be("CRONUS Core_1.0.0.0.app");
        // Source archives are not something we can install, so they are not listed.
        releases[1].AppFileNames.Should().BeEmpty();
    }

    [Fact]
    public async Task Listing_releases_is_refused_for_a_pipeline_that_releases_builds()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: false);

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx, StageableApi()).ListReleasesAsync(seed.ReleasePipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ArtifactSource");
    }

    [Fact]
    public async Task Staging_downloads_the_app_files_and_records_them_as_a_deliverable_build()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();

        int stagedId;
        await using (var ctx = _db.NewContext())
        {
            stagedId = await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }

        await using var verify = _db.NewContext();
        var staged = await verify.OeProjectBuilds.AsNoTracking()
            .Include(b => b.Artifacts).SingleAsync(b => b.Id == stagedId);
        staged.Status.Should().Be(ProjectBuildStatus.Ready);
        staged.PipelineId.Should().BeNull();          // not a run of anything - it was downloaded
        staged.GithubReleaseTag.Should().Be("v1.0.0.0");
        staged.StartedByUserId.Should().Be(UserId);
        // ...and the repository it came from, so another repository's pipeline refuses it (#1118).
        staged.StagedFromRepositoryId.Should().Be(
            (await verify.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == seed.ReleasePipelineId)).GithubReleaseRepositoryId);
        // The manifest inside the .app is what names the app, not the file name.
        staged.Artifacts.Should().ContainSingle()
            .Which.Should().Match<OeProjectBuildArtifact>(a => a.AppName == "CRONUS Core" && a.AppVersion == "1.0.0.0"
                // ...and its app id, so a later build can resolve a dependency on it (#901).
                && a.AppId == StagedAppId);
    }

    [Fact]
    public async Task Downloading_an_asset_follows_the_redirect_without_handing_the_token_to_storage()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }

        // The storage host has its own signed URL: sending it our credential would
        // hand a token to a service that never asked for one.
        var storageCall = api.Credentials.Single(c => c.Call.Contains("objects.githubusercontent.com"));
        storageCall.Token.Should().BeNull();
        // ... and the API call that produced the redirect did carry it.
        api.Credentials.Single(c => c.Call.Contains($"/releases/assets/{AssetId}")).Token.Should().Be(InstallationToken);
    }

    [Fact]
    public async Task Staging_the_same_release_twice_returns_the_build_already_staged()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();

        int first, second;
        await using (var ctx = _db.NewContext())
        {
            first = await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }
        await using (var ctx = _db.NewContext())
        {
            second = await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }

        second.Should().Be(first);
        await using var verify = _db.NewContext();
        (await verify.OeProjectBuilds.AsNoTracking().CountAsync(b => b.PipelineId == null)).Should().Be(1);
    }

    [Fact]
    public async Task Staging_again_records_the_repository_on_a_release_staged_before_it_was_recorded()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();
        int first;
        await using (var ctx = _db.NewContext())
        {
            first = await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
            await ctx.OeProjectBuilds.Where(b => b.Id == first)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.StagedFromRepositoryId, (int?)null));
        }

        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0")).Should().Be(first);
        }

        await using var verify = _db.NewContext();
        (await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == first)).StagedFromRepositoryId.Should().Be(
            (await verify.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == seed.ReleasePipelineId)).GithubReleaseRepositoryId);
    }

    [Fact]
    public async Task Staging_again_after_the_repository_was_removed_and_added_back_records_the_new_row()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();
        int first;
        await using (var ctx = _db.NewContext())
        {
            first = await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }

        // The repository is removed from the solution and added back as a new row, and
        // the deployment pipeline is pointed at it (#1178).
        int readded;
        await using (var ctx = _db.NewContext())
        {
            var rp = await ctx.OeReleasePipelines.SingleAsync(r => r.Id == seed.ReleasePipelineId);
            var oldId = rp.GithubReleaseRepositoryId!.Value;
            var row = new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId,
                Provider = RepositoryProvider.GitHub, Url = RepoUrl, DisplayName = RepoName,
            };
            ctx.OeProjectRepositories.Add(row);
            await ctx.SaveChangesAsync();
            rp.GithubReleaseRepositoryId = row.Id;
            await ctx.SaveChangesAsync();
            await ctx.OeProjectRepositories.Where(r => r.Id == oldId).ExecuteDeleteAsync();
            readded = row.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0")).Should().Be(first);
        }

        await using var verify = _db.NewContext();
        (await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == first))
            .StagedFromRepositoryId.Should().Be(readded);
    }

    [Fact]
    public async Task Staging_again_through_a_second_row_for_the_same_repository_records_that_row()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);
        var api = StageableApi();
        int first;
        await using (var ctx = _db.NewContext())
        {
            first = await NewService(ctx, api).StageReleaseAsync(seed.ReleasePipelineId, "v1.0.0.0");
        }

        // The solution lists the same GitHub repository twice; a second deployment
        // pipeline draws from the second row (#1178).
        int twinPipelineId, twinId;
        await using (var ctx = _db.NewContext())
        {
            var rp = await ctx.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == seed.ReleasePipelineId);
            var twin = new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId,
                Provider = RepositoryProvider.GitHub, Url = RepoUrl, DisplayName = RepoName + " (again)",
            };
            ctx.OeProjectRepositories.Add(twin);
            await ctx.SaveChangesAsync();
            var now = DateTime.UtcNow;
            var twinPipeline = new OeReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "CRONUS App -> Production (again)",
                ArtifactSource = ReleaseArtifactSource.GithubRelease, GithubReleaseRepositoryId = twin.Id,
                ProjectEnvironmentId = rp.ProjectEnvironmentId,
                DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
                CreatedAt = now, UpdatedAt = now,
            };
            ctx.OeReleasePipelines.Add(twinPipeline);
            await ctx.SaveChangesAsync();
            (twinPipelineId, twinId) = (twinPipeline.Id, twin.Id);
        }

        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx, api).StageReleaseAsync(twinPipelineId, "v1.0.0.0")).Should().Be(first);
        }

        await using var verify = _db.NewContext();
        (await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == first))
            .StagedFromRepositoryId.Should().Be(twinId);
    }

    [Theory]
    [InlineData("https://github.com/cronus-dk/cronus-customer", "https://www.github.com/CRONUS-dk/cronus-customer.git/", true)]
    [InlineData("https://github.com/cronus-dk/cronus-customer", "https://github.com/cronus-dk/cronus-warehouse", false)]
    [InlineData("https://github.com/cronus-dk/cronus-customer", null, false)]
    [InlineData("https://dev.azure.com/cronus/customer/_git/customer", "https://dev.azure.com/cronus/customer/_git/customer", false)]
    public void IsSameRepository_compares_the_github_owner_and_name(string? first, string? second, bool expected)
    {
        GitHubReleaseService.IsSameRepository(first, second).Should().Be(expected);
    }

    [Fact]
    public async Task A_release_with_no_app_files_is_refused_at_staging_time()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx, StageableApi()).StageReleaseAsync(seed.ReleasePipelineId, "v0.9.0.0");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Tag"].Should().Contain("no app files");
    }

    [Fact]
    public async Task A_tag_that_is_gone_says_so_rather_than_failing()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")], releaseSourced: true);

        await using var ctx = _db.NewContext();
        var act = () => NewService(ctx, StageableApi()).StageReleaseAsync(seed.ReleasePipelineId, "v7.0.0.0");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Tag"].Should().Contain("no release tagged");
    }

    [Fact]
    public async Task The_repository_options_are_this_solutions_github_repositories_inside_the_connected_org()
    {
        await ConnectOrganisationAsync();
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")]);
        await using (var ctx = _db.NewContext())
        {
            ctx.OeProjectRepositories.Add(new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId,
                Provider = RepositoryProvider.GitHub, Url = "https://github.com/another-org/thing.git",
                DisplayName = "Somebody else's",
            });
            ctx.OeProjectRepositories.Add(new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId,
                Provider = RepositoryProvider.AzureDevOps, Url = "https://dev.azure.com/cronus/_git/thing",
                DisplayName = "On Azure DevOps",
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var options = await NewService(read, StageableApi()).ListRepositoryOptionsAsync(seed.ProjectId);

        options.Should().ContainSingle().Which.FullName.Should().Be(Repo);
    }

    [Fact]
    public async Task Without_a_connection_the_editors_still_learn_the_solution_is_on_github()
    {
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")]);

        await using var ctx = _db.NewContext();
        var choices = await NewService(ctx, StageableApi()).DescribeRepositoryOptionsAsync(seed.ProjectId);

        choices.Options.Should().BeEmpty();
        choices.IsConnected.Should().BeFalse();
        // The solution's own repository is on GitHub, so the editor can say who
        // to ask rather than hiding the field.
        choices.HasGitHubRepositories.Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_github_connection_no_repository_is_offered()
    {
        var seed = await SeedAsync(publishTo: false, apps: [("CRONUS Core", "1.0.0.0")]);

        await using var ctx = _db.NewContext();
        (await NewService(ctx, StageableApi()).ListRepositoryOptionsAsync(seed.ProjectId)).Should().BeEmpty();
    }

    // --- helpers ------------------------------------------------------------

    private GitHubReleaseService NewService(AppDbContext ctx, FakeGitHubApi api)
    {
        var client = _db.NewGitHubAppClient(ctx, api);
        return _db.NewGitHubReleaseService(ctx, client, _db.NewGitHubAccessService(ctx, client));
    }

    private async Task AssertBuildStillReadyAsync(int buildId, string expectError)
    {
        await using var verify = _db.NewContext();
        var build = await verify.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == buildId);
        // The .app files exist and download whatever GitHub said.
        build.Status.Should().Be(ProjectBuildStatus.Ready);
        build.FailureMessage.Should().BeNull();
        build.GithubReleaseTag.Should().BeNull();
        build.GithubReleaseError.Should().Contain(expectError);
    }

    /// <summary>The body of the one POST that created a Release (uploads post to a longer path).</summary>
    private static string CreateReleaseBody(FakeGitHubApi api) =>
        api.Bodies
            .Where(b => b.Call.EndsWith($"/repos/{Repo}/releases", StringComparison.Ordinal))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException("No release was created.");

    /// <summary>A GitHub that answers everything publishing needs.</summary>
    private static FakeGitHubApi PublishableApi(bool existingRelease, string tag = "v1.0.0.0")
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            // The upload host: a longer path than the create-release route, so the two
            // stay apart even though one is a prefix of the other.
            .On(HttpMethod.Post, $"/repos/{Repo}/releases/{ReleaseId}/assets", HttpStatusCode.Created,
                "{\"id\":77,\"name\":\"uploaded.app\",\"size\":3}");

        if (existingRelease)
        {
            api.On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/", HttpStatusCode.OK,
                    FakeGitHubApi.ReleaseJson(Repo, tag, ReleaseId, assets: (AssetId, "old.app")))
                .On(HttpMethod.Delete, $"/repos/{Repo}/releases/assets/", HttpStatusCode.NoContent)
                .On(HttpMethod.Patch, $"/repos/{Repo}/releases/{ReleaseId}", HttpStatusCode.OK,
                    FakeGitHubApi.ReleaseJson(Repo, tag, ReleaseId));
        }
        else
        {
            api.On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/", HttpStatusCode.NotFound)
                .On(HttpMethod.Post, $"/repos/{Repo}/releases", HttpStatusCode.Created,
                    FakeGitHubApi.ReleaseJson(Repo, tag, ReleaseId));
        }
        return api;
    }

    /// <summary>A GitHub with two releases on the repository, one of them installable.</summary>
    private static FakeGitHubApi StageableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(HttpMethod.Get, $"/repos/{Repo}/releases", HttpStatusCode.OK, FakeGitHubApi.ReleasesJson(
                FakeGitHubApi.ReleaseJson(Repo, "v1.0.0.0", ReleaseId, assets: (AssetId, "CRONUS Core_1.0.0.0.app")),
                FakeGitHubApi.ReleaseJson(Repo, "v0.9.0.0", 899, assets: (5599, "source.zip"))))
            .On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/v1.0.0.0", HttpStatusCode.OK,
                FakeGitHubApi.ReleaseJson(Repo, "v1.0.0.0", ReleaseId, assets: (AssetId, "CRONUS Core_1.0.0.0.app")))
            .On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/v0.9.0.0", HttpStatusCode.OK,
                FakeGitHubApi.ReleaseJson(Repo, "v0.9.0.0", 899, assets: (5599, "source.zip")))
            .On(HttpMethod.Get, $"/repos/{Repo}/releases/tags/", HttpStatusCode.NotFound)
            // GitHub answers the asset route with a hop to storage, never the bytes.
            .OnRedirect(HttpMethod.Get, $"/repos/{Repo}/releases/assets/{AssetId}",
                "https://objects.githubusercontent.com/app-bytes")
            .OnBytes(HttpMethod.Get, "/app-bytes", AppFile("CRONUS Core", "1.0.0.0"));

    private const string StagedAppId = "c0ffee00-0000-0000-0000-000000000001";

    /// <summary>A minimal but real <c>.app</c>: the NAVX header, then a zip holding the manifest.</summary>
    private static byte[] AppFile(string name, string version)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("NavxManifest.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write($"<Package><App Id=\"{StagedAppId}\" Name=\"{name}\" Version=\"{version}\" Publisher=\"CRONUS\" /></Package>");
        }
        var header = new byte[40];
        Encoding.ASCII.GetBytes("NAVX").CopyTo(header, 0);
        return [.. header, .. zip.ToArray()];
    }

    private sealed record Seed(int ProjectId, int PipelineId, int ReleasePipelineId, int BuildId);

    /// <summary>
    /// A solution with one GitHub repository, a build pipeline (optionally publishing
    /// releases to it), an environment, a release pipeline (optionally drawing from the
    /// repository's releases), and one successful build with the named apps.
    /// </summary>
    /// <summary>The commit every seeded build compiled from its repository.</summary>
    private const string BuiltCommit = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private async Task<Seed> SeedAsync(
        bool publishTo, (string Name, string Version)[] apps, string? repoUrl = null, bool releaseSourced = false)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;

        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Provider = RepositoryProvider.GitHub, Url = repoUrl ?? RepoUrl, DisplayName = RepoName,
        };
        ctx.OeProjectRepositories.Add(repository);
        await ctx.SaveChangesAsync();

        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Build",
            GithubReleaseRepositoryId = publishTo ? repository.Id : null,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);

        var environment = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Name = "Production", Type = "Production", FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(environment);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App -> Production",
            ArtifactSource = releaseSourced ? ReleaseArtifactSource.GithubRelease : ReleaseArtifactSource.Build,
            BuildPipelineId = releaseSourced ? null : pipeline.Id,
            GithubReleaseRepositoryId = releaseSourced ? repository.Id : null,
            ProjectEnvironmentId = environment.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        foreach (var app in apps)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"{app.Name}_{app.Version}.app", AppName = app.Name, AppVersion = app.Version,
                SizeBytes = 3, Content = [1, 2, 3], CreatedAt = now,
            });
        }
        // The commit the build compiled from the repository it publishes to.
        ctx.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, ProjectRepositoryId = repository.Id,
            RepoUrl = repository.Url, RepoDisplayName = RepoName, CommitHash = BuiltCommit,
        });
        await ctx.SaveChangesAsync();

        return new Seed(project.Id, pipeline.Id, releasePipeline.Id, build.Id);
    }

    private async Task ConnectOrganisationAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));

        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = OrgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
