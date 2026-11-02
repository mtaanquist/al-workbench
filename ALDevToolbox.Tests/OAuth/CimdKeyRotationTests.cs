using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// A <c>private_key_jwt</c> client that rotates its signing key keeps refreshing its
/// tokens (#1576, MCP2-6). Through the real app: a client-metadata client connects
/// with key A, rotates to key B at its <c>jwks_uri</c>, and signs its refresh request
/// with B. The token endpoint re-reads the key set once the stored one is older than
/// <see cref="CimdClientResolver.RefreshInterval"/>, and not before.
/// </summary>
public sealed class CimdKeyRotationTests : IDisposable
{
    private const string Email = "consultant@cronus.example";
    private const string Password = "correct horse battery staple";
    private const string ClientId = "https://assistant.cronus.example/oauth/client.json";
    private const string JwksUri = "https://assistant.cronus.example/oauth/jwks.json";
    private const string RedirectUri = "http://127.0.0.1:41999/callback";
    // The token endpoint, on the address the test client calls it at.
    private const string Audience = "https://localhost/";

    private readonly TestDb _db = new();
    private readonly StubHandler _http = new();
    private readonly RSA _keyA = RSA.Create(2048);
    private readonly RSA _keyB = RSA.Create(2048);

    public void Dispose()
    {
        _keyA.Dispose();
        _keyB.Dispose();
        _http.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_refresh_signed_with_a_rotated_key_succeeds_once_the_stored_keys_are_stale()
    {
        using var factory = NewFactory();
        var refreshToken = await ConnectWithKeyAAsync(factory);

        // The client rotates: its jwks_uri now holds only key B.
        _http.Serve(JwksUri, Jwks((_keyB, "key-b")));
        await AgeStoredKeysAsync(factory, CimdClientResolver.RefreshInterval + TimeSpan.FromMinutes(1));
        using var assistant = factory.CreateClient();

        using var refreshed = await RefreshAsync(assistant, refreshToken, _keyB, "key-b");

        var body = await refreshed.Content.ReadAsStringAsync();
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Keys_read_within_the_refresh_interval_are_not_fetched_again()
    {
        using var factory = NewFactory();
        var refreshToken = await ConnectWithKeyAAsync(factory);
        _http.Serve(JwksUri, Jwks((_keyB, "key-b")));
        var fetches = _http.RequestsTo(JwksUri);
        using var assistant = factory.CreateClient();

        using var refused = await RefreshAsync(assistant, refreshToken, _keyB, "key-b");

        refused.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "the stored keys were read moments ago, so the rotated key is not known yet");
        _http.RequestsTo(JwksUri).Should().Be(fetches, "a fresh key set is not fetched again");

        // The old key still works meanwhile.
        using var withOldKey = await RefreshAsync(assistant, refreshToken, _keyA, "key-a");
        withOldKey.StatusCode.Should().Be(HttpStatusCode.OK, await withOldKey.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_key_set_that_cannot_be_read_leaves_the_stored_keys_working()
    {
        using var factory = NewFactory();
        var refreshToken = await ConnectWithKeyAAsync(factory);
        _http.Fail(JwksUri);
        await AgeStoredKeysAsync(factory, CimdClientResolver.RefreshInterval + TimeSpan.FromMinutes(1));
        using var assistant = factory.CreateClient();

        using var refreshed = await RefreshAsync(assistant, refreshToken, _keyA, "key-a");

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    private EndpointFactory NewFactory()
    {
        _http.Serve(ClientId, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["client_id"] = ClientId,
            ["client_name"] = "CRONUS assistant",
            ["redirect_uris"] = new[] { RedirectUri },
            ["token_endpoint_auth_method"] = "private_key_jwt",
            ["token_endpoint_auth_signing_alg"] = "RS256",
            ["jwks_uri"] = JwksUri,
        }));
        _http.Serve(JwksUri, Jwks((_keyA, "key-a")));
        // The resolver's named client, pointed at the stub instead of the network
        // (the last primary-handler registration wins).
        return new EndpointFactory(_db, services => services
            .AddHttpClient(nameof(CimdClientResolver))
            .ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(_http)));
    }

    /// <summary>Approves the client as the signed-in person and redeems the code with key A. Returns the refresh token.</summary>
    private async Task<string> ConnectWithKeyAAsync(EndpointFactory factory)
    {
        using var browser = await SignedInAsync(factory);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "cronus",
        };

        using var consentRedirect = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", authorize!));
        consentRedirect.StatusCode.Should().Be(HttpStatusCode.Redirect, await consentRedirect.Content.ReadAsStringAsync());
        using var consentPage = await browser.GetAsync(consentRedirect.Headers.Location!);
        consentPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var antiforgery = Regex.Match(await consentPage.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;

        using var approved = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(
            new Dictionary<string, string>(authorize) { ["decision"] = "allow", ["__RequestVerificationToken"] = antiforgery }));
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = QueryHelpers.ParseQuery(approved.Headers.Location!.Query)["code"].ToString();
        code.Should().NotBeEmpty();

        using var exchanged = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["client_assertion_type"] = OpenIddictConstants.ClientAssertionTypes.JwtBearer,
            ["client_assertion"] = Assertion(_keyA, "key-a"),
        }));
        var body = await exchanged.Content.ReadAsStringAsync();
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("refresh_token").GetString()!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken, RSA key, string keyId) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_assertion_type"] = OpenIddictConstants.ClientAssertionTypes.JwtBearer,
            ["client_assertion"] = Assertion(key, keyId),
        }));

    /// <summary>Back-dates the row's last key read, as if it was read <paramref name="age"/> ago.</summary>
    private static async Task AgeStoredKeysAsync(EndpointFactory factory, TimeSpan age)
    {
        using var scope = factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await applications.FindByClientIdAsync(ClientId))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application);
        var then = JsonSerializer.SerializeToElement(DateTime.UtcNow - age);
        descriptor.Properties[CimdClientResolver.RefreshedAtProperty] = then;
        descriptor.Properties[CimdClientResolver.JwksRefreshedAtProperty] = then;
        await applications.UpdateAsync(application, descriptor);
    }

    private static string Assertion(RSA key, string keyId) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        // OpenIddict refuses a plain "JWT" here (ID2089): a client assertion is typed.
        TokenType = "client-authentication+jwt",
        Issuer = ClientId,
        Audience = Audience,
        Claims = new Dictionary<string, object>
        {
            ["sub"] = ClientId,
            ["jti"] = Guid.NewGuid().ToString("N"),
        },
        IssuedAt = DateTime.UtcNow,
        NotBefore = DateTime.UtcNow.AddSeconds(-5),
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.RsaSha256),
    });

    private static string Jwks(params (RSA Key, string KeyId)[] keys) => JsonSerializer.Serialize(new
    {
        keys = keys.Select(k =>
        {
            var p = k.Key.ExportParameters(includePrivateParameters: false);
            return new Dictionary<string, string>
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["kid"] = k.KeyId,
                ["n"] = Base64Url(p.Modulus!),
                ["e"] = Base64Url(p.Exponent!),
            };
        }).ToArray(),
    });

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<HttpClient> SignedInAsync(EndpointFactory factory)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = Email,
                DisplayName = "Nils Consultant",
                PasswordHash = TestPasswords.Hash(Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = Regex.Match(await loginPage.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", Email),
            new KeyValuePair<string, string>("Password", Password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    /// <summary>Answers each registered URL with its JSON body, a 500 for a failed one, and 404 otherwise.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string?> _bodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _requests = new(StringComparer.Ordinal);

        public void Serve(string url, string body) { lock (_bodies) _bodies[url] = body; }

        public void Fail(string url) { lock (_bodies) _bodies[url] = null; }

        public int RequestsTo(string url) { lock (_bodies) return _requests.GetValueOrDefault(url); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_bodies)
            {
                _requests[url] = _requests.GetValueOrDefault(url) + 1;
                if (!_bodies.TryGetValue(url, out var body)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }

    /// <summary>Lets the factory's handler pipeline dispose its wrapper without disposing the shared stub.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
