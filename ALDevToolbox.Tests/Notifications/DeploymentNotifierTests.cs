using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// Deployment notifications (issue #1036): approval requests and results to the
/// solution's followers who look after deployments (#1463), approval requests
/// straight away; results also to whoever started or approved the deployment.
/// </summary>
public sealed class DeploymentNotifierTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();
    private int _owner;
    private int _leader;
    private int _approver;
    private int _projectId;
    private int _releasePipelineId;
    private int _buildId;

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_deployment_waiting_for_approval_goes_to_the_followers_who_deploy()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["owner@cronus.example", "leader@cronus.example"]);
        _email.Sent[0].Subject.Should().Be("Waiting for approval: CRONUS Coffee to Production");
        _email.Sent[0].Purpose.Should().Be(EmailPurpose.DeploymentNotification);
        _email.Sent[0].Html.Should().Contain("CRONUS Coffee 1.4.0.0").And.Contain($"{Origin}/pipelines/deployments/{_releasePipelineId}");
    }

    [Fact]
    public async Task Only_an_approval_request_names_the_deployment_it_waits_on()
    {
        await SeedAsync();
        var waiting = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);
        var deployed = await AddDeliveryAsync(ProjectDeliveryStatus.Deployed, triggeredBy: _approver);

        await Notifier().ProposedAsync([waiting]);
        await Notifier().NotifyAsync(deployed);

        await using var ctx = _db.NewContext();
        var rows = await ctx.UserNotifications.AsNoTracking().ToListAsync();
        rows.Where(r => r.Subject != null).Should().HaveCount(2)
            .And.OnlyContain(r => r.Subject == NotificationSubject.Delivery(waiting));
        rows.Where(r => r.Subject == null).Select(r => r.UserId).Should().BeEquivalentTo([_approver, _owner, _leader],
            "a finished deployment asks nothing of anyone");
    }

    [Fact]
    public async Task Someone_who_can_no_longer_see_a_private_solution_is_left_out()
    {
        await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjects.Where(p => p.Id == _projectId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Visibility, ProjectVisibility.Private));
        }
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Select(s => s.To).Should().Equal(["owner@cronus.example"],
            "the project leader is on no team of the now private solution");
    }

    [Fact]
    public async Task An_approval_request_skips_a_digest_choice()
    {
        await SeedAsync();
        await ChooseAsync(_owner, NotificationDelivery.Weekly);
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Select(s => s.To).Should().Contain("owner@cronus.example");
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(ProjectDeliveryStatus.Deployed, "Deployed: CRONUS Coffee to Production")]
    [InlineData(ProjectDeliveryStatus.HandedOff, "Accepted by Business Central: CRONUS Coffee to Production")]
    [InlineData(ProjectDeliveryStatus.Failed, "Deployment failed: CRONUS Coffee to Production")]
    public async Task A_finished_deployment_goes_to_whoever_ran_it_and_the_followers(string status, string subject)
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(status, triggeredBy: _approver, failure: "Install refused.");

        await Notifier().NotifyAsync(id);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["approver@cronus.example", "owner@cronus.example", "leader@cronus.example"]);
        var sent = _email.Sent.Single(s => s.To == "approver@cronus.example");
        sent.Subject.Should().Be(subject);
        if (status == ProjectDeliveryStatus.Failed) sent.Html.Should().Contain("Install refused.");
        else sent.Html.Should().NotContain("Install refused.");
    }

    [Fact]
    public async Task A_deployment_nobody_started_goes_to_the_followers()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Deployed, triggeredBy: null);

        await Notifier().NotifyAsync(id);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["owner@cronus.example", "leader@cronus.example"]);
    }

    [Fact]
    public async Task Followers_hear_about_deployments_by_their_role_on_the_people_list()
    {
        await SeedAsync();
        var developer = await SeedUserAsync("developer@cronus.example");
        var consultant = await SeedUserAsync("consultant@cronus.example");
        await AddPersonAsync(developer, ProjectPersonRole.Developer);
        await AddPersonAsync(consultant, ProjectPersonRole.Consultant);
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(
            ["owner@cronus.example", "leader@cronus.example", "consultant@cronus.example"]);
    }

    [Fact]
    public async Task A_deployment_without_approval_is_not_news_to_whoever_turned_that_on()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Deployed, triggeredBy: _approver, withoutApproval: true);

        await Notifier().NotifyAsync(id);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["owner@cronus.example", "leader@cronus.example"]);
    }

    [Fact]
    public async Task With_no_follower_left_to_tell_the_owner_hears_about_it()
    {
        await SeedAsync();
        await AddPersonAsync(_owner, ProjectPersonRole.Developer);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectPeople.Where(p => p.UserId == _leader).ExecuteDeleteAsync();
        }
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Should().ContainSingle().Which.To.Should().Be("owner@cronus.example");
    }

    [Fact]
    public async Task A_deployment_refused_because_its_person_lost_access_goes_to_the_followers_only()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Failed, triggeredBy: _approver,
            failure: ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryService.SchedulerLostAccess);

        await Notifier().NotifyAsync(id);

        // #1125: the person it ran as can no longer deploy it; the people who can hear instead.
        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["owner@cronus.example", "leader@cronus.example"]);
    }

    [Theory]
    [InlineData(ProjectDeliveryStatus.Scheduled)]
    [InlineData(ProjectDeliveryStatus.Cancelled)]
    [InlineData(ProjectDeliveryStatus.Dismissed)]
    public async Task Other_states_send_nothing(string status)
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(status, triggeredBy: _approver);

        await Notifier().NotifyAsync(id);

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Off_still_stops_an_approval_request()
    {
        await SeedAsync();
        await ChooseAsync(_owner, NotificationDelivery.Off);
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Select(s => s.To).Should().Equal("leader@cronus.example");
    }

    [Fact]
    public async Task Without_a_public_base_url_nothing_is_emailed_but_it_shows_in_the_app()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Failed, triggeredBy: _approver, failure: "Install refused.");

        await Notifier(origin: null).NotifyAsync(id);

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        var listed = (await ctx.UserNotifications.ToListAsync()).Single(n => n.UserId == _approver);
        listed.Path.Should().StartWith("/pipelines/deployments/");
    }

    [Fact]
    public async Task A_digest_keeps_only_the_first_line_of_the_failure()
    {
        await SeedAsync();
        await ChooseAsync(_approver, NotificationDelivery.Daily);
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Failed, triggeredBy: _approver,
            failure: "The delivery failed unexpectedly.\n   at Something.Deep()");

        await Notifier().NotifyAsync(id);

        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.SingleAsync()).Detail.Should().Be("The delivery failed unexpectedly.");
    }

    // ---- helpers -----------------------------------------------------------

    private DeploymentNotifier Notifier(string? origin = Origin)
    {
        var ctx = _db.NewContext();
        var preferences = new NotificationPreferenceService(
            ctx, _db.OrgContext, TimeProvider.System, NullLogger<NotificationPreferenceService>.Instance);
        var notifications = new NotificationService(
            ctx, _db.NewContextFactory(), preferences, _email, new EmailRenderer(_services, NullLoggerFactory.Instance),
            new PublicOrigin(origin), _db.OrgContext, TimeProvider.System, NullLogger<NotificationService>.Instance);
        var follows = new ProjectFollowService(
            ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), TimeProvider.System, NullLogger<ProjectFollowService>.Instance);
        return new DeploymentNotifier(ctx, notifications, follows, NullLogger<DeploymentNotifier>.Instance);
    }

    private async Task ChooseAsync(int userId, NotificationDelivery delivery)
    {
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        await new NotificationPreferenceService(ctx, _db.OrgContext, TimeProvider.System,
            NullLogger<NotificationPreferenceService>.Instance).SetEmailForCurrentUserAsync(NotificationCategory.Deployments, delivery);
        _db.OrgContext.CurrentUserId = null;
    }

    private async Task SeedAsync()
    {
        _owner = await SeedUserAsync("owner@cronus.example");
        _leader = await SeedUserAsync("leader@cronus.example");
        _approver = await SeedUserAsync("approver@cronus.example");
        var now = DateTime.UtcNow;
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", CreatedByUserId = _owner, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Main", Branch = "main", CreatedAt = now, UpdatedAt = now,
        };
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production", FetchedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Coffee to Production",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id, CreatedByUserId = _leader,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        _projectId = project.Id;
        _releasePipelineId = releasePipeline.Id;
        _buildId = build.Id;
        await AddPersonAsync(_leader, ProjectPersonRole.ProjectLeader);
    }

    private async Task AddPersonAsync(int userId, params ProjectPersonRole[] roles)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectPeople.Add(new OeProjectPerson
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = _projectId, UserId = userId, Roles = [.. roles], CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<int> AddDeliveryAsync(string status, int? triggeredBy, string? failure = null, bool withoutApproval = false)
    {
        var now = DateTime.UtcNow;
        await using var ctx = _db.NewContext();
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = _projectId, ReleasePipelineId = _releasePipelineId,
            ProjectBuildId = _buildId, TriggeredByUserId = triggeredBy, EnvironmentName = "Production",
            ScheduledFor = now, Status = status, DeployedWithoutApproval = withoutApproval,
            FailureMessage = status == ProjectDeliveryStatus.Failed ? failure : null,
            CreatedAt = now, UpdatedAt = now,
        };
        delivery.Results.Add(new OeProjectDeliveryResult
        {
            OrganizationId = TestDb.DefaultOrgId, Ordering = 0, AppName = "CRONUS Coffee", AppVersion = "1.4.0.0",
            Status = ProjectDeliveryResultStatus.Pending, CreatedAt = now, UpdatedAt = now,
        });
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
        return delivery.Id;
    }

    private async Task<int> SeedUserAsync(string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = "Alex Hansen", PasswordHash = "x",
            Role = UserRole.User, Status = UserStatus.Active, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject, string Html, EmailPurpose Purpose)> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            Sent.Add((toEmail, content.Subject, content.HtmlBody, purpose));
            return Task.CompletedTask;
        }
    }
}
