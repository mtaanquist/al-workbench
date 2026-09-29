using ALDevToolbox.Components.Pages.Admin.Administration;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/admin/users</c>. Pins the three-state contract on the
/// two list sections that have an empty-state copy (invites + pending
/// signups) and the populated render for active users. The "Active &amp;
/// disabled" section deliberately has no empty-state today — a real admin
/// will always see at least themselves — so we don't pin one.
/// </summary>
public sealed class AdminUsersTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminUsersTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddSingleton(_db.OpenIddictTokens);
        _ctx.Services.AddScoped<UserAdministrationService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.Services.AddSingleton<IEmailService, StubEmailService>();
        _ctx.Services.AddSingleton(_db.DataProtectionProvider);
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(new Microsoft.AspNetCore.Http.HttpContextAccessor());
    }

    /// <summary>SMTP-not-configured stub; the admin page renders the "create a user" form regardless.</summary>
    private sealed class StubEmailService : IEmailService
    {
        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task SendAsync(
            string toEmail, string subject, string htmlBody, EmailPurpose purpose, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_org_renders_empty_state_copy_for_invites_and_pending_signups()
    {
        var cut = _ctx.Render<AdminAdministrationUsers>();

        // Asserted on the contract rather than the wording: each section owns
        // its own three-state render, so an empty org shows two empty states and
        // no phantom table. The copy is free to be reworded; the shape is not.
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".empty-state").Should().HaveCount(2,
                "pending invites and pending signups each have their own three-state "
                + "contract — admins should not see a phantom empty table for either");
            cut.Markup.Should().Contain("No pending invites");
            // The "Active & disabled" table below them has no empty state and
            // does not need one: whoever is reading this page is in it.
        });
    }

    [Fact]
    public async Task Active_users_section_renders_one_row_per_active_user()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "alice@example.com",
                DisplayName = "Alice",
                PasswordHash = "x",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "bob@example.com",
                DisplayName = "Bob",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = UserStatus.Disabled,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminAdministrationUsers>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("alice@example.com");
            cut.Markup.Should().Contain("bob@example.com");
            cut.Markup.Should().Contain("Active &amp; disabled (2)",
                "the section header counter must reflect the rendered rows");
        });
    }
}
