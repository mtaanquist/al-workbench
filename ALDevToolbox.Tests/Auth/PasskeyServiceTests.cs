using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Fido2NetLib;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Services.Email;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Passkey sign-in and step-up completion against a software ES256
/// authenticator: a real signature over real authenticator data, verified by
/// the real library. Each refusal in <see cref="PasskeyService.CompleteLoginAsync"/>
/// and <see cref="PasskeyService.CompleteStepUpAsync"/> gets a test of its own
/// (#1271): an inactive account, a step-up challenge used as a sign-in, a user
/// handle that does not own the credential, and a counter that did not move.
/// </summary>
public sealed class PasskeyServiceTests : IDisposable
{
    private const string RpId = "localhost";
    private const string Origin = "https://localhost";

    private readonly TestDb _db = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
    private readonly CapturingEmailService _transport = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        _key.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_new_passkey_is_saved_audited_and_its_owner_is_emailed()
    {
        var userId = await SeedUserAsync("ada@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginRegistrationAsync(userId);

        var passkey = await svc.CompleteRegistrationAsync(userId, "Work laptop",
            Attestation(options.Challenge), envelope, "https://workbench.cronus.example/account/security");

        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).Nickname.Should().Be("Work laptop");

        var audit = await read.AuditLog.SingleAsync(a => a.EntityType == AuditEntityType.Passkey);
        audit.Action.Should().Be(AuditAction.Created);
        audit.EntityId.Should().Be(passkey.Id);
        audit.EntityName.Should().Be("Work laptop");
        audit.ChangedByUserId.Should().Be(userId);
        audit.OrganizationId.Should().Be(TestDb.DefaultOrgId);

        // Queued through the outbox, not sent inline: it is a notice, not a
        // code someone is waiting on.
        var queued = await read.EmailOutboxMessages.AsNoTracking().SingleAsync();
        queued.Purpose.Should().Be(EmailPurpose.PasskeyAdded);
        queued.ToEmail.Should().Be("ada@cronus.example");
        queued.Subject.Should().Be(PasskeyAddedEmail.Subject);
        _transport.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_passkey_added_email_names_the_passkey_and_says_how_to_remove_it()
    {
        var content = await PasskeyAddedEmail.RenderAsync(new EmailRenderer(_services, NullLoggerFactory.Instance),
            "Ada", "Work laptop", "14 Oct 2026, 09:12 UTC", "https://workbench.cronus.example/account/security");

        content.HtmlBody.Should().Contain("Work laptop")
            .And.Contain("remove that passkey")
            .And.Contain("https://workbench.cronus.example/account/security");
    }

    [Fact]
    public async Task A_registration_started_for_one_account_cannot_be_finished_by_another()
    {
        var userId = await SeedUserAsync("ada@cronus.example");
        var otherId = await SeedUserAsync("bob@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginRegistrationAsync(userId);

        var act = () => svc.CompleteRegistrationAsync(otherId, "Work laptop",
            Attestation(options.Challenge), envelope, "https://workbench.cronus.example/account/security");

        await act.Should().ThrowAsync<PlanValidationException>();
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        (await read.EmailOutboxMessages.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_passkey_is_audited()
    {
        var userId = await SeedAsync("ada@cronus.example");
        await using var ctx = _db.NewContext();
        var id = (await ctx.UserPasskeys.IgnoreQueryFilters().SingleAsync()).Id;

        await NewService(ctx).DeleteAsync(userId, id);

        await using var read = _db.NewContext();
        var audit = await read.AuditLog.SingleAsync(a => a.EntityType == AuditEntityType.Passkey);
        audit.Action.Should().Be(AuditAction.Deleted);
        audit.EntityName.Should().Be("Software key");
    }

    [Fact]
    public async Task A_valid_assertion_signs_the_user_in_and_moves_the_counter_on()
    {
        var userId = await SeedAsync("ada@cronus.example", storedCounter: 4);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginLoginAsync("ada@cronus.example");

        var (user, verified) = await svc.CompleteLoginAsync(Assertion(options.Challenge, userId, signCount: 5, userVerified: true), envelope);

        user.Id.Should().Be(userId);
        verified.Should().BeTrue();
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).SignCounter.Should().Be(5);
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_account_that_is_no_longer_active_is_refused()
    {
        var userId = await SeedAsync("leaver@cronus.example", status: UserStatus.Disabled);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginLoginAsync("leaver@cronus.example");

        var act = () => svc.CompleteLoginAsync(Assertion(options.Challenge, userId, signCount: 1), envelope);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Passkey"].Should().Contain("isn't active");
    }

    [Fact]
    public async Task A_step_up_challenge_cannot_be_used_to_sign_in()
    {
        var userId = await SeedAsync("ada@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginStepUpAsync(userId);

        var act = () => svc.CompleteLoginAsync(Assertion(options.Challenge, userId, signCount: 1, userVerified: true), envelope);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Passkey"].Should().Contain("wasn't started as a sign-in");
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).SignCounter.Should().Be(0);
    }

    [Fact]
    public async Task A_user_handle_that_does_not_own_the_credential_is_refused()
    {
        var userId = await SeedAsync("ada@cronus.example");
        var otherId = await SeedUserAsync("bob@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginLoginAsync("ada@cronus.example");

        var act = () => svc.CompleteLoginAsync(Assertion(options.Challenge, otherId, signCount: 1), envelope);

        await act.Should().ThrowAsync<Exception>();
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).SignCounter.Should().Be(0);
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).LastLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task A_counter_that_did_not_move_forward_is_refused()
    {
        // A cloned authenticator replays an old counter value.
        var userId = await SeedAsync("ada@cronus.example", storedCounter: 7);
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginLoginAsync("ada@cronus.example");

        var act = () => svc.CompleteLoginAsync(Assertion(options.Challenge, userId, signCount: 7), envelope);

        await act.Should().ThrowAsync<Exception>();
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).SignCounter.Should().Be(7);
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).LastLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task A_step_up_completes_for_the_owner_with_user_verification()
    {
        var userId = await SeedAsync("ada@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginStepUpAsync(userId);

        await svc.CompleteStepUpAsync(userId, Assertion(options.Challenge, userId, signCount: 1, userVerified: true), envelope);

        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).SignCounter.Should().Be(1);
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).LastLoginAt
            .Should().BeNull("a step-up is not a sign-in");
    }

    [Fact]
    public async Task A_sign_in_challenge_cannot_finish_a_step_up()
    {
        var userId = await SeedAsync("ada@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginLoginAsync("ada@cronus.example");

        var act = () => svc.CompleteStepUpAsync(userId, Assertion(options.Challenge, userId, signCount: 1, userVerified: true), envelope);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Passkey"].Should().Contain("wasn't started for this account");
    }

    [Fact]
    public async Task A_step_up_for_one_account_cannot_be_finished_by_another()
    {
        var userId = await SeedAsync("ada@cronus.example");
        var otherId = await SeedUserAsync("bob@cronus.example");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (options, envelope) = await svc.BeginStepUpAsync(userId);

        var act = () => svc.CompleteStepUpAsync(otherId, Assertion(options.Challenge, userId, signCount: 1, userVerified: true), envelope);

        await act.Should().ThrowAsync<PlanValidationException>();
        await using var read = _db.NewContext();
        (await read.UserPasskeys.IgnoreQueryFilters().SingleAsync()).SignCounter.Should().Be(0);
    }

    private PasskeyService NewService(AppDbContext ctx)
    {
        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = RpId,
            ServerName = "Test",
            Origins = new HashSet<string> { Origin },
        });
        var config = new WebAuthnConfig(RpId, new[] { Origin }, "Test");
        var deployment = DeploymentIdentity.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "aldt-pk-deploy-" + Guid.NewGuid().ToString("N")),
            NullLogger.Instance);
        // The real outbox in front of a capturing transport, so a test can
        // tell queued mail (a row) from mail sent inline (the transport).
        var outbox = new OutboxEmailService(_transport,
            new EmailOutbox(_db.NewContextFactory(), _db.DataProtectionProvider, TimeProvider.System, NullLogger<EmailOutbox>.Instance),
            _db.OrgContext);
        return new PasskeyService(ctx, fido2, _db.DataProtectionProvider, TimeProvider.System, config, deployment,
            outbox, new EmailRenderer(_services, NullLoggerFactory.Instance), NullLogger<PasskeyService>.Instance);
    }

    private async Task<int> SeedUserAsync(string email, UserStatus status = UserStatus.Active)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            PasswordHash = "x",
            DisplayName = email,
            Role = UserRole.User,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A user holding this test's software passkey.</summary>
    private async Task<int> SeedAsync(string email, UserStatus status = UserStatus.Active, long storedCounter = 0)
    {
        var userId = await SeedUserAsync(email, status);
        await using var seed = _db.NewContext();
        seed.UserPasskeys.Add(new UserPasskey
        {
            UserId = userId,
            CredentialId = _credentialId,
            PublicKey = CosePublicKey(),
            SignCounter = storedCounter,
            Nickname = "Software key",
            CreatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
        return userId;
    }

    /// <summary>The key as a COSE EC2 / ES256 / P-256 map, the form a passkey row stores.</summary>
    private byte[] CosePublicKey()
    {
        var p = _key.ExportParameters(includePrivateParameters: false);
        return [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. p.Q.X!, 0x22, 0x58, 0x20, .. p.Q.Y!];
    }

    /// <summary>
    /// What a browser hands back from <c>navigator.credentials.create</c> with
    /// no attestation: authenticator data carrying this test's credential id
    /// and public key, CBOR-wrapped as a "none" attestation object.
    /// </summary>
    private AuthenticatorAttestationRawResponse Attestation(byte[] challenge)
    {
        var cose = CosePublicKey();
        byte[] authData = [.. SHA256.HashData(Encoding.UTF8.GetBytes(RpId)), 0x45, 0, 0, 0, 0,
            .. new byte[16], 0, (byte)_credentialId.Length, .. _credentialId, .. cose];
        byte[] attestationObject = [0xA3,
            0x63, .. "fmt"u8, 0x64, .. "none"u8,
            0x67, .. "attStmt"u8, 0xA0,
            0x68, .. "authData"u8, 0x58, (byte)authData.Length, .. authData];
        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "webauthn.create",
            challenge = WebEncoders.Base64UrlEncode(challenge),
            origin = Origin,
            crossOrigin = false,
        }));
        var json = JsonSerializer.Serialize(new
        {
            id = WebEncoders.Base64UrlEncode(_credentialId),
            rawId = WebEncoders.Base64UrlEncode(_credentialId),
            type = "public-key",
            response = new
            {
                attestationObject = WebEncoders.Base64UrlEncode(attestationObject),
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                transports = Array.Empty<string>(),
            },
            clientExtensionResults = new { },
        });
        return JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>
    /// What a browser hands back from <c>navigator.credentials.get</c>:
    /// authenticator data (RP id hash, flags, counter), the client data, and
    /// an ES256 signature over both, shaped as the endpoints deserialise it.
    /// </summary>
    private AuthenticatorAssertionRawResponse Assertion(byte[] challenge, int userHandle, uint signCount, bool userVerified = false)
    {
        var flags = (byte)(0x01 | (userVerified ? 0x04 : 0x00));
        byte[] authData = [.. SHA256.HashData(Encoding.UTF8.GetBytes(RpId)), flags,
            (byte)(signCount >> 24), (byte)(signCount >> 16), (byte)(signCount >> 8), (byte)signCount];
        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type = "webauthn.get",
            challenge = WebEncoders.Base64UrlEncode(challenge),
            origin = Origin,
            crossOrigin = false,
        }));
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)],
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var json = JsonSerializer.Serialize(new
        {
            id = WebEncoders.Base64UrlEncode(_credentialId),
            rawId = WebEncoders.Base64UrlEncode(_credentialId),
            type = "public-key",
            response = new
            {
                authenticatorData = WebEncoders.Base64UrlEncode(authData),
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                signature = WebEncoders.Base64UrlEncode(signature),
                userHandle = WebEncoders.Base64UrlEncode(BitConverter.GetBytes(userHandle)),
            },
            clientExtensionResults = new { },
        });
        return JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
