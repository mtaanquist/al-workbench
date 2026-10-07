using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// "Business Central version updates" on a solution's Repositories tab (issue #1104).
/// The named user is a consultant who wants the update pull request waiting in GitHub
/// the morning after a customer moves to a new Business Central: the option shows only
/// where it can work, says when it still needs saving, and when the nightly run has
/// stopped, says why and lets someone who manages the solution take it over.
/// </summary>
public sealed class ProjectDetailAutoUpdatesTests : IDisposable
{
    private const string Heading = "Business Central version updates";
    private readonly BunitContext _ctx = new();

    public ProjectDetailAutoUpdatesTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<ProjectDetailRepositories> Render(
        OeProject? saved,
        bool editOn,
        bool canManage = true,
        RepositoryProvider provider = RepositoryProvider.GitHub,
        Action? onResume = null,
        int? viewer = null,
        string? unsavedRepository = null) =>
        _ctx.Render<ProjectDetailRepositories>(p => p
            .Add(c => c.CanManage, canManage)
            .Add(c => c.Providers, new[] { RepositoryProvider.GitHub, RepositoryProvider.AzureDevOps })
            .Add(c => c.LoadedProject, saved)
            .Add(c => c.ViewerUserId, viewer)
            .Add(c => c.OnResumeAutoUpdates, EventCallback.Factory.Create(this, onResume ?? (() => { })))
            .Add(c => c.Edit, new ProjectDetail.EditModel
            {
                AutoUpdatePullRequests = editOn,
                Repos =
                [
                    new ProjectDetail.RepoRow
                    {
                        Provider = provider,
                        Url = UrlFor(provider),
                        SavedUrl = UrlFor(provider),
                        DisplayName = "Payments",
                    },
                    .. unsavedRepository is null
                        ? Array.Empty<ProjectDetail.RepoRow>()
                        : [new ProjectDetail.RepoRow { Provider = RepositoryProvider.GitHub, Url = unsavedRepository, DisplayName = "New" }],
                ],
            }));

    private const int RunAsUserId = 77;

    private static string UrlFor(RepositoryProvider provider) => provider == RepositoryProvider.GitHub
        ? "https://github.com/cronus-dk/payment-import"
        : "https://dev.azure.com/cronus/Base/_git/Payments";

    private static OeProject Saved(bool on, string? blocked = null) => new()
    {
        Id = 5,
        Name = "CRONUS A/S",
        AutoUpdatePullRequests = on,
        AutoUpdatePullRequestsBlocked = blocked,
        AutoUpdatePullRequestsByUserId = on ? RunAsUserId : null,
        AutoUpdatePullRequestsByUser = on ? new ALDevToolbox.Domain.Entities.User { DisplayName = "Mette" } : null,
    };

    [Fact]
    public void A_solution_still_being_created_has_no_section()
    {
        Render(saved: null, editOn: false).Markup.Should().NotContain(Heading);
    }

    [Fact]
    public void A_solution_with_no_GitHub_repository_has_no_section()
    {
        Render(Saved(on: false), editOn: false, provider: RepositoryProvider.AzureDevOps)
            .Markup.Should().NotContain(Heading);
    }

    [Fact]
    public void An_existing_solution_with_a_GitHub_repository_offers_it()
    {
        var cut = Render(Saved(on: false), editOn: false);

        cut.Markup.Should().Contain(Heading);
        cut.Find("#pd-auto-update-prs").HasAttribute("disabled").Should().BeFalse();
        cut.Markup.Should().Contain("Pull requests will be opened with your GitHub account.");
    }

    [Theory]
    [InlineData(false, true, "Save the solution to turn this on.")]
    [InlineData(true, false, "Save the solution to turn this off.")]
    public void A_change_not_saved_yet_says_it_needs_saving(bool savedOn, bool editOn, string expected)
    {
        Render(Saved(savedOn), editOn).Find("small.field__hint").TextContent.Should().Contain(expected);
    }

    [Fact]
    public void Ticking_the_box_marks_the_page_unsaved_and_says_to_save()
    {
        var cut = Render(Saved(on: false), editOn: false);

        cut.Find("#pd-auto-update-prs").Change(true);

        cut.Find("small.field__hint").TextContent.Should().Contain("Save the solution to turn this on.");
    }

    [Fact]
    public void Someone_who_cannot_manage_the_solution_sees_it_read_only()
    {
        var cut = Render(Saved(on: true, blocked: "the person they are opened as no longer has an active account."),
            editOn: true, canManage: false);

        cut.Find("#pd-auto-update-prs").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Resume"));
        cut.Markup.Should().Contain("Ask someone who manages this solution to resume them.");
    }

    [Fact]
    public void A_stopped_run_says_why_and_resuming_takes_it_over()
    {
        var resumed = 0;
        var cut = Render(Saved(on: true, blocked: "the person they are opened as no longer has an active account."),
            editOn: true, onResume: () => resumed++);

        cut.Markup.Should().Contain("Pull requests have stopped: the person they are opened as no longer has an active account.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Resume with my GitHub account")).Click();

        resumed.Should().Be(1);
    }

    [Fact]
    public void A_run_that_went_through_shows_no_warning_and_names_whose_account_is_used()
    {
        var cut = Render(Saved(on: true), editOn: true);

        cut.Markup.Should().NotContain("Pull requests have stopped");
        cut.Markup.Should().Contain("Pull requests are opened with Mette's GitHub account.");
    }

    [Fact]
    public void The_person_it_is_about_reads_the_warning_about_themselves()
    {
        var cut = Render(Saved(on: true, blocked: ALDevToolbox.Services.GitHub.DependencyDriftService.AutomaticNotLinkedMessage),
            editOn: true, viewer: RunAsUserId);

        cut.Markup.Should().Contain("Pull requests have stopped: you have not connected your GitHub account.");
        cut.Markup.Should().NotContain("the person they are opened as");
        cut.Markup.Should().Contain("Pull requests are opened with your GitHub account.");
    }

    [Fact]
    public void Someone_else_reads_the_warning_about_the_person_it_is_about()
    {
        Render(Saved(on: true, blocked: ALDevToolbox.Services.GitHub.DependencyDriftService.AutomaticNotLinkedMessage),
                editOn: true, viewer: 12)
            .Markup.Should().Contain("Pull requests have stopped: the person they are opened as has not connected their GitHub account.");
    }

    [Fact]
    public void Adding_a_repository_unsaved_says_saving_will_use_your_GitHub_account()
    {
        var cut = Render(Saved(on: true), editOn: true, viewer: 12, unsavedRepository: "https://github.com/cronus-dk/warehouse-ext");

        cut.Find("small.field__hint").TextContent
            .Should().Contain("once you save, pull requests will be opened with your GitHub account");
    }

    [Fact]
    public void Adding_a_repository_as_the_person_they_are_already_opened_as_changes_nothing_to_say()
    {
        var cut = Render(Saved(on: true), editOn: true, viewer: RunAsUserId, unsavedRepository: "https://github.com/cronus-dk/warehouse-ext");

        cut.Find("small.field__hint").TextContent.Should().Contain("Pull requests are opened with your GitHub account.");
    }
}
