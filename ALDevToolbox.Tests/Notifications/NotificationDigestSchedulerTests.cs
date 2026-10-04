using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// The digest sweep (issue #1037). The organisations table has no tenant
/// filter, so the sweep lists them and then enters each one's
/// <see cref="AmbientOrganizationScope"/> before reading its digest items.
/// These tests pin that each digest is built inside its own organisation, a
/// pending organisation is skipped, and one organisation failing does not
/// cost the others their digest.
/// </summary>
public sealed class NotificationDigestSchedulerTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";
    private static readonly DateTime Wednesday0700 = new(2026, 10, 7, 7, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly CapturingEmailService _email = new();
    private ServiceProvider? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Each_organisations_digest_is_built_inside_its_own_organisation()
    {
        var alex = await SeedUserAsync("alex@cronus.example", TestDb.DefaultOrgId);
        var kim = await SeedUserAsync("kim@fabrikam.example", TestDb.OtherOrgId);
        await KeepAsync(alex, TestDb.DefaultOrgId, "Build failed: CRONUS Coffee");
        await KeepAsync(kim, TestDb.OtherOrgId, "Build failed: Fabrikam Bikes");

        (await NewScheduler().SweepAsync(CancellationToken.None)).Should().Be(2);

        var toAlex = _email.Sent.Should().ContainSingle(s => s.To == "alex@cronus.example").Subject;
        toAlex.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        toAlex.Html.Should().Contain("Build failed: CRONUS Coffee").And.NotContain("Fabrikam Bikes");
        var toKim = _email.Sent.Should().ContainSingle(s => s.To == "kim@fabrikam.example").Subject;
        toKim.OrganizationId.Should().Be(TestDb.OtherOrgId);
        toKim.Html.Should().Contain("Build failed: Fabrikam Bikes").And.NotContain("CRONUS Coffee");
    }

    [Fact]
    public async Task A_pending_organisation_is_skipped()
    {
        var pendingOrg = await SeedOrganizationAsync("Pending Ltd", isPending: true);
        var pat = await SeedUserAsync("pat@pending.example", pendingOrg);
        await KeepAsync(pat, pendingOrg, "Build failed: Pending");

        (await NewScheduler().SweepAsync(CancellationToken.None)).Should().Be(0);

        _email.Sent.Should().BeEmpty();
        _db.OrgContext.CurrentOrganizationId = pendingOrg;
        try
        {
            await using var ctx = _db.NewContext();
            (await ctx.NotificationDigestItems.CountAsync()).Should().Be(1, "nothing ran in the pending organisation");
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }

    [Fact]
    public async Task One_organisation_failing_does_not_stop_the_others()
    {
        var alex = await SeedUserAsync("alex@cronus.example", TestDb.DefaultOrgId);
        var kim = await SeedUserAsync("kim@fabrikam.example", TestDb.OtherOrgId);
        await KeepAsync(alex, TestDb.DefaultOrgId, "Build failed: CRONUS Coffee");
        await KeepAsync(kim, TestDb.OtherOrgId, "Build failed: Fabrikam Bikes");
        _email.FailInOrganization = TestDb.DefaultOrgId;

        (await NewScheduler().SweepAsync(CancellationToken.None)).Should().Be(1);

        _email.Sent.Select(s => s.To).Should().Equal("kim@fabrikam.example");
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.CountAsync()).Should().Be(1, "the failed organisation keeps its item for the next run");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// The scheduler over a service provider shaped like the app's. The
    /// organisation comes only from the ambient scope the sweep enters, as in
    /// production; nothing pins one up front.
    /// </summary>
    private NotificationDigestScheduler NewScheduler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrganizationContext>(new AmbientOnlyOrganizationContext());
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IEmailService>(_email);
        services.AddSingleton(sp => new EmailRenderer(sp, NullLoggerFactory.Instance));
        services.AddSingleton(new PublicOrigin(Origin));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(Wednesday0700)));
        services.AddScoped<NotificationDigestService>();
        _services = services.BuildServiceProvider();

        return new NotificationDigestScheduler(
            _services, NullLogger<NotificationDigestScheduler>.Instance, new WorkerHeartbeatRegistry());
    }

    private async Task<int> SeedOrganizationAsync(string name, bool isPending)
    {
        await using var ctx = _db.NewContext();
        var org = new Organization
        {
            Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), IsPending = isPending, CreatedAt = DateTime.UtcNow,
        };
        ctx.Organizations.Add(org);
        await ctx.SaveChangesAsync();
        return org.Id;
    }

    private async Task<int> SeedUserAsync(string email, int organizationId)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId, Email = email, DisplayName = "Alex Hansen", PasswordHash = "x",
            Role = UserRole.User, Status = UserStatus.Active, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task KeepAsync(int userId, int organizationId, string title)
    {
        await using var ctx = _db.NewContext();
        ctx.NotificationDigestItems.Add(new NotificationDigestItem
        {
            UserId = userId, OrganizationId = organizationId, Category = NotificationCategory.Builds,
            Delivery = NotificationDelivery.Daily, Title = title, Url = $"{Origin}/pipelines/1",
            SolutionName = "CRONUS Coffee", CreatedAt = Wednesday0700.AddHours(-5),
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class AmbientOnlyOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => AmbientOrganizationScope.Current?.OrganizationId;
        public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
        public int? CurrentUserId => AmbientOrganizationScope.Current?.UserId;
        public bool IsSiteAdmin => AmbientOrganizationScope.Current?.IsSiteAdmin ?? false;
        public bool IsSystemOrganization => AmbientOrganizationScope.Current?.IsSystemOrganization ?? false;
    }

    /// <summary>Records the ambient organisation each email was queued from.</summary>
    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Html, int? OrganizationId)> Sent { get; } = [];

        /// <summary>An organisation whose sweep throws before it sends anything.</summary>
        public int? FailInOrganization { get; set; }

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        {
            if (FailInOrganization is { } orgId && AmbientOrganizationScope.Current?.OrganizationId == orgId)
            {
                throw new InvalidOperationException("The outbox for this organisation is broken.");
            }
            return Task.FromResult(true);
        }

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            Sent.Add((toEmail, content.HtmlBody, AmbientOrganizationScope.Current?.OrganizationId));
            return Task.CompletedTask;
        }
    }
}
