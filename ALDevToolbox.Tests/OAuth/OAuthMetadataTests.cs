using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// Pins the contract Claude relies on when adding AL Workbench as a custom
/// connector. Three guarantees:
///
/// <list type="bullet">
///   <item>An anonymous request to <c>/mcp</c> returns <c>401</c> with a
///         <c>WWW-Authenticate: Bearer resource_metadata="…"</c> header — the
///         only shape Claude follows to discover the OAuth server.</item>
///   <item>The protected-resource metadata at
///         <c>/.well-known/oauth-protected-resource</c> serves the RFC 9728
///         shape (resource, authorization_servers, scopes_supported).</item>
///   <item>The OpenIddict-managed AS metadata at
///         <c>/.well-known/oauth-authorization-server</c> advertises CIMD
///         (<c>client_id_metadata_document_supported: true</c>), both
///         <c>"none"</c> (Claude) and <c>"private_key_jwt"</c> (ChatGPT)
///         in <c>token_endpoint_auth_methods_supported</c> alongside
///         <c>RS256</c> in <c>token_endpoint_auth_signing_alg_values_supported</c>,
///         the DCR endpoint, and S256 PKCE.</item>
/// </list>
///
/// A regression in any of these breaks the Claude.ai custom-connector flow
/// silently — Claude won't add the connector, and there's no error
/// surface beyond "couldn't reach the MCP server."
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class OAuthMetadataTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public OAuthMetadataTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Mcp_401_emits_resource_metadata_pointer()
    {
        // /mcp returns 404 when the SiteAdmin toggle is off (kill switch),
        // so the auth-challenge contract is only observable once MCP is on.
        // Flip the toggle the same way McpEnabledToggleTests does.
        await EnableMcpAsync();

        using var client = _factory.CreateClient();

        // The MCP transport responds to POST. With no bearer, the McpBearer
        // policy fails authorisation and the PAT scheme is the one whose
        // ChallengeAsync runs (it's first in the policy's scheme list).
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        response.Headers.WwwAuthenticate.Should().NotBeEmpty(
            "Claude only follows OAuth discovery when the 401 carries WWW-Authenticate.");

        var bearer = response.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme == "Bearer");
        bearer.Should().NotBeNull("the discovery pointer is a Bearer challenge.");
        bearer!.Parameter.Should().Contain("resource_metadata=",
            "the pointer parameter is required for Claude to find the OAuth server.");
        bearer.Parameter.Should().Contain("/.well-known/oauth-protected-resource",
            "the URL must point at the RFC 9728 document Claude knows how to read.");
    }

    [Fact]
    public async Task Protected_resource_metadata_has_rfc9728_shape()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("resource").GetString().Should().EndWith("/mcp");
        root.GetProperty("authorization_servers").EnumerateArray().Should().NotBeEmpty();
        var scopes = root.GetProperty("scopes_supported").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        scopes.Should().Contain("mcp");
        scopes.Should().Contain("offline_access");
        var methods = root.GetProperty("bearer_methods_supported").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        methods.Should().Contain("header");
    }

    [Fact]
    public async Task Authorization_server_metadata_advertises_dcr_cimd_and_s256()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // DCR endpoint must be advertised — OpenIddict 7.5.0 doesn't surface
        // it out of the box, so the customisation event handler in
        // Program.cs is the only thing keeping this true.
        root.TryGetProperty("registration_endpoint", out var registration).Should().BeTrue();
        registration.GetString().Should().EndWith("/oauth/register");

        // CIMD trigger: clients pick CIMD over DCR only when both of these
        // are present. Missing either silently demotes us to DCR-only.
        root.TryGetProperty("client_id_metadata_document_supported", out var cimd).Should().BeTrue();
        cimd.GetBoolean().Should().BeTrue();
        var authMethods = root.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        // "none" — Claude's public PKCE clients.
        authMethods.Should().Contain("none");
        // "private_key_jwt" — ChatGPT's signed-assertion clients. Without
        // this advertised, ChatGPT refuses to pick the CIMD path.
        authMethods.Should().Contain("private_key_jwt");

        // ChatGPT's CIMD documents declare RS256 as the signing algorithm
        // for their JWT client assertions; the AS must advertise it as
        // supported, otherwise ChatGPT's discovery step rejects us.
        var signingAlgs = root.GetProperty("token_endpoint_auth_signing_alg_values_supported").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        signingAlgs.Should().Contain("RS256");

        // S256 PKCE is mandatory per the MCP spec.
        var pkceMethods = root.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        pkceMethods.Should().Contain("S256");
    }

    private async Task EnableMcpAsync()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var svc = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await svc.SaveAsync(new SystemSettingsInput(
            SmtpHost: null, SmtpPort: null, SmtpUser: null,
            SmtpPassword: null, ClearSmtpPassword: false,
            SmtpFrom: null, SmtpFromName: null, SmtpUseStartTls: null, BannerText: null,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: true,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: null, DisabledTools: System.Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: null));
    }
}
