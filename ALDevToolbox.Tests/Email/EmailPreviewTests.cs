using System.Reflection;
using System.Text.RegularExpressions;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Services.Email;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Email;

/// <summary>
/// The preview tab on /site-admin/email (issue #1030) only shows emails that
/// have an entry in <see cref="EmailPreviews"/>, so these keep the list whole
/// and every sample safe to show.
/// </summary>
public sealed partial class EmailPreviewTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private EmailRenderer Renderer => new(_services, NullLoggerFactory.Instance);

    /// <summary>
    /// An email is a component in Components/Email with a static RenderAsync;
    /// the building blocks (layout, button, paragraph) have none.
    /// </summary>
    [Fact]
    public void Every_email_has_a_preview()
    {
        var emails = typeof(EmailPreviews).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(EmailPreviews).Namespace
                && typeof(IComponent).IsAssignableFrom(t)
                && t.GetMethod("RenderAsync", BindingFlags.Public | BindingFlags.Static) is not null)
            .ToList();

        emails.Should().NotBeEmpty();
        EmailPreviews.All.Select(p => p.Component).Should().BeEquivalentTo(emails,
            "a new email needs an entry in EmailPreviews to show up on the preview tab");
    }

    [Fact]
    public void Keys_are_unique_and_fit_a_url()
    {
        EmailPreviews.All.Select(p => p.Key).Should().OnlyHaveUniqueItems();
        EmailPreviews.All.Should().AllSatisfy(p => p.Key.Should().MatchRegex("^[a-z0-9-]+$"));
    }

    public static TheoryData<string> Keys => [.. EmailPreviews.All.Select(p => p.Key)];

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Each_preview_renders_with_links_that_go_nowhere(string key)
    {
        var content = await EmailPreviews.Find(key)!.RenderAsync(Renderer, CancellationToken.None);

        content.Subject.Should().NotBeNullOrWhiteSpace();
        content.TextBody.Should().NotBeNullOrWhiteSpace();
        foreach (Match href in Href().Matches(content.HtmlBody))
        {
            href.Groups[1].Value.Should().StartWith(EmailPreviews.SampleOrigin + "/");
        }
    }

    [Fact]
    public void An_unknown_key_finds_nothing()
    {
        EmailPreviews.Find("no-such-email").Should().BeNull();
        EmailPreviews.Find(null).Should().BeNull();
    }

    [GeneratedRegex("href=\"([^\"]*)\"")]
    private static partial Regex Href();
}
