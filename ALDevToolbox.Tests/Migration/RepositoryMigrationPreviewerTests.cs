using System.Diagnostics;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Migration;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using static ALDevToolbox.Tests.Migration.MigrationTestFiles;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// A Migrate repositories preview run end to end against real git and local
/// repositories (#1699): main is cloned without any .alpackages folder, the
/// commit read is recorded, a repository without main is reported rather than
/// failing the run, and the clones are removed afterwards.
/// </summary>
public sealed class RepositoryMigrationPreviewerTests : IDisposable
{
    private const string BaseId = "11111111-1111-1111-1111-111111111111";

    private readonly TestDb _db = new();
    private readonly MigrationTempDir _temp = new();
    private readonly RepositoryMigrationQueue _queue = new(TimeProvider.System);

    public void Dispose()
    {
        _temp.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Clones_main_without_alpackages_and_reports_a_repository_without_main()
    {
        if (!GitAvailable()) return;
        var userId = await SeedUserWithTokenAsync();

        var withMain = _temp.Sub("origin-base");
        WriteApp(withMain, BaseId, "CRONUS Base");
        WriteFile(Path.Combine(withMain, "src", "Codeunit.al"), "codeunit 50100 X { }");
        WriteFile(Path.Combine(withMain, ".alpackages", "Microsoft_Base Application.app"), "big binary");
        WriteFile(Path.Combine(withMain, "Test", ".alpackages", "Microsoft_Test.app"), "big binary");
        WriteFile(Path.Combine(withMain, "Test", "TestCodeunit.al"), "codeunit 50200 T { }");
        var commit = CommitAll(withMain, "main");

        var withoutMain = _temp.Sub("origin-old");
        WriteApp(withoutMain, "22222222-2222-2222-2222-222222222222", "CRONUS Old");
        CommitAll(withoutMain, "master");

        var job = Job(userId, Repo("r1", "cronus-base", withMain), Repo("r2", "cronus-old", withoutMain));
        _queue.TryStart(job, "cronus/Sales", "https://dev.azure.com/cronus/Sales", out _).Should().NotBeNull();
        var workRoot = _temp.Sub("work");
        var checkedOut = new List<string>();

        await using (var ctx = _db.NewContext())
        {
            var previewer = NewPreviewer(ctx);
            previewer.WorkRoot = workRoot;
            previewer.Cloned = dir => checkedOut.AddRange(Directory
                .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal)));
            using (AmbientOrganizationScope.Enter(job.Identity))
            {
                await previewer.RunAsync(job, CancellationToken.None);
            }
        }

        var preview = _queue.Get(job.Id, TestDb.DefaultOrgId, userId)!;
        preview.Status.Should().Be(MigrationPreviewStatus.Done);
        preview.Repositories.Should().BeEquivalentTo(new[]
        {
            new { Name = "cronus-base", Status = MigrationRepositoryStatus.Cloned, Commit = (string?)commit },
            new { Name = "cronus-old", Status = MigrationRepositoryStatus.Failed, Commit = (string?)null },
        }, o => o.ExcludingMissingMembers());
        preview.Repositories[1].Message.Should().Contain("has no main branch");

        checkedOut.Should().BeEquivalentTo("app.json", "src/Codeunit.al", "Test/TestCodeunit.al");

        var analysis = preview.Analysis!;
        analysis.Extensions.Select(e => e.Folder).Should().Equal("cronus-base");
        analysis.Findings.Should().ContainSingle(f => f.Severity == MigrationFindingSeverity.Blocking && f.RepositoryName == "cronus-old");
        analysis.CanMigrate.Should().BeFalse();

