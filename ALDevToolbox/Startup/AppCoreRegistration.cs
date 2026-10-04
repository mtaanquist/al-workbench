using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using Microsoft.AspNetCore.HttpOverrides;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Startup;

/// <summary>
/// Framework plumbing and the process-wide singletons that don't belong to any
/// one tool: Razor Components, forwarded headers, the public origin, the icon
/// catalogue, the Markdown pipeline and email.
/// </summary>
public static class AppCoreRegistration
{
    /// <summary>Razor Components plus the cross-cutting singletons and per-request context.</summary>
    public static IServiceCollection AddAppCore(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddHttpContextAccessor();
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<CacheBust>();
        // IconCatalog reads the vendored Lucide SVGs from embedded resources once
        // at startup; singleton so we pay the parse cost a single time per process.
        services.AddSingleton<IconCatalog>();
        // MarkdownRenderer builds the Markdig pipeline once on construction and
        // reuses it for every render; safe as a singleton, no per-request state.
        services.AddSingleton<MarkdownRenderer>();
        // MaintenanceModeState is a process-local flag — singleton lifetime so the
        // middleware and BackupService share the same instance.
        services.AddSingleton<MaintenanceModeState>();
        // Email shares the AppDbContext lifetime (Scoped) so it can read the
        // hybrid SMTP override from system_settings. Callers get the outbox
        // decorator, not the transport: it writes most messages to email_outbox
        // for EmailOutboxScheduler to deliver and retry, so a failed send is
        // visible instead of a log warning nobody reads (issue #790).
        // SmtpEmailService stays registered as itself because the decorator and
        // the drain both send through it.
        services.AddScoped<SmtpEmailService>();
        services.AddScoped<EmailOutbox>();
        services.AddScoped<IEmailService>(sp => new OutboxEmailService(
            sp.GetRequiredService<SmtpEmailService>(),
            sp.GetRequiredService<EmailOutbox>(),
            sp.GetRequiredService<IOrganizationContext>()));
        // Renders the email components in Components/Email to HTML and text.
        services.AddScoped<EmailRenderer>();
        // Who gets which notification, and how. See .design/notifications.md.
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<InAppNotificationService>();
        services.AddScoped<BuildNotifier>();
        services.AddScoped<DeploymentNotifier>();
        services.AddScoped<UpgradeActionNotifier>();
        services.AddScoped<NotificationDigestService>();
        return services;
    }

    /// <summary>
    /// Forwarded-headers — production runs behind a TLS-terminating proxy
    /// (Traefik / nginx / Caddy). See <c>.design/auth-and-audit.md</c>.
    /// Only proxies listed in TRUSTED_PROXIES (plus the framework's loopback
    /// defaults) may set X-Forwarded-For; otherwise any client could choose its own
    /// rate-limit partition key and forge login_attempts.ip. See issue #672 and
    /// <c>Endpoints/ForwardedHeadersSetup.cs</c>. Returns the trusted-proxy list so
    /// the caller can log it once the app is built.
    /// </summary>
    public static ForwardedHeadersSetup.TrustedProxies AddForwardedHeaders(this IServiceCollection services)
    {
        var trustedProxies = ForwardedHeadersSetup.FromEnvironment();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            ForwardedHeadersSetup.Apply(options, trustedProxies);
        });
        return trustedProxies;
    }

    /// <summary>
    /// Public origin — credential-bearing email links must not be built from the
    /// inbound Host header (issue #670). See <c>Endpoints/PublicOrigin.cs</c>.
    /// Returns the resolved origin so the caller can log it once the app is built.
    /// </summary>
    public static PublicOrigin AddPublicOrigin(this IServiceCollection services)
    {
        var publicOrigin = PublicOrigin.FromEnvironment();
        services.AddSingleton(publicOrigin);
        return publicOrigin;
    }

    /// <summary>
    /// Single-tenant deployment flag. Fixed at boot from SINGLE_TENANT_MODE — an
    /// immutable singleton (no DB priming) that hides and disables the
    /// multi-tenant surfaces (storage quotas, per-tenant snapshots, self-service
    /// org creation at signup). See Services/SingleTenant/ISingleTenantMode.cs.
    /// Returns the flag because a couple of registrations are conditional on it.
    /// </summary>
    public static bool AddSingleTenantMode(this IServiceCollection services)
    {
        var singleTenantMode = Environment.GetEnvironmentVariable("SINGLE_TENANT_MODE") == "1";
        services.AddSingleton<ALDevToolbox.Services.SingleTenant.ISingleTenantMode>(
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(singleTenantMode));
        return singleTenantMode;
    }

    /// <summary>
    /// Site-wide per-tool toggles — a cached singleton so the sidebar and the
    /// route-access gate read them without a per-request DB hit.
    /// </summary>
    public static IServiceCollection AddToolAvailability(this IServiceCollection services)
    {
        // Primed at startup and updated by SystemSettingsService.SaveAsync. See
        // Services/Tools/IToolAvailability.cs.
        services.AddSingleton<ALDevToolbox.Services.Tools.ToolAvailabilityState>();
        services.AddSingleton<ALDevToolbox.Services.Tools.IToolAvailability>(
            sp => sp.GetRequiredService<ALDevToolbox.Services.Tools.ToolAvailabilityState>());
        // The site toggle plus the acting organisation's own opt-out, for the
        // callers that are not a rendered page (MCP above all). Scoped: it
        // holds the DbContext and caches the organisation's set per request.
        services.AddScoped<ALDevToolbox.Services.Tools.ToolEnablement>();
        return services;
    }
}
