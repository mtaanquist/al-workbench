using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Target selection for the nightly environment-refresh sweep
/// (<see cref="EnvironmentRefreshScheduler.ResolveProjectIdsAsync"/>): only projects with
/// a complete Business Central connection are offered, and a deleted project is never
/// swept. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class EnvironmentRefreshSchedulerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Only_live_projects_with_complete_credentials_are_swept()
    {
        var connected = await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher");
        await SeedProjectAsync("No secret", tenant: Guid.NewGuid(), clientId: "client-abc", secret: null);
        await SeedProjectAsync("No client id", tenant: Guid.NewGuid(), clientId: null, secret: "cipher");
        await SeedProjectAsync("Nothing configured", tenant: null, clientId: null, secret: null);
        await SeedProjectAsync("Deleted", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher",
            deletedAt: DateTime.UtcNow);

        await using var ctx = _db.NewContext();
        var targets = await EnvironmentRefreshScheduler.ResolveProjectIdsAsync(ctx, default);

        targets.Should().ContainSingle().Which.Should().Be(connected,
            "a project missing a credential part could only produce a nightly token failure, "
            + "and a deleted project has no customer to sweep");
    }

    [Fact]
    public async Task A_solution_with_only_a_tenant_is_swept_once_the_organisation_has_a_registration()
    {
        var tenantOnly = await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: null, secret: null);
        var halfOwn = await SeedProjectAsync("Own client id, no secret", tenant: Guid.NewGuid(), clientId: "client-abc", secret: null);

        await using (var before = _db.NewContext())
        {
            (await EnvironmentRefreshScheduler.ResolveProjectIdsAsync(before, default)).Should().BeEmpty();
        }

        await using (var seed = _db.NewContext())
        {
            var settings = await seed.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                seed.OrganizationSettings.Add(settings);
            }
            settings.BcClientId = "11111111-1111-1111-1111-111111111111";
            settings.BcClientSecretEncrypted = "cipher";
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var targets = await EnvironmentRefreshScheduler.ResolveProjectIdsAsync(ctx, default);

        targets.Should().ContainSingle().Which.Should().Be(tenantOnly,
            "a solution that chose its own registration never falls back to the organisation's");
        targets.Should().NotContain(halfOwn);
    }

    private static DateTime At(int day, int hour, int minute) => new(2026, 9, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Once_a_day_runs_a_few_minutes_into_the_quiet_hour_and_only_once()
    {
        var day = TimeSpan.FromDays(1);
        var offset = TimeSpan.FromMinutes(17);
        var tonight = At(21, 3, 17);

        EnvironmentRefreshScheduler.DueSlot(At(21, 3, 15), day, offset, null).Should().BeNull("it is not on the hour with everybody else");
        EnvironmentRefreshScheduler.DueSlot(At(21, 3, 20), day, offset, null).Should().Be(tonight, "the first five-minute poll past the offset");
        EnvironmentRefreshScheduler.DueSlot(At(21, 3, 25), day, offset, tonight).Should().BeNull("once a night");
        EnvironmentRefreshScheduler.DueSlot(At(21, 3, 20), day, offset, At(20, 3, 17)).Should().Be(tonight, "yesterday's run does not count");
        EnvironmentRefreshScheduler.DueSlot(At(21, 4, 20), day, offset, null).Should().BeNull("a run missed by more than an hour waits for the next night");
        EnvironmentRefreshScheduler.DueSlot(At(21, 12, 0), day, offset, null).Should().BeNull("a restart at noon does not start the night's run");
    }

    [Fact]
    public void A_shorter_interval_runs_every_slot_and_still_runs_in_the_quiet_hour()
    {
        var offset = TimeSpan.FromMinutes(17);
        var sixHours = TimeSpan.FromHours(6);

        EnvironmentRefreshScheduler.DueSlot(At(21, 3, 20), sixHours, offset, null).Should().Be(At(21, 3, 17),
            "the automatic update pull requests at 05:00 read what this run found");
        EnvironmentRefreshScheduler.DueSlot(At(21, 9, 20), sixHours, offset, At(21, 3, 17)).Should().Be(At(21, 9, 17));
        EnvironmentRefreshScheduler.DueSlot(At(21, 1, 0), sixHours, offset, null).Should().BeNull("the 21:17 run is long past");
        EnvironmentRefreshScheduler.DueSlot(At(21, 21, 30), sixHours, offset, null).Should().Be(At(21, 21, 17));

        var quarter = TimeSpan.FromMinutes(15);
        EnvironmentRefreshScheduler.DueSlot(At(21, 10, 5), quarter, offset, null).Should().Be(At(21, 10, 2));
        EnvironmentRefreshScheduler.DueSlot(At(21, 10, 10), quarter, offset, At(21, 10, 2)).Should().BeNull("one run per slot");
        EnvironmentRefreshScheduler.DueSlot(At(21, 10, 20), quarter, offset, At(21, 10, 2)).Should().Be(At(21, 10, 17));
    }

    [Fact]
    public async Task An_organisation_on_the_default_is_not_swept_outside_the_night()
    {
        await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher");
        var (scheduler, _) = NewScheduler();

        (await scheduler.SweepDueAsync(At(21, 12, 0), default)).Should().Be(0);
    }

    [Fact]
    public async Task An_hourly_organisation_is_swept_once_per_hour()
    {
        var id = await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher");
        await SetIntervalAsync(60);
        var (scheduler, queue) = NewScheduler();

        // The scheduler starts each slot up to MaxStartOffset (40 min) past the hour, at
        // random. Sweeping after that keeps all three calls in the slots they name
        // whatever offset it drew; 12:00 and 12:05 straddled a slot for offsets 1-5.
        (await scheduler.SweepDueAsync(At(21, 12, 45), default)).Should().Be(1);
        queue.Complete(id);
        (await scheduler.SweepDueAsync(At(21, 12, 50), default)).Should().Be(0, "this hour's run has happened");
        (await scheduler.SweepDueAsync(At(21, 13, 45), default)).Should().Be(1, "the next hour asks again");
    }

    [Fact]
    public async Task A_solution_Business_Central_asked_us_to_leave_alone_is_skipped_until_the_wait_is_over()
    {
        var throttled = await SeedProjectAsync("CRONUS A/S", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher");
        var other = await SeedProjectAsync("Fabrikam", tenant: Guid.NewGuid(), clientId: "client-abc", secret: "cipher");
        await SetIntervalAsync(15);
        var (scheduler, queue) = NewScheduler();
        queue.DeferUntil(throttled, At(21, 12, 20));

        (await scheduler.SweepDueAsync(At(21, 12, 0), default)).Should().Be(1);
        queue.IsInFlight(throttled).Should().BeFalse("asking early would only earn another refusal");
        queue.IsInFlight(other).Should().BeTrue();
        queue.Complete(other);

        (await scheduler.SweepDueAsync(At(21, 12, 30), default)).Should().Be(2, "the wait is over");
    }

    private async Task SetIntervalAsync(int minutes)
    {
        await using var seed = _db.NewContext();
        var settings = await seed.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
            seed.OrganizationSettings.Add(settings);
        }
        settings.EnvironmentRefreshIntervalMinutes = minutes;
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// A scheduler whose contexts take their organisation from the ambient scope, as the
    /// app's do off-request, so each org's settings and solutions are read under its own filter.
    /// </summary>
    private (EnvironmentRefreshScheduler Scheduler, EnvironmentRefreshQueue Queue) NewScheduler()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_db.ConnectionString).Options;
        var services = new ServiceCollection()
            .AddScoped(_ => new AppDbContext(options, new HttpOrganizationContext(new HttpContextAccessor())))
            .BuildServiceProvider();
        var queue = new EnvironmentRefreshQueue();
        var scheduler = new EnvironmentRefreshScheduler(
            services, queue, TimeProvider.System, NullLogger<EnvironmentRefreshScheduler>.Instance, new WorkerHeartbeatRegistry());
        return (scheduler, queue);
    }

    private async Task<int> SeedProjectAsync(
        string name, Guid? tenant, string? clientId, string? secret, DateTime? deletedAt = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt,
            BcTenantId = tenant,
            BcClientId = clientId,
            BcClientSecretEncrypted = secret,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }
}
