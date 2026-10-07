using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using System.Threading.RateLimiting;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Startup;

/// <summary>
/// Operator-facing plumbing: the health checks behind /healthz, /readyz and
/// /healthz/workers, plus the rate-limiter policies.
/// </summary>
public static class OperationsRegistration
{
    /// <summary>Registers the health checks and the DCR rate-limiter policy.</summary>
    public static IServiceCollection AddOperations(this IServiceCollection services)
    {
        // Health checks (M21). /healthz is the live probe — green when the database
        // is reachable and the Data Protection key ring round-trips. /readyz is
        // distinct: it stays red until startup work (migrations + seed) has finished,
        // so reverse proxies don't send traffic mid-migration.
        services.AddSingleton<StartupReadinessState>();
        services.AddScoped<DatabaseHealthCheck>();
        services.AddSingleton<DataProtectionHealthCheck>();
        services.AddSingleton<StartupReadinessHealthCheck>();
        // Singleton registry shared by every BackgroundService — each worker registers
        // its own WorkerHeartbeat at construction and beats while running. The check
        // reads them out-of-band and is surfaced on its own /healthz/workers endpoint
        // (tag "workers"), NOT on /healthz: the Dockerfile HEALTHCHECK polls /healthz
        // and would otherwise kill an otherwise-serving container just because a
        // background job is slow, contradicting the documented liveness contract. See
        // issue #377.
        services.AddSingleton<WorkerHeartbeatRegistry>();
        services.AddSingleton<BackgroundWorkerHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "healthz" })
            .AddCheck<DataProtectionHealthCheck>("data-protection", tags: new[] { "healthz" })
            .AddCheck<BackgroundWorkerHealthCheck>("background-workers", tags: new[] { "workers" })
            .AddCheck<StartupReadinessHealthCheck>("startup", tags: new[] { "readyz" });

        // Rate limiter for the anonymous surfaces. /oauth/register (RFC 7591
        // Dynamic Client Registration) is AllowAnonymous and creates an
        // oauth_applications row per POST, so a trivial script could grow the table
        // without limit. A per-IP fixed window caps the drip. See issue #378.
        // POST /api/compare/diff is anonymous for a different reason — the Diff
        // tool is deliberately account-free — and costs CPU rather than rows, so
        // it gets its own policy below. See issue #673.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(OAuthEndpoints.DcrRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(10),
                        QueueLimit = 0,
                    }));

            // The Diff tool re-diffs on a 300 ms debounce as the reader types
            // (source-viewer.js), so the request rate a *person* can produce is
            // bounded by that debounce at a little over three per second, and
            // only if they type one character per pause for minutes on end. A
            // token bucket is the right shape for that traffic: it lets a burst
            // through (a paste, a Swap, a Clear and an Ignore-whitespace toggle
            // all fire immediately, undebounced) and then meters the steady
            // state. 40 tokens per 10 seconds is four per second sustained —
            // above the debounce's own ceiling, so interactive editing never
            // trips it — with a 60-token burst on top. Rejections are 429 and
            // the client says so; nothing is queued, because a diff that
            // arrives late has already been superseded by the next keystroke.
            options.AddPolicy(CompareEndpoints.DiffRateLimitPolicy, httpContext =>
                RateLimitPartition.GetTokenBucketLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 60,
                        TokensPerPeriod = 40,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                        AutoReplenishment = true,
                        QueueLimit = 0,
                    }));

            // GET /palette/search is authenticated, so it is not exposed the way
            // the three policies around it are - the limit is here because the
            // palette fires one request per 120 ms debounce while somebody types,
            // which is the one route in the app a person can hit tens of times in
            // a few seconds without meaning to. A token bucket fits that traffic:
            // a burst while a query is typed out, then a quiet minute.
            //
            // Partitioned by the caller's address rather than their user id,
            // although the route is authenticated: UseRateLimiter runs ahead of
            // UseAuthentication in Program.cs (it has to stay above
            // UseStatusCodePagesWithReExecute, or a 429 would be re-executed into
            // the not-found page), so there is no user_id claim to read here yet.
            // The budget below is sized for that: 30 per second sustained with a
            // 120 burst is more than a floor of people behind one address can
            // produce through a 120 ms debounce, and still bounds a script.
            options.AddPolicy(PaletteEndpoints.SearchRateLimitPolicy, httpContext =>
                RateLimitPartition.GetTokenBucketLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 120,
                        TokensPerPeriod = 60,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(2),
                        AutoReplenishment = true,
                        QueueLimit = 0,
                    }));

            // POST /github/webhook is the workbench's one inbound route (#627). It is
            // anonymous by necessity - GitHub carries no cookie - so the limiter is
            // the backstop against somebody pointing a load generator at it. The
            // window is generous on purpose: a busy organisation legitimately
            // produces a burst of pull_request deliveries when a branch with many
            // open pull requests is rebased, and a delivery we reject is one GitHub
            // shows the operator as a failure. Verification is a single HMAC over at
            // most a megabyte, or 25 MB for a push with a well-formed signature, which
            // the endpoint reads only a few at a time; the limit is there to bound the
            // cost, not to shape traffic.
            options.AddPolicy(GitHubWebhookEndpoints.WebhookRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });
        return services;
    }
}
