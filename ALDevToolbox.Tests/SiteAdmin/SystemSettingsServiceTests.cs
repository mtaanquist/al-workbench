using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for <see cref="SystemSettingsService"/>: the singleton
/// row is auto-created if missing, the SMTP password is encrypted at rest
/// and round-trips through Data Protection, and the hybrid SMTP resolver
/// prefers DB values over env vars.
/// </summary>
public sealed class SystemSettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Get_view_auto_creates_singleton_row_when_missing()
    {
        // The migration inserts the singleton row, but TestDb-backed tests
        // that bypass it (or future databases that don't seed it) shouldn't
        // NRE. LoadAsync inserts on demand.
        await using (var ctx = _db.NewContext())
        {
            ctx.SystemSettings.RemoveRange(await ctx.SystemSettings.ToListAsync());
            await ctx.SaveChangesAsync();
        }

        var svc = NewService();
        var view = await svc.GetViewAsync();
        view.SmtpHost.Should().BeNull();
        view.HasSmtpPassword.Should().BeFalse();
    }

    [Fact]
    public async Task Build_concurrency_saves_and_applies_to_the_queue_at_once()
    {
        // #1164: the SiteAdmin resizes the server and sets the limit without a restart.
        var queue = new ProjectBuildQueue(defaultLimit: 2);

        await NewService(builds: queue).SaveAsync(NewInput(buildConcurrency: 5));

        (await NewService().GetViewAsync()).BuildConcurrency.Should().Be(5);
        queue.Limit.Should().Be(5);
    }

    [Fact]
    public async Task Clearing_build_concurrency_goes_back_to_the_default()
    {
        var queue = new ProjectBuildQueue(defaultLimit: 3);
        await NewService(builds: queue).SaveAsync(NewInput(buildConcurrency: 8));

        await NewService(builds: queue).SaveAsync(NewInput(buildConcurrency: null));

        (await NewService().GetViewAsync()).BuildConcurrency.Should().BeNull();
        queue.Limit.Should().Be(3);
    }

    [Theory]
    [InlineData(ProjectBuildQueue.MinConcurrency - 1)]
    [InlineData(-1)]
    [InlineData(ProjectBuildQueue.MaxConcurrency + 1)]
    public async Task Build_concurrency_outside_the_queues_bounds_is_refused_on_its_field(int value)
    {
        var queue = new ProjectBuildQueue(defaultLimit: 2);
        var svc = NewService(builds: queue);

        var act = () => svc.SaveAsync(NewInput(buildConcurrency: value));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["BuildConcurrency"].Should().Be(
                $"Enter a number from {ProjectBuildQueue.MinConcurrency} to {ProjectBuildQueue.MaxConcurrency}, or leave it empty to use the default.",
                "the message names the queue's own bounds, the ones the field enforces");
        (await NewService().GetViewAsync()).BuildConcurrency.Should().BeNull();
        queue.Limit.Should().Be(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public async Task Build_concurrency_accepts_the_ends_of_the_range(int value)
    {
        await NewService().SaveAsync(NewInput(buildConcurrency: value));

        (await NewService().GetViewAsync()).BuildConcurrency.Should().Be(value);
    }

    [Fact]
    public async Task Save_encrypts_smtp_password_at_rest()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(password: "supers3cret"));

        await using var read = _db.NewContext();
        var row = await read.SystemSettings.AsNoTracking().FirstAsync(s => s.Id == 1);
        row.SmtpPasswordEncrypted.Should().NotBeNullOrEmpty();
        row.SmtpPasswordEncrypted.Should().NotContain("supers3cret",
            "the password must be ciphertext, not plaintext");
    }

    [Fact]
    public async Task Save_with_blank_password_leaves_existing_value()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(password: "first-password"));

        // Blank password field on the second save shouldn't disturb the
        // existing ciphertext — that's what the form posts when the
        // SiteAdmin doesn't re-type the password.
        await svc.SaveAsync(NewInput(password: "", host: "different.example.com"));

        var resolved = await svc.ResolveSmtpAsync();
        resolved!.Password.Should().Be("first-password");
        resolved.Host.Should().Be("different.example.com");
    }

    [Fact]
    public async Task Save_with_clear_flag_drops_stored_value()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(password: "to-be-cleared"));
        await svc.SaveAsync(NewInput(password: "", clear: true));

        var view = await svc.GetViewAsync();
        view.HasSmtpPassword.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_prefers_db_settings_over_the_deployment_fallback()
    {
        var fallback = new SmtpFallbackOptions { Host = "env.example.com", From = "env@example.com" };
        var svc = NewService(fallback);
        await svc.SaveAsync(NewInput(host: "db.example.com", password: "db-password"));

        var resolved = await svc.ResolveSmtpAsync();
        resolved!.Host.Should().Be("db.example.com");
        resolved.Password.Should().Be("db-password");
    }

    [Fact]
    public async Task Resolve_falls_back_to_the_deployment_settings_when_db_unset()
    {
        // Empty DB row: host left null, so the deployment's own settings win.
        var svc = NewService(new SmtpFallbackOptions
        {
            Host = "env.example.com",
            From = "env@example.com",
            Port = 2525,
        });
        // GetView creates the singleton row but with no host/from — the
        // resolver should still see the row as "unset" and fall back.
        await svc.GetViewAsync();

        var resolved = await svc.ResolveSmtpAsync();
        resolved.Should().NotBeNull();
        resolved!.Host.Should().Be("env.example.com");
        resolved.Port.Should().Be(2525);
    }

    [Fact]
    public async Task Save_round_trips_sender_name_through_view_and_resolve()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput(fromName: "AL Workbench"));

        var view = await svc.GetViewAsync();
        view.SmtpFromName.Should().Be("AL Workbench");

        var resolved = await svc.ResolveSmtpAsync();
        resolved!.FromName.Should().Be("AL Workbench",
            "the resolver carries the sender name through so the email sender "
            + "can pair it with the from address");
    }

    [Fact]
    public async Task Save_validates_smtp_port_range()
    {
        var svc = NewService();
        Func<Task> bad = () => svc.SaveAsync(NewInput(port: 0));
        var ex = await bad.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SmtpPort");
    }

    [Fact]
    public async Task Save_validates_from_address()
    {
        var svc = NewService();
        Func<Task> bad = () => svc.SaveAsync(NewInput(from: "not-an-email"));
        var ex = await bad.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SmtpFrom");
    }

    [Fact]
    public async Task Save_validates_backup_retention_range()
    {
        var svc = NewService();
        Func<Task> bad = () => svc.SaveAsync(NewInput() with { BackupRetentionCount = 0 });
        var ex = await bad.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("BackupRetentionCount");
    }

    [Fact]
    public async Task Save_persists_backup_schedule_fields()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput() with
        {
            BackupScheduleEnabled = false,
            BackupScheduleTimeUtc = new TimeOnly(3, 15),
            BackupRetentionCount = 7,
        });

        var view = await svc.GetViewAsync();
        view.BackupScheduleEnabled.Should().BeFalse();
        view.BackupScheduleTimeUtc.Should().Be(new TimeOnly(3, 15));
        view.BackupRetentionCount.Should().Be(7);
    }

    [Fact]
    public async Task Allowlist_normalises_and_persists_lowercased_domains()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput() with
        {
            SignupEmailDomainAllowlist = "Acme.com\nexample.dk\n  acme.com  ",
        });

        var stored = await svc.GetSignupAllowedDomainsAsync();
        stored.Should().NotBeNull().And.BeEquivalentTo(new[] { "acme.com", "example.dk" });

        var view = await svc.GetViewAsync();
        view.SignupEmailDomainAllowlist.Should().Be("acme.com\nexample.dk");
    }

    [Fact]
    public async Task Allowlist_rejects_invalid_entries_with_field_keyed_error()
    {
        var svc = NewService();
        var act = async () => await svc.SaveAsync(NewInput() with
        {
            SignupEmailDomainAllowlist = "not a domain",
        });
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SignupEmailDomainAllowlist");
    }

    [Fact]
    public async Task Allowlist_blank_means_feature_off()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput() with { SignupEmailDomainAllowlist = "   " });
        (await svc.GetSignupAllowedDomainsAsync()).Should().BeNull();
        (await svc.GetViewAsync()).SignupEmailDomainAllowlist.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseDownloadAllowlist_normalises_and_persists_lowercased_hosts()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput() with
        {
            ReleaseDownloadDomainAllowlist = "Download.Microsoft.com\nexample.dk\n  download.microsoft.com  ",
        });

        var stored = await svc.GetReleaseDownloadAllowedHostsAsync();
        stored.Should().NotBeNull().And.BeEquivalentTo(new[] { "download.microsoft.com", "example.dk" });

        (await svc.GetViewAsync()).ReleaseDownloadDomainAllowlist
            .Should().Be("download.microsoft.com\nexample.dk");
    }

    [Fact]
    public async Task ReleaseDownloadAllowlist_rejects_invalid_entries_with_field_keyed_error()
    {
        var svc = NewService();
        var act = async () => await svc.SaveAsync(NewInput() with
        {
            ReleaseDownloadDomainAllowlist = "not a host",
        });
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("ReleaseDownloadDomainAllowlist");
    }

    [Fact]
    public async Task ReleaseDownloadAllowlist_blank_means_no_host_permitted()
    {
        var svc = NewService();
        await svc.SaveAsync(NewInput() with { ReleaseDownloadDomainAllowlist = "   " });
        (await svc.GetReleaseDownloadAllowedHostsAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("download.microsoft.com", true)]   // exact
    [InlineData("DOWNLOAD.MICROSOFT.COM", true)]   // case-insensitive
    [InlineData("cdn.download.microsoft.com", true)] // subdomain of an entry
    [InlineData("microsoft.com.evil.com", false)]  // not a subdomain
    [InlineData("notmicrosoft.com", false)]
    public void IsHostAllowed_does_suffix_match(string host, bool expected)
    {
        var allowlist = new[] { "download.microsoft.com", "microsoft.com" };
        SystemSettingsService.IsHostAllowed(host, allowlist).Should().Be(expected);
    }

    [Fact]
    public void IsHostAllowed_empty_or_null_list_permits_nothing()
    {
        SystemSettingsService.IsHostAllowed("download.microsoft.com", null).Should().BeFalse();
        SystemSettingsService.IsHostAllowed("download.microsoft.com", System.Array.Empty<string>()).Should().BeFalse();
    }

    private SystemSettingsService NewService(SmtpFallbackOptions? smtpFallback = null, ProjectBuildQueue? builds = null)
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        return new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System,
            smtpFallback: smtpFallback, buildQueue: builds);
    }

    private static SystemSettingsInput NewInput(
        string? password = "p",
        string? host = "smtp.example.com",
        int? port = 587,
        string? from = "noreply@example.com",
        string? fromName = null,
        bool clear = false,
        int? buildConcurrency = null)
        => new(
            SmtpHost: host,
            SmtpPort: port,
            SmtpUser: "user",
            SmtpPassword: password,
            ClearSmtpPassword: clear,
            SmtpFrom: from,
            SmtpFromName: fromName,
            SmtpUseStartTls: true,
            BannerText: null,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: false,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: null, DisabledTools: System.Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: buildConcurrency);
}
