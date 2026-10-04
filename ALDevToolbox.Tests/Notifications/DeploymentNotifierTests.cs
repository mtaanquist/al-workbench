using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// Deployment notifications (issue #1036): approval requests to the owners,
/// straight away; results to whoever started or approved the deployment.
/// </summary>
public sealed class DeploymentNotifierTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();
    private int _owner;
    private int _pipelineCreator;
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
    public async Task A_deployment_waiting_for_approval_goes_to_the_owner_and_the_pipeline_creator()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Proposed, triggeredBy: null);

        await Notifier().ProposedAsync([id]);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["owner@cronus.example", "creator@cronus.example"]);
        _email.Sent[0].Subject.Should().Be("Waiting for approval: CRONUS Coffee to Production");
        _email.Sent[0].Purpose.Should().Be(EmailPurpose.DeploymentNotification);
        _email.Sent[0].Html.Should().Contain("CRONUS Coffee 1.4.0.0").And.Contain($"{Origin}/pipelines/deployments/{_releasePipelineId}");
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
            "the pipeline's creator is on no team of the now private solution");
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
    public async Task A_finished_deployment_goes_to_whoever_ran_it(string status, string subject)
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(status, triggeredBy: _approver, failure: "Install refused.");

        await Notifier().NotifyAsync(id);

        var sent = _email.Sent.Should().ContainSingle().Subject;
        sent.To.Should().Be("approver@cronus.example");
        sent.Subject.Should().Be(subject);
        if (status == ProjectDeliveryStatus.Failed) sent.Html.Should().Contain("Install refused.");
        else sent.Html.Should().NotContain("Install refused.");
    }

    [Fact]
    public async Task A_deployment_nobody_started_goes_to_the_pipeline_creator()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Deployed, triggeredBy: null);

        await Notifier().NotifyAsync(id);

        _email.Sent.Should().ContainSingle().Which.To.Should().Be("creator@cronus.example");
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

        _email.Sent.Select(s => s.To).Should().Equal("creator@cronus.example");
    }

    [Fact]
    public async Task Without_a_public_base_url_nothing_is_emailed_but_it_shows_in_the_app()
    {
        await SeedAsync();
        var id = await AddDeliveryAsync(ProjectDeliveryStatus.Failed, triggeredBy: _approver, failure: "Install refused.");

        await Notifier(origin: null).NotifyAsync(id);

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        var listed = (await ctx.UserNotifications.ToListAsync()).Should().ContainSingle().Subject;
        listed.UserId.Should().Be(_approver);
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
        return new DeploymentNotifier(ctx, notifications, NullLogger<DeploymentNotifier>.Instance);
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
        _pipelineCreator = await SeedUserAsync("creator@cronus.example");
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
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id, CreatedByUserId = _pipelineCreator,
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
    }

    private async Task<int> AddDeliveryAsync(string status, int? triggeredBy, string? failure = null)
    {
        var now = DateTime.UtcNow;
        await using var ctx = _db.NewContext();
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = _projectId, ReleasePipelineId = _releasePipelineId,
            ProjectBuildId = _buildId, TriggeredByUserId = triggeredBy, EnvironmentName = "Production",
            ScheduledFor = now, Status = status,
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
