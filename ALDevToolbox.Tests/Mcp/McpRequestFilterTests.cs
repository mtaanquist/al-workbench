using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
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
/// step-up, and a tool nobody marked still reaches the tool.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
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

        // The tool itself answers (no such build or pipeline); what matters is that
        // the step-up filter did not refuse it first.
        var reply = ReplyText(doc);
        reply.Should().NotBeEmpty(doc.RootElement.GetRawText());
        reply.Should().NotContain("confirm it's them");
        reply.Should().NotContain("personal access token");
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

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Posts one JSON-RPC request. Accept names JSON alone, so the gateway shim
    /// (<c>McpAcceptCompatibility</c>) hands back the SDK's reply unwrapped from
    /// its event stream.
    /// </summary>
    private static async Task<JsonDocument> RpcAsync(HttpClient client, string pat, string method, object? parameters)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
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
}
