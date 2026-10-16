using System.Buffers.Text;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// <see cref="GatewayAuthenticationHandler"/> end to end against the database: the
/// principal a good token mounts, and every way a token is refused - signature,
/// algorithm, audience, times, request binding, body hash, replay (across a fresh
/// context, so the memory of seen ids is the table and not the process), and
/// revocation. See <c>.design/on-prem-gateway.md</c>, "Registration and authentication".
/// </summary>
public sealed class GatewayAuthenticationHandlerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly List<AppDbContext> _contexts = [];
    private User _admin = null!;

    public void Dispose()
    {
        foreach (var ctx in _contexts) ctx.Dispose();
        _db.Dispose();
    }

    private async Task<(GatewayRegistrationResponse Gateway, System.Security.Cryptography.ECDsa Key)> RegisterAsync(bool approve = true)
    {
        _admin ??= await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "admin@cronus.example");
        var key = GatewayTestKit.NewKey();
        var gateway = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve);
        return (gateway, key);
    }

    /// <summary>A request as the gateway would send it, with its token in the header.</summary>
    private static DefaultHttpContext Request(string token, byte[] body, string method = "POST", string path = GatewayProtocol.HeartbeatPath)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("workbench.example.com");
        http.Request.Path = path;
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        http.Request.Headers.Authorization = "Bearer " + token;
        return http;
    }

    /// <summary>
    /// A handler as the app builds one: over its own context with no organisation in
    /// scope, because nothing has authenticated yet when it runs.
    /// </summary>
    private async Task<AuthenticateResult> AuthenticateAsync(HttpContext http)
    {
        var anonymous = new AmbientOrganizationContext();
        var ctx = _db.NewContext(anonymous);
        _contexts.Add(ctx);
        var handler = new GatewayAuthenticationHandler(
            new StaticOptions(new AuthenticationSchemeOptions { TimeProvider = _clock }),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            GatewayTestKit.NewService(ctx, anonymous, _clock),
            new PublicOrigin(GatewayTestKit.Origin));
        await handler.InitializeAsync(
            new AuthenticationScheme(GatewayAuthenticationHandler.AuthenticationScheme, null, typeof(GatewayAuthenticationHandler)),
            http);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task A_valid_token_mounts_the_gateways_principal_with_no_user()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());
        var http = Request(token, body);

        var result = await AuthenticateAsync(http);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        var principal = result.Principal!;
        principal.Identity!.Name.Should().Be("Gateway CRONUS-BC01");
        principal.Identity.AuthenticationType.Should().Be(GatewayAuthenticationHandler.AuthenticationScheme);
        principal.FindFirst(HttpOrganizationContext.OrganizationIdClaim)!.Value.Should().Be(TestDb.DefaultOrgId.ToString());
        principal.FindFirst(GatewayAuthenticationHandler.GatewayIdClaim)!.Value.Should().Be(gateway.GatewayId.ToString());
        principal.FindFirst(GatewayAuthenticationHandler.GatewayApprovedClaim)!.Value.Should().Be("true");
        principal.FindFirst(HttpOrganizationContext.UserIdClaim).Should().BeNull();
        principal.FindFirst(ClaimTypes.Role).Should().BeNull();
        principal.FindFirst(HttpOrganizationContext.SiteAdminClaim).Should().BeNull();

        // The endpoint still gets the whole body.
        http.Request.Body.Position.Should().Be(0);
        (await new StreamReader(http.Request.Body).ReadToEndAsync()).Should().Be(Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task A_pending_gateway_authenticates_but_is_marked_not_approved()
    {
        var (gateway, key) = await RegisterAsync(approve: false);
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirst(GatewayAuthenticationHandler.GatewayApprovedClaim)!.Value.Should().Be("false");
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        using var stranger = GatewayTestKit.NewKey();
        var (body, token) = GatewayTestKit.SignedHeartbeat(stranger, gateway.GatewayId, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
        // The same words as for a gateway that does not exist; the log has the difference.
        result.Failure!.Message.Should().Be(GatewayAuthenticationHandler.UnrecognisedMessage);
    }

    [Fact]
    public async Task An_unknown_gateway_and_a_bad_signature_are_refused_with_the_same_message()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        using var stranger = GatewayTestKit.NewKey();
        var (body, forged) = GatewayTestKit.SignedHeartbeat(stranger, gateway.GatewayId, _clock.GetUtcNow());
        var (otherBody, unknown) = GatewayTestKit.SignedHeartbeat(stranger, gateway.GatewayId + 1000, _clock.GetUtcNow());

        var badSignature = await AuthenticateAsync(Request(forged, body));
        var unknownGateway = await AuthenticateAsync(Request(unknown, otherBody));

        badSignature.Failure!.Message.Should().Be(unknownGateway.Failure!.Message);
    }

    [Fact]
    public async Task An_unsigned_request_is_refused_without_its_body_being_read()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        using var stranger = GatewayTestKit.NewKey();
        var (_, forged) = GatewayTestKit.SignedHeartbeat(stranger, gateway.GatewayId, _clock.GetUtcNow());
        var http = Request(forged, []);
        var body = new UnreadableStream();
        http.Request.Body = body;
        http.Request.ContentLength = GatewayProtocol.MaxRequestBodyBytes;

        var result = await AuthenticateAsync(http);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be(GatewayAuthenticationHandler.UnrecognisedMessage);
        body.WasRead.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_another_algorithm_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());
        var parts = token.Split('.');
        var none = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));

        var result = await AuthenticateAsync(Request(none + "." + parts[1] + ".", body));

        result.Succeeded.Should().BeFalse();
        // To the caller, a token whose signature was never checked is a stranger's.
        result.Failure!.Message.Should().Be(GatewayAuthenticationHandler.UnrecognisedMessage);
        // The validator's own reason, which is what the log records.
        var (validatedNone, refusal) = GatewayTokenValidator.Validate(
            none + "." + parts[1] + ".", key.ExportSubjectPublicKeyInfo(), gateway.GatewayId, TokenRequest());
        validatedNone.Should().BeNull();
        refusal!.Reason.Should().Be("The token must be signed with ES256.");
        refusal.BeforeSignature.Should().BeTrue();
    }

    [Fact]
    public async Task A_token_signed_HS256_with_the_public_key_as_secret_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());
        var payload = token.Split('.')[1];
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        // The classic confusion: the public key is no secret, so an attacker can HMAC
        // with it, and a verifier that takes "alg" from the token would accept it.
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var mac = System.Security.Cryptography.HMACSHA256.HashData(publicKey, Encoding.ASCII.GetBytes(header + "." + payload));
        var forged = header + "." + payload + "." + Base64Url.EncodeToString(mac);

        var (validated, refusal) = GatewayTokenValidator.Validate(forged, publicKey, gateway.GatewayId, TokenRequest());
        var result = await AuthenticateAsync(Request(forged, body));

        validated.Should().BeNull();
        refusal!.Reason.Should().Be("The token must be signed with ES256.");
        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be(GatewayAuthenticationHandler.UnrecognisedMessage);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(
            key, gateway.GatewayId, _clock.GetUtcNow(), audience: GatewayProtocol.AudienceFor("https://elsewhere.example.com"));

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("address");
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        // Signed two and a half minutes ago: exp was 90 seconds ago, past the 60-second skew.
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow().AddSeconds(-150));

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task A_token_issued_too_far_in_the_future_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow().AddSeconds(90));

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("future");
    }

    [Fact]
    public async Task A_token_within_the_clock_skew_is_accepted()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow().AddSeconds(45));

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
    }

    [Fact]
    public async Task A_token_that_lives_longer_than_a_minute_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var body = Encoding.UTF8.GetBytes("{}");
        var token = GatewayRequestToken.Create(key, gateway.GatewayId, GatewayTestKit.Audience, "POST",
            GatewayProtocol.HeartbeatPath, body, hasBody: true, _clock.GetUtcNow(), lifetime: TimeSpan.FromMinutes(10));

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("PUT", GatewayProtocol.HeartbeatPath)]
    [InlineData("POST", GatewayProtocol.RegisterPath)]
    public async Task A_token_made_for_another_request_is_refused(string signedMethod, string signedPath)
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var body = Encoding.UTF8.GetBytes("{}");
        var token = GatewayRequestToken.Create(key, gateway.GatewayId, GatewayTestKit.Audience, signedMethod,
            signedPath, body, hasBody: true, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("different request");
    }

    [Fact]
    public async Task A_body_changed_after_signing_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (_, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, Encoding.UTF8.GetBytes("{\"version\":\"9.9.9\"}")));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("body");
    }

    [Fact]
    public async Task A_body_the_token_does_not_vouch_for_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var body = Encoding.UTF8.GetBytes("{}");
        var token = GatewayRequestToken.Create(key, gateway.GatewayId, GatewayTestKit.Audience, "POST",
            GatewayProtocol.HeartbeatPath, [], hasBody: false, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_request_without_a_body_needs_no_body_hash()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var token = GatewayRequestToken.Create(key, gateway.GatewayId, GatewayTestKit.Audience, "GET",
            "/gateway/commands/next", [], hasBody: false, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, [], "GET", "/gateway/commands/next"));

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
    }

    [Fact]
    public async Task A_replayed_token_is_refused_even_by_a_fresh_handler_and_context()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());

        (await AuthenticateAsync(Request(token, body))).Succeeded.Should().BeTrue();
        var replay = await AuthenticateAsync(Request(token, body));

        replay.Succeeded.Should().BeFalse();
        replay.Failure!.Message.Should().Contain("already received");
    }

    [Fact]
    public async Task A_revoked_gateway_is_refused()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        _db.OrgContext.CurrentUserId = _admin.Id;
        await using (var ctx = _db.NewContext())
        {
            await GatewayTestKit.NewService(ctx, _db.OrgContext, _clock).RevokeAsync(gateway.GatewayId);
        }
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());

        var result = await AuthenticateAsync(Request(token, body));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Be(GatewayAuthenticationHandler.UnrecognisedMessage);
    }

    [Fact]
    public async Task A_request_without_a_bearer_header_is_left_alone()
    {
        var http = new DefaultHttpContext();

        var result = await AuthenticateAsync(http);

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task A_gateways_principal_reads_only_its_own_organisation()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("ours", organizationId: TestDb.DefaultOrgId));
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("theirs", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }
        var otherAdmin = await GatewayTestKit.SeedUserAsync(_db, TestDb.OtherOrgId, UserRole.Admin, "admin@other.example");
        using var otherKey = GatewayTestKit.NewKey();
        await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.OtherOrgId, otherAdmin.Id, otherKey, machineName: "OTHER-BC01");

        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());
        var http = Request(token, body);
        var result = await AuthenticateAsync(http);
        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        http.User = result.Principal!;

        var asGateway = new HttpOrganizationContext(new TestDb.FixedHttpContextAccessor(http));
        asGateway.CurrentOrganizationId.Should().Be(TestDb.DefaultOrgId);
        asGateway.CurrentUserId.Should().BeNull();
        await using var ctx = _db.NewContext(asGateway);
        (await ctx.RuntimeTemplates.Select(t => t.Key).ToListAsync()).Should().Equal("ours");
        (await ctx.Gateways.Select(g => g.MachineName).ToListAsync()).Should().Equal("CRONUS-BC01");
        (await ctx.Users.Select(u => u.Email).ToListAsync()).Should().NotContain("admin@other.example");
    }

    [Fact]
    public async Task A_change_made_under_a_gateways_principal_is_audited_as_the_gateway()
    {
        var (gateway, key) = await RegisterAsync();
        using var _ = key;
        var (body, token) = GatewayTestKit.SignedHeartbeat(key, gateway.GatewayId, _clock.GetUtcNow());
        var http = Request(token, body);
        http.User = (await AuthenticateAsync(http)).Principal!;
        var asGateway = new HttpOrganizationContext(new TestDb.FixedHttpContextAccessor(http));

        await using (var ctx = _db.NewContext(new AuditInterceptor(new TestDb.FixedHttpContextAccessor(http))))
        {
            var row = await ctx.Gateways.SingleAsync(g => g.Id == gateway.GatewayId);
            row.Name = "CRONUS server room";
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext(asGateway);
        var entry = await read.AuditLog.OrderByDescending(a => a.Id).FirstAsync();
        entry.EntityType.Should().Be(AuditEntityType.Gateway);
        entry.ChangedBy.Should().Be("Gateway CRONUS-BC01");
        entry.ChangedByUserId.Should().BeNull();
        entry.OrganizationId.Should().Be(TestDb.DefaultOrgId);
    }

    private GatewayTokenRequest TokenRequest() =>
        new("POST", GatewayProtocol.HeartbeatPath, GatewayTestKit.Audience, _clock.GetUtcNow());

    /// <summary>A request body that records whether anyone tried to read it.</summary>
    private sealed class UnreadableStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; return 0; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            return ValueTask.FromResult(0);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StaticOptions(AuthenticationSchemeOptions value) : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => value;
        public AuthenticationSchemeOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
