using ALDevToolbox.Components.Email;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Email;

/// <summary>
/// What the notification emails say in their body, not only their subject:
/// someone reading the email alone must know what happened and what to do.
/// </summary>
public sealed class NotificationEmailCopyTests : IDisposable
{
    private const string Link = "https://workbench.cronus.example/environments/4/history";

    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private EmailRenderer Renderer => new(_services, NullLoggerFactory.Instance);

    private Task<EmailContent> UpgradeActionAsync(UpgradeActionResult result, string? outcome) =>
        UpgradeActionEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S", result, "Install CRONUS Coffee.app",
            "CRONUS", "Production", outcome, Link, Link);

    [Theory]
    [InlineData(UpgradeActionResult.Done)]
    [InlineData(UpgradeActionResult.Failed)]
    [InlineData(UpgradeActionResult.Unconfirmed)]
    public async Task A_scheduled_change_names_what_was_scheduled_in_the_body(UpgradeActionResult result)
    {
        var content = await UpgradeActionAsync(result, null);

        content.TextBody.Should().Contain("Install CRONUS Coffee.app, which you scheduled on Production for CRONUS");
    }

    [Fact]
    public async Task A_failed_change_points_at_the_cause_only_when_there_is_one_to_show()
    {
        var with = await UpgradeActionAsync(UpgradeActionResult.Failed, "The app needs CRONUS Base 2.0.");
        var without = await UpgradeActionAsync(UpgradeActionResult.Failed, null);

        with.TextBody.Should().Contain("Fix the cause shown below").And.Contain("The app needs CRONUS Base 2.0.");
        with.HtmlBody.Should().Contain("<blockquote");
        without.TextBody.Should().NotContain("shown below");
        without.HtmlBody.Should().NotContain("<blockquote");
    }

    [Fact]
    public async Task An_unconfirmed_change_says_who_could_not_confirm_it()
    {
        var content = await UpgradeActionAsync(UpgradeActionResult.Unconfirmed, null);

        content.TextBody.Should().Contain("AL Workbench could not confirm").And.NotContain("we could not");
    }

    [Fact]
    public async Task A_deployment_waiting_for_approval_says_nothing_is_installed_yet()
    {
        var content = await DeploymentNotificationEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S",
            DeploymentOutcome.WaitingForApproval, "CRONUS", "Nightly", "Production", ["CRONUS Coffee 1.4.0.0"],
            null, Link, Link);

        content.TextBody.Should().Contain("Nothing is installed until someone approves it.");
    }

    [Fact]
    public async Task A_quoted_failure_keeps_its_lines_in_both_parts()
    {
        var content = await DeploymentNotificationEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S",
            DeploymentOutcome.Failed, "CRONUS", "Nightly", "Production", [],
            "CRONUS Coffee: the schema is not compatible.\r\nCRONUS Tea: a dependency is missing.", Link, Link);

        content.HtmlBody.Should().Contain("compatible.<br>");
        content.TextBody.Should().Contain("compatible.\nCRONUS Tea");
    }

    [Fact]
    public async Task The_digest_does_not_claim_it_is_only_about_your_own_work()
    {
        var content = await DigestEmail.RenderAsync(Renderer, "Mads", "CRONUS A/S", weekly: false,
            [new DigestSection(NotificationCategories.Label(NotificationCategory.Solutions),
                [new NotificationDigestEntry("Update scheduled: CRONUS / Production", null, Link, "CRONUS")])],
            Link);

        content.TextBody.Should().Contain("Here is what happened since your last digest (1 update).");
        content.TextBody.Should().Contain("Solutions you follow");
    }

    [Fact]
    public void Every_category_has_a_label_of_its_own()
    {
        var labels = Enum.GetValues<NotificationCategory>().Select(NotificationCategories.Label).ToList();

        labels.Should().OnlyHaveUniqueItems();
        // The one whose code name is not what people read.
        NotificationCategories.Label(NotificationCategory.Solutions).Should().Be("Solutions you follow");
    }
}
