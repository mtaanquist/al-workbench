using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP request filters in <c>Startup/McpRegistration.cs</c>, driven through
/// the real <c>/mcp</c> endpoint (#1349). <see cref="McpToolGateTests"/> covers the
/// gate's answers and the OAuth consent check has its own tests; what is pinned
/// here is that the filters actually ask them: a switched-off tool is missing from
/// <c>tools/list</c> and refused by name on <c>tools/call</c>, a personal access
/// token is refused for a writing tool whose area the organisation marked for
/// step-up, an assistant connected through the permission screen gets through
/// only when that approval was made with a second factor, and a tool nobody
/// marked still reaches the tool.
/// </summary>
public sealed class McpRequestFilterTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Tools_list_leaves_out_a_tool_switched_off_for_the_organisation()
    {
        await EnableMcpAsync();
        await SetOrganisationAsync(disabled: [ToolKey.Releases]);
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var pat = await IssuePatAsync();

        using var doc = await RpcAsync(client, pat, "tools/list", null);

        var names = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain("list_solutions", "only Deployment pipelines was switched off");
        names.Should().NotContain("deploy_build");
        names.Should().NotContain("list_deployment_pipelines");
    }

    [Fact]
    public async Task Tools_call_refuses_a_switched_off_tool_by_name()
    {
        await EnableMcpAsync();
        await SetOrganisationAsync(disabled: [ToolKey.Releases]);
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var pat = await IssuePatAsync();

        using var doc = await RpcAsync(client, pat, "tools/call",
            new { name = "list_deployment_pipelines", arguments = new { } });

        Refusal(doc).Should().Contain("Deployment pipelines is switched off");
    }

    [Fact]
    public async Task A_personal_access_token_is_refused_for_a_writing_tool_marked_for_step_up()
    {
        await EnableMcpAsync();
        await SetOrganisationAsync(stepUp: [ToolKey.Releases]);
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var pat = await IssuePatAsync();

        using var doc = await RpcAsync(client, pat, "tools/call",
            new { name = "deploy_build", arguments = new { buildId = 1, deploymentPipelineId = 1 } });

        Refusal(doc).Should().Contain("personal access token cannot do");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_writing_tool_whose_area_is_not_marked_reaches_the_tool_with_a_personal_access_token(bool anotherAreaMarked)
    {
        await EnableMcpAsync();
        // deploy_build writes, so it gets past the "reads are never gated" check and
        // reaches the question of whether Deployment pipelines is marked. It is not:
        // either another area is, or none is.
        await SetOrganisationAsync(stepUp: anotherAreaMarked ? [ToolKey.Cookbook] : []);
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var pat = await IssuePatAsync();

        using var doc = await RpcAsync(client, pat, "tools/call",
            new { name = "deploy_build", arguments = new { buildId = 999999, deploymentPipelineId = 999999 } });

        // The tool itself answers (no such build or pipeline), so the step-up
        // filter did not refuse it first.
        ReplyText(doc).Should().Contain(ToolsOwnAnswer, doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task A_tool_nobody_marked_reaches_the_tool_with_a_personal_access_token()
    {
        await EnableMcpAsync();
        // Releases is marked, but list_solutions only reads, and reads are never gated.
        await SetOrganisationAsync(stepUp: [ToolKey.Releases]);
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        var pat = await IssuePatAsync();

        using var doc = await RpcAsync(client, pat, "tools/call",
            new { name = "list_solutions", arguments = new { } });

        var result = doc.RootElement.GetProperty("result");
        (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            .Should().BeFalse(doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task An_assistant_approved_without_a_second_factor_is_refused_for_a_writing_tool_marked_for_step_up()
    {
        await EnableMcpAsync();
        using var factory = new EndpointFactory(_db);
        var (_, accessToken) = await ConnectAssistantAsync(factory);
        // Marked after the approval, the case the check exists for: the assistant
        // was approved before the organisation asked for a second factor.
        await SetOrganisationAsync(stepUp: [ToolKey.Releases]);
        using var assistant = factory.CreateClient();

        using var doc = await RpcAsync(assistant, accessToken, "tools/call",
            new { name = "deploy_build", arguments = new { buildId = 999999, deploymentPipelineId = 999999 } });

        var refusal = Refusal(doc);
        refusal.Should().Contain("Approve this assistant again");
        refusal.Should().NotContain("personal access token", "an OAuth token is not a PAT");
    }

    [Fact]
    public async Task An_assistant_approved_with_a_second_factor_reaches_a_writing_tool_marked_for_step_up()
    {
        await EnableMcpAsync();
        using var factory = new EndpointFactory(_db);
        var (clientId, accessToken) = await ConnectAssistantAsync(factory);
        await StampConsentAsync(clientId, revoked: false);
        await SetOrganisationAsync(stepUp: [ToolKey.Releases]);
        using var assistant = factory.CreateClient();

        using var doc = await RpcAsync(assistant, accessToken, "tools/call",
            new { name = "deploy_build", arguments = new { buildId = 999999, deploymentPipelineId = 999999 } });

        // The tool itself answers (no such build or pipeline), so the step-up
        // filter did not refuse it first.
        ReplyText(doc).Should().Contain(ToolsOwnAnswer, doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task A_revoked_consent_is_refused_for_a_writing_tool_marked_for_step_up_even_with_a_second_factor()
    {
        await EnableMcpAsync();
        using var factory = new EndpointFactory(_db);
        var (clientId, accessToken) = await ConnectAssistantAsync(factory);
        // Revoking through the app also revokes the token, so /mcp would answer
        // 401 before the filter runs. Stamping the row alone reaches the filter
        // and pins that it ignores a revoked approval, second factor or not.
        await StampConsentAsync(clientId, revoked: true);
        await SetOrganisationAsync(stepUp: [ToolKey.Releases]);
        using var assistant = factory.CreateClient();

        using var doc = await RpcAsync(assistant, accessToken, "tools/call",
            new { name = "deploy_build", arguments = new { buildId = 999999, deploymentPipelineId = 999999 } });

        Refusal(doc).Should().Contain("Approve this assistant again");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// What deploy_build says when the build and pipeline do not exist: proof the
    /// call got past every filter to the tool itself.
    /// </summary>
    private const string ToolsOwnAnswer = "Couldn't deploy that build";

    /// <summary>
    /// Marks the assistant's consent as approved from a session with a recent
    /// second factor, which is what the authorize endpoint stamps, and optionally
    /// as revoked. The test context is scoped to the default organisation that
    /// owns the consent.
    /// </summary>
    private async Task StampConsentAsync(string clientId, bool revoked)
    {
        await using var ctx = _db.NewContext();
        var consent = await ctx.OAuthConsents.SingleAsync(c => c.ClientId == clientId);
        consent.StrongAuthAt = DateTime.UtcNow;
        if (revoked) consent.RevokedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Posts one JSON-RPC request. Accept names JSON alone, so the gateway shim
    /// (<c>McpAcceptCompatibility</c>) hands back the SDK's reply unwrapped from
    /// its event stream.
    /// </summary>
    private static async Task<JsonDocument> RpcAsync(HttpClient client, string bearer, string method, object? parameters)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// The text of a refused call. The SDK reports an <c>McpException</c> thrown
    /// from a filter either as a JSON-RPC error or as a tool result marked as an
    /// error, depending on version; either way the message is what the agent reads.
    /// </summary>
    private static string Refusal(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            return error.GetProperty("message").GetString() ?? string.Empty;
        }
        var result = root.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue(root.GetRawText());
        return string.Join("\n", result.GetProperty("content").EnumerateArray()
            .Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : null));
    }

    /// <summary>
    /// What the agent reads from any reply, refused or not: the JSON-RPC error
    /// message, or the text of the tool result's content, decoded (the raw JSON
    /// may carry an apostrophe as an escape).
    /// </summary>
    private static string ReplyText(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            return error.GetProperty("message").GetString() ?? string.Empty;
        }
        var result = root.GetProperty("result");
        if (!result.TryGetProperty("content", out var content)) return result.GetRawText();
        return string.Join("\n", content.EnumerateArray()
            .Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : null));
    }

    private async Task SetOrganisationAsync(ToolKey[]? disabled = null, ToolKey[]? stepUp = null)
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        org.McpEnabled = true;
        org.DisabledTools = ToolCatalog.Format(disabled ?? []);
        org.StepUpTools = ToolCatalog.Format(stepUp ?? []);
        await ctx.SaveChangesAsync();
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
            ReleaseDownloadDomainAllowlist: null, DisabledTools: Array.Empty<ToolKey>(),
            BuildConcurrency: null));
    }

    private async Task<string> IssuePatAsync()
    {
        await using var ctx = _db.NewContext();
        var user = new Domain.Entities.User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"mcp-filter-{Guid.NewGuid():N}@cronus.example",
            DisplayName = "MCP Filter Tester",
            PasswordHash = "ignored",
            Role = Domain.Entities.UserRole.Admin,
            Status = Domain.Entities.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var svc = new PersonalAccessTokenService(ctx, TimeProvider.System, NullLogger<PersonalAccessTokenService>.Instance);
        return (await svc.IssueAsync(user.Id, user.OrganizationId, "test", expiresAt: null)).Plaintext;
    }

    private const string RedirectUri = "http://127.0.0.1:41999/callback";

    /// <summary>
    /// Connects an assistant the way a real one does: an admin signs in with a
    /// password (no second factor), registers a client, approves it on the
    /// permission screen and the client redeems the code. Returns the client id
    /// and the access token. Run before anything is marked for step-up, or the
    /// permission screen itself asks for a second factor.
    /// </summary>
    private async Task<(string ClientId, string AccessToken)> ConnectAssistantAsync(EndpointFactory factory)
    {
        const string password = "correct horse battery staple";
        var email = $"mcp-oauth-{Guid.NewGuid():N}@cronus.example";
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new Domain.Entities.User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = email,
                DisplayName = "MCP OAuth Tester",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(password),
                Role = Domain.Entities.UserRole.Admin,
                Status = Domain.Entities.UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        using var browser = factory.CreateClient();
        using var loginPage = await browser.GetAsync("/login");
        using var login = await browser.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = AntiforgeryToken(await loginPage.Content.ReadAsStringAsync()),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");

        var registration = JsonSerializer.Serialize(new { redirect_uris = new[] { RedirectUri }, client_name = "CRONUS assistant" });
        using var registered = await browser.PostAsync("/oauth/register",
            new StringContent(registration, Encoding.UTF8, "application/json"));
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registeredDoc = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var clientId = registeredDoc.RootElement.GetProperty("client_id").GetString()!;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var authorize = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["state"] = "cronus",
        };
        using var consentRedirect = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", authorize));
        consentRedirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var consentPage = await browser.GetAsync(consentRedirect.Headers.Location!);
        consentPage.StatusCode.Should().Be(HttpStatusCode.OK);

        var approval = new Dictionary<string, string>(authorize.Select(kv => KeyValuePair.Create(kv.Key, kv.Value!)))
        {
            ["decision"] = "allow",
            ["__RequestVerificationToken"] = AntiforgeryToken(await consentPage.Content.ReadAsStringAsync()),
        };
        using var approved = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(approval));
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = QueryHelpers.ParseQuery(approved.Headers.Location!.Query)["code"].ToString();
        code.Should().NotBeEmpty();

        using var exchanged = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }));
        var exchangedBody = await exchanged.Content.ReadAsStringAsync();
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK, exchangedBody);
        using var tokenDoc = JsonDocument.Parse(exchangedBody);
        return (clientId, tokenDoc.RootElement.GetProperty("access_token").GetString()!);
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
