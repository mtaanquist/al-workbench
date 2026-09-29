namespace ALDevToolbox.Startup;

/// <summary>
/// The MCP server and its tool classes. Mounted at /mcp by McpEndpoints.
/// </summary>
public static class McpRegistration
{
    /// <summary>Registers the MCP options, availability cache, tool classes and transport.</summary>
    public static IServiceCollection AddMcp(this IServiceCollection services, IConfiguration configuration)
    {
        // MCP server (Model Context Protocol). Mounted at /mcp by McpEndpoints; the
        // PAT auth handler (see AuthenticationRegistration) turns Bearer tokens into
        // the same claim set the cookie handler does, so the tools rely on IOrganizationContext
        // resolving exactly like a browser sign-in. Tool classes live under
        // Services/Mcp/Tools/ and are picked up by WithToolsFromAssembly().
        services.Configure<ALDevToolbox.Services.Mcp.McpOptions>(configuration.GetSection("Mcp"));
        // In-memory MCP toggle cache. Singleton so NavMenu's per-render lookup
        // doesn't hit the DB and race with status-code-pages scope teardown. Primed
        // at startup and updated by SystemSettingsService.SaveAsync — see
        // Services/Mcp/IMcpAvailability.cs.
        services.AddSingleton<ALDevToolbox.Services.Mcp.McpAvailabilityState>();
        services.AddSingleton<ALDevToolbox.Services.Mcp.IMcpAvailability>(
            sp => sp.GetRequiredService<ALDevToolbox.Services.Mcp.McpAvailabilityState>());
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.WorkspaceTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.CookbookTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.ObjectExplorerTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.ArtifactsTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.DeliveryTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.DeliverTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.TranslatorTools>();
        services.AddScoped<ALDevToolbox.Services.Mcp.Tools.GitHubTools>();
        services
            .AddMcpServer()
            // Stateless Streamable-HTTP: each POST is self-contained, with no
            // Mcp-Session-Id. Protocol revision 2026-07-28 removed sessions from
            // Streamable HTTP outright (SEP-2567 dropped Mcp-Session-Id, SEP-2575
            // dropped the initialize handshake), so stateless is the SDK default and
            // the `Stateless = true` we used to pass here is a no-op. Our tools are
            // synchronous request/response with no server-initiated notifications, so
            // the default suits us; if a legacy client ever needs the handshake back,
            // that is HttpServerTransportOptions.SessionMode =
            // StatefulForInitializeClients.
            //
            // What that opt-in did NOT do, contrary to what this comment used to
            // claim: rescue gateway-fronted clients such as Copilot Studio, whose
            // Azure APIM layer narrows Accept to `application/json` and so trips the
            // SDK's unconditional "must accept both application/json and
            // text/event-stream" check with a 406. Measured on SDK 1.4.1 and 2.2.0,
            // with and without the opt-in: the 406 is identical in all four
            // combinations, and the SDK answers a conforming POST with an SSE body
            // either way. Whatever the opt-in once bought those clients, it had
            // stopped buying it well before the 2.x upgrade. That case is handled by
            // Endpoints/McpAcceptCompatibility.cs instead.
            .WithHttpTransport()
            .WithToolsFromAssembly()
            // Step-up for writing tools: an org can ask for a recent second
            // factor before a tool's writing MCP calls run (Administration →
            // Tools). A PAT can never satisfy that, and an OAuth session proved
            // it at consent (OAuthEndpoints.MapAuthorizeComplete), so the filter
            // only has to refuse PATs. Read-only tools are never gated; see
            // Domain/Tools/McpToolAccess.cs.
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, ct) =>
            {
                var tool = ALDevToolbox.Domain.Tools.McpToolAccess.ToolFor(request.Params?.Name ?? string.Empty);
                var services = request.Services ?? throw new InvalidOperationException("MCP call without request services.");
                // The transport sets request.User from the HTTP principal; the
                // accessor is the fallback so a future SDK change cannot turn
                // this check into a silent allow.
                var user = request.User
                    ?? services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
                if (tool is { } key && user?.HasClaim(c => c.Type == "pat_id") == true)
                {
                    var policy = services.GetRequiredService<ALDevToolbox.Services.Account.StepUpPolicy>();
                    if (await policy.RequiresStepUpAsync(key, ct))
                    {
                        throw new ModelContextProtocol.McpException(
                            $"{request.Params!.Name} needs a recent multi-factor sign-in, which a personal access token cannot provide. "
                            + "Connect this assistant through the permission screen on the MCP page instead of a token, then try again.");
                    }
                }
                return await next(request, ct);
            }));
        return services;
    }
}
