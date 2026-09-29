using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Planned upgrades (issue #984): the header, its lines, and the rules around them - the
/// grant on every line, one open upgrade per environment, nothing removed once something has
/// been sent, and lines from a solution the caller cannot see left out rather than shown.
/// Built on <see cref="UpgradeActionTestFixture"/>, whose acting user holds the
/// environment-updates grant on every customer it seeds.
/// </summary>
public sealed class EnvironmentUpgradeServiceTests : IDisposable
{
    private readonly UpgradeActionTestFixture _f = new();

    public void Dispose() => _f.Dispose();

    // ── Creating and editing the header ─────────────────────────────────

    [Fact]
    public async Task Create_stores_the_details_trimmed_and_names_the_creator()
    {
        var id = await CreateAsync("  28.5 in November 2026  ", " 28.05 ", note: "  Ring before 20:00  ");

        await using var ctx = _f.Db.NewContext();
        var stored = await ctx.OeEnvironmentUpgrades.AsNoTracking().SingleAsync(u => u.Id == id);
        stored.Name.Should().Be("28.5 in November 2026");
        stored.TargetVersion.Should().Be("28.5", "one spelling of a release, whatever was typed");
        stored.Note.Should().Be("Ring before 20:00");
        stored.CreatedBy.Should().Contain("Anna Jensen");
        stored.CreatedByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        stored.ClosedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("", "28.5", "Name")]
    [InlineData("   ", "28.5", "Name")]
    [InlineData("Wave", "", "TargetVersion")]
    [InlineData("Wave", "28", "TargetVersion")]
    [InlineData("Wave", "28.5.1", "TargetVersion")]
    [InlineData("Wave", "next", "TargetVersion")]
    public async Task Create_refuses_a_missing_name_or_a_version_that_is_not_major_minor(
        string name, string version, string field)
    {
        var act = () => CreateAsync(name, version);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task Create_refuses_a_name_over_the_column_length()
    {
        var act = () => CreateAsync(new string('x', EnvironmentUpgradeService.NameMaxLength + 1), "28.5");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Create_refuses_an_overlong_note()
    {
        var act = () => CreateAsync("Wave", "28.5", note: new string('x', EnvironmentUpgradeService.NoteMaxLength + 1));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Note");
    }

    [Fact]
    public async Task Details_can_be_changed_while_open_but_not_once_done()
    {
        var id = await CreateAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).UpdateDetailsAsync(id, "28.5 in December", "28.5", null, null);
        }
        await using (var ctx = _f.Db.NewContext())
        {
            (await ctx.OeEnvironmentUpgrades.AsNoTracking().SingleAsync(u => u.Id == id)).Name
                .Should().Be("28.5 in December");
        }

        await CloseAsync(id);

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Upgrades(ctx).UpdateDetailsAsync(id, "Renamed", "28.5", null, null);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Upgrade");
        }
    }

    // ── Adding lines ────────────────────────────────────────────────────

    [Fact]
    public async Task Adding_puts_the_environment_on_and_a_repeat_is_passed_over()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();

        var first = await AddAsync(id, envId);
        var again = await AddAsync(id, envId);

        first.Added.Should().Equal(envId);
        again.Added.Should().BeEmpty();
        again.AlreadyOnIt.Should().Equal(envId);
        again.Refused.Should().BeEmpty();

        await using var ctx = _f.Db.NewContext();
        var line = await ctx.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync();
        line.IsOpen.Should().BeTrue();
        line.UpgradeId.Should().Be(id);
    }

    [Fact]
    public async Task Adding_is_refused_per_environment_without_the_grant()
    {
        var (_, allowed) = await _f.SeedCustomerAsync();
        var (_, notAllowed) = await SeedCustomerWithoutUpdateTeamAsync(ProjectVisibility.Public);
        var id = await CreateAsync();

        var result = await AddAsync(id, allowed, notAllowed);

        result.Added.Should().Equal(allowed);
        result.Refused.Should().ContainKey(notAllowed.ToString())
            .WhoseValue.Should().Contain("can't manage updates");
    }

    [Fact]
    public async Task An_environment_on_another_open_upgrade_is_refused_naming_it()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var first = await CreateAsync("28.5 in November 2026");
        var second = await CreateAsync("28.5 stragglers");
        await AddAsync(first, envId);

        var result = await AddAsync(second, envId);

        result.Added.Should().BeEmpty();
        result.Refused[envId.ToString()].Should().Contain("\"28.5 in November 2026\"");
    }

    [Fact]
    public async Task An_environment_deleted_in_Business_Central_is_refused()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        await using (var ctx = _f.Db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == envId);
            env.SoftDeletedOn = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        var id = await CreateAsync();

        var result = await AddAsync(id, envId);

        result.Refused[envId.ToString()].Should().Contain("deleted in Business Central");
    }

    [Fact]
    public async Task An_environment_from_a_solution_the_caller_cannot_see_is_refused_as_if_it_did_not_exist()
    {
        var (_, hidden) = await SeedCustomerWithoutUpdateTeamAsync(ProjectVisibility.Private);
        var id = await CreateAsync();

        var result = await AddAsync(id, hidden);

        result.Refused[hidden.ToString()].Should().Contain("no longer exists");
    }

    [Fact]
    public async Task Nothing_can_be_added_to_a_done_upgrade()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await CloseAsync(id);

        var act = () => AddAsync(id, envId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Upgrade");
    }

    [Fact]
    public async Task The_database_holds_one_open_upgrade_per_environment_even_past_the_service()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var first = await CreateAsync("One");
        var second = await CreateAsync("Two");
        await AddAsync(first, envId);

        await using var ctx = _f.Db.NewContext();
        ctx.OeEnvironmentUpgradeLines.Add(new OeEnvironmentUpgradeLine
        {
            OrganizationId = TestDb.DefaultOrgId, UpgradeId = second, EnvironmentId = envId,
            ProjectId = projectId, IsOpen = true, AddedAt = DateTime.UtcNow,
        });
        var act = () => ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("the filtered unique index is what holds when two requests race");
    }

    // ── Removing lines ──────────────────────────────────────────────────

    [Fact]
    public async Task A_line_can_be_removed_while_nothing_has_been_sent()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).RemoveLineAsync(lineId);
        }

        await using var verify = _f.Db.NewContext();
        (await verify.OeEnvironmentUpgradeLines.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_line_stays_once_an_action_has_been_taken_from_the_upgrade()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);
        await BookFromUpgradeAsync(projectId, envId, id);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).RemoveLineAsync(lineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Line");
    }

    // ── Checking and assigning ──────────────────────────────────────────

    [Fact]
    public async Task Checking_stamps_the_person_and_time_and_unchecking_clears_them()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(lineId, true, "  posting OK, reports OK  ");
        }
        await using (var ctx = _f.Db.NewContext())
        {
            var line = await ctx.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync();
            line.CheckedAt.Should().Be(_f.Clock.GetUtcNow().UtcDateTime);
            line.CheckedByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
            line.CheckedBy.Should().Contain("Anna Jensen");
            line.Note.Should().Be("posting OK, reports OK");
        }

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(lineId, false, "posting OK, reports OK");
        }
        await using (var ctx = _f.Db.NewContext())
        {
            var line = await ctx.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync();
            line.CheckedAt.Should().BeNull();
            line.CheckedByUserId.Should().BeNull();
            line.CheckedBy.Should().BeNull();
            line.Note.Should().Be("posting OK, reports OK", "the note is the person's, not the tick's");
        }
    }

    [Fact]
    public async Task Changing_the_note_on_a_checked_line_keeps_the_stamp_of_whoever_checked_it()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(lineId, true, null);
        }
        var checkedAt = _f.Clock.GetUtcNow().UtcDateTime;

        // Later, somebody else on the update team adds what was looked at.
        await using (var ctx = _f.Db.NewContext())
        {
            (await ctx.TeamMembers.SingleAsync(m => m.UserId == UpgradeActionTestFixture.PlainTeamUserId)).ManagesUpdates = true;
            await ctx.SaveChangesAsync();
        }
        _f.Clock.Advance(TimeSpan.FromHours(2));
        _f.ActAs(UpgradeActionTestFixture.PlainTeamUserId);
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(lineId, true, "reports OK");
        }

        await using var read = _f.Db.NewContext();
        var line = await read.OeEnvironmentUpgradeLines.AsNoTracking().SingleAsync();
        line.Note.Should().Be("reports OK");
        line.CheckedAt.Should().Be(checkedAt);
        line.CheckedByUserId.Should().Be(UpgradeActionTestFixture.FlagUserId);
        line.CheckedBy.Should().Contain("Anna Jensen");
    }

    [Fact]
    public async Task A_check_note_over_500_characters_is_refused()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).SetCheckedAsync(lineId, true, new string('x', 501));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Note");
    }

    [Fact]
    public async Task Checking_needs_the_grant()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);

        // On the team, so can see the solution, but without the updates flag.
        _f.ActAs(UpgradeActionTestFixture.PlainTeamUserId);
        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).SetCheckedAsync(lineId, true, null);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task A_line_can_be_assigned_and_the_upgrade_names_the_assignee()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var lineId = await LineIdAsync(id, envId);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).AssignAsync(lineId, UpgradeActionTestFixture.PlainTeamUserId);
        }

        await using (var ctx = _f.Db.NewContext())
        {
            var detail = await _f.Upgrades(ctx).GetAsync(id);
            detail!.Lines.Single().AssigneeUserId.Should().Be(UpgradeActionTestFixture.PlainTeamUserId);
            detail.Lines.Single().AssigneeName.Should().Be("colleague@example.com");
        }

        await using (var ctx = _f.Db.NewContext())
        {
            var act = () => _f.Upgrades(ctx).AssignAsync(lineId, 424242);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Assignee");
        }
    }

    // ── Done and reopen ─────────────────────────────────────────────────

    [Fact]
    public async Task Marking_done_returns_the_unchecked_count_and_frees_the_environments()
    {
        var (projectId, a) = await _f.SeedCustomerAsync();
        var b = await SeedEnvironmentAsync(projectId, "UAT");
        var id = await CreateAsync();
        await AddAsync(id, a, b);
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(await LineIdAsync(id, a), true, null);
        }

        int unchecked_;
        await using (var ctx = _f.Db.NewContext())
        {
            unchecked_ = await _f.Upgrades(ctx).CloseAsync(id);
        }

        unchecked_.Should().Be(1);
        await using (var ctx = _f.Db.NewContext())
        {
            var upgrade = await ctx.OeEnvironmentUpgrades.AsNoTracking().Include(u => u.Lines).SingleAsync();
            upgrade.ClosedAt.Should().Be(_f.Clock.GetUtcNow().UtcDateTime);
            upgrade.ClosedBy.Should().Contain("Anna Jensen");
            upgrade.Lines.Should().OnlyContain(l => !l.IsOpen);
        }

        // Freed: the same environment can now go on a new open upgrade.
        var next = await CreateAsync("Next wave");
        (await AddAsync(next, a)).Added.Should().Equal(a);
    }

    [Fact]
    public async Task Reopen_puts_it_back_and_holds_its_environments_again()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        await CloseAsync(id);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).ReopenAsync(id);
        }

        await using var verify = _f.Db.NewContext();
        var upgrade = await verify.OeEnvironmentUpgrades.AsNoTracking().Include(u => u.Lines).SingleAsync();
        upgrade.ClosedAt.Should().BeNull();
        upgrade.ClosedBy.Should().BeNull();
        upgrade.Lines.Should().OnlyContain(l => l.IsOpen);
    }

    [Fact]
    public async Task Reopen_is_refused_when_an_environment_has_moved_to_another_open_upgrade()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync("28.5 in November 2026");
        await AddAsync(id, envId);
        await CloseAsync(id);
        var other = await CreateAsync("28.5 stragglers");
        await AddAsync(other, envId);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).ReopenAsync(id);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Upgrade"]
            .Should().Contain("Production").And.Contain("\"28.5 stragglers\"");
    }

    // ── Leftovers ───────────────────────────────────────────────────────

    [Fact]
    public async Task Leftovers_carry_the_failed_and_the_never_started_into_a_new_upgrade()
    {
        var (projectId, updated) = await _f.SeedCustomerAsync();
        var failed = await SeedEnvironmentAsync(projectId, "UAT");
        var planned = await SeedEnvironmentAsync(projectId, "Training");
        var checkedEnv = await SeedEnvironmentAsync(projectId, "Test");
        var id = await CreateAsync("28.5 in November 2026", "28.5", note: "Ring first");
        await AddAsync(id, updated, failed, planned, checkedEnv);

        await SetVersionAsync(updated, "28.5.40000.0");
        await AddActionAsync(id, projectId, failed, UpgradeActionKind.RunNow, UpgradeActionStatus.Failed);
        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).SetCheckedAsync(await LineIdAsync(id, checkedEnv), true, null);
        }
        await CloseAsync(id);

        int next;
        await using (var ctx = _f.Db.NewContext())
        {
            next = await _f.Upgrades(ctx).CreateFromLeftoversAsync(id, "28.5 second pass");
        }

        await using var verify = _f.Db.NewContext();
        var created = await verify.OeEnvironmentUpgrades.AsNoTracking().Include(u => u.Lines).SingleAsync(u => u.Id == next);
        created.TargetVersion.Should().Be("28.5");
        created.Note.Should().Be("Ring first");
        created.ClosedAt.Should().BeNull();
        created.Lines.Select(l => l.EnvironmentId).Should().BeEquivalentTo([failed, planned]);
        created.Lines.Should().OnlyContain(l => l.IsOpen);
    }

    [Fact]
    public async Task Leftovers_need_the_source_marked_done_first()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).CreateFromLeftoversAsync(id, "Second pass");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Upgrade"].Should().Contain("done first");
    }

    [Fact]
    public async Task Leftovers_are_refused_when_there_are_none()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        await SetVersionAsync(envId, "28.5.1.0");
        await CloseAsync(id);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).CreateFromLeftoversAsync(id, "Second pass");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Upgrade");
    }

    // ── Delete ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_upgrade_nothing_was_done_from_can_be_deleted_with_its_lines()
    {
        var (_, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);

        await using (var ctx = _f.Db.NewContext())
        {
            await _f.Upgrades(ctx).DeleteAsync(id);
        }

        await using var verify = _f.Db.NewContext();
        (await verify.OeEnvironmentUpgrades.AnyAsync()).Should().BeFalse();
        (await verify.OeEnvironmentUpgradeLines.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_upgrade_something_was_sent_from_cannot_be_deleted()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        await BookFromUpgradeAsync(projectId, envId, id);

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Upgrades(ctx).DeleteAsync(id);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Upgrade"].Should().Contain("Mark it done");
    }

    // ── Reading, and the visibility join ────────────────────────────────

    [Fact]
    public async Task The_upgrade_shows_each_line_with_its_state_and_its_last_action()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync("28.5 in November 2026");
        await AddAsync(id, envId);
        await BookFromUpgradeAsync(projectId, envId, id);

        await using var ctx = _f.Db.NewContext();
        var detail = await _f.Upgrades(ctx).GetAsync(id);

        detail.Should().NotBeNull();
        var line = detail!.Lines.Single();
        line.Environment.EnvironmentId.Should().Be(envId);
        line.State.Should().Be(UpgradeLineState.Booked);
        line.LastAction!.UpgradeName.Should().Be("28.5 in November 2026");
        detail.Upgrade.Status.Should().Be(UpgradeStatus.InProgress);
    }

    [Fact]
    public async Task A_line_from_a_solution_the_caller_cannot_see_is_left_out_of_the_upgrade_and_its_counts()
    {
        var (_, visibleEnv) = await _f.SeedCustomerAsync();
        var (hiddenProject, hiddenEnv) = await SeedCustomerWithoutUpdateTeamAsync(ProjectVisibility.Private);
        var id = await CreateAsync();
        await AddAsync(id, visibleEnv);

        // Put there by somebody who could see it; this caller cannot.
        await using (var ctx = _f.Db.NewContext())
        {
            ctx.OeEnvironmentUpgradeLines.Add(new OeEnvironmentUpgradeLine
            {
                OrganizationId = TestDb.DefaultOrgId, UpgradeId = id, EnvironmentId = hiddenEnv,
                ProjectId = hiddenProject, IsOpen = true, AddedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _f.Db.NewContext())
        {
            var detail = await _f.Upgrades(ctx).GetAsync(id);
            detail!.Lines.Select(l => l.Environment.EnvironmentId).Should().Equal(visibleEnv);
            detail.Upgrade.LineCount.Should().Be(1);

            var open = await _f.Upgrades(ctx).ListOpenAsync();
            open.Single().LineCount.Should().Be(1);
            open.Single().Count(UpgradeLineState.Planned).Should().Be(1);
        }
    }

    [Fact]
    public async Task The_open_list_counts_lines_by_state_and_the_archive_is_searchable()
    {
        var (projectId, a) = await _f.SeedCustomerAsync();
        var b = await SeedEnvironmentAsync(projectId, "UAT");
        var open = await CreateAsync("29.1 in January 2027", "29.1");
        await AddAsync(open, a, b);
        await SetVersionAsync(a, "29.1.1.0");
        var done = await CreateAsync("28.5 in November 2026", "28.5");
        await CloseAsync(done);

        await using var ctx = _f.Db.NewContext();
        var svc = _f.Upgrades(ctx);

        var list = await svc.ListOpenAsync();
        list.Select(u => u.Id).Should().Equal(open);
        list.Single().Count(UpgradeLineState.Updated).Should().Be(1);
        list.Single().Count(UpgradeLineState.Planned).Should().Be(1);
        list.Single().Status.Should().Be(UpgradeStatus.InProgress);

        (await svc.ListArchivedAsync()).Select(u => u.Id).Should().Equal(done);
        (await svc.ListArchivedAsync("november")).Select(u => u.Id).Should().Equal(done);
        (await svc.ListArchivedAsync("28.5")).Select(u => u.Id).Should().Equal(done);
        (await svc.ListArchivedAsync("29.1")).Should().BeEmpty();
    }

    // ── The picker's lock ───────────────────────────────────────────────

    [Fact]
    public async Task Open_line_owners_name_the_open_upgrade_and_forget_a_done_one()
    {
        var (projectId, onOpen) = await _f.SeedCustomerAsync();
        var onDone = await SeedEnvironmentAsync(projectId, "UAT");
        await SeedEnvironmentAsync(projectId, "Training");
        var open = await CreateAsync("29.1 in January 2027", "29.1");
        var done = await CreateAsync("28.5 in November 2026", "28.5");
        await AddAsync(open, onOpen);
        await AddAsync(done, onDone);
        await CloseAsync(done);

        await using var ctx = _f.Db.NewContext();
        var owners = await _f.Upgrades(ctx).ListOpenLineOwnersAsync();

        owners.Keys.Should().BeEquivalentTo([onOpen], "a done upgrade frees its environments, and one on none is free");
        owners[onOpen].Should().Be(new UpgradeLineOwner(open, "29.1 in January 2027"));
    }

    [Fact]
    public async Task Open_line_owners_leave_out_a_line_from_a_solution_the_caller_cannot_see()
    {
        var (_, visibleEnv) = await _f.SeedCustomerAsync();
        var (hiddenProject, hiddenEnv) = await SeedCustomerWithoutUpdateTeamAsync(ProjectVisibility.Private);
        var id = await CreateAsync();
        await AddAsync(id, visibleEnv);

        // Put there by somebody who could see it; this caller cannot.
        await using (var ctx = _f.Db.NewContext())
        {
            ctx.OeEnvironmentUpgradeLines.Add(new OeEnvironmentUpgradeLine
            {
                OrganizationId = TestDb.DefaultOrgId, UpgradeId = id, EnvironmentId = hiddenEnv,
                ProjectId = hiddenProject, IsOpen = true, AddedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = _f.Db.NewContext())
        {
            var owners = await _f.Upgrades(ctx).ListOpenLineOwnersAsync();
            owners.Keys.Should().BeEquivalentTo([visibleEnv]);
        }
    }

    // ── Actions carry the upgrade ───────────────────────────────────────

    [Fact]
    public async Task An_action_run_from_an_upgrade_carries_its_id_into_the_history()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync("28.5 in November 2026");
        await AddAsync(id, envId);
        var actionId = await BookFromUpgradeAsync(projectId, envId, id);

        (await _f.ReadActionAsync(actionId)).UpgradeId.Should().Be(id);

        await using var ctx = _f.Db.NewContext();
        var feed = await _f.Svc(ctx).ListEnvironmentActivityAsync(projectId, envId);
        feed.Single().UpgradeId.Should().Be(id);
        feed.Single().UpgradeName.Should().Be("28.5 in November 2026");
    }

    [Fact]
    public async Task An_ad_hoc_action_carries_no_upgrade()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();

        await using var ctx = _f.Db.NewContext();
        var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, envId, UpgradeActionKind.RunNow, _f.Clock.GetUtcNow().AddHours(4));

        row.UpgradeId.Should().BeNull();
        (await _f.ReadActionAsync(row.Id)).UpgradeId.Should().BeNull();
    }

    [Fact]
    public async Task An_action_cannot_claim_an_upgrade_its_environment_is_not_on()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();

        await using var ctx = _f.Db.NewContext();
        var act = () => _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, envId, UpgradeActionKind.RunNow, _f.Clock.GetUtcNow().AddHours(4), upgradeId: id);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Upgrade");
    }

    [Fact]
    public async Task A_booked_action_keeps_its_upgrade_when_the_worker_fires_it()
    {
        var (projectId, envId) = await _f.SeedCustomerAsync();
        var id = await CreateAsync();
        await AddAsync(id, envId);
        var actionId = await BookFromUpgradeAsync(projectId, envId, id);

        _f.Clock.Advance(TimeSpan.FromHours(13));
        await _f.Worker().RunDueActionsAsync(TestDb.DefaultOrgId, isSystem: false, CancellationToken.None);

        var stored = await _f.ReadActionAsync(actionId);
        stored.Status.Should().Be(UpgradeActionStatus.Sent);
        stored.UpgradeId.Should().Be(id);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<int> CreateAsync(string name = "28.5 in November 2026", string version = "28.5", string? note = null)
    {
        await using var ctx = _f.Db.NewContext();
        return await _f.Upgrades(ctx).CreateAsync(name, version, null, note);
    }

    private async Task<AddUpgradeLinesResult> AddAsync(int upgradeId, params int[] environmentIds)
    {
        await using var ctx = _f.Db.NewContext();
        return await _f.Upgrades(ctx).AddLinesAsync(upgradeId, environmentIds);
    }

    private async Task CloseAsync(int upgradeId)
    {
        await using var ctx = _f.Db.NewContext();
        await _f.Upgrades(ctx).CloseAsync(upgradeId);
    }

    private async Task<int> LineIdAsync(int upgradeId, int environmentId)
    {
        await using var ctx = _f.Db.NewContext();
        return await ctx.OeEnvironmentUpgradeLines.AsNoTracking()
            .Where(l => l.UpgradeId == upgradeId && l.EnvironmentId == environmentId)
            .Select(l => l.Id)
            .SingleAsync();
    }

    /// <summary>Books a Start update for twelve hours out from the upgrade, through the real service.</summary>
    private async Task<int> BookFromUpgradeAsync(int projectId, int environmentId, int upgradeId)
    {
        await using var ctx = _f.Db.NewContext();
        var row = await _f.Svc(ctx).ScheduleUpgradeActionAsync(
            projectId, environmentId, UpgradeActionKind.RunNow,
            _f.Clock.GetUtcNow().AddHours(12), upgradeId: upgradeId);
        return row.Id;
    }

    /// <summary>An action row in a given state, written straight to the table, for a derived-state setup.</summary>
    private async Task AddActionAsync(
        int upgradeId, int projectId, int environmentId, UpgradeActionKind kind, UpgradeActionStatus status)
    {
        var now = _f.Clock.GetUtcNow().UtcDateTime;
        await using var ctx = _f.Db.NewContext();
        ctx.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, EnvironmentId = environmentId,
            UpgradeId = upgradeId, Kind = kind, Status = status, RequestedBy = "Anna Jensen",
            RequestedAt = now, ExecuteAfter = now, SentAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedEnvironmentAsync(int projectId, string name)
    {
        await using var ctx = _f.Db.NewContext();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = "Sandbox",
            ApplicationFamily = "BusinessCentral", Status = "Active", Version = "27.5.12345.0",
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }

    private async Task SetVersionAsync(int environmentId, string version)
    {
        await using var ctx = _f.Db.NewContext();
        var env = await ctx.OeProjectEnvironments.SingleAsync(e => e.Id == environmentId);
        env.Version = version;
        await ctx.SaveChangesAsync();
    }

    /// <summary>A customer with no team assigned: the acting user holds the grant nowhere on it.</summary>
    private async Task<(int ProjectId, int EnvironmentId)> SeedCustomerWithoutUpdateTeamAsync(ProjectVisibility visibility)
    {
        await using var ctx = _f.Db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Sverige",
            CreatedByUserId = UpgradeActionTestFixture.OwnerUserId, Visibility = visibility,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            ApplicationFamily = "BusinessCentral", Status = "Active", Version = "27.5.12345.0",
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return (project.Id, env.Id);
    }
}
