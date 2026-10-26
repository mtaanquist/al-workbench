using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Tests.Templates.ModuleSourceTestKit;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Modules copied from a GitHub repository (#1415): which repositories may be
/// copied and with which credential, what a save stores, and how the copy is
/// refreshed on demand, on a push and daily. GitHub is <see cref="FakeGitHubApi"/>.
/// </summary>
public sealed class ModuleSourceServiceTests : IDisposable
{
    private const int UserId = 601;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Public = "contoso/document-capture";
    private const string Private = "cronus-dk/doc";

    private readonly TestDb _db = new();

    public ModuleSourceServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "editor@cronus.example",
            DisplayName = "editor@cronus.example",
            PasswordHash = "x",
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    // --- Saving ----------------------------------------------------------------

    [Fact]
    public async Task A_public_repository_is_copied_anonymously_when_the_organisation_has_no_connection()
    {
        var api = new FakeGitHubApi()
            .Repository(Public, isPrivate: false)
            .ResolveRef(Public, "HEAD", ShaA)
            .ServeTarball(Public, ShaA, SimpleExtension());

        var module = await CreateAsync(api, new ModuleSourceInput($"https://github.com/{Public}", null, null));

        api.Credentials.Should().OnlyContain(c => c.Token == null, "there is no installation to read with");
        await using var ctx = _db.NewContext();
        var saved = await ctx.Modules.SingleAsync(m => m.Id == module.Id);
        saved.SourceRepository.Should().Be(Public);
        saved.SourceRef.Should().BeNull();
        saved.SourceCommitSha.Should().Be(ShaA);
        saved.SourceFetchedAt.Should().NotBeNull();
        saved.IdRangeSize.Should().Be(100, "a blank size takes the repository's own app.json range");
        ModuleSourceColumns.ReadDependencies(saved.SourceDependenciesJson)
            .Should().ContainSingle(d => d.DepName == "Continia Core");
        ModuleSourceColumns.ReadSkipped(saved.SourceSkippedJson)
            .Should().ContainSingle(s => s.Path == "README.md");
        (await ctx.ModuleExtensionFiles.CountAsync(f => f.Folder!.ModuleId == module.Id)).Should().Be(1);
    }

    [Fact]
    public async Task With_a_connection_even_a_public_repository_is_read_with_the_installation_token()
    {
        await ConnectAsync();
        var api = InstallationApi()
            .Repository(Public, isPrivate: false)
            .ResolveRef(Public, "main", ShaA)
            .ServeTarball(Public, ShaA, SimpleExtension());

        await CreateAsync(api, new ModuleSourceInput(Public, "main", null));

        api.Credentials.Where(c => c.Call.Contains($"/repos/{Public}"))
            .Should().OnlyContain(c => c.Token == "ghs_installation");
    }

    [Fact]
    public async Task A_private_repository_outside_the_connected_organisation_is_refused()
    {
        await ConnectAsync();
        var api = InstallationApi().Repository("contoso/secret", isPrivate: true);

        var act = () => CreateAsync(api, new ModuleSourceInput("contoso/secret", null, null));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Source.Address");
        api.Calls.Should().NotContain(c => c.Contains("/tarball/"));
    }

    [Fact]
    public async Task A_private_repository_in_the_connected_organisation_needs_the_editor_to_see_it()
    {
        await ConnectAsync();
        await _db.LinkGitHubAccountAsync(UserId, "ghu_editor");
        var api = InstallationApi()
            .On(HttpMethod.Get, $"/repos/{Private}", req =>
                req.Headers.Authorization?.Parameter == "ghu_editor"
                    ? (HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")
                    : (HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Private, isPrivate: true)));

        var act = () => CreateAsync(api, new ModuleSourceInput(Private, null, null));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Source.Address"].Should().Contain("Your GitHub account can't see this repository");
        api.Calls.Should().NotContain(c => c.Contains("/tarball/"));
    }

    [Fact]
    public async Task A_private_repository_the_editor_can_see_is_copied_with_the_installation_token()
    {
        await ConnectAsync();
        await _db.LinkGitHubAccountAsync(UserId, "ghu_editor");
        var api = InstallationApi()
            .Repository(Private, isPrivate: true)
            .ResolveRef(Private, "HEAD", ShaA)
            .ServeTarball(Private, ShaA, SimpleExtension());

        var module = await CreateAsync(api, new ModuleSourceInput($"https://github.com/{Private}", null, null));

        module.SourceRepository.Should().Be(Private);
        api.Credentials.Where(c => c.Call.Contains("/tarball/")).Should().OnlyContain(c => c.Token == "ghs_installation");
        api.Credentials.Where(c => c.Call.Contains("codeload.github.com"))
            .Should().OnlyContain(c => c.Token == null, "the archive host gets no credential of ours");
    }

    [Fact]
    public async Task A_branch_that_does_not_exist_is_refused_under_the_branch_field()
    {
        var api = new FakeGitHubApi()
            .Repository(Public, isPrivate: false)
            .On(HttpMethod.Get, $"/repos/{Public}/commits/nope", HttpStatusCode.NotFound, "{\"message\":\"No commit found\"}");

        var act = () => CreateAsync(api, new ModuleSourceInput(Public, "nope", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Source.Ref");
        await using var ctx = _db.NewContext();
        (await ctx.Modules.AnyAsync()).Should().BeFalse("no module is saved without its files");
    }

    [Fact]
    public async Task A_folder_missing_from_the_repository_is_refused_under_the_folder_field()
    {
        var api = PublicApi(ShaA, SimpleExtension());

        var act = () => CreateAsync(api, new ModuleSourceInput(Public, null, "Missing"));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Source.Path");
    }

    [Fact]
    public async Task A_tarball_redirect_to_another_host_is_not_followed()
    {
        var api = new FakeGitHubApi()
            .Repository(Public, isPrivate: false)
            .ResolveRef(Public, "HEAD", ShaA)
            .OnRedirect(HttpMethod.Get, $"/repos/{Public}/tarball/{ShaA}", "https://evil.example/x.tar.gz");

        var act = () => CreateAsync(api, new ModuleSourceInput(Public, null, null));

        await act.Should().ThrowAsync<PlanValidationException>();
        api.Calls.Should().NotContain(c => c.Contains("evil.example"));
    }

    [Fact]
    public async Task GitHub_not_answering_refuses_the_save_and_says_to_try_again()
    {
        var api = new FakeGitHubApi().On(HttpMethod.Get, $"/repos/{Public}", FakeGitHubApi.Unreachable);

        var act = () => CreateAsync(api, new ModuleSourceInput(Public, null, null));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Source.Address"].Should().Contain("Try again");
    }

    [Fact]
    public async Task Saving_a_repository_module_as_written_here_keeps_the_copy_as_editable_files()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));

        await using (var ctx = _db.NewContext())
        {
            var loaded = await NewModuleService(ctx).GetByKeyAsync(module.Key);
            var folders = loaded!.ExtensionFolders.Select(ModuleAuthoringMapper.BuildFolderAuthoring).ToList();
            await NewModuleService(ctx).UpdateAsync(module.Id, Input() with { Folders = folders });
        }

        await using var verify = _db.NewContext();
        var saved = await verify.Modules.SingleAsync(m => m.Id == module.Id);
        saved.SourceRepository.Should().BeNull();
        saved.SourceCommitSha.Should().BeNull();
        (await verify.ModuleExtensionFiles.CountAsync(f => f.Folder!.ModuleId == module.Id)).Should().Be(1);
    }

    // --- Refreshing ------------------------------------------------------------

    [Fact]
    public async Task A_refresh_at_the_same_commit_only_notes_that_it_checked()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));
        var api = new FakeGitHubApi().Repository(Public, isPrivate: false).ResolveRef(Public, "HEAD", ShaA);

        var outcome = await RefreshAsync(api, module.Id);

        outcome.Should().Be(ModuleSourceRefreshOutcome.Unchanged);
        api.Calls.Should().NotContain(c => c.Contains("/tarball/"));
        await using var ctx = _db.NewContext();
        var saved = await ctx.Modules.SingleAsync(m => m.Id == module.Id);
        saved.SourceCheckedAt.Should().BeOnOrAfter(saved.SourceFetchedAt!.Value);
        saved.SourceError.Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_at_a_new_commit_replaces_the_copy()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));
        var api = PublicApi(ShaB, SimpleExtension("codeunit 91000 \"{{affix}} Doc Adapter v2\" { }"));

        var outcome = await RefreshAsync(api, module.Id);

        outcome.Should().Be(ModuleSourceRefreshOutcome.Updated);
        await using var ctx = _db.NewContext();
        (await ctx.Modules.SingleAsync(m => m.Id == module.Id)).SourceCommitSha.Should().Be(ShaB);
        (await ctx.ModuleExtensionFiles.Where(f => f.Folder!.ModuleId == module.Id).Select(f => f.Content).SingleAsync())
            .Should().Contain("v2");
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_copy_and_records_why()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension(), "main"), new ModuleSourceInput(Public, "main", null));
        var api = new FakeGitHubApi()
            .Repository(Public, isPrivate: false)
            .On(HttpMethod.Get, $"/repos/{Public}/commits/main", HttpStatusCode.NotFound, "{\"message\":\"No commit found\"}");

        var outcome = await RefreshAsync(api, module.Id);

        outcome.Should().Be(ModuleSourceRefreshOutcome.Failed);
        await using var ctx = _db.NewContext();
        var saved = await ctx.Modules.SingleAsync(m => m.Id == module.Id);
        saved.SourceCommitSha.Should().Be(ShaA);
        saved.SourceError.Should().Contain("'main'");
        (await ctx.ModuleExtensionFiles.CountAsync(f => f.Folder!.ModuleId == module.Id)).Should().Be(1);

        // The next good refresh clears it.
        await RefreshAsync(new FakeGitHubApi().Repository(Public, isPrivate: false).ResolveRef(Public, "main", ShaA), module.Id);
        await using var after = _db.NewContext();
        (await after.Modules.SingleAsync(m => m.Id == module.Id)).SourceError.Should().BeNull();
    }

    [Fact]
    public async Task A_copy_that_grew_past_the_limits_keeps_the_old_one()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));
        var tooMany = Tarball(Enumerable.Range(0, ModuleSourceLimits.MaxFiles + 1).Select(i => Entry.Text($"src/f{i}.al", "x")));

        var outcome = await RefreshAsync(PublicApi(ShaB, tooMany), module.Id);

        outcome.Should().Be(ModuleSourceRefreshOutcome.Failed);
        await using var ctx = _db.NewContext();
        (await ctx.Modules.SingleAsync(m => m.Id == module.Id)).SourceCommitSha.Should().Be(ShaA);
    }

    [Fact]
    public async Task A_push_refreshes_the_modules_following_that_branch_and_no_others()
    {
        var onDefault = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null), "on-default");
        var onMain = await CreateAsync(PublicApi(ShaA, SimpleExtension(), "main"), new ModuleSourceInput(Public, "main", null), "on-main");
        var onTest = await CreateAsync(PublicApi(ShaA, SimpleExtension(), "test"), new ModuleSourceInput(Public, "test", null), "on-test");
        var api = PublicApi(ShaB, SimpleExtension()).ResolveRef(Public, "main", ShaB);

        await using var ctx = _db.NewContext();
        var updated = await _db.NewModuleSourceService(ctx, _db.NewGitHubAppClient(ctx, api))
            .RefreshForPushAsync(Public.ToUpperInvariant(), "main", "main");

        updated.Should().Be(2);
        var shas = await ctx.Modules.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.SourceCommitSha);
        shas[onDefault.Id].Should().Be(ShaB);
        shas[onMain.Id].Should().Be(ShaB);
        shas[onTest.Id].Should().Be(ShaA);
    }

    [Fact]
    public async Task The_daily_pass_skips_a_module_pinned_to_a_commit()
    {
        var pinned = await CreateAsync(PublicApi(ShaA, SimpleExtension(), ShaA), new ModuleSourceInput(Public, ShaA, null), "pinned");
        var following = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null), "following");
        var api = PublicApi(ShaB, SimpleExtension());

        await using var ctx = _db.NewContext();
        var updated = await _db.NewModuleSourceService(ctx, _db.NewGitHubAppClient(ctx, api)).RefreshAllAsync();

        updated.Should().Be(1);
        api.Calls.Should().NotContain(c => c.Contains($"/commits/{ShaA}"));
        (await ctx.Modules.AsNoTracking().SingleAsync(m => m.Id == following.Id)).SourceCommitSha.Should().Be(ShaB);
        (await ctx.Modules.AsNoTracking().SingleAsync(m => m.Id == pinned.Id)).SourceCommitSha.Should().Be(ShaA);
    }

    [Fact]
    public async Task A_renamed_repository_is_followed_and_the_new_name_stored()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));
        const string Renamed = "contoso/doc-capture";
        var api = new FakeGitHubApi()
            .On(HttpMethod.Get, $"/repos/{Public}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Renamed, isPrivate: false))
            .ResolveRef(Renamed, "HEAD", ShaB)
            .ServeTarball(Renamed, ShaB, SimpleExtension());

        (await RefreshAsync(api, module.Id)).Should().Be(ModuleSourceRefreshOutcome.Updated);

        await using var ctx = _db.NewContext();
        var saved = await ctx.Modules.SingleAsync(m => m.Id == module.Id);
        saved.SourceRepository.Should().Be(Renamed);
        saved.SourceCommitSha.Should().Be(ShaB);
    }

    [Fact]
    public async Task A_repository_made_private_outside_the_connected_organisation_stops_refreshing()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));
        var api = new FakeGitHubApi().Repository(Public, isPrivate: true).ResolveRef(Public, "HEAD", ShaB);

        (await RefreshAsync(api, module.Id)).Should().Be(ModuleSourceRefreshOutcome.Failed);

        api.Calls.Should().NotContain(c => c.Contains("/commits/"));
        await using var ctx = _db.NewContext();
        (await ctx.Modules.SingleAsync(m => m.Id == module.Id)).SourceCommitSha.Should().Be(ShaA);
    }

    [Fact]
    public async Task A_copy_is_not_replaced_when_the_module_changed_while_it_downloaded()
    {
        var module = await CreateAsync(PublicApi(ShaA, SimpleExtension()), new ModuleSourceInput(Public, null, null));
        var now = DateTime.UtcNow;
        var stale = new ModuleSourceState(Public, "main", null, ShaA, now, [], []);
        var fresh = new ModuleSourceState(Public, "main", null, ShaB, now, [], []);

        await using var ctx = _db.NewContext();
        var replaced = await NewModuleService(ctx).ReplaceSourceCopyAsync(module.Id, stale, [], fresh);

        replaced.Should().BeFalse("the module follows the default branch, not 'main'");
        await using var verify = _db.NewContext();
        (await verify.Modules.SingleAsync(m => m.Id == module.Id)).SourceCommitSha.Should().Be(ShaA);
        (await verify.ModuleExtensionFiles.CountAsync(f => f.Folder!.ModuleId == module.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData("../../orgs/cronus-dk")]
    [InlineData("main branch")]
    [InlineData("feature/")]
    [InlineData("v1~2")]
    public async Task A_branch_that_is_not_a_ref_name_is_refused_before_GitHub_is_asked(string gitRef)
    {
        var api = new FakeGitHubApi();

        var act = () => CreateAsync(api, new ModuleSourceInput(Public, gitRef, null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Source.Ref");
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_typed_module_is_not_refreshed()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            id = (await NewModuleService(ctx).CreateAsync(Input())).Id;
        }
        var api = new FakeGitHubApi();

        (await RefreshAsync(api, id)).Should().Be(ModuleSourceRefreshOutcome.NotARepositoryModule);
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public void The_daily_pass_is_due_once_in_its_hour()
    {
        var hour = new DateTime(2026, 10, 26, ModuleSourceRefreshScheduler.SweepHourUtc, 10, 0, DateTimeKind.Utc);
        ModuleSourceRefreshScheduler.IsDue(hour, null).Should().BeTrue();
        ModuleSourceRefreshScheduler.IsDue(hour, DateOnly.FromDateTime(hour)).Should().BeFalse();
        ModuleSourceRefreshScheduler.IsDue(hour.AddHours(1), null).Should().BeFalse();
    }

    // --- Helpers -----------------------------------------------------------------

    private async Task<Module> CreateAsync(FakeGitHubApi api, ModuleSourceInput source, string key = "document-capture")
    {
        await using var ctx = _db.NewContext();
        return await _db.NewModuleSourceService(ctx, _db.NewGitHubAppClient(ctx, api))
            .CreateAsync(Input(key), source);
    }

    private async Task<ModuleSourceRefreshOutcome> RefreshAsync(FakeGitHubApi api, int moduleId)
    {
        await using var ctx = _db.NewContext();
        return await _db.NewModuleSourceService(ctx, _db.NewGitHubAppClient(ctx, api)).RefreshAsync(moduleId);
    }

    private static ModuleInput Input(string key = "document-capture") => new(
        Key: key,
        Name: "Document Capture",
        ExtensionName: string.Concat(key.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..])),
        IdRangeSize: null,
        Deprecated: false,
        Dependencies: [],
        Folders: []);

    private ModuleService NewModuleService(AppDbContext ctx) =>
        new(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx));

    private static FakeGitHubApi PublicApi(string sha, byte[] tarball, string gitRef = "HEAD") =>
        new FakeGitHubApi()
            .Repository(Public, isPrivate: false)
            .ResolveRef(Public, gitRef, sha)
            .ResolveRef(Public, "HEAD", sha)
            .ServeTarball(Public, sha, tarball);

    private static FakeGitHubApi InstallationApi() =>
        new FakeGitHubApi().On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
            HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson());

    private async Task ConnectAsync()
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
