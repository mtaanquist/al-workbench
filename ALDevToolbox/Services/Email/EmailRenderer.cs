using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ALDevToolbox.Services.Email;

/// <summary>
/// Turns an email component (one of <c>Components/Email/</c>) into an
/// <see cref="EmailContent"/>: the HTML through Blazor's <see cref="HtmlRenderer"/>,
/// the plain-text part converted from that HTML by <see cref="EmailPlainText"/>.
/// One template per email, so the two parts cannot drift apart. See
/// <c>.design/email.md</c> (issue #1028).
///
/// <para>
/// Scoped, and renders with the scope's service provider, so an email
/// component may inject scoped services. A background sender must resolve it
/// from a scope of its own (<see cref="IServiceScopeFactory"/>), never make it
/// a singleton: the root provider cannot hand out scoped services.
/// </para>
/// </summary>
public sealed class EmailRenderer
{
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;

    public EmailRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Renders <typeparamref name="TEmail"/> with <paramref name="parameters"/>.
    /// Values reach the HTML through Razor, which encodes them; the subject is
    /// plain text, so it only has control characters collapsed (see <see cref="CleanSubject"/>).
    /// </summary>
    public async Task<EmailContent> RenderAsync<TEmail>(
        string subject, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct = default)
        where TEmail : IComponent
    {
        ct.ThrowIfCancellationRequested();

        // A renderer per email: it is cheap, and it holds the rendered component
        // tree until disposed, which a long-lived one would accumulate.
        await using var renderer = new HtmlRenderer(_services, _loggerFactory);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TEmail>(ParameterView.FromDictionary(
                parameters.ToDictionary(p => p.Key, p => p.Value)));
            return output.ToHtmlString();
        });
        // Rendering is synchronous work with no token of its own, so the token
        // is honoured either side of it rather than inside.
        ct.ThrowIfCancellationRequested();

        return new EmailContent(CleanSubject(subject), html, EmailPlainText.FromHtml(html));
    }

    /// <summary>
    /// Collapses control characters (newlines, tabs) to spaces and trims. MimeKit
    /// already strips CR/LF on the Subject setter, so this is not the
    /// header-injection guard; it keeps a pasted value from leaving a subject
    /// with odd gaps in it. #417
    /// </summary>
    public static string CleanSubject(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
    }
}
