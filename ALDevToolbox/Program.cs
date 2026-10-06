using ALDevToolbox.Components;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Startup;

var builder = WebApplication.CreateBuilder(args);

// Legacy C/AL TXT exports are Windows-1252 / codepage 850, neither of which
// .NET Core ships by default — register the code-pages provider so
// CalImportService can decode them. See Services/Cal/.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = false;
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});

// Service registration — one Add* per area, each in its own file under
// Startup/. New registrations go into the matching Add* method rather than
// back into this file. The call order below is the order these ran in when
// they were inline, which keeps hosted services starting in the same order.
builder.Services.AddAppCore();
var trustedProxies = builder.Services.AddForwardedHeaders();
var publicOrigin = builder.Services.AddPublicOrigin();
var singleTenantMode = builder.Services.AddSingleTenantMode();
builder.Services.AddAppAuthentication();
builder.Services.AddAppDatabase(builder.Configuration);
builder.Services.AddContent();
builder.Services.AddCookbook();
builder.Services.AddTranslator();
builder.Services.AddObjectExplorer();
builder.Services.AddBcQuality();
builder.Services.AddGeneration();
builder.Services.AddOrganizationServices();
builder.Services.AddGitHub();
builder.Services.AddAccountServices(builder.Configuration);
builder.Services.AddOAuthServer(builder.Environment);
builder.Services.AddDeploymentIdentity();
builder.Services.AddMcp(builder.Configuration);
builder.Services.AddToolAvailability();
builder.Services.AddSiteAdmin();
builder.Services.AddPalette();
builder.Services.AddBackgroundWorkers(singleTenantMode);
builder.Services.AddDataProtectionKeyRing();
builder.Services.AddOperations();

var app = builder.Build();

ForwardedHeadersSetup.Log(app.Logger, trustedProxies);
PublicOrigin.Log(app.Logger, publicOrigin);

app.UseForwardedHeaders();

// Baseline security response headers (nosniff, Referrer-Policy,
// X-Frame-Options and a CSP). Registered this early so *every* response
// carries them - static assets, health probes, error pages, and the
// short-circuiting middleware further down. See Endpoints/SecurityHeaders.cs
// and issue #677.
app.UseSecurityHeaders();

// After UseForwardedHeaders so the per-IP partition sees the real client IP.
app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// MCP runtime kill-switch: short-circuits /mcp requests to 404 when the
// SiteAdmin has the toggle off. Runs ahead of authentication/authorization
// and the antiforgery middleware so off-state isn't masked by an earlier
// 400/401. See Endpoints/McpEndpoints.cs.
app.UseMcpKillSwitch();
// Accept-header shim for gateway-fronted MCP clients. Sits after the
// kill-switch (so a disabled deployment 404s without buffering) and ahead of
// authentication, because it has to wrap the whole downstream response.
// See Endpoints/McpAcceptCompatibility.cs.
app.UseMcpAcceptCompatibility();

app.UseAuthentication();

// Tool visibility gate: 404 a disabled tool's end-user routes (site-wide or
// per-org) so a hidden tool isn't reachable by typing its URL. Runs after
// authentication so the org_disabled_tools claim is available, but *before*
// authorization so a disabled auth-gated tool 404s rather than redirecting an
// anonymous visitor to /login. Ahead of routing-based auth, so the 404
// re-executes /not-found. See Endpoints/ToolAccessGate.cs.
app.UseToolAccessGate();

app.UseAuthorization();
app.UseAntiforgery();

// Per-org strong-auth gate. When an org has RequireStrongAuth=true, any
// authenticated request from a member of that org who hasn't yet enrolled
// TOTP, email-MFA, or a passkey is redirected to /account/security?required=1 (or
// gets a 403 for non-GET). Runs after authentication so it can read the
// cookie's user_id claim. See Endpoints/StrongAuthGate.cs.
app.UseStrongAuthGate();

// Maintenance mode (M18): 503 every non-SiteAdmin request while a restore
// is mid-flight. See Endpoints/MaintenanceModeMiddleware.cs.
app.UseMaintenanceMode();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// /healthz: liveness probe — green when the database is reachable and the
// Data Protection key ring round-trips. /readyz: readiness probe — only
// green once startup work (migrations + seed) has finished.
app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("healthz"),
});
app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("readyz"),
});
// /healthz/workers: background-worker liveness, for operator alerting only.
// Deliberately separate from /healthz so a stuck import / wedged scheduler
// doesn't trigger a container restart (the HEALTHCHECK polls /healthz). See #377.
app.MapHealthChecks("/healthz/workers", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("workers"),
});

// Endpoint groups (see Endpoints/ — one extension per concern).
app.MapGenerationEndpoints();
app.MapArtifactEndpoints();
app.MapTranslatorEndpoints();
app.MapAdminEndpoints();
app.MapAccountEndpoints();
app.MapNotificationEndpoints();
app.MapEntraAuthEndpoints();
app.MapStepUpEndpoints();
app.MapGitHubAppEndpoints();
app.MapGitHubWebhookEndpoints();
app.MapAdminUserEndpoints();
app.MapObjectExplorerEndpoints();
app.MapCookbookEndpoints();
app.MapCompareEndpoints();
app.MapPaletteEndpoints();
app.MapLegacyRedirects();
app.MapSiteAdminEndpoints();
app.MapMcpEndpoints();
app.MapOAuthEndpoints();

// Run migrations + bootstrap, then flip /readyz to green.
await StartupTasks.RunAsync(app);

app.Run();

public partial class Program { }
