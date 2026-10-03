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
                .SetForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Daily);
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
    [InlineData(true, ProjectBuildStatus.Ready, true)]
    [InlineData(true, ProjectBuildStatus.Failed, false)]
    [InlineData(false, null, false)]
    [InlineData(false, ProjectBuildStatus.Ready, false)]
    [InlineData(false, ProjectBuildStatus.Failed, true)]
    public void Only_a_change_is_news(bool failed, string? previous, bool expected) =>
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
        var content = await EmailPreviews.Find("build-failed")!.RenderAsync(
            new EmailRenderer(_services, NullLoggerFactory.Instance), CancellationToken.None);

        content.HtmlBody.Should().Contain("Coffee Extension: 2 errors.<br");
        content.TextBody.Should().Contain("Coffee Extension: 2 errors.\nsrc/Codeunit/CoffeeMgt.Codeunit.al(41,17)");
        content.TextBody.Should().Contain("Change which emails you get");
    }

    // ---- helpers -----------------------------------------------------------

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
        string? failure = null)
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
                ReleaseId = release.Id, StartedByUserId = trigger == ProjectBuildTrigger.Manual ? _starter : null,
                Trigger = trigger, BcTarget = target, BcVersion = bcVersion, Status = status,
                FailureMessage = failure ?? (status == ProjectBuildStatus.Failed ? "Compile failed." : null),
                StartedAt = _clock, FinishedAt = _clock.AddMinutes(3),
            });
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
