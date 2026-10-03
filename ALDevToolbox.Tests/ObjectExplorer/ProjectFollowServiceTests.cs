using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Following a solution (issue #1048): the owner and the People list follow by
/// default, anyone who can see it may follow, anyone may stop, and a follower who
/// can no longer see a Private solution is left out of its notifications.
/// </summary>
public sealed class ProjectFollowServiceTests : IDisposable
{
    private const int OwnerUserId = 9950;
    private const int PersonUserId = 9951;
    private const int ColleagueUserId = 9952;
    private const int AdminUserId = 9953;
    private const int DisabledUserId = 9954;

    private readonly TestDb _db = new();

    public ProjectFollowServiceTests()
    {
        using var seed = _db.NewContext();
        seed.Users.AddRange(
            NewUser(OwnerUserId, "owner@cronus.example"),
            NewUser(PersonUserId, "person@cronus.example"),
            NewUser(ColleagueUserId, "colleague@cronus.example"),
            NewUser(AdminUserId, "admin@cronus.example", UserRole.Admin),
            NewUser(DisabledUserId, "gone@cronus.example", status: UserStatus.Disabled));
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_owner_and_the_people_list_follow_by_default()
    {
        var projectId = await SeedAsync(people: [PersonUserId]);

        (await ListAsync(projectId)).Should().Equal(OwnerUserId, PersonUserId);
        (await IsFollowingAsAsync(OwnerUserId, projectId)).Should().BeTrue();
        (await IsFollowingAsAsync(ColleagueUserId, projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task Anyone_who_can_see_a_solution_can_follow_it_and_stop_again()
    {
        var projectId = await SeedAsync();

        await SetAsAsync(ColleagueUserId, projectId, true);
        await SetAsAsync(ColleagueUserId, projectId, true); // a double click is harmless
        (await IsFollowingAsAsync(ColleagueUserId, projectId)).Should().BeTrue();
        (await ListAsync(projectId)).Should().Equal(OwnerUserId, ColleagueUserId);

        await SetAsAsync(ColleagueUserId, projectId, false);
        (await ListAsync(projectId)).Should().Equal(OwnerUserId);
    }

    [Fact]
    public async Task The_owner_can_stop_following()
    {
        var projectId = await SeedAsync(people: [PersonUserId]);

        await SetAsAsync(OwnerUserId, projectId, false);

        (await IsFollowingAsAsync(OwnerUserId, projectId)).Should().BeFalse();
        (await ListAsync(projectId)).Should().Equal(PersonUserId);
    }

    [Fact]
    public async Task A_private_solution_cannot_be_followed_by_someone_who_cannot_see_it()
    {
        var projectId = await SeedAsync(visibility: ProjectVisibility.Private);

        var act = () => SetAsAsync(ColleagueUserId, projectId, true);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task A_follower_who_lost_access_to_a_private_solution_is_left_out_and_admins_are_not()
    {
        var projectId = await SeedAsync(people: [PersonUserId]);
        await SetAsAsync(AdminUserId, projectId, true);
        await SetAsAsync(ColleagueUserId, projectId, true);
        await using (var ctx = _db.NewContext())
        {
            var team = new Team { OrganizationId = TestDb.DefaultOrgId, Name = "Coffee", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            team.Members.Add(new TeamMember { OrganizationId = TestDb.DefaultOrgId, UserId = PersonUserId, CreatedAt = DateTime.UtcNow });
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            ctx.OeProjectTeams.Add(new OeProjectTeam { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id, CreatedAt = DateTime.UtcNow });
            var project = await ctx.OeProjects.FindAsync(projectId);
            project!.Visibility = ProjectVisibility.Private;
            await ctx.SaveChangesAsync();
        }

        (await ListAsync(projectId)).Should().Equal(OwnerUserId, PersonUserId, AdminUserId);
    }

    [Fact]
    public async Task A_team_member_can_follow_a_private_solution_and_anyone_can_follow_a_read_only_one()
    {
        var privateId = await SeedAsync(people: [PersonUserId]);
        await AssignTeamAsync(privateId, ColleagueUserId, ProjectVisibility.Private);
        var readOnlyId = await SeedAsync(name: "CRONUS Tea");
        await AssignTeamAsync(readOnlyId, PersonUserId, ProjectVisibility.ReadOnly);

        await SetAsAsync(ColleagueUserId, privateId, true);
        await SetAsAsync(ColleagueUserId, readOnlyId, true);

        (await ListAsync(privateId)).Should().Equal(OwnerUserId, ColleagueUserId);
        (await ListAsync(readOnlyId)).Should().Equal(OwnerUserId, ColleagueUserId);
    }

    [Fact]
    public async Task Someone_on_the_people_list_can_stop_following()
    {
        var projectId = await SeedAsync(people: [PersonUserId]);

        await SetAsAsync(PersonUserId, projectId, false);

        (await ListAsync(projectId)).Should().Equal(OwnerUserId);
    }

    [Fact]
    public async Task A_deleted_solution_cannot_be_followed_and_has_no_followers()
    {
        var projectId = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            (await ctx.OeProjects.FindAsync(projectId))!.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var act = () => SetAsAsync(ColleagueUserId, projectId, true);

        await act.Should().ThrowAsync<PlanValidationException>();
        (await ListAsync(projectId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organisations_solution_cannot_be_followed_or_read()
    {
        var projectId = await SeedAsync(people: [PersonUserId]);
        int otherOrg;
        await using (var ctx = _db.NewContext())
        {
            var org = new Organization { Name = "Fabrikam", Slug = "fabrikam", CreatedAt = DateTime.UtcNow };
            ctx.Organizations.Add(org);
            await ctx.SaveChangesAsync();
            otherOrg = org.Id;
        }

        _db.OrgContext.CurrentOrganizationId = otherOrg;
        try
        {
            var act = () => SetAsAsync(ColleagueUserId, projectId, true);
            await act.Should().ThrowAsync<PlanValidationException>();
            (await ListAsync(projectId)).Should().BeEmpty();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
        await using var verify = _db.NewContext();
        (await verify.OeProjectFollowers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_disabled_account_is_left_out()
    {
        var projectId = await SeedAsync(people: [DisabledUserId]);

        (await ListAsync(projectId)).Should().Equal(OwnerUserId);
    }

    // ---- helpers -----------------------------------------------------------

    private ProjectFollowService Svc(AppDbContext ctx) => new(
        ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), TimeProvider.System, NullLogger<ProjectFollowService>.Instance);

    private async Task<List<int>> ListAsync(int projectId)
    {
        _db.OrgContext.CurrentUserId = null;
        await using var ctx = _db.NewContext();
        return await Svc(ctx).ListFollowerIdsAsync(projectId);
    }

    private async Task<bool> IsFollowingAsAsync(int userId, int projectId)
    {
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        return await Svc(ctx).IsFollowingAsync(projectId);
    }

    private async Task SetAsAsync(int userId, int projectId, bool following)
    {
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        await Svc(ctx).SetFollowingAsync(projectId, following);
    }

    private async Task AssignTeamAsync(int projectId, int memberId, ProjectVisibility visibility)
    {
        await using var ctx = _db.NewContext();
        var team = new Team { OrganizationId = TestDb.DefaultOrgId, Name = $"Team {projectId}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        team.Members.Add(new TeamMember { OrganizationId = TestDb.DefaultOrgId, UserId = memberId, CreatedAt = DateTime.UtcNow });
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        ctx.OeProjectTeams.Add(new OeProjectTeam { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, TeamId = team.Id, CreatedAt = DateTime.UtcNow });
        (await ctx.OeProjects.FindAsync(projectId))!.Visibility = visibility;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedAsync(ProjectVisibility visibility = ProjectVisibility.Public, int[]? people = null, string name = "CRONUS Coffee")
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedByUserId = OwnerUserId,
            Visibility = visibility, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        foreach (var userId in people ?? [])
        {
            ctx.OeProjectPeople.Add(new OeProjectPerson
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, UserId = userId,
                Role = ProjectPersonRole.Consultant, CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private static User NewUser(int id, string email, UserRole role = UserRole.User, UserStatus status = UserStatus.Active) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = email,
        Role = role, Status = status, CreatedAt = DateTime.UtcNow,
    };
}
