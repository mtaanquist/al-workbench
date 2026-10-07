using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The nightly drift pass (issue #1104): when it runs, and what a solution shows when
/// the person its update pull requests are opened as is gone.
/// </summary>
public sealed class DependencyDriftSchedulerTests : IDisposable
{
    private static readonly DateTime Tonight = new(2026, 10, 8, DependencyDriftScheduler.SweepHourUtc, 10, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void It_runs_once_inside_its_hour()
    {
        var today = DateOnly.FromDateTime(Tonight);
        DependencyDriftScheduler.IsDue(Tonight, null).Should().BeTrue();
        DependencyDriftScheduler.IsDue(Tonight, today).Should().BeFalse("it already ran tonight");
        DependencyDriftScheduler.IsDue(Tonight.AddHours(1), null).Should().BeFalse("it is past the hour");
        DependencyDriftScheduler.IsDue(Tonight.AddDays(1), today).Should().BeTrue();
    }

    [Theory]
    [InlineData(UserStatus.Disabled)]
    [InlineData(null)]
    public async Task A_solution_whose_person_is_gone_says_so(UserStatus? ownerStatus)
    {
        var projectId = await SeedAsync(ownerStatus);

        (await NewScheduler().SweepAsync(CancellationToken.None)).Should().Be(0);

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).AutoUpdatePullRequestsBlocked
            .Should().Be(DependencyDriftService.AutomaticNoOwnerMessage);
    }

    /// <summary>A shipped release to scan against, and a solution with automatic update pull requests on.</summary>
    private async Task<int> SeedAsync(UserStatus? ownerStatus)
    {
        await using var ctx = _db.NewContext();
        int? ownerId = null;
        if (ownerStatus is { } status)
        {
            var owner = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "gone@cronus.example",
                DisplayName = "Gone",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = status,
                CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(owner);
            await ctx.SaveChangesAsync();
            ownerId = owner.Id;
        }
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 28.2 (DK)",
            Kind = "first_party",
            Status = "ready",
            BcVersion = "28.2.50931.51727",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS A/S",
            DefaultArtifactCountry = "dk",
            AutoUpdatePullRequests = true,
            AutoUpdatePullRequestsByUserId = ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>The scheduler over a provider that hands out the fixture's drift service; GitHub is never reached.</summary>
    private DependencyDriftScheduler NewScheduler()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped(sp =>
        {
            var ctx = sp.GetRequiredService<AppDbContext>();
            var client = _db.NewGitHubAppClient(ctx, new FakeGitHubApi());
            return _db.NewDependencyDriftService(ctx, client, _db.NewGitHubAccessService(ctx, client));
        });
        return new DependencyDriftScheduler(
            services.BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<DependencyDriftScheduler>.Instance,
            new WorkerHeartbeatRegistry());
    }
}
