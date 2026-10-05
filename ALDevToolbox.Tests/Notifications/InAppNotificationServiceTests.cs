using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// The Notifications page and header count (issue #1043): a person sees,
/// opens and marks read only their own notifications.
/// </summary>
public sealed class InAppNotificationServiceTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_list_is_the_signed_in_persons_own_newest_first()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        await AddAsync(alex, "Older", Monday);
        await AddAsync(alex, "Newer", Monday.AddHours(1));
        await AddAsync(sam, "Sam's", Monday.AddHours(2));
        _db.OrgContext.CurrentUserId = alex;

        var rows = await Service().ListForCurrentUserAsync();

        rows.Select(r => r.Title).Should().Equal("Newer", "Older");
        rows.Should().AllSatisfy(r => r.Read.Should().BeFalse());
    }

    [Fact]
    public async Task The_bell_has_the_unread_count_and_the_newest_few()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        for (var i = 0; i < InAppNotificationService.FlyoutSize + 2; i++)
        {
            await AddAsync(alex, $"Event {i}", Monday.AddMinutes(i));
        }
        await AddAsync(alex, "Read", Monday.AddMinutes(-1), readAt: Monday);
        await AddAsync(sam, "Sam's", Monday.AddHours(1));
        _db.OrgContext.CurrentUserId = alex;

        var (unread, rows) = await Service().GetBellForCurrentUserAsync();

        unread.Should().Be(InAppNotificationService.FlyoutSize + 2);
        rows.Should().HaveCount(InAppNotificationService.FlyoutSize);
        rows[0].Title.Should().Be($"Event {InAppNotificationService.FlyoutSize + 1}");
        rows[^1].Title.Should().Be("Event 2");
    }

    [Fact]
    public async Task The_bell_is_empty_when_nobody_is_signed_in()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await AddAsync(alex, "Unread", Monday);

        var (unread, rows) = await Service().GetBellForCurrentUserAsync();

        unread.Should().Be(0);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task The_count_is_the_signed_in_persons_unread_only()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        await AddAsync(alex, "Unread", Monday);
        await AddAsync(alex, "Read", Monday, readAt: Monday.AddMinutes(5));
        await AddAsync(sam, "Sam's", Monday);
        _db.OrgContext.CurrentUserId = alex;

        (await Service().CountUnreadForCurrentUserAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Nobody_signed_in_counts_nothing()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await AddAsync(alex, "Unread", Monday);

        (await Service().CountUnreadForCurrentUserAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Opening_one_marks_it_read_and_returns_its_page()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var id = await AddAsync(alex, "Build failed", Monday);
        var other = await AddAsync(alex, "Still unread", Monday);
        _db.OrgContext.CurrentUserId = alex;

        (await Service().OpenForCurrentUserAsync(id)).Should().Be("/pipelines/1?build=7");

        await using var ctx = _db.NewContext();
        (await ctx.UserNotifications.SingleAsync(n => n.Id == id)).ReadAt.Should().NotBeNull();
        (await ctx.UserNotifications.SingleAsync(n => n.Id == other)).ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task Someone_elses_notification_cannot_be_opened()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        var samsId = await AddAsync(sam, "Sam's", Monday);
        _db.OrgContext.CurrentUserId = alex;

        (await Service().OpenForCurrentUserAsync(samsId)).Should().BeNull();

        await using var ctx = _db.NewContext();
        (await ctx.UserNotifications.SingleAsync(n => n.Id == samsId)).ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task Another_organisations_notification_is_not_found()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var stranger = await SeedUserAsync("stranger@fabrikam.example", TestDb.OtherOrgId);
        var theirs = await AddAsync(stranger, "Theirs", Monday, TestDb.OtherOrgId);
        _db.OrgContext.CurrentUserId = alex;

        (await Service().OpenForCurrentUserAsync(theirs)).Should().BeNull();
        (await Service().ListForCurrentUserAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Mark_all_as_read_touches_only_the_signed_in_persons_unread()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        await AddAsync(alex, "One", Monday);
        await AddAsync(alex, "Already read", Monday, readAt: Monday);
        var newest = await AddAsync(alex, "Two", Monday);
        await AddAsync(sam, "Sam's", Monday);
        _db.OrgContext.CurrentUserId = alex;

        (await Service().MarkAllReadForCurrentUserAsync(newest)).Should().Be(2);

        (await Service().CountUnreadForCurrentUserAsync()).Should().Be(0);
        _db.OrgContext.CurrentUserId = sam;
        (await Service().CountUnreadForCurrentUserAsync()).Should().Be(1);
    }

    [Fact]
    public async Task One_that_arrived_after_the_page_loaded_stays_unread()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var shown = await AddAsync(alex, "Shown", Monday);
        await AddAsync(alex, "Arrived later", Monday.AddMinutes(1));
        _db.OrgContext.CurrentUserId = alex;

        (await Service().MarkAllReadForCurrentUserAsync(upToId: shown)).Should().Be(1);

        (await Service().ListForCurrentUserAsync()).Single(r => !r.Read).Title.Should().Be("Arrived later");
    }

    [Fact]
    public async Task A_private_solution_the_person_can_no_longer_see_drops_out_of_the_list_count_and_open()
    {
        var owner = await SeedUserAsync("owner@cronus.example");
        var member = await SeedUserAsync("member@cronus.example");
        var removed = await SeedUserAsync("removed@cronus.example");
        var admin = await SeedUserAsync("admin@cronus.example", role: UserRole.Admin);
        var projectId = await SeedPrivateProjectAsync(owner, member);
        foreach (var userId in new[] { owner, member, removed, admin })
        {
            await AddAsync(userId, "Secret", Monday, projectId: projectId);
            await AddAsync(userId, "Open", Monday);
        }

        _db.OrgContext.CurrentUserId = removed;
        int hiddenId;
        await using (var ctx = _db.NewContext())
        {
            hiddenId = (await ctx.UserNotifications.SingleAsync(n => n.UserId == removed && n.ProjectId == projectId)).Id;
        }
        (await Service().ListForCurrentUserAsync()).Select(r => r.Title).Should().Equal("Open");
        (await Service().CountUnreadForCurrentUserAsync()).Should().Be(1);
        (await Service().OpenForCurrentUserAsync(hiddenId)).Should().BeNull();

        foreach (var userId in new[] { owner, member, admin })
        {
            _db.OrgContext.CurrentUserId = userId;
            (await Service().ListForCurrentUserAsync()).Select(r => r.Title).Should().BeEquivalentTo(["Secret", "Open"]);
            (await Service().CountUnreadForCurrentUserAsync()).Should().Be(2);
        }
    }

    [Theory]
    [InlineData("/pipelines/1?build=7", true)]
    [InlineData("/pipelines/1\t", false)]
    [InlineData("/pipelines/1\r\nLocation: x", false)]
    [InlineData("/", true)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_paths_within_the_app_are_followed(string? path, bool followed) =>
        NotificationEndpoints.IsAppPath(path).Should().Be(followed);

    // ---- helpers -----------------------------------------------------------

    private InAppNotificationService Service() => new(
        _db.NewContextFactory(), _db.OrgContext, TimeProvider.System, NullLogger<InAppNotificationService>.Instance);

    private async Task<int> AddAsync(
        int userId, string title, DateTime createdAt, int organizationId = TestDb.DefaultOrgId, DateTime? readAt = null,
        int? projectId = null)
    {
        await using var ctx = _db.NewContext();
        var row = new UserNotification
        {
            UserId = userId, OrganizationId = organizationId, Category = NotificationCategory.Builds,
            Title = title, Path = "/pipelines/1?build=7", CreatedAt = createdAt, ReadAt = readAt,
            ProjectId = projectId,
        };
        ctx.UserNotifications.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> SeedPrivateProjectAsync(int ownerId, int memberId)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Secret", CreatedByUserId = ownerId,
            Visibility = ProjectVisibility.Private, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var team = new Team { OrganizationId = TestDb.DefaultOrgId, Name = "Secret", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        team.Members.Add(new TeamMember { OrganizationId = TestDb.DefaultOrgId, UserId = memberId, CreatedAt = DateTime.UtcNow });
        ctx.OeProjects.Add(project);
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        ctx.OeProjectTeams.Add(new OeProjectTeam { OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, TeamId = team.Id, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> SeedUserAsync(string email, int organizationId = TestDb.DefaultOrgId, UserRole role = UserRole.User)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId, Email = email, DisplayName = "Alex Hansen", PasswordHash = "x",
            Role = role, Status = UserStatus.Active, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }
}
