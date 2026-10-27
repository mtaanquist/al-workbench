using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Round-trip and refusal coverage for the destructive per-tenant restore
/// (#668). The snapshot/restore path is pure SQL — no <c>pg_dump</c> — so it
/// runs against the fixture's Postgres unconditionally.
///
/// The round-trip test seeds two organisations across a spread of
/// <c>TenantTableCatalog.ContentTables</c> (including the cascade children
/// that #665 added), snapshots one, wrecks it, restores, and then compares a
/// per-table <c>to_jsonb</c> fingerprint of every catalogued table for both
/// orgs. Fingerprinting every table rather than the seeded handful means a
/// table dropped from the snapshot, or restored under the wrong org, fails
/// here rather than silently.
/// </summary>
public sealed class PerTenantBackupServiceTests : IDisposable
{
    private const int OrgA = TestDb.DefaultOrgId;
    private const int OrgB = TestDb.OtherOrgId;

    private readonly TestDb _db = new();
    private readonly string _backupsDir;
    private readonly BackupOptions _options;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero));

    public PerTenantBackupServiceTests()
    {
        _backupsDir = Path.Combine(Path.GetTempPath(), "aldt-pertenant-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupsDir);
        // This fixture's own directory, handed to the service rather than set
        // as a process-wide variable every other fixture also reads (#733).
        _options = new BackupOptions { Directory = _backupsDir };
        _db.OrgContext.IsSiteAdmin = true;
        _db.OrgContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_backupsDir, recursive: true); } catch { /* best effort */ }
        _db.Dispose();
    }

    [Fact]
    public async Task Restore_puts_the_org_back_and_leaves_the_other_org_alone()
    {
        await SeedOrgAsync(OrgA, "a");
        await SeedOrgAsync(OrgB, "b");

        var beforeA = await FingerprintAsync(OrgA);
        var beforeB = await FingerprintAsync(OrgB);

        var backup = await CreateAsync(OrgA);

        // Wreck org A: rename a parent, drop a whole parent chain (which
        // cascades its children away), and delete a leaf outright.
        await ExecuteAsync("UPDATE runtime_templates SET name = 'wrecked' WHERE organization_id = @org", OrgA);
        await ExecuteAsync("DELETE FROM recipes WHERE organization_id = @org", OrgA);
        await ExecuteAsync("DELETE FROM oe_module_translations WHERE organization_id = @org", OrgA);
        await ExecuteAsync("DELETE FROM translation_memory WHERE organization_id = @org", OrgA);
        await ExecuteAsync("DELETE FROM teams WHERE organization_id = @org", OrgA);
        (await FingerprintAsync(OrgA)).Should().NotBe(beforeA, "the test must actually damage org A before restoring");

        // Rewind an identity sequence so the restore has real work to do:
        // without the setval realignment the next insert would collide with a
        // re-imported id.
        await ExecuteAsync("SELECT setval(pg_get_serial_sequence('modules', 'id'), 1)", null);

        await RestoreAsync(backup.Id);

        (await FingerprintAsync(OrgA)).Should().Be(beforeA, "the restore must reproduce the snapshot exactly");
        (await FingerprintAsync(OrgB)).Should().Be(beforeB, "another org's rows must never be touched by a restore");

        // Cascade children specifically named in #665 / #668 — they hang off a
        // parent the delete phase removes, so they only survive if they are in
        // the catalogue and re-inserted.
        (await ScalarAsync("SELECT COUNT(*) FROM runtime_template_included_files WHERE organization_id = @org", OrgA))
            .Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM recipe_downloads WHERE organization_id = @org", OrgA))
            .Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM oe_module_translations WHERE organization_id = @org", OrgA))
            .Should().Be(1);

        // Sequence realignment: a fresh insert must get an unused id.
        var maxModuleId = await ScalarAsync("SELECT COALESCE(MAX(id), 0) FROM modules", null);
        await using var ctx = _db.NewContext();
        var fresh = new Module
        {
            OrganizationId = OrgA,
            Key = "post-restore",
            Name = "Post restore",
            ExtensionName = "Post Restore",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Modules.Add(fresh);
        await ctx.SaveChangesAsync();
        ((long)fresh.Id).Should().BeGreaterThan(maxModuleId, "the identity sequence must be realigned past restored ids");
    }

    [Fact]
    public async Task Restore_refuses_a_non_site_admin_caller()
    {
        await SeedOrgAsync(OrgA, "a");
        var backup = await CreateAsync(OrgA);

        _db.OrgContext.IsSiteAdmin = false;
        var act = () => RestoreAsync(backup.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SiteAdmin context is required*");
    }

    /// <summary>
    /// <c>per_tenant_backups</c> carries no tenant filter on the strength of every
    /// method checking for SiteAdmin first (see <c>TenantQueryFilterTests</c>), so
    /// each of them is checked here, not only the restore. The row and its file
    /// exist, so a method that skipped the check would succeed rather than throw.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("list-organisations")]
    [InlineData("download")]
    [InlineData("create")]
    [InlineData("pin")]
    [InlineData("delete")]
    public async Task Every_method_refuses_a_non_site_admin_caller(string method)
    {
        await SeedOrgAsync(OrgA, "a");
        var backup = await CreateAsync(OrgA);
        _db.OrgContext.IsSiteAdmin = false;
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        Func<Task> act = method switch
        {
            "list" => () => svc.ListAsync(),
            "list-organisations" => () => svc.ListOrganizationNamesAsync(),
            "download" => () => svc.OpenForDownloadAsync(backup.Id),
            "create" => () => svc.CreateAsync(OrgA, BackupKind.AdHoc, CancellationToken.None),
            "pin" => () => svc.SetPinnedAsync(backup.Id, pinned: true),
            "delete" => () => svc.DeleteAsync(backup.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SiteAdmin context is required*");
        File.Exists(PathFor(backup)).Should().BeTrue();
    }

    /// <summary>
    /// A per-tenant restore takes the same lock as the whole-database backup and
    /// restore (#370), so it cannot run while one of those is in flight, and
    /// refuses before it deletes any of the organisation's rows.
    /// </summary>
    [Fact]
    public async Task Restore_is_refused_while_another_backup_or_restore_holds_the_lock()
    {
        await SeedOrgAsync(OrgA, "a");
        var backup = await CreateAsync(OrgA);
        await ExecuteAsync("UPDATE runtime_templates SET name = 'changed since' WHERE organization_id = @org", OrgA);
        var before = await FingerprintAsync(OrgA);

        // Unpooled, so disposing it ends the session and with it the lock.
        await using (var holder = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_db.ConnectionString) { Pooling = false }.ConnectionString))
        {
            await holder.OpenAsync();
            await using var cmd = holder.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@k)";
            cmd.Parameters.AddWithValue("@k", BackupCoordination.BackupLockKey);
            ((bool)(await cmd.ExecuteScalarAsync())!).Should().BeTrue();

            var act = () => RestoreAsync(backup.Id);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Another backup or restore is already in progress*");
            (await FingerprintAsync(OrgA)).Should().Be(before, "a refused restore changes nothing");
        }

        await RestoreAsync(backup.Id);
        (await FingerprintAsync(OrgA)).Should().NotBe(before, "with the lock free the restore goes through");
    }

    [Fact]
    public async Task Restore_refuses_a_snapshot_from_an_older_schema_version()
    {
        await SeedOrgAsync(OrgA, "a");
        var backup = await CreateAsync(OrgA);
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.PerTenantBackups.IgnoreQueryFilters().FirstAsync(b => b.Id == backup.Id);
            row.SchemaVersion = PerTenantBackupService.CurrentSchemaVersion - 1;
            await ctx.SaveChangesAsync();
        }

        var act = () => RestoreAsync(backup.Id);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("SchemaVersion");
    }

    [Fact]
    public async Task Restore_refuses_a_snapshot_whose_manifest_names_another_org()
    {
        await SeedOrgAsync(OrgA, "a");
        await SeedOrgAsync(OrgB, "b");
        var backup = await CreateAsync(OrgA);
        RewriteManifestOrganizationId(PathFor(backup), OrgB);

        var act = () => RestoreAsync(backup.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*manifest organisation id*");
    }

    [Fact]
    public async Task Restore_refuses_when_the_snapshot_file_is_gone()
    {
        await SeedOrgAsync(OrgA, "a");
        var backup = await CreateAsync(OrgA);
        File.Delete(PathFor(backup));

        var act = () => RestoreAsync(backup.Id);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("BackupId");
    }

    [Fact]
    public async Task Restore_refuses_an_unknown_backup_id()
    {
        var act = () => RestoreAsync(4242);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("BackupId");
    }

    [Fact]
    public async Task Restore_drops_the_cached_organisation_config()
    {
        // #1218: the restore rewrites organization_files underneath the cached
        // config, which never expires on its own.
        await SeedOrgAsync(OrgA, "a");
        var backup = await CreateAsync(OrgA);
        await using (var ctx = _db.NewContext())
        {
            // Written past the config service, so its cache is not dropped here.
            ctx.OrganizationFiles.Add(new OrganizationFile
            {
                OrganizationId = OrgA, Path = "after-snapshot.txt", Content = "x", Ordering = 1,
                UpdatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _db.NewContext())
        {
            var warm = await _db.NewOrganizationConfigService(ctx).GetForAsync(OrgA);
            warm.Files.Select(f => f.Path).Should().Contain("after-snapshot.txt",
                "the cache must hold the post-snapshot state before the restore");
        }

        await RestoreAsync(backup.Id);

        await using (var ctx = _db.NewContext())
        {
            var after = await _db.NewOrganizationConfigService(ctx).GetForAsync(OrgA);
            after.Files.Select(f => f.Path).Should().Contain("a/.gitattributes")
                .And.NotContain("after-snapshot.txt",
                    "the config served after a restore must be the restored one, not the cached pre-restore copy");
        }
    }

    [Fact]
    public async Task Snapshot_reads_every_table_as_of_one_moment()
    {
        // #1217: a row committed while the snapshot is part-way through must not
        // appear in a table read later. Hold a lock on a late table, commit a
        // row into it once the snapshot is blocked there, and check the row is
        // absent: an autocommit read would take a fresh view after the wait and
        // include it, which is how orphan children got into snapshots.
        await SeedOrgAsync(OrgA, "a");

        await using var blocker = new NpgsqlConnection(_db.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        await using (var cmd = blocker.CreateCommand())
        {
            cmd.Transaction = blockerTx;
            cmd.CommandText =
                "LOCK TABLE translation_memory IN ACCESS EXCLUSIVE MODE; " +
                // A copy of the seeded row with a new id, text and hashes.
                "INSERT INTO translation_memory SELECT (jsonb_populate_record(t, jsonb_build_object(" +
                "'id', nextval(pg_get_serial_sequence('translation_memory', 'id')), 'source_text', 'Vendor', " +
                "'source_hash', 'src-late', 'target_hash', 'tgt-late'))).* " +
                "FROM translation_memory t WHERE t.organization_id = @org";
            cmd.Parameters.AddWithValue("@org", OrgA);
            await cmd.ExecuteNonQueryAsync();
        }

        var create = CreateAsync(OrgA);

        // Wait until the snapshot is queued behind the lock, so its earlier
        // tables have already been read.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await ScalarAsync(
                   "SELECT COUNT(*) FROM pg_locks WHERE relation = 'translation_memory'::regclass AND NOT granted", null) == 0)
        {
            create.IsCompleted.Should().BeFalse("the snapshot must wait on the locked table");
            DateTime.UtcNow.Should().BeBefore(deadline, "the snapshot never reached the locked table");
            await Task.Delay(50);
        }
        await blockerTx.CommitAsync();

        var backup = await create;

        using var zip = ZipFile.OpenRead(PathFor(backup));
        using var reader = new StreamReader(zip.GetEntry("translation_memory.json")!.Open());
        var rows = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
        rows.EnumerateArray().Select(r => r.GetProperty("source_text").GetString())
            .Should().Equal(["Customer"], "a row committed after the snapshot began must not be in it");
    }

    /// <summary>
    /// A snapshot whose row cannot be written must not leave its file behind:
    /// retention only ever sees files through their rows (#1269).
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_fails_leaves_no_file_behind()
    {
        await using var ctx = _db.NewContext(new FailingSnapshotSave());
        var act = () => NewService(ctx).CreateAsync(OrgA, BackupKind.AdHoc, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*row write failed*");

        var dir = Path.Combine(_backupsDir, "tenants", "default");
        Directory.GetFiles(dir).Should().BeEmpty("neither the finished file nor its staging copy may outlive the failure");
    }

    /// <summary>
    /// Snapshots carry no tenant query filter, so the prune's own organisation
    /// predicate is the only thing between one tenant's retention and another
    /// tenant's snapshots (#1279).
    /// </summary>
    [Fact]
    public async Task Prune_removes_only_that_organisations_oldest_unpinned_snapshots()
    {
        await using (var ctx = _db.NewContext())
        {
            var settings = await ctx.SystemSettings.SingleAsync(s => s.Id == 1);
            settings.PerTenantBackupRetentionCount = 1;
            await ctx.SaveChangesAsync();
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        var aNewest = await SeedSnapshotRowAsync(OrgA, "a-newest", now.AddDays(-1));
        await SeedSnapshotRowAsync(OrgA, "a-old", now.AddDays(-2));
        await SeedSnapshotRowAsync(OrgA, "a-oldest", now.AddDays(-3));
        var aPinned = await SeedSnapshotRowAsync(OrgA, "a-pinned", now.AddDays(-9), pinned: true);
        var bOld = await SeedSnapshotRowAsync(OrgB, "b-old", now.AddDays(-5));
        var bOldest = await SeedSnapshotRowAsync(OrgB, "b-oldest", now.AddDays(-6));

        await using (var ctx = _db.NewContext())
            await NewService(ctx).PruneRetentionAsync(OrgA);

        await using var read = _db.NewContext();
        (await read.PerTenantBackups.AsNoTracking().Select(b => b.Id).ToListAsync())
            .Should().BeEquivalentTo([aNewest, aPinned, bOld, bOldest]);
    }

    private async Task<int> SeedSnapshotRowAsync(int organizationId, string name, DateTime createdAt, bool pinned = false)
    {
        await using var ctx = _db.NewContext();
        var row = new PerTenantBackup
        {
            OrganizationId = organizationId,
            FileName = name + PerTenantBackupService.FileSuffix,
            FileSizeBytes = 1,
            CreatedAt = createdAt,
            Kind = BackupKind.Scheduled,
            SchemaVersion = PerTenantBackupService.CurrentSchemaVersion,
            IsPinned = pinned,
        };
        ctx.PerTenantBackups.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Fails the save that registers a snapshot, after its file has been written.</summary>
    private sealed class FailingSnapshotSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken ct = default) =>
            eventData.Context!.ChangeTracker.Entries<PerTenantBackup>().Any(e => e.State == EntityState.Added)
                ? throw new InvalidOperationException("The row write failed.")
                : ValueTask.FromResult(result);
    }

    // ===== Fixture helpers =====

    private PerTenantBackupService NewService(AppDbContext ctx)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString,
        }).Build();
        return new PerTenantBackupService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), _db.NewOrganizationConfigService(ctx), config,
            NullLogger<PerTenantBackupService>.Instance, _clock, _options);
    }

    private async Task<PerTenantBackup> CreateAsync(int organizationId)
    {
        await using var ctx = _db.NewContext();
        var row = await NewService(ctx).CreateAsync(organizationId, BackupKind.AdHoc, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1)); // keep snapshot file names distinct
        return row;
    }

    private async Task RestoreAsync(int backupId)
    {
        await using var ctx = _db.NewContext();
        await NewService(ctx).RestoreAsync(backupId, CancellationToken.None);
    }

    private string PathFor(PerTenantBackup row)
    {
        using var ctx = _db.NewContext();
        var slug = ctx.Organizations.IgnoreQueryFilters().AsNoTracking()
            .First(o => o.Id == row.OrganizationId).Slug;
        return Path.Combine(_backupsDir, "tenants", slug, row.FileName);
    }

    /// <summary>
    /// Rewrites the <c>organization_id</c> inside a snapshot's manifest so the
    /// restore's manifest/row consistency check has something to catch.
    /// </summary>
    private static void RewriteManifestOrganizationId(string zipPath, int organizationId)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entry = zip.GetEntry(PerTenantBackupService.ManifestEntryName)!;
        string json;
        using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
        using var document = JsonDocument.Parse(json);
        var rewritten = new Dictionary<string, object?>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            rewritten[property.Name] = property.NameEquals("organization_id")
                ? organizationId
                : JsonSerializer.Deserialize<object>(property.Value.GetRawText());
        }
        entry.Delete();
        var replacement = zip.CreateEntry(PerTenantBackupService.ManifestEntryName);
        using var writer = new StreamWriter(replacement.Open());
        writer.Write(JsonSerializer.Serialize(rewritten));
    }

    /// <summary>
    /// A stable, order-independent digest of every catalogued content table's
    /// rows for one organisation. Built from <c>to_jsonb</c> so a changed
    /// column value, a lost row, or a row landing under the wrong org all
    /// show up as a difference.
    /// </summary>
    private async Task<string> FingerprintAsync(int organizationId)
    {
        var sb = new StringBuilder();
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in TenantTableCatalog.ContentTables
                     .Where(TenantTableCatalog.TablesWithDirectOrgColumn.Contains))
        {
            sb.Append(table).Append(":\n");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT to_jsonb(t)::text FROM {table} t WHERE t.organization_id = @org ORDER BY to_jsonb(t)::text";
            cmd.Parameters.AddWithValue("@org", organizationId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) sb.Append("  ").Append(reader.GetString(0)).Append('\n');
        }
        return sb.ToString();
    }

    private async Task ExecuteAsync(string sql, int? organizationId)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (organizationId is int org) cmd.Parameters.AddWithValue("@org", org);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql, int? organizationId)
    {
        await using var conn = new NpgsqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (organizationId is int org) cmd.Parameters.AddWithValue("@org", org);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Seeds one organisation with content spanning both the long-standing
    /// authoring tables and the ones #665 added to the catalogue, plus three
    /// cascade children.
    /// </summary>
    private async Task SeedOrgAsync(int organizationId, string tag)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var ctx = _db.NewContext();

        var file = new OrganizationFile
        {
            OrganizationId = organizationId,
            Path = $"{tag}/.gitattributes",
            Content = "* text=auto\n",
            UpdatedAt = now,
        };
        var template = new RuntimeTemplate
        {
            OrganizationId = organizationId,
            Key = $"{tag}-template",
            Runtime = "14.0",
            Name = $"Template {tag}",
            CoreIdRangeFrom = 50000,
            CoreIdRangeTo = 50099,
            ModuleIdRangeStart = 50100,
            ModuleIdRangeSize = 100,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var module = new Module
        {
            OrganizationId = organizationId,
            Key = $"{tag}-module",
            Name = $"Module {tag}",
            ExtensionName = $"Module {tag}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var team = new Team
        {
            OrganizationId = organizationId,
            Name = $"Team {tag}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var recipe = new Recipe
        {
            OrganizationId = organizationId,
            Title = $"Recipe {tag}",
            Description = "Posts a CRONUS sales order.",
            Keywords = "sales",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var release = new OeRelease
        {
            OrganizationId = organizationId,
            Label = $"Release {tag}",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var translationMemory = new TranslationMemoryEntry
        {
            OrganizationId = organizationId,
            SourceLanguage = "en-US",
            TargetLanguage = "da-DK",
            SourceText = "Customer",
            TargetText = "Debitor",
            SourceHash = $"src-{tag}",
            TargetHash = $"tgt-{tag}",
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
        };
        ctx.OrganizationFiles.Add(file);
        ctx.RuntimeTemplates.Add(template);
        ctx.Modules.Add(module);
        ctx.Teams.Add(team);
        ctx.Recipes.Add(recipe);
        ctx.OeReleases.Add(release);
        ctx.TranslationMemory.Add(translationMemory);
        await ctx.SaveChangesAsync();

        var oeModule = new OeModule
        {
            OrganizationId = organizationId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = $"Base {tag}",
            Publisher = "CRONUS",
            Version = "26.0.0.0",
            CreatedAt = now,
        };
        ctx.OeModules.Add(oeModule);
        ctx.RuntimeTemplateIncludedFiles.Add(new RuntimeTemplateIncludedFile
        {
            OrganizationId = organizationId,
            RuntimeTemplateId = template.Id,
            OrganizationFileId = file.Id,
            Ordering = 0,
        });
        ctx.RecipeDownloads.Add(new RecipeDownload
        {
            OrganizationId = organizationId,
            RecipeId = recipe.Id,
            CustomerName = "CRONUS A/S",
            DownloadedAt = now,
        });
        await ctx.SaveChangesAsync();

        ctx.OeModuleObjects.Add(new OeModuleObject
        {
            OrganizationId = organizationId,
            ModuleId = oeModule.Id,
            Kind = "table",
            ObjectId = 18,
            Name = "Customer",
            LineNumber = 1,
        });
        ctx.OeModuleTranslations.Add(new OeModuleTranslation
        {
            OrganizationId = organizationId,
            ModuleId = oeModule.Id,
            LanguageCode = "da-DK",
            TransUnitId = $"Table 18 - Field 1 - Property Caption ({tag})",
            SourceText = "No.",
            TargetText = "Nr.",
            CreatedAt = now,
        });
        await ctx.SaveChangesAsync();
    }
}
