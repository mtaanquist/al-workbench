using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>An email service that keeps what it is asked to send, for tests that drive notifications through real services.</summary>
internal sealed class CapturingEmailService : IEmailService
{
    public List<(string To, string Subject, string Html, EmailPurpose Purpose)> Sent { get; } = [];

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add((toEmail, content.Subject, content.HtmlBody, purpose));
        return Task.CompletedTask;
    }
}
