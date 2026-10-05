using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Light-weight behavioural coverage for <see cref="SmtpEmailService"/>:
/// <c>IsConfiguredAsync</c> tracks SMTP resolution, <c>SendAsync</c> throws
/// when nothing is configured (so misconfiguration is loud rather than
/// silent), and the resolved settings are cached for the request lifetime.
/// Actual SMTP I/O is covered by the SiteAdmin "send a test email" flow
/// against a real server.
/// </summary>
public sealed class EmailServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    /// <summary>
    /// The deployment SMTP fallback this fixture's service sees. Empty unless a
    /// test asks for one, and private to this instance: it used to be the
    /// process-wide SMTP_* variables, which every sibling fixture read and
    /// cleared out from under each other (#733).
    /// </summary>
    private SmtpFallbackOptions _smtpFallback = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task IsConfiguredAsync_returns_false_when_nothing_is_set()
    {
        // No DB row, no env vars: resolver returns null, IsConfiguredAsync is false.
        await using var ctx = _db.NewContext();
        ctx.SystemSettings.RemoveRange(await ctx.SystemSettings.ToListAsync());
        await ctx.SaveChangesAsync();

        var email = NewService();
        (await email.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_throws_when_smtp_unconfigured()
    {
        await using var ctx = _db.NewContext();
        ctx.SystemSettings.RemoveRange(await ctx.SystemSettings.ToListAsync());
        await ctx.SaveChangesAsync();

        var email = NewService();
        var act = () => email.SendAsync("user@example.com", new EmailContent("Subject", "<p>Body</p>"), EmailPurpose.PasswordReset);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task IsConfiguredAsync_returns_true_when_the_deployment_supplies_smtp()
    {
        _smtpFallback = new SmtpFallbackOptions { Host = "env.example.com", From = "env@example.com" };

        var email = NewService();
        (await email.IsConfiguredAsync()).Should().BeTrue();
    }

    private SmtpEmailService NewService()
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var settings = new SystemSettingsService(ctx, _db.DataProtectionProvider,
            NullLogger<SystemSettingsService>.Instance, TimeProvider.System,
            smtpFallback: _smtpFallback);
        return new SmtpEmailService(settings, NullLogger<SmtpEmailService>.Instance);
    }

}
