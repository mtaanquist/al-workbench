using ALDevToolbox.Components.Pages.Teams;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/teams/{id}</c>: anyone signed in can read a team's roster, but only its managers
/// and org Admins get the controls that change it, and removing somebody asks first
/// (#1438). See <c>.design/teams-and-visibility.md</c>.
/// </summary>
public sealed class TeamDetailTests : IAsyncDisposable
{
    private const int ManagerUserId = 9340;
    private const int MemberUserId = 9341;
    private const int AdminUserId = 9342;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly int _teamId;
    private readonly int _memberRowId;

    public TeamDetailTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(
                NewUser(ManagerUserId, "rita@cronus.test", "Rita Reviewer"),
                NewUser(MemberUserId, "max@cronus.test", "Max Member"));
            var team = new Team
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "Nordics",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.Teams.Add(team);
            seed.SaveChanges();
            var member = NewMember(team.Id, MemberUserId, isManager: false);
            seed.TeamMembers.AddRange(NewMember(team.Id, ManagerUserId, isManager: true), member);
            seed.SaveChanges();
            _teamId = team.Id;
            _memberRowId = member.Id;
        }

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<TeamService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    private static User NewUser(int id, string email, string name) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = name,
        Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private static TeamMember NewMember(int teamId, int userId, bool isManager) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = userId,
        IsManager = isManager, CreatedAt = DateTime.UtcNow,
    };

    private IRenderedComponent<TeamDetail> RenderAs(int userId, string email)
    {
        _db.OrgContext.CurrentUserId = userId;
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized(email);
        auth.SetRoles("User");
        var cut = _ctx.Render<TeamDetail>(p => p.Add(x => x.Id, _teamId));
        // The roster is the loaded sign: before it, no button would be there either.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Max Member").And.Contain("Rita Reviewer"));
        return cut;
    }

    private static IEnumerable<string> Buttons(IRenderedComponent<TeamDetail> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim());

    private bool MaxIsOnTheTeam()
    {
        using var ctx = _db.NewContext();
        return ctx.TeamMembers.AsNoTracking().Any(m => m.Id == _memberRowId);
    }

    [Fact]
    public void A_member_who_does_not_manage_the_team_sees_the_roster_without_any_control_that_changes_it()
    {
        var cut = RenderAs(MemberUserId, "max@cronus.test");

        Buttons(cut).Should().NotContain(["Rename", "Add member", "Remove", "Make manager", "Make member"]);
        cut.FindAll("input[type=checkbox]").Should().BeEmpty("a non-manager may not hand out update scheduling");
    }

    [Fact]
    public void The_teams_manager_gets_rename_add_remove_and_the_manager_toggles()
    {
        var cut = RenderAs(ManagerUserId, "rita@cronus.test");

        Buttons(cut).Should().Contain(["Rename", "Add member", "Remove", "Make manager", "Make member"]);
        // #1547: the update-scheduling grant reaches the customer's production tenant,
        // so a manager sees it read-only; only an admin hands it out.
        cut.FindAll("input[type=checkbox]").Should().BeEmpty("a manager could otherwise grant it to themselves");
        cut.Markup.Should().Contain("Only an admin");
    }

    [Fact]
    public void An_org_admin_gets_the_update_scheduling_checkbox_on_every_row()
    {
        using (var seed = _db.NewContext())
        {
            var admin = NewUser(AdminUserId, "ada@cronus.test", "Ada Admin");
            admin.Role = UserRole.Admin;
            seed.Users.Add(admin);
            seed.SaveChanges();
        }

        var cut = RenderAs(AdminUserId, "ada@cronus.test");

        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().HaveCount(2));
    }

    [Fact]
    public void Dismissing_the_remove_question_leaves_the_member_on_the_team()
    {
        var cut = RenderAs(ManagerUserId, "rita@cronus.test");
        RemoveButtonFor(cut, "Max Member").Click();
        cut.WaitForAssertion(() => cut.Find(".confirm-dialog__title").TextContent.Should().Be("Remove Max Member from this team?"));

        cut.FindAll(".confirm-dialog__actions .btn").Single(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions").Should().BeEmpty());
        StillTrue.For(MaxIsOnTheTeam, "saying no must leave the member on the team");
    }

    [Fact]
    public void Confirming_removes_the_member()
    {
        var cut = RenderAs(ManagerUserId, "rita@cronus.test");
        RemoveButtonFor(cut, "Max Member").Click();

        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .Single(b => b.TextContent.Trim() == "Remove from team").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Max Member"));
        MaxIsOnTheTeam().Should().BeFalse();
    }

    private static AngleSharp.Dom.IElement RemoveButtonFor(IRenderedComponent<TeamDetail> cut, string name) =>
        cut.FindAll("tbody tr").Single(r => r.TextContent.Contains(name))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Remove");
}
