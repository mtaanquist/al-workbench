using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Which factors the step-up page offers. The rule worth a test of its own is
/// that an emailed code is not offered to a session that began with a magic
/// link: someone holding only the mailbox would otherwise sign in and confirm
/// with the same inbox. See ".design/auth-and-audit.md", "Step-up for
/// sensitive tools".
/// </summary>
public sealed class StepUpServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData(SignInMethod.Password, true)]
    [InlineData(SignInMethod.Passkey, true)]
    [InlineData(SignInMethod.MagicLink, false)]
    public async Task An_emailed_code_is_offered_unless_the_session_began_with_a_magic_link(SignInMethod session, bool offered)
    {
        var userId = await SeedUserAsync(totp: false, emailMfa: true);
        await using var ctx = _db.NewContext();

        var methods = await NewService(ctx).AvailableMethodsAsync(userId, session, CancellationToken.None);

        methods!.EmailCode.Should().Be(offered);
        methods.Any.Should().Be(offered, "email was this account's only way to confirm");
    }

    [Fact]
    public async Task A_magic_link_session_keeps_its_authenticator()
    {
        var userId = await SeedUserAsync(totp: true, emailMfa: true);
        await using var ctx = _db.NewContext();

        var methods = await NewService(ctx).AvailableMethodsAsync(userId, SignInMethod.MagicLink, CancellationToken.None);

        methods.Should().Be(new StepUpMethods(Totp: true, EmailCode: false, Passkey: false, Entra: false));
    }

    private StepUpService NewService(AppDbContext ctx)
    {
        var clock = TimeProvider.System;
        var auth = new AuthService(ctx, NullLogger<AuthService>.Instance, clock);
        // No RP id: passkeys are switched off, so they never muddy the answer.
        var passkeys = new PasskeyService(ctx,
            new Fido2(new Fido2Configuration { Origins = new HashSet<string>() }),
            _db.DataProtectionProvider, clock, new WebAuthnConfig("", [], "Test"),
            DeploymentIdentity.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "aldt-stepup-deploy-" + Guid.NewGuid().ToString("N")),
                NullLogger.Instance),
            new CapturingEmailService(), new EmailRenderer(_services, NullLoggerFactory.Instance),
            NullLogger<PasskeyService>.Instance);
        var entra = new EntraSignInService(ctx, auth, _db.OrgContext, _db.DataProtectionProvider, clock,
            NullLogger<EntraSignInService>.Instance);
        return new StepUpService(ctx, passkeys, entra, auth, _db.NewToolEnablement(ctx), clock);
    }

    private async Task<int> SeedUserAsync(bool totp, bool emailMfa)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "ada@cronus.example",
            DisplayName = "Ada",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            TotpEnabled = totp,
            EmailMfaEnabled = emailMfa,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }
}
