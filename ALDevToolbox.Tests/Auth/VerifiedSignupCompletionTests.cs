using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Behavioural tests for <see cref="AccountService.CompleteVerifiedSignupAsync"/>
/// — step 3 of the email-first flow. Covers the domain-match branches (gated by
/// the per-org <see cref="OrganizationSettings.AutoJoinVerifiedDomainUsers"/>
/// toggle), the new-org branch, slug disambiguation, validation field keys,
/// the single-use guard, and the race against an already-created account.
/// </summary>
public sealed class VerifiedSignupCompletionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private AccountService NewAccounts(Data.AppDbContext ctx, bool singleTenant = false) =>
        new(ctx,
            new AuthService(ctx, NullLogger<AuthService>.Instance, _clock),
            new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock),
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(singleTenant),
            NullLogger<AccountService>.Instance,
            _clock,
            _db.OpenIddictTokens);

    private async Task<int> SeedVerifiedPendingAsync(string email)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var ctx = _db.NewContext();
        var row = new PendingSignup
        {
            Email = email,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            CodeHash = "ignored",
            CreatedAt = now,
            ExpiresAt = now + PendingSignupService.Lifetime,
            VerifiedAt = now,
        };
        ctx.PendingSignups.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private async Task ClaimDomainAsync(int orgId, string domain)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationEmailDomains.Add(new OrganizationEmailDomain
        {
            OrganizationId = orgId,
            Domain = domain,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SetAutoJoinAsync(int orgId, bool enabled)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.OrganizationSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            ctx.OrganizationSettings.Add(row);
        }
        row.AutoJoinVerifiedDomainUsers = enabled;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await ctx.SaveChangesAsync();
    }

    private async Task<(SignupOutcome Outcome, User? User, Organization? Org)> CompleteAsync(
        int pendingId, string display, string password, string? orgName = null, string? slug = null)
    {
        var ctx = _db.NewContext();
        var row = await ctx.PendingSignups.IgnoreQueryFilters().SingleAsync(p => p.Id == pendingId);
        return await NewAccounts(ctx).CompleteVerifiedSignupAsync(row, display, password, orgName, slug);
    }

    [Fact]
    public async Task No_domain_match_creates_a_new_org_with_an_active_admin()
    {
        var id = await SeedVerifiedPendingAsync("founder@brandnew.example");
        var (outcome, user, org) = await CompleteAsync(id, "Founder", "verylongpassword12345", orgName: "Brand New");

        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
        user!.Status.Should().Be(UserStatus.Active);
        user.Role.Should().Be(UserRole.Admin);
        org!.IsPending.Should().BeFalse();
        org.Name.Should().Be("Brand New");

        await using var read = _db.NewContext();
        var request = await read.SignupRequests.IgnoreQueryFilters().SingleAsync(r => r.UserId == user.Id);
        request.Decision.Should().Be(SignupDecision.Approved);
        var pending = await read.PendingSignups.IgnoreQueryFilters().SingleAsync(p => p.Id == id);
        pending.CompletedAt.Should().NotBeNull("the verification row is burned on completion");
    }

    [Fact]
    public async Task Domain_match_lands_pending_when_auto_join_is_off()
    {
        await ClaimDomainAsync(TestDb.DefaultOrgId, "acme.com");

        var id = await SeedVerifiedPendingAsync("alice@acme.com");
        var (outcome, user, org) = await CompleteAsync(id, "Alice", "verylongpassword12345");

        outcome.Should().Be(SignupOutcome.PendingApproval);
        user!.Status.Should().Be(UserStatus.Pending);
        user.Role.Should().Be(UserRole.User);
        org!.Id.Should().Be(TestDb.DefaultOrgId);

        await using var read = _db.NewContext();
        var request = await read.SignupRequests.IgnoreQueryFilters().SingleAsync(r => r.UserId == user.Id);
        request.Decision.Should().Be(SignupDecision.Pending);
    }

    [Fact]
    public async Task Domain_match_joins_active_when_auto_join_is_on()
    {
        await ClaimDomainAsync(TestDb.DefaultOrgId, "acme.com");
        await SetAutoJoinAsync(TestDb.DefaultOrgId, true);

        var id = await SeedVerifiedPendingAsync("bob@acme.com");
        var (outcome, user, org) = await CompleteAsync(id, "Bob", "verylongpassword12345");

        outcome.Should().Be(SignupOutcome.JoinedActive);
        user!.Status.Should().Be(UserStatus.Active);
        user.Role.Should().Be(UserRole.User, "auto-joined domain users are regular members, not admins");
        org!.Id.Should().Be(TestDb.DefaultOrgId);

        await using var read = _db.NewContext();
        var request = await read.SignupRequests.IgnoreQueryFilters().SingleAsync(r => r.UserId == user.Id);
        request.Decision.Should().Be(SignupDecision.Approved);
    }

    [Fact]
    public async Task New_org_slug_collision_is_disambiguated()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization
            {
                Name = "Acme", Slug = "acme", IsPending = false,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var id = await SeedVerifiedPendingAsync("carol@fresh.example");
        var (_, _, org) = await CompleteAsync(id, "Carol", "verylongpassword12345", orgName: "Acme");
        org!.Slug.Should().Be("acme-2", "the slugifier disambiguates collisions with a numeric suffix");
    }

    [Fact]
    public async Task New_org_branch_requires_an_organisation_name()
    {
        var id = await SeedVerifiedPendingAsync("dan@fresh.example");
        Func<Task> act = () => CompleteAsync(id, "Dan", "verylongpassword12345", orgName: null);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("OrganizationName");
    }

    [Fact]
    public async Task Weak_password_and_short_display_name_surface_field_keys()
    {
        var id = await SeedVerifiedPendingAsync("ed@fresh.example");

        var weak = await ((Func<Task>)(() => CompleteAsync(id, "Ed", "short", orgName: "Ed Co")))
            .Should().ThrowAsync<PlanValidationException>();
        weak.Which.Errors.Should().ContainKey("Password");

        var shortName = await ((Func<Task>)(() => CompleteAsync(id, "E", "verylongpassword12345", orgName: "Ed Co")))
            .Should().ThrowAsync<PlanValidationException>();
        shortName.Which.Errors.Should().ContainKey("DisplayName");
    }

    [Fact]
    public async Task A_double_submit_creates_one_account_and_one_organisation()
    {
        // Both posts passed the endpoint's FindVerifiedAsync before either
        // completed, as a double-clicked Create does (#1215).
        var id = await SeedVerifiedPendingAsync("double@fresh.example");

        var results = await Task.WhenAll(
            CompleteAsync(id, "Double", "verylongpassword12345", orgName: "Double Co"),
            CompleteAsync(id, "Double", "verylongpassword12345", orgName: "Double Co"));

        results.Select(r => r.Outcome).Should().BeEquivalentTo(
            [SignupOutcome.OrganizationProvisioned, SignupOutcome.EmailAlreadyTaken]);
        await using var read = _db.NewContext();
        (await read.Organizations.CountAsync(o => o.Name == "Double Co"))
            .Should().Be(1, "the losing submit must not leave a second organisation behind");
        (await read.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "double@fresh.example"))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_refused_completion_leaves_the_verification_usable()
    {
        // The row is claimed before the organisation name is checked; the
        // refusal must roll that claim back so the visitor can fix the field.
        var id = await SeedVerifiedPendingAsync("retry@fresh.example");

        await ((Func<Task>)(() => CompleteAsync(id, "Retry", "verylongpassword12345", orgName: "")))
            .Should().ThrowAsync<PlanValidationException>();
        var (outcome, _, _) = await CompleteAsync(id, "Retry", "verylongpassword12345", orgName: "Retry Co");

        outcome.Should().Be(SignupOutcome.OrganizationProvisioned);
    }

    [Fact]
    public async Task Refuses_when_a_user_for_the_email_already_exists()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "racer@fresh.example",
                PasswordHash = "x",
                DisplayName = "Racer",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var id = await SeedVerifiedPendingAsync("racer@fresh.example");
        var (outcome, user, _) = await CompleteAsync(id, "Racer Two", "verylongpassword12345", orgName: "Racer Co");

        outcome.Should().Be(SignupOutcome.EmailAlreadyTaken);
        user.Should().BeNull();
        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "racer@fresh.example"))
            .Should().Be(1, "no duplicate account is created");
    }
}
