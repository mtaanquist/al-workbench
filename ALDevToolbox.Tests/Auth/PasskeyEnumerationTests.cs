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
    public async Task Unknown_email_returns_an_allow_list_of_the_constant_length()
    {
        await using var ctx = _db.NewContext();
        var (options, _) = await NewService(ctx).BeginLoginAsync("nobody@nowhere.test");
        // The length is the same for every address, so it can't say whether
        // the address has an account or how many passkeys it has (#490, #1614).
        options.AllowCredentials.Should().HaveCount(PasskeyService.SignInAllowListLength);
        options.AllowCredentials.Select(c => Convert.ToBase64String(c.Id)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Synthetic_credentials_are_deterministic_per_email()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (a, _) = await svc.BeginLoginAsync("nobody@nowhere.test");
        var (b, _) = await svc.BeginLoginAsync("nobody@nowhere.test");
        // A per-request random id would itself distinguish synthetic from real
        // across two probes; the same email must always yield the same ids.
        a.AllowCredentials.Select(c => c.Id).Should().BeEquivalentTo(b.AllowCredentials.Select(c => c.Id), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Different_emails_get_different_synthetic_credentials()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var (a, _) = await svc.BeginLoginAsync("one@nowhere.test");
        var (b, _) = await svc.BeginLoginAsync("two@nowhere.test");
        a.AllowCredentials.First().Id.Should().NotEqual(b.AllowCredentials.First().Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Known_user_with_passkeys_returns_them_padded_to_the_same_length(int passkeyCount)
    {
        var credIds = Enumerable.Range(1, passkeyCount).Select(n => new byte[] { (byte)n, 2, 3, 4, 5, 6, 7, 8 }).ToList();
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
            foreach (var credId in credIds)
            {
                seed.UserPasskeys.Add(new UserPasskey
                {
                    UserId = user.Id,
                    CredentialId = credId,
                    PublicKey = new byte[] { 0xAB },
                    CreatedAt = DateTime.UtcNow,
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var (options, _) = await NewService(ctx).BeginLoginAsync("real@cronus.example");
        options.AllowCredentials.Should().HaveCount(PasskeyService.SignInAllowListLength);
        options.AllowCredentials.Take(passkeyCount).Select(c => c.Id).Should().BeEquivalentTo(credIds);
    }
}
