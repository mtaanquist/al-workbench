using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Upgrades source (#984, sub-issue F). An upgrade's header belongs to the
/// organisation, so the harness's Private-solution case reports as skipped; what hangs off
/// a solution is the lines, and the cases below check that a line the caller cannot see
/// neither names its solution nor moves the status. The seeded caller holds the
/// environment-updates grant through a team on the visible solution, because that is the
/// gate the sidebar's Upgrades entry has.
/// </summary>
public sealed class UpgradePaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context)
    {
        var fleet = new UpgradeFleetService(context.Db, context.OrgContext, context.Access,
            new EnvironmentRefreshQueue(), NullLogger<UpgradeFleetService>.Instance);
        var upgrades = new EnvironmentUpgradeService(context.Db, context.OrgContext, context.Access, fleet,
            TimeProvider.System, NullLogger<EnvironmentUpgradeService>.Instance);
        return new UpgradePaletteSource(context.Db, context.Access, upgrades);
    }

    /// <summary>
    /// The header is the organisation's, not a solution's: the Upgrades page lists every
    /// one to anyone holding the grant, so an upgrade named after the Private solution is
    /// somewhere this caller may go.
    /// </summary>
    protected override bool RowsBelongToSolutions => false;

    /// <summary>
    /// One open upgrade per solution the harness seeds, named after it, with a line on one
    /// of that solution's environments - and, on the visible solution, the team that gives
    /// the caller the grant.
    /// </summary>
    protected override async Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        var environmentId = await AddEnvironmentAsync(ctx, seed.OrganizationId, seed.ProjectId, "28.2.41125.0");
        var now = DateTime.UtcNow;
        ctx.OeEnvironmentUpgrades.Add(new OeEnvironmentUpgrade
        {
            OrganizationId = seed.OrganizationId, Name = seed.Title, TargetVersion = "28.5",
            CreatedBy = "Anna Jensen", CreatedAt = now, UpdatedAt = now,
            Lines =
            [
                new OeEnvironmentUpgradeLine
                {
                    OrganizationId = seed.OrganizationId, EnvironmentId = environmentId,
                    ProjectId = seed.ProjectId, IsOpen = true, AddedAt = now,
                },
            ],
        });

        if (seed.Title == VisibleName)
        {
            var team = new Team { OrganizationId = seed.OrganizationId, Name = "Upgrades", CreatedAt = now, UpdatedAt = now };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = seed.OrganizationId, TeamId = team.Id, UserId = CallerUserId,
                ManagesUpdates = true, CreatedAt = now,
            });
            ctx.OeProjectTeams.Add(new OeProjectTeam
            {
                OrganizationId = seed.OrganizationId, ProjectId = seed.ProjectId, TeamId = team.Id, CreatedAt = now,
            });
        }
    }

    [Fact]
    public async Task An_open_upgrade_reads_its_target_and_derived_status_and_opens_its_page()
    {
        await SeedWorldAsync();

        var row = (await SearchAsync(VisibleName)).Should().ContainSingle().Subject;

        row.Kind.Should().Be("upgrade");
        row.Title.Should().Be(VisibleName);
        row.Subtitle.Should().Be("to 28.5 - Planned");
        row.Href.Should().Be($"/upgrades/{await UpgradeIdAsync(VisibleName)}");
    }

    [Fact]
    public async Task The_target_release_finds_open_and_done_upgrades_open_first()
    {
        await SeedWorldAsync();
        var done = await AddUpgradeAsync("28.4 in October 2026", "28.4", closed: true);
        var alsoDone = await AddUpgradeAsync("28.5 stragglers", "28.5", closed: true);

        var byTarget = await SearchAsync("28.5");
        byTarget.Select(r => r.Title).Should().Contain([VisibleName, PrivateName, "28.5 stragglers"]);
        byTarget.Single(r => r.Href == $"/upgrades/{alsoDone}").Subtitle.Should().Be("to 28.5 - Done");

        var byName = await SearchAsync("october");
        byName.Should().ContainSingle().Which.Href.Should().Be($"/upgrades/{done}");
    }

    [Fact]
    public async Task A_line_the_caller_cannot_see_neither_names_its_solution_nor_moves_the_status()
    {
        await SeedWorldAsync();
        // The Private solution's environment is already on the target: were its line
        // counted, the upgrade would read Updated rather than Planned.
        await using (var ctx = Db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.ProjectId == PrivateProjectId);
            env.Version = "28.5.1.0";
            await ctx.SaveChangesAsync();
        }

        var row = (await SearchAsync(PrivateName)).Should().ContainSingle().Subject;

        row.Subtitle.Should().Be("to 28.5 - Planned", "an upgrade with no visible lines is planned");
    }

    [Fact]
    public async Task A_member_without_the_environment_updates_grant_is_not_asked()
    {
        await SeedWorldAsync();
        Db.OrgContext.CurrentUserId = StrangerUserId;

        await using var ctx = Db.NewContext();
        var source = CreateSource(new PaletteSourceUnderTest(
            ctx, Db.OrgContext, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, Db.OrgContext)));

        (await source.IsAvailableAsync(CallerPrincipal(), CancellationToken.None)).Should().BeFalse(
            "the Upgrades entry in the sidebar is only there for people holding the grant");
    }

    [Fact]
    public void Describe_words_the_status_as_the_page_does()
    {
        UpgradePaletteSource.Describe("29.1", UpgradeStatus.InProgress).Should().Be("to 29.1 - In progress");
        UpgradePaletteSource.Describe("29.1", null).Should().Be("to 29.1");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<int> AddEnvironmentAsync(AppDbContext ctx, int organizationId, int projectId, string version)
    {
        var env = new OeProjectEnvironment
        {
            OrganizationId = organizationId, ProjectId = projectId, Name = "Production", Type = "Production",
            ApplicationFamily = "BusinessCentral", Status = "Active", Version = version, FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }

    private async Task<int> AddUpgradeAsync(string name, string target, bool closed)
    {
        var now = DateTime.UtcNow;
        await using var ctx = Db.NewContext();
        var upgrade = new OeEnvironmentUpgrade
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, TargetVersion = target,
            CreatedBy = "Anna Jensen", CreatedAt = now, UpdatedAt = now,
            ClosedAt = closed ? now : null, ClosedBy = closed ? "Anna Jensen" : null,
        };
        ctx.OeEnvironmentUpgrades.Add(upgrade);
        await ctx.SaveChangesAsync();
        return upgrade.Id;
    }

    private async Task<int> UpgradeIdAsync(string name)
    {
        await using var ctx = Db.NewContext();
        return (await ctx.OeEnvironmentUpgrades.AsNoTracking().SingleAsync(u => u.Name == name)).Id;
    }
}
