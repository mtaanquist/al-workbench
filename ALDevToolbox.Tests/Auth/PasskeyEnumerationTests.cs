using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using Fido2NetLib;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Account-enumeration guard on passkey login (#490): the allow-list returned by
/// <see cref="PasskeyService.BeginLoginAsync"/> must look the same whether the
/// email hint matches a real user-with-passkeys or not, so it can't be used to
/// probe which accounts exist.
/// </summary>
public sealed class PasskeyEnumerationTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private PasskeyService NewService(AppDbContext ctx)
    {
        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "Test",
            Origins = new HashSet<string> { "https://localhost" },
        });
        var config = new WebAuthnConfig("localhost", new[] { "https://localhost" }, "Test");
        var deployment = DeploymentIdentity.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "aldt-pk-deploy-" + Guid.NewGuid().ToString("N")),
            NullLogger.Instance);
        return new PasskeyService(ctx, fido2, _db.DataProtectionProvider, TimeProvider.System, config, deployment,
            new CapturingEmailService(),
            new ALDevToolbox.Services.Email.EmailRenderer(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance),
            NullLogger<PasskeyService>.Instance);
    }

    [Fact]
    public async Task Unknown_email_still_returns_a_non_empty_allow_list()
    {
        await using var ctx = _db.NewContext();
        var (options, _) = await NewService(ctx).BeginLoginAsync("nobody@nowhere.test");
        // A real user with one passkey returns one descriptor; an unknown email
        // must be indistinguishable, so it returns a single synthetic one.
        options.AllowCredentials.Should().ContainSingle();
    }

    [Fact]
    public async Task Synthetic_credential_is_deterministic_per_email()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (a, _) = await svc.BeginLoginAsync("nobody@nowhere.test");
        var (b, _) = await svc.BeginLoginAsync("nobody@nowhere.test");
        // A per-request random id would itself distinguish synthetic from real
        // across two probes; the same email must always yield the same id.
        a.AllowCredentials.Single().Id.Should().Equal(b.AllowCredentials.Single().Id);
    }

    [Fact]
    public async Task Different_emails_get_different_synthetic_credentials()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (a, _) = await svc.BeginLoginAsync("one@nowhere.test");
        var (b, _) = await svc.BeginLoginAsync("two@nowhere.test");
        a.AllowCredentials.Single().Id.Should().NotEqual(b.AllowCredentials.Single().Id);
    }

    [Fact]
    public async Task Known_user_with_a_passkey_returns_the_real_credential()
    {
        var credId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await using (var seed = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "real@cronus.example",
                PasswordHash = "x",
                DisplayName = "Real User",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            seed.UserPasskeys.Add(new UserPasskey
            {
                UserId = user.Id,
                CredentialId = credId,
                PublicKey = new byte[] { 0xAB },
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var (options, _) = await NewService(ctx).BeginLoginAsync("real@cronus.example");
        options.AllowCredentials.Should().ContainSingle();
        options.AllowCredentials.Single().Id.Should().Equal(credId);
    }
}
