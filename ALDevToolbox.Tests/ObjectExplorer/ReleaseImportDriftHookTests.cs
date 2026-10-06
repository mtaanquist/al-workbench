using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The dependency drift scan hangs off the end of a Release import (issue
/// #630): a new Business Central release is exactly the moment somebody wants
/// to know which customer repositories still target the old one.
///
/// <para>Two things matter here and nothing else does. It runs for a
/// <em>first-party</em> release - a pipeline build or a partner upload is not a
/// Business Central version anybody's <c>app.json</c> targets - and it never
/// costs the import anything: the modules are already in by the time it runs,
/// so a GitHub that will not answer is a log line, not a failed release.</para>
/// </summary>
public sealed class ReleaseImportDriftHookTests : IDisposable
{
    private const int UserId = 6310;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoA = "cronus-dk/payment-import";

    private const string BehindManifest = """
        {"id":"1c0ffee0-0000-4000-8000-000000000001","name":"Payment Import","publisher":"CRONUS",
         "version":"1.0.0.0","application":"27.0.0.0"}
        """;

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private readonly TestDb _db = new();

    public ReleaseImportDriftHookTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "consultant@cronus.example",
            DisplayName = "consultant@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_first_party_release_reaching_ready_records_what_the_repositories_are_behind()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var releaseId = await SeedReleaseAsync("first_party");

        await AmendAsync(releaseId, DriftingApi());

        await using var read = _db.NewContext();
        var rows = await read.GitHubRepositoryDrift.ToListAsync();
        rows.Should().ContainSingle()
            .Which.Should().Match<GitHubRepositoryDrift>(
                d => d.Repository == RepoA && d.Field == "application"
                     && d.Current == "27.0.0.0" && d.Proposed == "28.2.0.0");
    }

    [Fact]
    public async Task A_pipeline_build_release_is_not_something_to_compare_repositories_against()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var releaseId = await SeedReleaseAsync("project");
        var api = DriftingApi();

        await AmendAsync(releaseId, api);

        api.Calls.Should().BeEmpty();
        await using var read = _db.NewContext();
        (await read.GitHubRepositoryDrift.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_github_that_will_not_answer_does_not_fail_the_import()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var releaseId = await SeedReleaseAsync("first_party");
        // The installation token itself is refused, which is the shape of a
        // GitHub outage in the middle of a nightly import.
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.ServiceUnavailable, "{\"message\":\"unavailable\"}");

        var summary = await AmendAsync(releaseId, api);

        summary.ModulesImported.Should().BeGreaterThan(0);
        await using var read = _db.NewContext();
        (await read.OeReleases.SingleAsync(r => r.Id == releaseId)).Status.Should().Be("ready");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>
    /// Amends one <c>.app</c> into the release, which takes it through the same
    /// "back to ready" completion the first import ends on - the point the drift
    /// scan hangs off.
    /// </summary>
    private async Task<ReleaseImportSummary> AmendAsync(int releaseId, FakeGitHubApi api)
    {
        await using var ctx = _db.NewContext();
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(
                ctx, _db.OrgContext,
                new ALDevToolbox.Services.Translation.TranslationMemoryService(
                    ctx, _db.OrgContext,
                    NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
                NullLogger<TranslationImportService>.Instance),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance,
            _db.NewDependencyDriftService(ctx, client, access));

        return await importer.AmendReleaseAsync(
            releaseId, [new AppFileUpload("Microsoft_DK_Core.app", stream, null)]);
    }

    /// <summary>A GitHub whose one tracked repository is a wave behind.</summary>
    private static FakeGitHubApi DriftingApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(RepoA))
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK,
                "{\"sha\":\"t\",\"truncated\":false,\"tree\":[{\"path\":\"app.json\",\"type\":\"blob\",\"sha\":\"b\"}]}")
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", BehindManifest));

    /// <summary>
    /// A ready release carrying a Base Application module, so the version the
    /// import re-infers on the way back to ready is the one repositories are
    /// measured against.
    /// </summary>
    private async Task<int> SeedReleaseAsync(string kind)
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 28.2 (DK)",
            Kind = kind,
            Status = "ready",
            ProjectName = kind == "project" ? "CRONUS A/S payments" : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        ctx.OeModules.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = "Base Application",
            Publisher = "Microsoft",
            Version = "28.2.50931.51727",
        });
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private async Task SeedSolutionAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS A/S payments",
            DefaultArtifactCountry = "dk",
            CreatedByUserId = UserId,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories =
            [
                new OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = RepositoryProvider.GitHub,
                    Url = $"https://github.com/{RepoA}.git",
                    DisplayName = "payment-import",
                },
            ],
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        // A live customer: repositories are measured against what production runs.
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            Type = "Production",
            Version = "28.2.45123.0",
            FetchedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ReadyAsync()
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
