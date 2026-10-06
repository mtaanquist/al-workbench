using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Tests.ObjectExplorer;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Catalogue;

/// <summary>
/// The daily sync that adds newly shipped Business Central release waves to the
/// application-version catalogue: how a wave's row is derived from its major
/// version, which waves count as new, and the sweep over every organisation.
/// </summary>
public sealed class ApplicationVersionSyncTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "aldt-appversion-sync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, recursive: true);
    }

    // ── Deriving a wave ─────────────────────────────────────────────────

    [Theory]
    [InlineData(28, "bc-2026-rw1", "Business Central 2026 release wave 1", "28.0.0.0", "17.0")]
    [InlineData(27, "bc-2025-rw2", "Business Central 2025 release wave 2", "27.0.0.0", "16.0")]
    [InlineData(29, "bc-2026-rw2", "Business Central 2026 release wave 2", "29.0.0.0", "18.0")]
    [InlineData(17, "bc-2020-rw2", "Business Central 2020 release wave 2", "17.0.0.0", "6.0")]
    public void A_wave_row_follows_from_its_major_version(int major, string key, string name, string application, string runtime)
    {
        BusinessCentralWaves.ForMajor(major).Should().Be(new WaveEntry(major, key, name, application, runtime));
    }

    [Fact]
    public void Only_a_wave_whose_first_release_has_shipped_counts_newest_first()
    {
        var waves = BusinessCentralWaves.FromVersions(
        [
            "28.5.54151.0", "28.0.46665.48632", "29.0.54011.55644",
            // An update with no .0 alongside it, a prerelease, too old for the feed, and noise.
            "30.1.1.0", "31.0.1.0-preview", "16.0.1.0", "not-a-version",
        ]);

        waves.Select(w => w.Major).Should().Equal(29, 28);
    }

    // ── Adding to the catalogue ─────────────────────────────────────────

    [Fact]
    public async Task A_newer_wave_is_added_above_the_existing_rows_and_becomes_latest()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null), ("bc-2025-rw2", "27.0.0.0", 1, null));

        await using (var ctx = _db.NewContext())
        {
            var added = await NewService(ctx).AddNewWavesAsync(["27.0.38460.40293", "28.2.50931.51111", "29.0.54011.55644"]);
            added.Select(a => a.Key).Should().Equal("bc-2026-rw2");
        }

        await using var read = _db.NewContext();
        var latest = await NewService(read).GetLatestAsync();
        latest!.Key.Should().Be("bc-2026-rw2");
        latest.Name.Should().Be("Business Central 2026 release wave 2");
        latest.Application.Should().Be("29.0.0.0");
        latest.Runtime.Should().Be("18.0");
        latest.Deprecated.Should().BeFalse();

        // The admin's own order is untouched.
        var all = await NewService(read).GetActiveAsync();
        all.Select(a => a.Key).Should().Equal("bc-2026-rw2", "bc-2026-rw1", "bc-2025-rw2");
        all.Single(a => a.Key == "bc-2026-rw1").Ordering.Should().Be(0);
    }

    [Fact]
    public async Task Several_new_waves_are_added_newest_on_top()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2025-rw1", "26.0.0.0", 0, null));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).AddNewWavesAsync(["26.0.1.0", "27.0.1.0", "28.0.1.0"]);
        }

        await using var read = _db.NewContext();
        (await NewService(read).GetActiveAsync()).Select(a => a.Key)
            .Should().Equal("bc-2026-rw1", "bc-2025-rw2", "bc-2025-rw1");
    }

    [Fact]
    public async Task Older_waves_missing_from_the_catalogue_are_not_backfilled()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null));

        await using var ctx = _db.NewContext();
        var added = await NewService(ctx).AddNewWavesAsync(["25.0.1.0", "26.0.1.0", "27.0.1.0", "28.0.1.0"]);

        added.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wave_an_admin_removed_does_not_come_back()
    {
        await SeedAsync(TestDb.DefaultOrgId,
            ("bc-2026-rw1", "28.0.0.0", 0, null),
            ("bc-2026-rw2", "29.0.0.0", 1, DateTime.UtcNow));

        await using var ctx = _db.NewContext();
        var added = await NewService(ctx).AddNewWavesAsync(["28.0.1.0", "29.0.1.0"]);

        added.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deprecated_newest_wave_still_counts_as_present()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 1, null), ("bc-2026-rw2", "29.0.0.0", 0, null));
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.ApplicationVersions.SingleAsync(a => a.Key == "bc-2026-rw2");
            row.Deprecated = true;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await NewService(read).AddNewWavesAsync(["28.0.1.0", "29.0.1.0"])).Should().BeEmpty();
        (await NewService(read).GetLatestAsync())!.Key.Should().Be("bc-2026-rw1");
    }

    [Fact]
    public async Task Saving_an_editor_opened_before_the_sync_keeps_the_added_wave()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null), ("bc-2025-rw2", "27.0.0.0", 1, null));

        // The admin opens the editor ...
        List<ApplicationVersion> shown;
        await using (var ctx = _db.NewContext())
        {
            shown = await NewService(ctx).GetActiveAsync(includeDeprecated: true);
        }
        // ... the sync adds 29 ...
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).AddNewWavesAsync(["29.0.1.0"]);
        }
        // ... and the admin saves what they saw, minus the row they removed.
        var kept = shown.Single(r => r.Key == "bc-2026-rw1");
        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).SaveAsync(
                [new ApplicationVersionInput(kept.Id, kept.Key, kept.Name, kept.Application, kept.Runtime, Deprecated: false)],
                shown.Select(r => r.Id).ToList());
        }

        (await KeysAsync(TestDb.DefaultOrgId)).Should().Equal("bc-2026-rw2", "bc-2026-rw1");
    }

    [Fact]
    public async Task An_update_row_an_admin_added_counts_as_its_wave()
    {
        // 29.1 typed in by hand means wave 29 is already there.
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw2-u1", "29.1.0.0", 0, null));

        await using var ctx = _db.NewContext();
        (await NewService(ctx).AddNewWavesAsync(["29.0.1.0", "29.1.1.0"])).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_catalogue_gets_only_the_newest_wave()
    {
        await ClearAsync();

        await using var ctx = _db.NewContext();
        var added = await NewService(ctx).AddNewWavesAsync(["27.0.1.0", "28.0.1.0", "29.0.1.0"]);

        added.Select(a => a.Key).Should().Equal("bc-2026-rw2");
    }

    [Fact]
    public async Task Running_twice_adds_nothing_the_second_time()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null));
        string[] versions = ["28.0.1.0", "29.0.1.0"];

        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).AddNewWavesAsync(versions)).Should().ContainSingle();
        }
        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).AddNewWavesAsync(versions)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Adding_waves_needs_an_organisation_in_scope()
    {
        await using var ctx = new AppDbContext(Options(), new HttpOrganizationContext(new HttpContextAccessor()));
        var service = new ApplicationVersionService(ctx, NullLogger<ApplicationVersionService>.Instance,
            new HttpOrganizationContext(new HttpContextAccessor()));

        await service.Invoking(s => s.AddNewWavesAsync(["29.0.1.0"]))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Reading the feed ────────────────────────────────────────────────

    [Fact]
    public async Task The_shipped_versions_are_read_off_the_Microsoft_feeds_Application_package()
    {
        var feeds = FeedWith("28.0.46665.48632", "28.1.49838.49886", "29.0.54011.55644");

        var versions = await NewResolver(feeds).ListMicrosoftApplicationVersionsAsync();

        versions.Should().BeEquivalentTo(["28.0.46665.48632", "28.1.49838.49886", "29.0.54011.55644"]);
        feeds.Requests.Should().Contain("https://feeds.test/mssymbols/flat2/microsoft.application.symbols/index.json");
    }

    // ── The sweep ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_adds_the_new_wave_to_every_organisation()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null));
        await SeedAsync(TestDb.OtherOrgId, ("bc-2025-rw2", "27.0.0.0", 0, null));

        var ok = await NewScheduler(FeedWith("27.0.1.0", "28.0.1.0", "29.0.1.0")).SyncAsync(CancellationToken.None);

        ok.Should().BeTrue();
        (await KeysAsync(TestDb.DefaultOrgId)).Should().Equal("bc-2026-rw2", "bc-2026-rw1");
        (await KeysAsync(TestDb.OtherOrgId)).Should().Equal("bc-2026-rw2", "bc-2026-rw1", "bc-2025-rw2");
    }

    [Fact]
    public async Task A_feed_that_cannot_be_read_changes_nothing_and_asks_for_a_retry()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null));
        var feeds = FeedWith("29.0.1.0");
        feeds.Down.Add("mssymbols");

        var ok = await NewScheduler(feeds).SyncAsync(CancellationToken.None);

        ok.Should().BeFalse();
        (await KeysAsync(TestDb.DefaultOrgId)).Should().Equal("bc-2026-rw1");
    }

    [Fact]
    public async Task A_feed_with_no_waves_on_it_asks_for_a_retry()
    {
        await SeedAsync(TestDb.DefaultOrgId, ("bc-2026-rw1", "28.0.0.0", 0, null));

        // Updates only: something is wrong with the index, not "nothing new".
        var ok = await NewScheduler(FeedWith("28.1.1.0", "28.2.1.0")).SyncAsync(CancellationToken.None);

        ok.Should().BeFalse();
        (await KeysAsync(TestDb.DefaultOrgId)).Should().Equal("bc-2026-rw1");
    }

    [Fact]
    public async Task A_pending_organisation_is_not_synced()
    {
        await using (var ctx = _db.NewContext())
        {
            var other = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.OtherOrgId);
            other.IsPending = true;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await ApplicationVersionSyncScheduler.ResolveTargetsAsync(read, CancellationToken.None))
            .Select(t => t.Id).Should().NotContain(TestDb.OtherOrgId).And.Contain(TestDb.DefaultOrgId);
    }

    // ── Harness ────────────────────────────────────────────────────────

    private ApplicationVersionService NewService(AppDbContext ctx) =>
        new(ctx, NullLogger<ApplicationVersionService>.Instance, _db.OrgContext);

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private static FakeSymbolFeeds FeedWith(params string[] versions)
    {
        var feeds = new FakeSymbolFeeds();
        foreach (var version in versions)
        {
            feeds.Add("mssymbols", AlSymbolFeedResolver.MicrosoftApplicationPackageId,
                "c1335042-3002-4257-bf8a-75c898ccb1b8", "Application", version);
        }
        return feeds;
    }

    private AlSymbolFeedResolver NewResolver(FakeSymbolFeeds feeds) =>
        new(feeds, NullLogger<AlSymbolFeedResolver>.Instance, new AlSymbolFeedOptions
        {
            AppSourceFeedUrl = FakeSymbolFeeds.AppSourceIndex,
            MicrosoftFeedUrl = FakeSymbolFeeds.MicrosoftIndex,
            CacheDirectory = _cacheRoot,
        });

    /// <summary>
    /// The scheduler over a container shaped like production's: the organisation
    /// context falls back to the ambient scope the sweep enters per org.
    /// </summary>
    private ApplicationVersionSyncScheduler NewScheduler(FakeSymbolFeeds feeds)
    {
        var options = Options();
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(NewResolver(feeds))
            .AddScoped<IOrganizationContext>(_ => new HttpOrganizationContext(new HttpContextAccessor()))
            .AddScoped(sp => new AppDbContext(options, sp.GetRequiredService<IOrganizationContext>()))
            .AddScoped<ApplicationVersionService>()
            .BuildServiceProvider();
        return new ApplicationVersionSyncScheduler(provider, TimeProvider.System,
            NullLogger<ApplicationVersionSyncScheduler>.Instance, new WorkerHeartbeatRegistry());
    }

    private async Task<List<string>> KeysAsync(int orgId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.ApplicationVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == orgId && a.DeletedAt == null)
            .OrderBy(a => a.Ordering)
            .Select(a => a.Key)
            .ToListAsync();
    }

    private async Task ClearAsync()
    {
        await using var ctx = _db.NewContext();
        await ctx.ApplicationVersions.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    private async Task SeedAsync(int orgId, params (string Key, string Application, int Ordering, DateTime? DeletedAt)[] rows)
    {
        await using var ctx = _db.NewContext();
        // Start each organisation from exactly the rows the test names.
        await ctx.ApplicationVersions.IgnoreQueryFilters().Where(a => a.OrganizationId == orgId).ExecuteDeleteAsync();
        var now = DateTime.UtcNow;
        foreach (var (key, application, ordering, deletedAt) in rows)
        {
            var major = int.Parse(application.Split('.')[0]);
            ctx.ApplicationVersions.Add(new ApplicationVersion
            {
                OrganizationId = orgId,
                Key = key,
                Name = key,
                Application = application,
                Runtime = $"{major - 11}.0",
                Ordering = ordering,
                CreatedAt = now,
                UpdatedAt = now,
                DeletedAt = deletedAt,
            });
        }
        await ctx.SaveChangesAsync();
    }
}
