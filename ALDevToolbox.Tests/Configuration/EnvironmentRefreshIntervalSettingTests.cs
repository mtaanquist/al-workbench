using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// How often the background sweep asks Business Central about an organisation's
/// environments, as Administration → Business Central saves it. What the scheduler does
/// with it is in <c>EnvironmentRefreshSchedulerTests</c>.
/// </summary>
public sealed class EnvironmentRefreshIntervalSettingTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<OrganizationSettings?> StoredAsync()
    {
        await using var read = _db.NewContext();
        return await read.OrganizationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task An_organisation_that_never_chose_is_checked_once_a_day()
    {
        var minutes = await _db.NewOrganizationAdminService(_db.NewContext()).GetEnvironmentRefreshIntervalAsync();

        minutes.Should().Be(EnvironmentRefreshInterval.DefaultMinutes);
    }

    [Fact]
    public async Task A_listed_interval_is_stored_and_read_back()
    {
        await _db.NewOrganizationAdminService(_db.NewContext()).SetEnvironmentRefreshIntervalAsync(60);

        (await StoredAsync())!.EnvironmentRefreshIntervalMinutes.Should().Be(60);
        (await _db.NewOrganizationAdminService(_db.NewContext()).GetEnvironmentRefreshIntervalAsync()).Should().Be(60);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(45)]
    [InlineData(-60)]
    public async Task An_interval_that_is_not_on_the_list_is_refused_and_nothing_is_saved(int minutes)
    {
        var act = () => _db.NewOrganizationAdminService(_db.NewContext()).SetEnvironmentRefreshIntervalAsync(minutes);

        var thrown = await act.Should().ThrowAsync<PlanValidationException>();
        thrown.Which.Errors.Should().ContainKey("EnvironmentRefreshInterval");
        (await StoredAsync())?.EnvironmentRefreshIntervalMinutes.Should().Be(EnvironmentRefreshInterval.DefaultMinutes);
    }

    [Fact]
    public void Every_choice_divides_a_day_so_one_run_always_lands_in_the_quiet_hour()
    {
        foreach (var (minutes, _) in EnvironmentRefreshInterval.Choices)
        {
            (1440 % minutes).Should().Be(0, $"{minutes} minutes has to divide a day");
            minutes.Should().BeGreaterThanOrEqualTo(15, "the scheduler only looks every five minutes");
        }
    }

    [Fact]
    public void A_stored_value_that_is_not_on_the_list_runs_as_the_default()
    {
        EnvironmentRefreshInterval.Normalise(null).Should().Be(TimeSpan.FromDays(1));
        EnvironmentRefreshInterval.Normalise(0).Should().Be(TimeSpan.FromDays(1), "a stray zero must never mean every poll");
        EnvironmentRefreshInterval.Normalise(30).Should().Be(TimeSpan.FromMinutes(30));
    }
}
