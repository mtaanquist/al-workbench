using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// A click on an open page can start before a restore and keep writing after
/// it: the circuit's per-event hook only sees the part of the handler before
/// its first await. So the database itself is guarded (#1571). These go
/// through a real <see cref="AppDbContext"/> with the guard installed: every
/// command is counted for the restore to wait on, and once maintenance mode
/// is on an ordinary command is refused.
/// </summary>
public sealed class MaintenanceDatabaseGuardTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly MaintenanceModeState _maintenance = new();

    public void Dispose() => _db.Dispose();

    private AppDbContext NewGuardedContext(bool siteAdmin = false, string? requestPath = null) =>
        _db.NewContext(NewGuard(siteAdmin, requestPath));

    private MaintenanceDatabaseGuard NewGuard(bool siteAdmin = false, string? requestPath = null) =>
        new(_maintenance,
            new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId, IsSiteAdmin = siteAdmin },
            new TestDb.FixedHttpContextAccessor(requestPath is null ? null : new DefaultHttpContext { Request = { Path = requestPath } }));

    [Fact]
    public async Task While_the_site_is_open_commands_run_and_release_their_count()
    {
        await using var ctx = NewGuardedContext();

        (await ctx.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().BeGreaterThanOrEqualTo(0);
        ctx.Users.Add(NewUser());
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        _maintenance.ActiveJobs.Should().Be(0);
    }

    [Fact]
    public async Task A_write_issued_after_the_restore_began_is_refused_and_nothing_is_written()
    {
        // The click handler did its first awaited work while the site was open.
        await using var ctx = NewGuardedContext();
        await ctx.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);

        _maintenance.Enter("Restoring backup test.dump");
        var user = NewUser();
        ctx.Users.Add(user);
        var save = () => ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await save.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<DatabaseClosedForMaintenanceException>();
        _maintenance.ActiveJobs.Should().Be(0);
        await using var check = _db.NewContext();
        (await check.Users.AsNoTracking().AnyAsync(u => u.Email == user.Email, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_command_in_flight_when_the_restore_begins_holds_the_restore_until_it_finishes()
    {
        await using var ctx = NewGuardedContext();
        var running = ctx.Database.ExecuteSqlRawAsync("SELECT pg_sleep(1)", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _maintenance.ActiveJobs == 1);

        _maintenance.Enter("Restoring backup test.dump");
        (await _maintenance.WaitForActiveJobsAsync(TimeSpan.Zero, TestContext.Current.CancellationToken))
            .Should().BeFalse("the restore must not drop the schema under a running command");

        await running;
        (await _maintenance.WaitForActiveJobsAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_query_stays_counted_until_its_reader_is_disposed()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(NewUser());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var ctx = NewGuardedContext();
        await using (var rows = ctx.Users.AsNoTracking().AsAsyncEnumerable().GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            (await rows.MoveNextAsync()).Should().BeTrue();
            _maintenance.ActiveJobs.Should().Be(1);
        }
        _maintenance.ActiveJobs.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_command_releases_its_count()
    {
        await using var ctx = NewGuardedContext();
        var bad = () => ctx.Database.ExecuteSqlRawAsync("SELECT * FROM no_such_table", TestContext.Current.CancellationToken);

        await bad.Should().ThrowAsync<Exception>();
        _maintenance.ActiveJobs.Should().Be(0);
    }

    [Fact]
    public async Task The_restores_own_work_reaches_the_database_uncounted_and_the_allowance_ends_with_its_scope()
    {
        _maintenance.Enter("Restoring backup test.dump");

        await using (var ctx = NewGuardedContext())
        using (_maintenance.AllowDatabase())
        {
            await ctx.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
            ctx.Users.Add(NewUser());
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        _maintenance.ActiveJobs.Should().Be(0, "allowed work during maintenance is not what the restore waits for");
        await using var ordinary = NewGuardedContext();
        var read = () => ordinary.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
        await read.Should().ThrowAsync<DatabaseClosedForMaintenanceException>("the allowance ended with its scope");
    }

    [Fact]
    public async Task A_request_that_arrived_during_the_restore_may_read_but_not_write()
    {
        _maintenance.Enter("Restoring backup test.dump");
        await using var ctx = NewGuardedContext();
        using var reads = _maintenance.AllowDatabaseReads();

        await ctx.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
        var user = NewUser();
        ctx.Users.Add(user);
        var save = () => ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await save.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<DatabaseClosedForMaintenanceException>("a token refresh or first sign-in must not write");
        var update = () => ctx.Database.ExecuteSqlRawAsync("UPDATE users SET display_name = display_name", TestContext.Current.CancellationToken);
        await update.Should().ThrowAsync<DatabaseClosedForMaintenanceException>();
    }

    [Fact]
    public async Task A_site_admin_reaches_the_database_only_on_the_site_admin_console()
    {
        _maintenance.Enter("Restoring backup test.dump");

        await using (var console = NewGuardedContext(siteAdmin: true, requestPath: "/site-admin/backups"))
        {
            console.Users.Add(NewUser());
            await console.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var circuitGuard = NewGuard(siteAdmin: true, requestPath: "/_blazor");
        var onConsole = true;
        circuitGuard.CircuitOnSiteAdminConsole = () => onConsole;
        await using (var circuit = _db.NewContext(circuitGuard))
        {
            await circuit.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
            onConsole = false;
            var elsewhere = () => circuit.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
            await elsewhere.Should().ThrowAsync<DatabaseClosedForMaintenanceException>("the circuit has moved off the console");
        }

        await using var otherPage = NewGuardedContext(siteAdmin: true, requestPath: "/solutions/3");
        var write = () => otherPage.Database.ExecuteSqlRawAsync("UPDATE users SET display_name = display_name", TestContext.Current.CancellationToken);
        await write.Should().ThrowAsync<DatabaseClosedForMaintenanceException>();
        _maintenance.ActiveJobs.Should().Be(0);
    }

    [Fact]
    public async Task A_canceled_command_releases_its_count()
    {
        await using var ctx = NewGuardedContext();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = ctx.Database.ExecuteSqlRawAsync("SELECT pg_sleep(10)", cts.Token);
        await WaitUntilAsync(() => _maintenance.ActiveJobs == 1);

        await cts.CancelAsync();
        var wait = () => running;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        _maintenance.ActiveJobs.Should().Be(0);
    }

    [Fact]
    public async Task A_count_left_behind_is_released_when_the_scope_ends()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(NewUser());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var guard = NewGuard();
        await using var ctx = _db.NewContext(guard);
        // A reader that is never disposed stands in for one whose close threw
        // before the guard heard about it.
        var rows = ctx.Users.AsNoTracking().AsAsyncEnumerable().GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await rows.MoveNextAsync()).Should().BeTrue();
        _maintenance.ActiveJobs.Should().Be(1);

        guard.Dispose();

        _maintenance.ActiveJobs.Should().Be(0, "otherwise every later restore would wait for it and refuse");
        await rows.DisposeAsync();
        _maintenance.ActiveJobs.Should().Be(0, "a late dispose does not release the count twice");
    }

    [Theory]
    [InlineData("SELECT u.id FROM users AS u", true)]
    [InlineData("  -- from the Users page\nSELECT 1", true)]
    [InlineData("/* tag */ select 1", true)]
    [InlineData("SELECT id FROM users FOR UPDATE", false)]
    [InlineData("INSERT INTO users (email) VALUES ('x') RETURNING id", false)]
    [InlineData("UPDATE users SET display_name = 'x'", false)]
    [InlineData("WITH d AS (DELETE FROM users RETURNING id) SELECT * FROM d", false)]
    [InlineData("-- unterminated comment", false)]
    [InlineData("", false)]
    [InlineData("SELECT 1;", true)]
    [InlineData("SELECT 1; DELETE FROM users", false)]
    [InlineData("SELECT 1;\nUPDATE users SET display_name = 'x';", false)]
    [InlineData("SELECT id FROM users FOR\tNO   KEY\nUPDATE", false)]
    [InlineData("SELECT id FROM users FOR KEY SHARE", false)]
    [InlineData("SELECT id FROM users\nFOR SHARE", false)]
    [InlineData("SELECT * INTO users_copy FROM users", false)]
    [InlineData("SELECT nextval('users_id_seq')", false)]
    [InlineData("SELECT setval('users_id_seq', 1)", false)]
    [InlineData("SELECT pg_advisory_lock(42)", false)]
    [InlineData("SELECT pg_try_advisory_xact_lock(42)", false)]
    [InlineData("SELECT set_config('search_path', 'x', false)", false)]
    [InlineData("SELECT pg_notify('channel', 'x')", false)]
    [InlineData("SELECT lo_unlink(123)", false)]
    [InlineData("/* outer /* inner */ still outer */ SELECT 1", true)]
    [InlineData("/* outer /* inner */ SELECT 1", false)]
    [InlineData("/* a */ SELECT 1 /* b /* c */ */", true)]
    [InlineData("SELECT '/* not a comment' FROM users", true)]
    [InlineData("SELECT 1 /* ; DELETE FROM users */", true)]
    [InlineData("SELECT $$x$$", false)]
    [InlineData("SELECT E'\\' -- '; DELETE FROM users; --'\n", false)]
    [InlineData("SELECT 'unterminated", false)]
    [InlineData("select u.\"Id\" from users as u where u.email = 'o''brien@cronus.example'", true)]
    public void Only_a_plain_select_counts_as_a_read(string sql, bool isRead) =>
        MaintenanceDatabaseGuard.IsRead(sql).Should().Be(isRead);

    private static User NewUser() => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Email = $"{Guid.NewGuid():N}@cronus.example",
        DisplayName = "Sam Berg",
        PasswordHash = "x",
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never held.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
