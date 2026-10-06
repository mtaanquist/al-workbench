using ALDevToolbox.Components.Email;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
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
/// Build notifications (issue #1035): one email when a pipeline starts failing
/// and one when it works again, to the people behind the build.
/// </summary>
public sealed class BuildNotifierTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();
    private int _creator;
    private int _starter;
    private int _nightlyOwner;
    private int _projectId;
    private int _pipelineId;
    private DateTime _clock = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_first_build_that_fails_is_news_and_one_that_works_is_not()
    {
        await SeedAsync();

        await FinishAsync(ProjectBuildStatus.Ready);
        _email.Sent.Should().BeEmpty();

        await FinishAsync(ProjectBuildStatus.Failed, failure: "error AL0118: The name 'Brew' does not exist.");
        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["starter@cronus.example", "creator@cronus.example"]);
        _email.Sent.Should().AllSatisfy(s =>
        {
            s.Subject.Should().Be("Build failed: CRONUS Coffee - Main");
            s.Purpose.Should().Be(EmailPurpose.BuildNotification);
            s.Html.Should().Contain("error AL0118").And.Contain($"{Origin}/pipelines/{_pipelineId}?build=");
        });
    }

    [Fact]
    public async Task Someone_who_can_no_longer_see_a_private_solution_is_left_out()
    {
        await SeedAsync();
        await MakePrivateAsync(_projectId);

        await FinishAsync(ProjectBuildStatus.Ready);
        await FinishAsync(ProjectBuildStatus.Failed, failure: "error AL0118: The name 'Brew' does not exist.");

        _email.Sent.Select(s => s.To).Should().Equal(["creator@cronus.example"],
            "the person who started it is on no team of the now private solution; its owner still sees it");
        await using var ctx = _db.NewContext();
        (await ctx.UserNotifications.Select(n => n.UserId).ToListAsync()).Should().Equal(_creator);
    }

    [Fact]
    public async Task Repeated_failures_send_once_and_the_fix_sends_once()
    {
        await SeedAsync();

        await FinishAsync(ProjectBuildStatus.Failed);
        await FinishAsync(ProjectBuildStatus.Failed);
        _email.Sent.Should().HaveCount(2, "one failure email each for the starter and the creator");

        _email.Sent.Clear();
        await FinishAsync(ProjectBuildStatus.Ready);
        await FinishAsync(ProjectBuildStatus.Ready);
        _email.Sent.Select(s => s.Subject).Should().AllBe("Build working again: CRONUS Coffee - Main")
            .And.HaveCount(2);
    }

    [Fact]
    public async Task A_build_on_push_and_a_manual_build_count_as_the_same_kind()
    {
        await SeedAsync();
        await FinishAsync(ProjectBuildStatus.Failed);
        await FinishAsync(ProjectBuildStatus.Failed, ProjectBuildTrigger.Push);
        _email.Sent.Should().HaveCount(2, "the push build failing again is not news");

        _email.Sent.Clear();
        await FinishAsync(ProjectBuildStatus.Ready, ProjectBuildTrigger.Push);
        _email.Sent.Select(s => s.Subject).Should().AllBe("Build working again: CRONUS Coffee - Main");
    }

    [Fact]
    public async Task Nightly_checks_and_targets_are_tracked_apart_from_manual_builds()
    {
        await SeedAsync();
        await FinishAsync(ProjectBuildStatus.Failed);
        _email.Sent.Clear();

        // A nightly next-major failure is news even though the manual build is failing too.
        await FinishAsync(ProjectBuildStatus.Failed, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMajor, "27.0");

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["nightly@cronus.example", "creator@cronus.example"]);
        _email.Sent[0].Subject.Should().Be("Nightly check failed: CRONUS Coffee - Main (Next major)");
        _email.Sent[0].Html.Should().Contain("next major Business Central version (27.0)");
    }

    [Fact]
    public async Task Pull_request_builds_send_nothing()
    {
        await SeedAsync();

        await FinishAsync(ProjectBuildStatus.Failed, ProjectBuildTrigger.PullRequest);

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_person_in_both_roles_gets_one_email()
    {
        await SeedAsync();
        _starter = _creator;

        await FinishAsync(ProjectBuildStatus.Failed);

        _email.Sent.Should().ContainSingle().Which.To.Should().Be("creator@cronus.example");
    }

    [Fact]
    public async Task A_digest_choice_keeps_the_first_line_of_the_failure()
    {
        await SeedAsync();
        _db.OrgContext.CurrentUserId = _creator;
        await using (var ctx = _db.NewContext())
        {
            await new NotificationPreferenceService(ctx, _db.OrgContext, TimeProvider.System,
                NullLogger<NotificationPreferenceService>.Instance)
                .SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Daily);
        }
        _db.OrgContext.CurrentUserId = null;

        await FinishAsync(ProjectBuildStatus.Failed, failure: "Coffee Extension: 2 errors.\nerror AL0118");

        await using var read = _db.NewContext();
        var item = await read.NotificationDigestItems.SingleAsync();
        item.UserId.Should().Be(_creator);
        item.Title.Should().Be("Build failed: CRONUS Coffee - Main");
        item.Detail.Should().Be("Coffee Extension: 2 errors.");
        item.SolutionName.Should().Be("CRONUS Coffee");
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, null, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void Only_a_change_is_news(bool failed, bool? previous, bool expected) =>
        BuildNotifier.IsChange(failed, previous).Should().Be(expected);

    [Fact]
    public void A_long_failure_message_is_cut_to_its_first_lines()
    {
        var message = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));

        var excerpt = BuildNotificationEmail.Excerpt(message);

        excerpt.Should().StartWith("line 1\n").And.Contain("line 6").And.NotContain("line 7").And.EndWith("...");
    }

    [Fact]
    public async Task The_failure_keeps_its_lines_in_both_versions()
    {
        var content = await BuildNotificationEmail.RenderAsync(
            new EmailRenderer(_services, NullLoggerFactory.Instance), "Alex Hansen", "CRONUS A/S", "CRONUS Coffee", "Main",
            failed: true, nightlyCheck: false, target: null, bcVersion: "26.4",
            failureMessage: "CRONUS Coffee: error AL0118\nCRONUS Coffee Reports: error AL0132",
            $"{Origin}/pipelines/1?build=2", $"{Origin}{NotificationService.SettingsPath}");

        content.HtmlBody.Should().Contain("CRONUS Coffee: error AL0118<br");
        content.TextBody.Should().Contain("CRONUS Coffee: error AL0118\nCRONUS Coffee Reports: error AL0132");
        content.TextBody.Should().Contain("Change which emails you get");
    }

    [Fact]
    public async Task A_nightly_check_with_a_failed_extension_is_a_failure()
    {
        await SeedAsync();
        await FinishAsync(ProjectBuildStatus.Ready, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor);

        await FinishAsync(ProjectBuildStatus.Ready, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor,
            failedApps: ["CRONUS Coffee Reports"]);

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["nightly@cronus.example", "creator@cronus.example"]);
        _email.Sent[0].Subject.Should().Be("Nightly check failed: CRONUS Coffee - Main (Next minor)");
        _email.Sent[0].Html.Should().Contain("CRONUS Coffee Reports: error AL0118");
    }

    [Fact]
    public async Task A_nightly_check_still_failing_an_extension_is_not_working_again()
    {
        await SeedAsync();
        await FinishAsync(ProjectBuildStatus.Failed, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor);
        _email.Sent.Clear();

        await FinishAsync(ProjectBuildStatus.Ready, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor,
            failedApps: ["CRONUS Coffee Reports"]);
        _email.Sent.Should().BeEmpty();

        await FinishAsync(ProjectBuildStatus.Ready, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor);
        _email.Sent.Should().NotBeEmpty().And.AllSatisfy(s => s.Subject.Should().StartWith("Nightly check working again"));
    }

    [Fact]
    public async Task A_current_build_with_a_failed_extension_counts_as_working_like_the_pipeline_page()
    {
        await SeedAsync();

        await FinishAsync(ProjectBuildStatus.Ready, failedApps: ["CRONUS Coffee Reports"]);

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_pipeline_does_not_count_as_the_build_before()
    {
        await SeedAsync();
        await FinishAsync(ProjectBuildStatus.Failed);
        _email.Sent.Clear();
        var first = _pipelineId;
        _pipelineId = await AddPipelineAsync("Release");

        await FinishAsync(ProjectBuildStatus.Failed);
        _email.Sent.Should().NotBeEmpty("the other pipeline's failure says nothing about this one");

        _email.Sent.Clear();
        _pipelineId = first;
        await FinishAsync(ProjectBuildStatus.Failed);
        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_build_before_is_the_one_that_finished_before()
    {
        await SeedAsync();
        // Started second but finished first, as after a restart.
        await FinishAsync(ProjectBuildStatus.Ready, finishedAfterMinutes: 1);
        await FinishAsync(ProjectBuildStatus.Failed, finishedAfterMinutes: -90);

        _email.Sent.Should().NotBeEmpty("compared with nothing before it, a failure is news");
        _email.Sent.Clear();
        await FinishAsync(ProjectBuildStatus.Ready);

        _email.Sent.Should().BeEmpty("the build that finished last before it worked");
    }

    // ---- helpers -----------------------------------------------------------

    private async Task MakePrivateAsync(int projectId)
    {
        await using var ctx = _db.NewContext();
        await ctx.OeProjects.Where(p => p.Id == projectId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Visibility, ProjectVisibility.Private));
    }

    private async Task SeedAsync()
    {
        _creator = await SeedUserAsync("creator@cronus.example");
        _starter = await SeedUserAsync("starter@cronus.example");
        _nightlyOwner = await SeedUserAsync("nightly@cronus.example");
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", CreatedByUserId = _creator,
            CreatedAt = _clock, UpdatedAt = _clock,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Main", Branch = "main",
            CreatedByUserId = _creator, PreviewCheckByUserId = _nightlyOwner, CreatedAt = _clock, UpdatedAt = _clock,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        _projectId = project.Id;
        _pipelineId = pipeline.Id;
    }

    /// <summary>Adds a finished build with its release, then runs the notifier as the worker does.</summary>
    private async Task FinishAsync(
        string status,
        string trigger = ProjectBuildTrigger.Manual,
        string target = ProjectBuildTarget.Current,
        string? bcVersion = "26.4",
        string? failure = null,
        string[]? failedApps = null,
        int finishedAfterMinutes = 3)
    {
        _clock = _clock.AddHours(1);
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = $"Build {_clock:HHmm}", BcVersion = bcVersion ?? "26.4",
                DedupKey = Guid.NewGuid().ToString(), Kind = "first_party", Status = "ready",
                ImportedAt = _clock, CreatedAt = _clock,
            };
            ctx.OeReleases.Add(release);
            await ctx.SaveChangesAsync();
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = _projectId, PipelineId = _pipelineId,
                ReleaseId = release.Id,
                StartedByUserId = trigger == ProjectBuildTrigger.PreviewCheck ? _nightlyOwner : _starter,
                Trigger = trigger, BcTarget = target, BcVersion = bcVersion, Status = status,
                FailureMessage = failure ?? (status == ProjectBuildStatus.Failed ? "Compile failed." : null),
                StartedAt = _clock, FinishedAt = _clock.AddMinutes(finishedAfterMinutes),
            });
            foreach (var app in failedApps ?? [])
            {
                ctx.OeProjectBuildResults.Add(new OeProjectBuildResult
                {
                    OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id, AppName = app, AppId = Guid.NewGuid().ToString(),
                    Status = ProjectBuildResultStatus.Failed, Message = "error AL0118: The name 'Brew' does not exist.",
                    CreatedAt = _clock,
                });
            }
            await ctx.SaveChangesAsync();
            releaseId = release.Id;
        }

        await using var work = _db.NewContext();
        var preferences = new NotificationPreferenceService(
            work, _db.OrgContext, TimeProvider.System, NullLogger<NotificationPreferenceService>.Instance);
        var notifications = new NotificationService(
            work, _db.NewContextFactory(), preferences, _email, new EmailRenderer(_services, NullLoggerFactory.Instance),
            new PublicOrigin(Origin), _db.OrgContext, TimeProvider.System, NullLogger<NotificationService>.Instance);
        await new BuildNotifier(work, notifications, NullLogger<BuildNotifier>.Instance).BuildFinishedAsync(releaseId);
    }

    private async Task<int> AddPipelineAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = _projectId, Name = name, Branch = "release",
            CreatedByUserId = _creator, CreatedAt = _clock, UpdatedAt = _clock,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private async Task<int> SeedUserAsync(string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = "Alex Hansen",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
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
