using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Tests for the Entra sign-in resolution (issue #552, slice 2): the tenant
/// allow-list boundary, email-domain org routing, link-on-first-login, JIT
/// provisioning through the signup approval machinery, and the strong-auth
/// interaction. The OIDC handshake itself is not unit-testable — it needs
/// the manual live-tenant smoke test described in the issue.
/// </summary>
public sealed class EntraSignInServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Ip = "203.0.113.10";

    private EntraSignInService NewService(AppDbContext ctx) =>
        new(ctx,
            new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System),
            _db.OrgContext, _db.DataProtectionProvider, TimeProvider.System,
            NullLogger<EntraSignInService>.Instance);

    private async Task SeedOrgEntraAsync(int orgId, string tenantId, bool autoJoin = false, string? domain = null)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.OrganizationSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            ctx.OrganizationSettings.Add(row);
        }
        row.EntraEnabled = true;
        row.EntraAllowedTenantIds = new List<string> { tenantId };
        row.AutoJoinVerifiedDomainUsers = autoJoin;
        row.UpdatedAt = DateTime.UtcNow;
        if (domain is not null)
        {
            ctx.OrganizationEmailDomains.Add(new OrganizationEmailDomain
            {
                OrganizationId = orgId,
                Domain = domain,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedUserAsync(int orgId, string email, UserStatus status = UserStatus.Active)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            DisplayName = "Existing User",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Complete_refuses_a_tenant_no_org_allows_and_records_the_attempt()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantB, Oid, "user@cronus.com", "User"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.TenantNotAllowed);
        (await ctx.LoginAttempts.AnyAsync(a => a.Email == "user@cronus.com" && !a.Succeeded))
            .Should().BeTrue("refused federated attempts must land in login_attempts");
        (await ctx.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "user@cronus.com"))
            .Should().BeFalse("no account may be provisioned for a disallowed tenant");
    }

    [Fact]
    public async Task Complete_matches_an_existing_user_by_email_and_creates_the_link()
    {
        // Microsoft confirms the email (xms_edov), which is what licenses the bind.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "Mette@CRONUS.com", "Mette", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success);
        result.User!.Id.Should().Be(userId);
        result.User.Organization.Should().NotBeNull("BuildIdentity needs the org nav");
        var link = await ctx.UserExternalLogins.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        link.UserId.Should().Be(userId);
        link.Issuer.Should().Be(TenantA);
        link.Subject.Should().Be(Oid);
        link.LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Complete_signs_in_via_an_existing_link_without_touching_email()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using (var seed = _db.NewContext())
        {
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = userId, Provider = "entra", Issuer = TenantA, Subject = Oid,
                DisplayIdentity = "mette@cronus.com", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        // Email/UPN changed at the IdP — the stable object id still matches.
        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "renamed@cronus.com", "Mette"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success);
        result.User!.Id.Should().Be(userId);
        (await ctx.UserExternalLogins.IgnoreQueryFilters().CountAsync()).Should().Be(1, "no duplicate link");
    }

    [Fact]
    public async Task Complete_refuses_a_link_whose_org_removed_the_tenant()
    {
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using (var seed = _db.NewContext())
        {
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = userId, Provider = "entra", Issuer = TenantA, Subject = Oid,
                DisplayIdentity = "mette@cronus.com", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        // Org never enabled Entra (or removed the tenant) — the link alone
        // must not grant access.
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "mette@cronus.com", "Mette"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.TenantNotAllowed);
    }

    [Fact]
    public async Task Complete_routes_a_shared_tenant_by_claimed_email_domain()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await SeedOrgEntraAsync(TestDb.OtherOrgId, TenantA, autoJoin: true, domain: "other.example");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "jonas@other.example", "Jonas", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success, "auto-join org claimed the domain");
        result.User!.OrganizationId.Should().Be(TestDb.OtherOrgId);
    }

    [Fact]
    public async Task Complete_routes_an_unverified_email_by_domain_but_leaves_it_pending()
    {
        // Routing on the claimed domain is fine without xms_edov; joining is not.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await SeedOrgEntraAsync(TestDb.OtherOrgId, TenantA, autoJoin: true, domain: "other.example");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "jonas@other.example", "Jonas"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.PendingApproval);
        var user = await ctx.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Email == "jonas@other.example");
        user.OrganizationId.Should().Be(TestDb.OtherOrgId);
        user.Status.Should().Be(UserStatus.Pending);
    }

    [Fact]
    public async Task Complete_refuses_a_shared_tenant_when_the_domain_does_not_decide()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await SeedOrgEntraAsync(TestDb.OtherOrgId, TenantA);
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "user@unclaimed.example", "User"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Ambiguous);
        (await ctx.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "user@unclaimed.example"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Complete_jit_provisions_a_pending_user_with_a_signup_request()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "new@cronus.com", "New Person"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.PendingApproval);
        var user = await ctx.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Email == "new@cronus.com");
        user.Status.Should().Be(UserStatus.Pending);
        user.Role.Should().Be(UserRole.User);
        user.PasswordHash.Should().NotBeNullOrEmpty("the column is NOT NULL; a random hash that can never verify");
        (await ctx.SignupRequests.IgnoreQueryFilters()
            .AnyAsync(r => r.UserId == user.Id && r.Decision == SignupDecision.Pending))
            .Should().BeTrue("approval runs through the existing machinery");
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AnyAsync(l => l.UserId == user.Id))
            .Should().BeTrue("the link is stamped at provisioning so approval needs no re-match");
    }

    [Fact]
    public async Task Complete_jit_auto_joins_when_the_domain_is_claimed_the_org_opted_in_and_the_email_is_verified()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, autoJoin: true, domain: "cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "new@cronus.com", "New Person", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success);
        result.User!.Status.Should().Be(UserStatus.Active);
        result.User.Organization.Should().NotBeNull();
        (await ctx.SignupRequests.IgnoreQueryFilters()
            .AnyAsync(r => r.UserId == result.User.Id && r.Decision == SignupDecision.Approved))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Complete_jit_does_not_auto_join_on_an_unverified_email_even_on_a_claimed_domain()
    {
        // Issue #1323: an admin in an allow-listed tenant can set the email
        // claim to anything, so without xms_edov the claimed domain proves
        // nothing and the account waits for an Admin.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, autoJoin: true, domain: "cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "new@cronus.com", "New Person"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.PendingApproval);
        var user = await ctx.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Email == "new@cronus.com");
        user.Status.Should().Be(UserStatus.Pending);
        user.LastLoginAt.Should().BeNull();
        (await ctx.SignupRequests.IgnoreQueryFilters()
            .AnyAsync(r => r.UserId == user.Id && r.Decision == SignupDecision.Pending))
            .Should().BeTrue("an Admin still has to approve it");
    }

    [Fact]
    public async Task Complete_does_not_auto_join_on_an_unclaimed_domain_even_with_the_toggle_on()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, autoJoin: true);
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "new@elsewhere.example", "New Person", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.PendingApproval,
            "auto-join requires ownership of a claimed domain, mirroring the verified signup flow");
    }

    private const string OidB = "bbbbbbbb-0000-0000-0000-000000000001";

    private async Task SetUserStateAsync(string email, UserStatus status, DateTime? lastLoginAt = null)
    {
        await using var ctx = _db.NewContext();
        var user = await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == email);
        user.Status = status;
        user.LastLoginAt = lastLoginAt;
        await ctx.SaveChangesAsync();
    }

    private async Task SeedLinkAsync(int userId, string tid, string oid)
    {
        await using var ctx = _db.NewContext();
        ctx.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = userId,
            Provider = EntraSignInService.ProviderName,
            Issuer = tid,
            Subject = oid,
            DisplayIdentity = "seeded",
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<List<string>> LinkedSubjectsAsync(string email)
    {
        await using var ctx = _db.NewContext();
        return await ctx.UserExternalLogins.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.User!.Email == email)
            .Select(l => l.Subject)
            .ToListAsync();
    }

    private async Task<EntraCompletionOutcome> SignInAsync(EntraTokenIdentity token)
    {
        await using var ctx = _db.NewContext();
        return (await NewService(ctx).CompleteAsync(token, Ip)).Outcome;
    }

    [Fact]
    public async Task Complete_verified_bind_replaces_an_unverified_link_on_a_never_used_pending_account()
    {
        // Issue #1323: A claims new@cronus.com without proof and gets a Pending
        // account; B, who proves the address, must end up the only way in.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, autoJoin: true, domain: "cronus.com");
        var claimant = new EntraTokenIdentity(TenantA, Oid, "new@cronus.com", "Claimant");
        var owner = new EntraTokenIdentity(TenantA, OidB, "new@cronus.com", "Owner", EmailVerified: true);

        (await SignInAsync(claimant)).Should().Be(EntraCompletionOutcome.PendingApproval);
        (await SignInAsync(owner)).Should().Be(EntraCompletionOutcome.AccountPending);
        (await LinkedSubjectsAsync("new@cronus.com")).Should().BeEquivalentTo(new[] { OidB });

        await SetUserStateAsync("new@cronus.com", UserStatus.Active);

        (await SignInAsync(claimant)).Should().NotBe(EntraCompletionOutcome.Success);
        (await SignInAsync(owner)).Should().Be(EntraCompletionOutcome.Success);
    }

    [Fact]
    public async Task Complete_verified_bind_keeps_other_links_on_an_active_account()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await SeedLinkAsync(userId, TenantB, Oid);

        (await SignInAsync(new EntraTokenIdentity(TenantA, OidB, "mette@cronus.com", "Mette", EmailVerified: true)))
            .Should().Be(EntraCompletionOutcome.Success);

        (await LinkedSubjectsAsync("mette@cronus.com")).Should().BeEquivalentTo(new[] { Oid, OidB });
    }

    [Fact]
    public async Task Complete_verified_bind_keeps_other_links_on_a_pending_account_that_has_signed_in()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com", UserStatus.Pending);
        await SetUserStateAsync("mette@cronus.com", UserStatus.Pending, DateTime.UtcNow.AddDays(-1));
        await SeedLinkAsync(userId, TenantB, Oid);

        (await SignInAsync(new EntraTokenIdentity(TenantA, OidB, "mette@cronus.com", "Mette", EmailVerified: true)))
            .Should().Be(EntraCompletionOutcome.AccountPending);

        (await LinkedSubjectsAsync("mette@cronus.com")).Should().BeEquivalentTo(new[] { Oid, OidB });
    }

    [Fact]
    public async Task Complete_refuses_a_returning_identity_whose_account_is_still_pending()
    {
        // The link stored at unverified provisioning must not open the account
        // before an Admin approves it.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, autoJoin: true, domain: "cronus.com");
        var token = new EntraTokenIdentity(TenantA, Oid, "new@cronus.com", "New Person");
        await using (var first = _db.NewContext())
        {
            (await NewService(first).CompleteAsync(token, Ip)).Outcome
                .Should().Be(EntraCompletionOutcome.PendingApproval);
        }
        await using var ctx = _db.NewContext();

        var again = await NewService(ctx).CompleteAsync(token, Ip);

        again.Outcome.Should().Be(EntraCompletionOutcome.AccountPending);
    }

    [Fact]
    public async Task Complete_refuses_disabled_and_pending_accounts()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        await SeedUserAsync(TestDb.DefaultOrgId, "disabled@cronus.com", UserStatus.Disabled);
        await SeedUserAsync(TestDb.DefaultOrgId, "pending@cronus.com", UserStatus.Pending);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        (await svc.CompleteAsync(new EntraTokenIdentity(TenantA, Oid, "disabled@cronus.com", null, EmailVerified: true), Ip))
            .Outcome.Should().Be(EntraCompletionOutcome.AccountDisabled);
        (await svc.CompleteAsync(new EntraTokenIdentity(TenantA, "aaaaaaaa-0000-0000-0000-000000000002", "pending@cronus.com", null, EmailVerified: true), Ip))
            .Outcome.Should().Be(EntraCompletionOutcome.AccountPending);
    }

    [Fact]
    public async Task Complete_refuses_to_bind_an_existing_account_on_an_unverified_email()
    {
        // Tenant is allow-listed, but nothing proves it owns cronus.com: no
        // xms_edov and the org has claimed no domain. Issue #674.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "admin@cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "admin@cronus.com", "Not Really Mette"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.EmailNotVerified);
        result.User.Should().BeNull();
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AnyAsync(l => l.UserId == userId))
            .Should().BeFalse("an unverified email claim must never create a link");
        (await ctx.LoginAttempts.AnyAsync(a => a.Email == "admin@cronus.com" && !a.Succeeded))
            .Should().BeTrue("the refusal is auditable");
    }

    [Fact]
    public async Task Complete_refuses_to_bind_a_site_admin_by_email_even_with_verified_ownership()
    {
        // Both kinds of ownership evidence present: the org verified the domain
        // and the token carries xms_edov. A SiteAdmin still has to connect the
        // Microsoft account from their own signed-in session.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com");
        await using (var seed = _db.NewContext())
        {
            (await seed.Users.SingleAsync(u => u.Id == userId)).IsSiteAdmin = true;
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "operator@cronus.com", "Operator", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.LinkFromAccountRequired);
        result.User.Should().BeNull();
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AnyAsync(l => l.UserId == userId))
            .Should().BeFalse("no link may be created by an email match on a SiteAdmin");
    }

    [Fact]
    public async Task Complete_signs_in_a_site_admin_through_a_link_they_made_themselves()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "operator@cronus.com");
        await using (var seed = _db.NewContext())
        {
            (await seed.Users.SingleAsync(u => u.Id == userId)).IsSiteAdmin = true;
            await seed.SaveChangesAsync();
        }
        var token = new EntraTokenIdentity(TenantA, Oid, "operator@cronus.com", "Operator", EmailVerified: true);
        await using (var linkCtx = _db.NewContext())
        {
            await NewService(linkCtx).LinkAsync(userId, token);
        }
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(token, Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success);
        result.User!.Id.Should().Be(userId);
    }

    [Fact]
    public async Task Complete_binds_an_existing_account_when_the_token_carries_xms_edov()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "mette@cronus.com", "Mette", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success);
        result.User!.Id.Should().Be(userId);
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AnyAsync(l => l.UserId == userId)).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_refuses_to_bind_an_existing_account_when_only_the_org_claimed_the_domain()
    {
        // A claimed domain is not proof the tenant owns the address (#1211).
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "mette@cronus.com", "Mette"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.EmailNotVerified);
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AnyAsync(l => l.UserId == userId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Complete_still_signs_in_an_existing_link_on_an_unverified_email()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using (var seed = _db.NewContext())
        {
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = userId, Provider = "entra", Issuer = TenantA, Subject = Oid,
                DisplayIdentity = "mette@cronus.com", CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "mette@cronus.com", "Mette"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success, "the oid link predates the rule and is unaffected");
        result.User!.Id.Should().Be(userId);
    }

    [Fact]
    public async Task Complete_jit_provisions_when_the_email_is_unverified_and_no_account_exists()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "guest@partner.example", "Guest"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.PendingApproval,
            "with no account to take over, the approval path still applies");
    }

    [Fact]
    public async Task Complete_refuses_an_email_registered_in_another_org()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await SeedUserAsync(TestDb.OtherOrgId, "taken@cronus.com");
        await using var ctx = _db.NewContext();

        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "taken@cronus.com", null), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.EmailTakenElsewhere);
    }

    [Fact]
    public async Task ResolveChallenge_routes_by_claimed_domain_and_falls_back_to_the_deployment_app()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        await using (var seed = _db.NewContext())
        {
            var sys = await seed.SystemSettings.FirstAsync(s => s.Id == 1);
            sys.EntraClientId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        var (config, error) = await NewService(ctx).ResolveChallengeAsync("mette@cronus.com");

        error.Should().BeNull();
        config!.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        config.ClientId.Should().Be("dddddddd-dddd-dddd-dddd-dddddddddddd");
        config.ConfigSource.Should().Be("system");
    }

    [Fact]
    public async Task ResolveChallenge_prefers_the_orgs_own_registration()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        await using (var seed = _db.NewContext())
        {
            var row = await seed.OrganizationSettings.IgnoreQueryFilters()
                .FirstAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
            row.EntraClientId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        var (config, _) = await NewService(ctx).ResolveChallengeAsync("mette@cronus.com");

        config!.ClientId.Should().Be("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        config.ConfigSource.Should().Be("org");
    }

    [Fact]
    public async Task ResolveChallenge_without_email_works_only_when_exactly_one_org_is_enabled()
    {
        await using (var seed = _db.NewContext())
        {
            var sys = await seed.SystemSettings.FirstAsync(s => s.Id == 1);
            sys.EntraClientId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
            await seed.SaveChangesAsync();
        }
        await using (var ctx = _db.NewContext())
        {
            var (config, error) = await NewService(ctx).ResolveChallengeAsync(null);
            config.Should().BeNull();
            error.Should().Be("entra-not-configured", "no org has Entra on yet");
        }

        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        await using (var ctx = _db.NewContext())
        {
            var (config, error) = await NewService(ctx).ResolveChallengeAsync(null);
            error.Should().BeNull();
            config!.OrganizationId.Should().Be(TestDb.DefaultOrgId, "the single-tenant shape needs no email");
        }

        await SeedOrgEntraAsync(TestDb.OtherOrgId, TenantB);
        await using (var ctx = _db.NewContext())
        {
            var (config, error) = await NewService(ctx).ResolveChallengeAsync(null);
            config.Should().BeNull();
            error.Should().Be("entra-email-needed", "two enabled orgs need the email to route");
        }
    }

    [Fact]
    public async Task A_linked_microsoft_account_counts_as_strong_auth()
    {
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await using var ctx = _db.NewContext();
        var auth = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System);

        (await auth.HasStrongAuthAsync(userId)).Should().BeFalse();

        ctx.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = userId, Provider = "entra", Issuer = TenantA, Subject = Oid,
            DisplayIdentity = "mette@cronus.com", CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        (await auth.HasStrongAuthAsync(userId)).Should().BeTrue(
            "MFA for a federated account is the Entra tenant's job; RequireStrongAuth must not trap it");
    }

    // ---- Concurrent-link races (issue #754) ----
    //
    // CompleteAsync looks the identity up, finds nothing, and inserts a link.
    // Another request can claim the same (provider, issuer, subject) in between:
    // two sign-ins for the same person in different tabs, or LinkAsync finishing
    // in another organisation. The insert then violates the unique index. Before
    // #754 nothing caught it, so the endpoint had no outcome to render and the
    // visitor got the error page - having already spent the handshake's one-shot
    // state, so they could not simply retry.

    /// <summary>
    /// Claims the Microsoft identity under test through a second context while
    /// the context under test is saving, which is the only way to land between
    /// <c>CompleteAsync</c>'s lookup and its insert. Mirrors the interceptor
    /// <c>EntraLoginPolicyTests</c> uses for the <c>LinkAsync</c> half (#736).
    /// </summary>
    private sealed class ClaimIdentityDuringSaveInterceptor : SaveChangesInterceptor
    {
        private readonly TestDb _db;
        private readonly int _winnerUserId;
        private bool _claimed;

        public ClaimIdentityDuringSaveInterceptor(TestDb db, int winnerUserId)
        {
            _db = db;
            _winnerUserId = winnerUserId;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_claimed)
            {
                _claimed = true;
                await using var other = _db.NewContext();
                other.UserExternalLogins.Add(new UserExternalLogin
                {
                    UserId = _winnerUserId,
                    Provider = EntraSignInService.ProviderName,
                    Issuer = TenantA,
                    Subject = Oid,
                    DisplayIdentity = "winner@cronus.com",
                    CreatedAt = DateTime.UtcNow,
                });
                await other.SaveChangesAsync(cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Complete_refuses_when_another_account_claims_the_identity_mid_link()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        var strangerId = await SeedUserAsync(TestDb.DefaultOrgId, "stranger@cronus.com");

        await using var ctx = _db.NewContext(new ClaimIdentityDuringSaveInterceptor(_db, strangerId));
        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "Mette@CRONUS.com", "Mette", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.IdentityTakenElsewhere,
            "the identity belongs to the account that won the race, and this sign-in must refuse rather than throw");
        var link = await ctx.UserExternalLogins.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        link.UserId.Should().Be(strangerId, "the winner keeps the identity");
    }

    [Fact]
    public async Task Complete_carries_on_when_the_same_account_won_the_link_race()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA, domain: "cronus.com");
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");

        // The winner is the same user: what a double-submitted callback looks
        // like. Adopting that link is better than refusing a sign-in that is,
        // in substance, the one the visitor asked for.
        await using var ctx = _db.NewContext(new ClaimIdentityDuringSaveInterceptor(_db, userId));
        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "Mette@CRONUS.com", "Mette", EmailVerified: true), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.Success);
        result.User!.Id.Should().Be(userId);
        (await ctx.UserExternalLogins.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.Issuer == TenantA && l.Subject == Oid))
            .Should().Be(1, "the losing insert must not survive alongside the winner");
    }

    [Fact]
    public async Task A_lost_jit_race_leaves_no_user_behind()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var strangerId = await SeedUserAsync(TestDb.DefaultOrgId, "stranger@cronus.com");

        await using var ctx = _db.NewContext(new ClaimIdentityDuringSaveInterceptor(_db, strangerId));
        var result = await NewService(ctx).CompleteAsync(
            new EntraTokenIdentity(TenantA, Oid, "new@cronus.com", "New Person"), Ip);

        result.Outcome.Should().Be(EntraCompletionOutcome.IdentityTakenElsewhere);
        // The whole point of folding the two saves into one: a half-provisioned
        // account with no signup request and no link used to survive here, and
        // the next sign-in would then treat it as step 3's "existing account".
        (await ctx.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u => u.Email == "new@cronus.com"))
            .Should().BeFalse("nothing may be committed when the link loses");
        (await ctx.SignupRequests.IgnoreQueryFilters().AsNoTracking().AnyAsync(r => r.Email == "new@cronus.com"))
            .Should().BeFalse();
    }

    // IsLinkedAsync is the identity check behind Microsoft step-up: the forced
    // re-sign-in must come back as an identity linked to the step-up user, in
    // a tenant their organisation still allows (#1346).

    private async Task<bool> IsLinkedAsync(int userId, string tid, string oid)
    {
        await using var ctx = _db.NewContext();
        return await NewService(ctx).IsLinkedAsync(userId, new EntraTokenIdentity(tid, oid, "mette@cronus.com", "Mette"));
    }

    [Fact]
    public async Task IsLinked_accepts_the_users_own_identity_in_an_allowed_tenant()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await SeedLinkAsync(userId, TenantA, Oid);

        (await IsLinkedAsync(userId, TenantA.ToUpperInvariant(), Oid))
            .Should().BeTrue("the tenant id is compared case-insensitively, as at sign-in");
    }

    [Fact]
    public async Task IsLinked_refuses_an_identity_linked_to_another_user()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        var colleagueId = await SeedUserAsync(TestDb.DefaultOrgId, "jonas@cronus.com");
        await SeedLinkAsync(colleagueId, TenantA, OidB);

        (await IsLinkedAsync(userId, TenantA, OidB))
            .Should().BeFalse("a colleague signing in at the prompt must not confirm someone else's step-up");
    }

    [Fact]
    public async Task IsLinked_refuses_the_same_object_id_from_another_tenant()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await SeedLinkAsync(userId, TenantA, Oid);

        (await IsLinkedAsync(userId, TenantB, Oid))
            .Should().BeFalse("an object id is only unique inside its own tenant");
    }

    [Fact]
    public async Task IsLinked_refuses_once_the_tenant_is_removed_from_the_allow_list()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await SeedLinkAsync(userId, TenantA, Oid);
        // The admin swaps the allowed tenant out; the link row is left behind.
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantB);

        (await IsLinkedAsync(userId, TenantA, Oid))
            .Should().BeFalse("the allow-list is the boundary for step-up as it is for sign-in");
    }

    [Fact]
    public async Task IsLinked_refuses_once_microsoft_sign_in_is_turned_off()
    {
        await SeedOrgEntraAsync(TestDb.DefaultOrgId, TenantA);
        var userId = await SeedUserAsync(TestDb.DefaultOrgId, "mette@cronus.com");
        await SeedLinkAsync(userId, TenantA, Oid);
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.OrganizationSettings.SingleAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
            row.EntraEnabled = false;
            await ctx.SaveChangesAsync();
        }

        (await IsLinkedAsync(userId, TenantA, Oid)).Should().BeFalse();
    }
}
