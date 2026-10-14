using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// What the gateway tests share: a key pair as the gateway would make one, the
/// registration request it sends, and a token signed the way it signs one.
/// </summary>
internal static class GatewayTestKit
{
    public const string Origin = "https://workbench.example.com";

    public static readonly string Audience = GatewayProtocol.AudienceFor(Origin);

    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static GatewayRegistrationRequest Request(string code, ECDsa key, string machineName = "CRONUS-BC01") => new()
    {
        Code = code,
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        MachineName = machineName,
        Version = "1.0.0",
        ProtocolVersion = GatewayProtocol.Version,
    };

    /// <summary>A heartbeat body and a token for it, signed with <paramref name="key"/>.</summary>
    public static (byte[] Body, string Token) SignedHeartbeat(
        ECDsa key, int gatewayId, DateTimeOffset now, string? audience = null, string? tokenId = null)
    {
        var body = Encoding.UTF8.GetBytes("{\"version\":\"1.0.1\",\"machineName\":\"CRONUS-BC01\"}");
        var token = GatewayRequestToken.Create(
            key, gatewayId, audience ?? Audience, "POST", GatewayProtocol.HeartbeatPath, body, hasBody: true, now, tokenId: tokenId);
        return (body, token);
    }

    public static async Task<User> SeedUserAsync(TestDb db, int organizationId, UserRole role, string email)
    {
        await using var ctx = db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            DisplayName = email,
            PasswordHash = "ignored",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Registers a gateway in <paramref name="organizationId"/> the way it really
    /// happens: an Admin of that organisation makes the code, then an anonymous
    /// caller exchanges it. Leaves the fixture's context as it found it.
    /// </summary>
    public static async Task<GatewayRegistrationResponse> RegisterAsync(
        TestDb db, TimeProvider clock, int organizationId, int adminUserId, ECDsa key, bool approve = false,
        string machineName = "CRONUS-BC01")
    {
        var (previousOrg, previousUser) = (db.OrgContext.CurrentOrganizationId, db.OrgContext.CurrentUserId);
        try
        {
            db.OrgContext.CurrentOrganizationId = organizationId;
            db.OrgContext.CurrentUserId = adminUserId;
            IssuedGatewayCode code;
            await using (var ctx = db.NewContext())
            {
                code = await NewService(ctx, db.OrgContext, clock).CreateRegistrationCodeAsync();
            }

            GatewayRegistrationResponse registered;
            var anonymous = new AmbientOrganizationContext();
            await using (var ctx = db.NewContext(anonymous))
            {
                registered = await NewService(ctx, anonymous, clock).RegisterAsync(Request(code.Code, key, machineName));
            }

            if (approve)
            {
                await using var ctx = db.NewContext();
                await NewService(ctx, db.OrgContext, clock).ApproveAsync(registered.GatewayId);
            }
            return registered;
        }
        finally
        {
            db.OrgContext.CurrentOrganizationId = previousOrg;
            db.OrgContext.CurrentUserId = previousUser;
        }
    }

    public static GatewayService NewService(ALDevToolbox.Data.AppDbContext ctx, IOrganizationContext org, TimeProvider clock) =>
        new(ctx, org, clock, NullLogger<GatewayService>.Instance);
}
