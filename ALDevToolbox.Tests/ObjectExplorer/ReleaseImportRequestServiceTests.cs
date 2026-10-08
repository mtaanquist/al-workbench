using System.IO.Compression;
using System.Net.Http;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The path-selection rule on <see cref="ReleaseImportRequestService"/>: which
/// shape of upload picks which ingest path, and what each path puts on the
/// queue. This is the policy the import endpoint used to hold inline, so the
/// tests drive the service directly with plain <see cref="UploadedFile"/>s
/// rather than posting a multipart form.
/// </summary>
public sealed class ReleaseImportRequestServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _tempPaths = new();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
        _db.Dispose();
    }

    // ── Retrying a project build (#1110) ─────────────────────────────────

    [Fact]
    public async Task Retrying_a_project_build_is_refused_before_anything_is_wiped_for_someone_who_cannot_manage_it()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", Visibility = Domain.Entities.ObjectExplorer.ProjectVisibility.Private,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        var release = new Domain.Entities.ObjectExplorer.OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "CRONUS build", Kind = "project", Status = "ready",
            ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        ctx.Users.Add(new Domain.Entities.User
        {
            Id = 9632, OrganizationId = TestDb.DefaultOrgId, Email = "nils@example.com", PasswordHash = "x",
            DisplayName = "Nils", Role = Domain.Entities.UserRole.Editor, Status = Domain.Entities.UserStatus.Active, CreatedAt = now,
        });
        await ctx.SaveChangesAsync();
        await new PersistedImportJobs(ctx, TimeProvider.System).CreateAsync(release.Id,
            new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, null, false, true),
            new ReleaseImportSource.ProjectBuild(project.Id), storeSymbolReference: false);
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = 9632;
        var queue = new ReleaseImportQueue();

        var act = () => NewService(ctx, queue).RetryAsync(release.Id, new ReleaseRetrySubmission(
            DvdUrl: string.Empty, CalEncoding: "850", StoreSymbolReference: false, FolderZip: null, CalTxtFile: null));

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == release.Id)).Status.Should().Be("ready");
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    // ── A retried release reads as importing until its job is queued (#1180) ──

    [Fact]
    public async Task A_retried_release_reads_as_importing_while_it_is_reopened_and_requeued()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new Domain.Entities.ObjectExplorer.OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "BC 29.0 (DK)", Kind = "first_party", Status = "failed",
            ImportedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        var ingests = new ReleaseIngests();
        // A full queue holds the retry at its last step, the enqueue, after the
        // release is reopened and wiped but before its job is queued.
        var queue = new ReleaseImportQueue();
        var filler = new ReleaseImportJob(int.MaxValue, new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, null, false, true),
            new ReleaseImportSource.Backfill());
        for (var i = 0; i < 16; i++) await queue.EnqueueAsync(filler);

        var retry = NewService(ctx, queue, ingests: ingests).RetryAsync(release.Id, new ReleaseRetrySubmission(
            DvdUrl: string.Empty, CalEncoding: "850", StoreSymbolReference: false,
            FolderZip: EmptyZip("applications.zip"), CalTxtFile: null));
        while (!retry.IsCompleted && !(ingests.IsRunning(release.Id) && await StatusAsync(release.Id) == "ingesting"))
        {
            await Task.Delay(20);
        }
        retry.IsCompleted.Should().BeFalse("the full queue holds the retry at its enqueue");
        ingests.IsRunning(release.Id).Should().BeTrue("a build polling the release must not take it for abandoned");

        queue.Reader.TryRead(out _).Should().BeTrue();
        (await retry.WaitAsync(TimeSpan.FromMinutes(1))).Should().BeOfType<ReleaseImportOutcome.Queued>();
        ingests.IsRunning(release.Id).Should().BeFalse("its queued job says it is being worked on from here");
        while (queue.Reader.TryRead(out var job))
        {
            if (job.Source is ReleaseImportSource.StagedZip staged) Track(staged.TempPath);
        }
    }

    private async Task<string?> StatusAsync(int releaseId)
    {
        await using var read = _db.NewContext();
        return await read.OeReleases.AsNoTracking().Where(r => r.Id == releaseId).Select(r => r.Status).SingleAsync();
    }

    // ── C/AL TXT wins, and decides the kind server-side ──────────────────

    [Fact]
    public async Task A_cal_txt_upload_queues_a_cal_job_with_the_chosen_codepage()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        var outcome = await NewService(ctx, queue).SubmitAsync(Submission(
            calTxtFile: Text("export.txt", "OBJECT Codeunit 50000 CRONUS Test"),
            calEncoding: "1252"));

        var releaseId = outcome.Should().BeOfType<ReleaseImportOutcome.Queued>().Subject.ReleaseId;
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.ReleaseId.Should().Be(releaseId);
        var source = job.Source.Should().BeOfType<ReleaseImportSource.CalTxt>().Subject;
        source.EncodingName.Should().Be("1252");
        Track(source.TempPath);
        File.ReadAllText(source.TempPath).Should().Be("OBJECT Codeunit 50000 CRONUS Test");
    }

    [Fact]
    public async Task A_cal_txt_upload_is_a_cal_release_whatever_the_form_said()
    {
        // The C/AL tab hides kind / parent / publisher, so a stale-form or
        // no-JS post must not be able to smuggle them in.
        await using var ctx = _db.NewContext();

        var outcome = await NewService(ctx, new ReleaseImportQueue()).SubmitAsync(Submission(
            kind: "first_party",
            publisher: "Someone Else",
            projectName: "Someone Else's project",
            parentReleaseId: 12345,
            calTxtFile: Text("export.txt", "OBJECT Codeunit 50000 CRONUS Test")));

        var releaseId = outcome.Should().BeOfType<ReleaseImportOutcome.Queued>().Subject.ReleaseId;
        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Kind.Should().Be("cal");
        release.ParentReleaseId.Should().BeNull();
        release.Publisher.Should().BeNullOrEmpty();
        release.ProjectName.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task A_cal_txt_upload_wins_over_a_folder_zip_in_the_same_post()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        await NewService(ctx, queue).SubmitAsync(Submission(
            calTxtFile: Text("export.txt", "OBJECT Codeunit 50000 CRONUS Test"),
            folderZip: EmptyZip("applications.zip")));

        queue.Reader.TryRead(out var job).Should().BeTrue();
        var source = job!.Source.Should().BeOfType<ReleaseImportSource.CalTxt>().Subject;
        Track(source.TempPath);
    }

    // ── URL, then folder ZIP, then individual files ──────────────────────

    [Fact]
    public async Task A_pasted_url_queues_a_url_job_and_beats_a_folder_zip()
    {
        await SetAllowlistAsync("cronus.example");
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        await NewService(ctx, queue).SubmitAsync(Submission(
            dvdUrl: "https://cronus.example/bc.zip",
            folderZip: EmptyZip("applications.zip")));

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.Source.Should().BeOfType<ReleaseImportSource.Url>()
            .Which.DownloadUrl.Should().Be("https://cronus.example/bc.zip");
    }

    [Fact]
    public async Task A_url_off_the_allow_list_is_refused_before_a_release_row_exists()
    {
        // No allow-list is configured, so no host is permitted. The refusal has
        // to land before BeginReleaseAsync, or a bad paste would leave a
        // half-created release behind.
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        var act = async () => await NewService(ctx, queue).SubmitAsync(Submission(
            dvdUrl: "https://not-allowed.example/bc.zip"));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DvdUrl");
        queue.Reader.TryRead(out _).Should().BeFalse();
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_zip_queues_a_staged_zip_walked_as_a_whole_archive()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();

        var outcome = await NewService(ctx, queue).SubmitAsync(Submission(
            folderZip: EmptyZip("applications.zip")));

        var releaseId = outcome.Should().BeOfType<ReleaseImportOutcome.Queued>().Subject.ReleaseId;
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.ReleaseId.Should().Be(releaseId);
        var source = job.Source.Should().BeOfType<ReleaseImportSource.StagedZip>().Subject;
        source.IsDvd.Should().BeFalse("an uploaded folder ZIP is walked whole, not as a DVD subset");
        Track(source.TempPath);
        File.Exists(source.TempPath).Should().BeTrue("the worker reopens the staged file after the request ends");
    }

    [Fact]
    public async Task Individual_app_files_import_in_request_rather_than_queueing()
    {
        await using var ctx = _db.NewContext();
        var queue = new ReleaseImportQueue();
        var appPath = Path.Combine(FixtureRoot, "Microsoft_DK_Core.app");

        var outcome = await NewService(ctx, queue).SubmitAsync(Submission(
            appFiles: new[] { FromFile(appPath) }));

        var summary = outcome.Should().BeOfType<ReleaseImportOutcome.Imported>().Subject.Summary;
        summary.ModulesImported.Should().BeGreaterThan(0);
        queue.Reader.TryRead(out _).Should().BeFalse("a handful of .app files is small enough to stay synchronous");

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == summary.ReleaseId);
        release.Kind.Should().Be("first_party");
    }

    // ── Refusals ────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_a_post_with_nothing_picked()
    {
        await using var ctx = _db.NewContext();

        var act = async () => await NewService(ctx, new ReleaseImportQueue()).SubmitAsync(Submission());

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("AppFiles");
    }

    [Fact]
    public async Task Refuses_a_kind_that_is_neither_first_nor_third_party()
    {
        await using var ctx = _db.NewContext();

        var act = async () => await NewService(ctx, new ReleaseImportQueue()).SubmitAsync(Submission(
            kind: "project",
            folderZip: EmptyZip("applications.zip")));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Kind");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static ReleaseImportSubmission Submission(
        string? label = null,
        string kind = "first_party",
        int? parentReleaseId = null,
        string publisher = "",
        string projectName = "",
        bool storeSymbolReference = false,
        string dvdUrl = "",
        string calEncoding = "850",
        UploadedFile? calTxtFile = null,
        UploadedFile? folderZip = null,
        IReadOnlyList<UploadedFile>? appFiles = null,
        IReadOnlyList<UploadedFile>? sourceZips = null) =>
        new(
            Label: label ?? "BC " + Guid.NewGuid().ToString("N"),
            Kind: kind,
            ParentReleaseId: parentReleaseId,
            Publisher: publisher,
            ProjectName: projectName,
            StoreSymbolReference: storeSymbolReference,
            DvdUrl: dvdUrl,
            CalEncoding: calEncoding,
            CalTxtFile: calTxtFile,
            FolderZip: folderZip,
            AppFiles: appFiles ?? Array.Empty<UploadedFile>(),
            SourceZips: sourceZips ?? Array.Empty<UploadedFile>());

    private static UploadedFile Text(string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new UploadedFile(name, bytes.Length, () => new MemoryStream(bytes, writable: false));
    }

    private static UploadedFile EmptyZip(string name)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("applications/");
        }
        var bytes = buffer.ToArray();
        return new UploadedFile(name, bytes.Length, () => new MemoryStream(bytes, writable: false));
    }

    private static UploadedFile FromFile(string path) =>
        new(Path.GetFileName(path), new FileInfo(path).Length, () => File.OpenRead(path));

    private void Track(string tempPath) => _tempPaths.Add(tempPath);

    private ReleaseImportRequestService NewService(
        Data.AppDbContext ctx, ReleaseImportQueue queue, ProjectBuildQueue? builds = null, ReleaseIngests? ingests = null)
    {
        var translations = new TranslationImportService(
            ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations,
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, ingests: ingests);
        var management = new ReleaseManagementService(
            ctx, _db.OrgContext, NullLogger<ReleaseManagementService>.Instance);
        var downloads = new DvdDownloadService(
            new ThrowingHttpClientFactory(),
            _db.NewSystemSettingsService(ctx),
            NullLogger<DvdDownloadService>.Instance);
        var persistedJobs = new PersistedImportJobs(ctx, TimeProvider.System);
        var projectBuilds = new ProjectBuildImporter(
            importer, management, builds ?? new ProjectBuildQueue(), persistedJobs, ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            new CloneCredentialResolver(
                new UserRepositoryTokenService(ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider),
                _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, new FakeGitHubApi())),
                _db.OrgContext, NullLogger<CloneCredentialResolver>.Instance),
            TimeProvider.System, NullLogger<ProjectBuildImporter>.Instance);
        return new ReleaseImportRequestService(
            importer, management, downloads, queue, persistedJobs, _db.OrgContext, projectBuilds);
    }

    private async Task SetAllowlistAsync(string hosts)
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(
            ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await settings.SaveAsync(new SystemSettingsInput(
            SmtpHost: null, SmtpPort: null, SmtpUser: null,
            SmtpPassword: null, ClearSmtpPassword: false,
            SmtpFrom: null, SmtpFromName: null, SmtpUseStartTls: null, BannerText: null,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: false,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: hosts, DisabledTools: Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: null));
    }

    // A queued URL is only validated, never fetched, in these tests.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Queueing a URL import must not download anything.");
    }
}