        Directory.EnumerateFileSystemEntries(workRoot).Should().BeEmpty("the clones are removed when the preview ends");
    }

    [Fact]
    public async Task Builds_the_templated_workspace_once_the_sources_can_move()
    {
        if (!GitAvailable()) return;
        var userId = await SeedUserWithTokenAsync();
        await MigrationTestServices.SeedTemplateAsync(_db);
        var origin = _temp.Sub("origin-base");
        WriteApp(origin, BaseId, "CRONUS Base");
        WriteFile(Path.Combine(origin, "README.md"), "# Old");
        CommitAll(origin, "main");

        var job = Job(userId, Repo("r1", "cronus-base", origin));
        _queue.TryStart(job, "cronus/Sales", "https://dev.azure.com/cronus/Sales", out _).Should().NotBeNull();
        var workRoot = _temp.Sub("work");
        await using (var ctx = _db.NewContext())
        {
            var previewer = NewPreviewer(ctx);
            previewer.WorkRoot = workRoot;
            using (AmbientOrganizationScope.Enter(job.Identity))
            {
                await previewer.RunAsync(job, CancellationToken.None);
            }
        }

        var preview = _queue.Get(job.Id, TestDb.DefaultOrgId, userId)!;
        preview.Status.Should().Be(MigrationPreviewStatus.Done);
        preview.CanMigrate.Should().BeTrue();
        preview.Workspace!.AppJson.Select(a => a.Folder).Should().Equal("cronus-base");
        preview.Workspace.RootFiles.Should().Contain("README.md");
        Directory.EnumerateFileSystemEntries(workRoot).Should().BeEmpty("the built workspace is removed with the clones");
    }

    [Fact]
    public async Task A_migration_stops_before_creating_anything_when_main_moved_since_the_preview()
    {
        if (!GitAvailable()) return;
        var userId = await SeedUserWithTokenAsync();
        await MigrationTestServices.SeedTemplateAsync(_db);
        var origin = _temp.Sub("origin-moved");
        WriteApp(origin, BaseId, "CRONUS Base");
        CommitAll(origin, "main");

        var job = Job(userId, Repo("r1", "cronus-base", origin)) with
        {
            Create = new MigrationCreateRequest("cronus-as", true, false, null,
                new Dictionary<string, string> { ["cronus-base"] = "0000000000000000000000000000000000000000" }),
        };
        _queue.TryStart(job, "cronus/Sales", "https://dev.azure.com/cronus/Sales", out _).Should().NotBeNull();
        await using (var ctx = _db.NewContext())
        {
            var previewer = NewPreviewer(ctx);
            previewer.WorkRoot = _temp.Sub("work-moved");
            using (AmbientOrganizationScope.Enter(job.Identity))
            {
                await previewer.RunAsync(job, CancellationToken.None);
            }
        }

        var run = _queue.Get(job.Id, TestDb.DefaultOrgId, userId)!;
        run.IsMigration.Should().BeTrue();
        run.Status.Should().Be(MigrationPreviewStatus.Failed);
        run.Error.Should().Contain("main has changed in cronus-base").And.Contain("Nothing was created");
        run.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_token_fails_the_preview_with_where_to_add_one()
    {
        var userId = await SeedUserAsync();
        var job = Job(userId, Repo("r1", "cronus-base", _temp.Sub("unused")));
        _queue.TryStart(job, "cronus/Sales", "https://dev.azure.com/cronus/Sales", out _);

        await using (var ctx = _db.NewContext())
        {
            using (AmbientOrganizationScope.Enter(job.Identity))
            {
                await NewPreviewer(ctx).RunAsync(job, CancellationToken.None);
            }
        }

        var preview = _queue.Get(job.Id, TestDb.DefaultOrgId, userId)!;
        preview.Status.Should().Be(MigrationPreviewStatus.Failed);
        preview.Error.Should().Contain("Account → Repository access");
    }

    private RepositoryMigrationPreviewer NewPreviewer(AppDbContext ctx) => new(
        new ProcessRunner(),
        new UserRepositoryTokenService(ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider),
        _queue,
        MigrationTestServices.NewBuilder(_db, ctx),
        _db.NewRepositoryMigrationPublisher(ctx),
        NullLogger<RepositoryMigrationPreviewer>.Instance);

    private static MigrationPreviewJob Job(int userId, params AzureDevOpsRepository[] repositories) => new(
        Guid.NewGuid(),
        new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, userId, false, false),
        new AzureDevOpsProject("cronus", "Sales"),
        repositories,
        MigrationTestServices.Target());

    private static AzureDevOpsRepository Repo(string id, string name, string originDir) =>
        new(id, name, new Uri(originDir).AbsoluteUri, "https://dev.azure.com/cronus/Sales/_git/" + name, "main", 1, false);

    private static string CommitAll(string dir, string branch)
    {
        Git(dir, "init", "--quiet", "--initial-branch", branch);
        // A blobless clone of a local repository needs the server side to allow filters.
        Git(dir, "config", "uploadpack.allowFilter", "true");
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "--quiet", "-m", "initial");
        return Git(dir, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<int> SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            DisplayName = "Admin",
            Role = UserRole.Admin,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = user.Id;
        return user.Id;
    }

    private async Task<int> SeedUserWithTokenAsync()
    {
        var id = await SeedUserAsync();
        await using var ctx = _db.NewContext();
        await new UserRepositoryTokenService(ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider)
            .SaveTokenAsync(RepositoryProvider.AzureDevOps, "ado-pat", clear: false);
        return id;
    }
}
