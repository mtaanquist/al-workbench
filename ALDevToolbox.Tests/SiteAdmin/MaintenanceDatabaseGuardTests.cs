using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
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

    private AppDbContext NewGuardedContext(bool siteAdmin = false) =>
        _db.NewContext(new MaintenanceDatabaseGuard(_maintenance, new AmbientOrganizationContext
        {
            CurrentOrganizationId = TestDb.DefaultOrgId,
            IsSiteAdmin = siteAdmin,
        }));

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
    public async Task The_restores_own_work_and_a_site_admin_still_reach_the_database()
    {
        _maintenance.Enter("Restoring backup test.dump");

        await using (var ctx = NewGuardedContext())
        using (_maintenance.AllowDatabase())
        {
            await ctx.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
        }

        await using (var siteAdmin = NewGuardedContext(siteAdmin: true))
        {
            await siteAdmin.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
        }

        _maintenance.ActiveJobs.Should().Be(0, "allowed work during maintenance is not what the restore waits for");
        await using var ordinary = NewGuardedContext();
        var read = () => ordinary.Users.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);
        await read.Should().ThrowAsync<DatabaseClosedForMaintenanceException>("the allowance ended with its scope");
    }

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
