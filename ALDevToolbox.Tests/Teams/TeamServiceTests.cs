using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Teams;

/// <summary>
/// The authorisation and validation contract for <see cref="TeamService"/>:
/// creating and deleting a team is an admin act, renaming and membership are
/// open to the team's managers too, names are unique per org case-insensitively,
/// deletes cascade, the org query filter hides another org's teams, and every
/// change lands in the audit log. See <c>.design/teams-and-visibility.md</c>.
/// </summary>
public sealed class TeamServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    private const int AdminUserId = 9200;
    private const int ManagerUserId = 9201;
    private const int MemberUserId = 9202;
    private const int OutsiderUserId = 9203;
    private const int OtherOrgUserId = 9204;
    private const int DisabledUserId = 9205;

    public TeamServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser(AdminUserId, "admin@example.com", "Admin", UserRole.Admin),
            NewUser(ManagerUserId, "manager@example.com", "Mona Manager", UserRole.Editor),
            NewUser(MemberUserId, "member@example.com", "Mel Member", UserRole.User),
            NewUser(OutsiderUserId, "outsider@example.com", "Otto Outsider", UserRole.User),
            NewUser(DisabledUserId, "disabled@example.com", "Dana Disabled", UserRole.User, UserStatus.Disabled),
            NewUser(OtherOrgUserId, "other@example.com", "Olga Other", UserRole.Admin, orgId: TestDb.OtherOrgId));
        ctx.SaveChanges();

        // Default acting user: the org Admin. Individual tests switch.
        _db.OrgContext.CurrentUserId = AdminUserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(
        int id, string email, string displayName, UserRole role,
        UserStatus status = UserStatus.Active, int orgId = TestDb.DefaultOrgId) => new()
        {
            Id = id,
            OrganizationId = orgId,
            Email = email,
            PasswordHash = "x",
            DisplayName = displayName,
            Role = role,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };

    private TeamService Svc(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TeamService>.Instance);

    /// <summary>Acts as <paramref name="userId"/> for the rest of the test.</summary>
    private void ActAs(int? userId, bool siteAdmin = false)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    /// <summary>Seeds a team (bypassing the service) with the given membership.</summary>
    private async Task<int> SeedTeamAsync(string name, params (int UserId, bool IsManager)[] members)
    {
        await using var ctx = _db.NewContext();
        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();

        foreach (var (userId, isManager) in members)
        {
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId,
                TeamId = team.Id,
                UserId = userId,
                IsManager = isManager,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
        return team.Id;
    }

    // ------------------------------------------------------------- creating

    [Fact]
    public async Task Create_persists_a_team_the_admin_can_list()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        var id = await svc.CreateTeamAsync("  Nordics  ");

        var teams = await svc.ListTeamsAsync();
        teams.Should().ContainSingle();
        teams[0].Id.Should().Be(id);
        // Trimmed on the way in — a trailing space is a typo, not a name.
        teams[0].Name.Should().Be("Nordics");
        teams[0].MemberCount.Should().Be(0);
        teams[0].ManagerNames.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_rejects_a_blank_name_with_the_Name_field_key()
    {
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).CreateTeamAsync("   ");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_name_ignoring_case()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.CreateTeamAsync("Nordics");

        var act = () => svc.CreateTeamAsync("nordics");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Create_is_refused_for_a_non_admin()
    {
        ActAs(MemberUserId);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).CreateTeamAsync("Nordics");

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task Create_is_refused_for_a_team_manager_who_is_not_an_admin()
    {
        // Managing a team you're on is not the same authority as creating new ones.
        await SeedTeamAsync("Nordics", (ManagerUserId, true));
        ActAs(ManagerUserId);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).CreateTeamAsync("Benelux");

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    // ------------------------------------------------------------- renaming

    [Fact]
    public async Task Rename_changes_the_name()
    {
        var teamId = await SeedTeamAsync("Nordics");
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await svc.RenameTeamAsync(teamId, "Nordics and Benelux");

        (await svc.GetTeamAsync(teamId))!.Name.Should().Be("Nordics and Benelux");
    }

    [Fact]
    public async Task Rename_is_allowed_for_a_manager_of_that_team()
    {
        var teamId = await SeedTeamAsync("Nordics", (ManagerUserId, true));
        ActAs(ManagerUserId);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await svc.RenameTeamAsync(teamId, "Nordics team");

        (await svc.GetTeamAsync(teamId))!.Name.Should().Be("Nordics team");
    }

    [Fact]
    public async Task Rename_rejects_a_name_another_team_already_uses()
    {
        await SeedTeamAsync("Benelux");
        var teamId = await SeedTeamAsync("Nordics");
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).RenameTeamAsync(teamId, "BENELUX");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task Rename_to_the_same_name_is_allowed()
    {
        // The duplicate check must not trip over the row being renamed.
        var teamId = await SeedTeamAsync("Nordics");
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).RenameTeamAsync(teamId, "Nordics");

        await act.Should().NotThrowAsync();
    }

    // ------------------------------------------------------------- deleting

    [Fact]
    public async Task Delete_removes_the_team_and_cascades_its_members()
    {
        var teamId = await SeedTeamAsync("Nordics", (ManagerUserId, true), (MemberUserId, false));
        await using var ctx = _db.NewContext();

        await Svc(ctx).DeleteTeamAsync(teamId);

        await using var read = _db.NewContext();
        (await read.Teams.AnyAsync()).Should().BeFalse();
        (await read.TeamMembers.AnyAsync()).Should().BeFalse();
        // The accounts themselves are untouched.
        (await read.Users.CountAsync(u => u.Id == ManagerUserId || u.Id == MemberUserId)).Should().Be(2);
    }

    [Fact]
    public async Task Delete_is_refused_for_a_manager_who_is_not_an_admin()
    {
        var teamId = await SeedTeamAsync("Nordics", (ManagerUserId, true));
        ActAs(ManagerUserId);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).DeleteTeamAsync(teamId);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    // -------------------------------------------------------------- members

    [Fact]
    public async Task AddMember_puts_the_person_on_the_roster_as_a_plain_member()
    {
        var teamId = await SeedTeamAsync("Nordics");
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await svc.AddMemberAsync(teamId, MemberUserId);

        var roster = (await svc.GetTeamAsync(teamId))!.Members;
        roster.Should().ContainSingle();
        roster[0].UserId.Should().Be(MemberUserId);
        roster[0].DisplayName.Should().Be("Mel Member");
        roster[0].Email.Should().Be("member@example.com");
        roster[0].IsManager.Should().BeFalse();
    }

    [Fact]
    public async Task AddMember_refuses_somebody_already_on_the_team()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).AddMemberAsync(teamId, MemberUserId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task AddMember_refuses_a_user_from_another_organisation()
    {
        var teamId = await SeedTeamAsync("Nordics");
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).AddMemberAsync(teamId, OtherOrgUserId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task AddMember_refuses_a_disabled_account()
    {
        var teamId = await SeedTeamAsync("Nordics");
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).AddMemberAsync(teamId, DisabledUserId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task RemoveMember_takes_the_person_off_the_roster()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var memberId = (await svc.GetTeamAsync(teamId))!.Members[0].MemberId;

        await svc.RemoveMemberAsync(teamId, memberId);

        (await svc.GetTeamAsync(teamId))!.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task SetManager_promotes_and_demotes()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var memberId = (await svc.GetTeamAsync(teamId))!.Members[0].MemberId;

        await svc.SetManagerAsync(teamId, memberId, true);
        (await svc.GetTeamAsync(teamId))!.Members[0].IsManager.Should().BeTrue();

        // Demoting the only manager is allowed — no last-manager guard.
        await svc.SetManagerAsync(teamId, memberId, false);
        (await svc.GetTeamAsync(teamId))!.Members[0].IsManager.Should().BeFalse();
    }

    [Fact]
    public async Task A_manager_cannot_reach_another_teams_member_through_their_own_team()
    {
        // The manager check runs against the team id passed in, so the member
        // lookup must be pinned to that team too. Without the pin, a manager
        // of Nordics could remove or promote anyone on Benelux by naming
        // their own team (#1272).
        var nordics = await SeedTeamAsync("Nordics", (ManagerUserId, true));
        var benelux = await SeedTeamAsync("Benelux", (MemberUserId, false));
        int beneluxMemberId;
        await using (var read = _db.NewContext())
        {
            beneluxMemberId = (await Svc(read).GetTeamAsync(benelux))!.Members[0].MemberId;
        }
        ActAs(ManagerUserId);

        await using (var ctx = _db.NewContext())
        {
            var svc = Svc(ctx);
            await svc.SetManagerAsync(nordics, beneluxMemberId, true);
            await svc.RemoveMemberAsync(nordics, beneluxMemberId);
        }

        ActAs(AdminUserId);
        await using var check = _db.NewContext();
        var members = (await Svc(check).GetTeamAsync(benelux))!.Members;
        members.Should().ContainSingle(m => m.MemberId == beneluxMemberId);
        members[0].IsManager.Should().BeFalse();
    }

    // ------------------------------------------- update-ops flag (issue #657)

    [Fact]
    public async Task SetManagesUpdates_grants_and_withdraws_the_flag()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var memberId = (await svc.GetTeamAsync(teamId))!.Members[0].MemberId;

        await svc.SetManagesUpdatesAsync(teamId, memberId, true);
        var granted = (await svc.GetTeamAsync(teamId))!.Members[0];
        granted.ManagesUpdates.Should().BeTrue();
        // A separate axis: granting it does not make anybody a team manager.
        granted.IsManager.Should().BeFalse();

        await svc.SetManagesUpdatesAsync(teamId, memberId, false);
        (await svc.GetTeamAsync(teamId))!.Members[0].ManagesUpdates.Should().BeFalse();
    }

    [Fact]
    public async Task A_team_manager_can_grant_the_update_flag()
    {
        var teamId = await SeedTeamAsync("Nordics", (ManagerUserId, true), (MemberUserId, false));
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var memberId = (await svc.GetTeamAsync(teamId))!.Members.Single(m => m.UserId == MemberUserId).MemberId;

        ActAs(ManagerUserId);
        await svc.SetManagesUpdatesAsync(teamId, memberId, true);

        (await svc.GetTeamAsync(teamId))!.Members.Single(m => m.UserId == MemberUserId)
            .ManagesUpdates.Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_member_cannot_grant_themselves_the_update_flag()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var memberId = (await svc.GetTeamAsync(teamId))!.Members[0].MemberId;

        ActAs(MemberUserId);
        var act = () => svc.SetManagesUpdatesAsync(teamId, memberId, true);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        ActAs(AdminUserId);
        (await svc.GetTeamAsync(teamId))!.Members[0].ManagesUpdates.Should().BeFalse();
    }

    [Fact]
    public async Task SetManagesUpdates_refuses_a_membership_of_another_team()
    {
        var nordics = await SeedTeamAsync("Nordics", (MemberUserId, false));
        var benelux = await SeedTeamAsync("Benelux", (OutsiderUserId, false));
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        var elsewhere = (await svc.GetTeamAsync(benelux))!.Members[0].MemberId;

        var act = () => svc.SetManagesUpdatesAsync(nordics, elsewhere, true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("MemberId");
    }

    [Fact]
    public async Task SetManagesUpdates_refuses_a_membership_that_does_not_exist()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SetManagesUpdatesAsync(teamId, 987654, true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("MemberId");
    }

    [Fact]
    public async Task ListAddableUsers_excludes_current_members_and_disabled_accounts()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await using var ctx = _db.NewContext();

        var candidates = await Svc(ctx).ListAddableUsersAsync(teamId);

        candidates.Select(c => c.UserId).Should()
            .Contain(new[] { AdminUserId, ManagerUserId, OutsiderUserId })
            .And.NotContain(MemberUserId)
            .And.NotContain(DisabledUserId)
            .And.NotContain(OtherOrgUserId);
    }

    // ---------------------------------------------------------- manage gate

    [Fact]
    public async Task SiteAdmin_can_manage_any_team()
    {
        var teamId = await SeedTeamAsync("Nordics");
        ActAs(OutsiderUserId, siteAdmin: true);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).CanManageTeamAsync(teamId)).Should().BeTrue();
    }

    [Fact]
    public async Task Org_admin_can_manage_a_team_they_are_not_on()
    {
        var teamId = await SeedTeamAsync("Nordics");
        ActAs(AdminUserId);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).CanManageTeamAsync(teamId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_manager_of_the_team_can_manage_it()
    {
        var teamId = await SeedTeamAsync("Nordics", (ManagerUserId, true));
        ActAs(ManagerUserId);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).CanManageTeamAsync(teamId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_plain_member_cannot_manage_the_team()
    {
        var teamId = await SeedTeamAsync("Nordics", (MemberUserId, false));
        ActAs(MemberUserId);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).CanManageTeamAsync(teamId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_non_member_cannot_manage_the_team()
    {
        var teamId = await SeedTeamAsync("Nordics", (ManagerUserId, true));
        ActAs(OutsiderUserId);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).CanManageTeamAsync(teamId)).Should().BeFalse();
    }

    [Fact]
    public async Task Managing_one_team_does_not_grant_managing_another()
    {
        await SeedTeamAsync("Nordics", (ManagerUserId, true));
        var otherTeamId = await SeedTeamAsync("Benelux");
        ActAs(ManagerUserId);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).CanManageTeamAsync(otherTeamId)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_denied_rather_than_throwing()
    {
        var teamId = await SeedTeamAsync("Nordics");
        ActAs(null);
        await using var ctx = _db.NewContext();

        // A background worker under an ambient org scope has no user; the check is
        // called from render paths, so it must answer rather than blow up.
        (await Svc(ctx).CanManageTeamAsync(teamId)).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCanManage_throws_for_somebody_who_cannot()
    {
        var teamId = await SeedTeamAsync("Nordics");
        ActAs(OutsiderUserId);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).EnsureCanManageTeamAsync(teamId);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task List_names_the_managers_rather_than_counting_them()
    {
        // The list surfaces "Managed by ..." so a reader knows who to ask; a
        // count would not tell them that.
        await SeedTeamAsync("Nordics",
            (ManagerUserId, true), (AdminUserId, true), (MemberUserId, false));
        await using var ctx = _db.NewContext();

        var teams = await Svc(ctx).ListTeamsAsync();

        var nordics = teams.Single(t => t.Name == "Nordics");
        nordics.MemberCount.Should().Be(3);
        nordics.HasManager.Should().BeTrue();
        // Ordered by display name so the column is stable between renders.
        nordics.ManagerNames.Should().Equal("Admin", "Mona Manager");
    }

    // ----------------------------------------------------------- "my teams"

    [Fact]
    public async Task ListMyTeams_returns_only_the_teams_the_user_is_on()
    {
        var nordics = await SeedTeamAsync("Nordics", (MemberUserId, false));
        await SeedTeamAsync("Benelux", (ManagerUserId, true));
        ActAs(MemberUserId);
        await using var ctx = _db.NewContext();

        var mine = await Svc(ctx).ListMyTeamsAsync();

        mine.Should().ContainSingle().Which.Id.Should().Be(nordics);
    }

    // -------------------------------------------------------- org isolation

    [Fact]
    public async Task A_team_from_another_org_is_invisible()
    {
        await SeedTeamAsync("Nordics");

        // Switch the ambient context to the other org: the EF query filter is the
        // only thing standing between the two, so this is the test that matters.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        ActAs(OtherOrgUserId);
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        (await svc.ListTeamsAsync()).Should().BeEmpty();

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
    }

    [Fact]
    public async Task An_admin_of_another_org_cannot_manage_this_orgs_team()
    {
        var teamId = await SeedTeamAsync("Nordics");

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        ActAs(OtherOrgUserId);
        await using var ctx = _db.NewContext();

        // Their Admin role is real, but it belongs to the other org — and the team
        // isn't visible from there at all.
        (await Svc(ctx).GetTeamAsync(teamId)).Should().BeNull();

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
    }

    [Fact]
    public async Task Two_orgs_may_each_have_a_team_with_the_same_name()
    {
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).CreateTeamAsync("Nordics");
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        ActAs(OtherOrgUserId);
        await using (var ctx = _db.NewContext())
        {
            var act = () => Svc(ctx).CreateTeamAsync("Nordics");
            await act.Should().NotThrowAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task Creating_renaming_and_deleting_a_team_are_audited()
    {
        int teamId;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            teamId = await Svc(ctx).CreateTeamAsync("Nordics");
        }
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Svc(ctx).RenameTeamAsync(teamId, "Nordics and Benelux");
        }
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Svc(ctx).DeleteTeamAsync(teamId);
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.Team)
            .OrderBy(r => r.Id)
            .ToListAsync();

        rows.Select(r => r.Action).Should().Equal(
            AuditAction.Created, AuditAction.Updated, AuditAction.Deleted);
        // The name is captured at write time, and an update names what the thing
        // was called going in — so the rename row still says "Nordics".
        rows[0].EntityName.Should().Be("Nordics");
        rows[1].EntityName.Should().Be("Nordics");
        rows[2].EntityName.Should().Be("Nordics and Benelux");
    }

    [Fact]
    public async Task Membership_changes_are_audited()
    {
        var teamId = await SeedTeamAsync("Nordics");

        int memberId;
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            memberId = await Svc(ctx).AddMemberAsync(teamId, MemberUserId);
        }
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Svc(ctx).SetManagerAsync(teamId, memberId, true);
        }
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Svc(ctx).RemoveMemberAsync(teamId, memberId);
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.TeamMember)
            .OrderBy(r => r.Id)
            .ToListAsync();

        rows.Select(r => r.Action).Should().Equal(
            AuditAction.Created, AuditAction.Updated, AuditAction.Deleted);
        // A join row has no name of its own; the snapshot carries the ids.
        rows.Should().OnlyContain(r => r.EntityName == null);
        rows[1].SnapshotJson.Should().Contain("\"IsManager\"");
    }

    /// <summary>
    /// Issue #702: the pre-check reads before the save writes, so a second admin
    /// can take the name in between. Reproduced deterministically by holding a
    /// repeatable-read snapshot from before the other team existed — the
    /// pre-check sees a free name, the unique index does not — and asserting the
    /// user still gets the inline Name error rather than a raw database fault.
    /// </summary>
    [Fact]
    public async Task Create_translates_a_lost_name_race_into_the_Name_field_error()
    {
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await using var tx = await ctx.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead);
        // Any read fixes the snapshot; from here this connection cannot see
        // rows another session commits.
        await ctx.Teams.AsNoTracking().AnyAsync(t => t.Name == "anything");

        // The other admin wins the race.
        await SeedTeamAsync("Nordics");

        var act = () => svc.CreateTeamAsync("Nordics");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
    }
}
