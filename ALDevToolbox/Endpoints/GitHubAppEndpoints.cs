using System.Security.Cryptography;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using static ALDevToolbox.Endpoints.EndpointHelpers;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The GitHub App's two handshakes, modelled on <see cref="EntraAuthEndpoints"/>.
///
/// <list type="bullet">
/// <item><description><em>Install</em> (issue #620) — an org Admin posts to
/// <c>/admin/github/connect</c>, picks the organisation on GitHub, and GitHub
/// sends them back to <c>/github/setup</c> with the installation id.</description></item>
/// <item><description><em>Link</em> (issue #621) — any member posts to
/// <c>/account/github/link</c>, authorises the app as themselves, and GitHub
/// sends them back to <c>/signin-github</c> with a code the workbench trades for
/// a user-to-server token.</description></item>
/// </list>
///
/// <para><strong>Neither is a sign-in provider.</strong> No route here issues a
/// cookie or creates a user; all four run inside the caller's existing session,
/// which is also why none needs to cross the tenant fence — the acting
/// organisation and user come from the caller's own cookie. Microsoft Entra ID
/// remains the one federated sign-in. See <c>.design/github-integration.md</c>.</para>
/// </summary>
internal static class GitHubAppEndpoints
{
    /// <summary>Data Protection purpose for the install handshake's <c>state</c> parameter.</summary>
    public const string StateProtectionPurpose = "ALDevToolbox.GitHub.InstallState";

    /// <summary>
    /// Data Protection purpose for the account-link handshake's <c>state</c>.
    /// Separate from <see cref="StateProtectionPurpose"/> so a state minted for
    /// one handshake cannot be spent on the other, even though the two carry the
    /// same payload shape.
    /// </summary>
    public const string LinkStateProtectionPurpose = "ALDevToolbox.GitHub.LinkState";

    /// <summary>Where GitHub sends the admin back to after the install.</summary>
    public const string SetupCallbackPath = "/github/setup";

    /// <summary>
    /// Where GitHub sends a member back to after they authorise the app as
    /// themselves. This is the app's registered Callback URL, shown on
    /// <c>/site-admin/settings/github</c> — the handshake deliberately sends no
    /// <c>redirect_uri</c> of its own, so GitHub uses the registered one and
    /// there is nothing to keep in step across a reverse proxy.
    /// </summary>
    public const string LinkCallbackPath = "/signin-github";

    /// <summary>The tab both install routes redirect back to, in success and failure.</summary>
    private const string RepositoriesTab = "/admin/administration/repositories";

    /// <summary>The Account section the link routes redirect back to.</summary>
    private const string AccountReposSection = "/account/repos";

    /// <summary>
    /// The one place a member can be sent to link from and expect to be sent
    /// back: Administration -> Repositories, where connecting the organisation
    /// now needs the admin's own link first. Deliberately a closed vocabulary
    /// of one literal rather than a URL - a caller-supplied return address
    /// riding a handshake is how open redirects happen.
    /// </summary>
    public const string AdminRepositoriesReturn = "admin-repos";

    /// <summary>
    /// How long a started handshake stays valid. Long enough to read GitHub's
    /// permission screen, short enough that an abandoned state is not a
    /// standing invitation.
    /// </summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapGitHubAppEndpoints(this IEndpointRouteBuilder app)
    {
        // "Connect a GitHub organisation" on Administration -> Repositories.
        app.MapPost("/admin/github/connect", async (
            HttpContext ctx,
            SystemSettingsService settings,
            IDataProtectionProvider protection,
            IMemoryCache cache,
            IOrganizationContext org,
            IAntiforgery antiforgery,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var view = await settings.GetGitHubAppViewAsync(ct);
            if (!view.IsConfigured)
            {
                RedirectWithMessage(ctx, RepositoriesTab,
                    "GitHub is not set up on this server yet. Ask whoever runs AL Workbench to add it, then try again.");
                return;
            }

            var state = TryCreateState(StateProtectionPurpose, protection, cache, org, clock);
            if (state is null)
            {
                ctx.Response.Redirect(RouteConstants.Login);
                return;
            }

            ctx.Response.Redirect(
                $"https://github.com/apps/{Uri.EscapeDataString(view.AppSlug!)}/installations/new"
                + $"?state={Uri.EscapeDataString(state)}");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        // GitHub's post-install redirect. Runs inside the same admin's session,
        // so the acting organisation still comes from the cookie.
        app.MapGet(SetupCallbackPath, async (
            HttpContext ctx,
            GitHubAppClient github,
            GitHubConnectionService connection,
            IDataProtectionProvider protection,
            IMemoryCache cache,
            IOrganizationContext org,
            TimeProvider clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("GitHubAppSetup");

            if (!TryConsumeState(StateProtectionPurpose, ctx.Request.Query["state"].ToString(), protection, cache, org, clock))
            {
                logger.LogWarning("Rejected a GitHub install callback whose state did not validate.");
                // An org that is already connected has almost certainly just
                // revisited a used link, and telling it to press a Connect
                // button that isn't on the page would send it looking for one.
                var alreadyConnected = (await connection.GetStatusAsync(ct)).IsConnected;
                RedirectWithMessage(ctx, RepositoriesTab, alreadyConnected
                    ? "That setup link had already been used. Nothing changed - your GitHub organisation is still connected."
                    : "That link has expired or was already used. Start again from Connect a GitHub organisation.");
                return;
            }

            if (!long.TryParse(ctx.Request.Query["installation_id"].ToString(), out var installationId)
                || installationId <= 0)
            {
                // setup_action=request means an owner still has to approve the
                // install; GitHub sends no installation id in that case.
                RedirectWithMessage(ctx, RepositoriesTab,
                    "GitHub has not finished installing the app yet. An owner of your GitHub organisation needs to approve it - come back and connect once they have.");
                return;
            }

            try
            {
                var installation = await github.GetInstallationAsync(installationId, ct);
                await connection.ConnectAsync(installation, ct);
                ctx.Response.Redirect($"{RepositoriesTab}?{RouteConstants.OkQuery}=github-connected");
            }
            catch (PlanValidationException ex)
            {
                RedirectWithMessage(ctx, RepositoriesTab, ex.Errors.First().Value);
            }
            catch (GitHubAppNotConfiguredException)
            {
                RedirectWithMessage(ctx, RepositoriesTab,
                    "GitHub is not set up on this server yet. Ask whoever runs AL Workbench to add it, then try again.");
            }
            catch (GitHubApiException ex)
            {
                logger.LogWarning(ex,
                    "GitHub refused to describe installation {InstallationId} during setup.", installationId);
                RedirectWithMessage(ctx, RepositoriesTab, $"GitHub could not confirm the installation: {ex.Message}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        // "Connect GitHub" on Account -> Repository access. Any member, not
        // just admins: the link is about what that one person may see.
        app.MapPost("/account/github/link", async (
            HttpContext ctx,
            SystemSettingsService settings,
            IDataProtectionProvider protection,
            IMemoryCache cache,
            IOrganizationContext org,
            IAntiforgery antiforgery,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var app = await settings.GetGitHubAppViewAsync(ct);
            if (!app.IsConfigured || string.IsNullOrEmpty(app.ClientId) || !app.HasClientSecret)
            {
                RedirectToAccount(ctx,
                    "GitHub is not set up on this server yet. Ask whoever runs AL Workbench to finish setting it up, then try again.");
                return;
            }

            // Only the one literal survives; anything else is dropped rather than
            // followed. See AdminRepositoriesReturn.
            var form = await ctx.Request.ReadFormAsync(ct);
            var returnTo = form["Return"].ToString() == AdminRepositoriesReturn ? AdminRepositoriesReturn : null;

            var state = TryCreateState(LinkStateProtectionPurpose, protection, cache, org, clock, returnTo);
            if (state is null)
            {
                ctx.Response.Redirect(RouteConstants.Login);
                return;
            }

            // No redirect_uri: GitHub falls back to the app's registered
            // Callback URL, which is the one thing a deployment behind a proxy
            // can get wrong in only one place instead of two.
            ctx.Response.Redirect(
                "https://github.com/login/oauth/authorize"
                + $"?client_id={Uri.EscapeDataString(app.ClientId)}"
                + $"&state={Uri.EscapeDataString(state)}");
        }).RequireAuthorization();

        // GitHub's post-authorisation redirect for the account link. Runs inside
        // the member's own session: no cookie is issued here, and the state must
        // name the user whose cookie is on this request.
        app.MapGet(LinkCallbackPath, async (
            HttpContext ctx,
            GitHubAccessService access,
            IDataProtectionProvider protection,
            IMemoryCache cache,
            IOrganizationContext org,
            TimeProvider clock,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("GitHubAccountLink");

            if (!TryConsumeState(LinkStateProtectionPurpose, ctx.Request.Query["state"].ToString(),
                    protection, cache, org, clock, out var returnTo))
            {
                logger.LogWarning("Rejected a GitHub account-link callback whose state did not validate.");
                // No field-name prefix on this one: they pressed a button, and
                // "GitHub:" in front of it reads as a rejected form field.
                RedirectToAccountPlain(ctx,
                    "Connecting your GitHub account did not finish in time, or had already been done. Choose Connect GitHub to start again.");
                return;
            }

            var code = ctx.Request.Query["code"].ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                // GitHub sends error=access_denied when the person pressed
                // Cancel on the authorisation screen. That is a decision, not a
                // fault, so it gets a plain sentence.
                RedirectToAccount(ctx,
                    "GitHub was not connected. You can choose Connect GitHub whenever you want to try again.");
                return;
            }

            try
            {
                await access.LinkAsync(code, ct);
                var next = returnTo is null ? string.Empty : $"&return={returnTo}";
                ctx.Response.Redirect($"{AccountReposSection}?{RouteConstants.OkQuery}=github-linked{next}");
            }
            catch (PlanValidationException ex)
            {
                RedirectToAccount(ctx, ex.Errors.First().Value);
            }
            catch (GitHubAppNotConfiguredException)
            {
                RedirectToAccount(ctx,
                    "GitHub is not set up on this server yet. Ask whoever runs AL Workbench to finish setting it up, then try again.");
            }
            catch (GitHubApiException ex)
            {
                logger.LogWarning(ex, "GitHub refused to complete an account link.");
                RedirectToAccount(ctx,
                    $"GitHub could not finish connecting your account: {ex.Message}");
            }
        }).RequireAuthorization();

        app.MapPost("/account/github/unlink", async (
            HttpContext ctx,
            GitHubAccessService access,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await access.UnlinkAsync(ct);
                ctx.Response.Redirect($"{AccountReposSection}?{RouteConstants.OkQuery}=github-unlinked");
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("GitHubAccountLink").LogError(ex, "Failed to unlink a GitHub account.");
                RedirectToAccount(ctx,
                    "An unexpected error occurred. Your GitHub account is still connected.");
            }
        }).RequireAuthorization();

        return app;
    }

    private static string NonceCacheKey(string purpose, string nonce) => $"github-state:{purpose}:{nonce}";

    /// <summary>
    /// Redirects back to <paramref name="target"/> carrying a message. The
    /// Account section already has a query string, so the separator is chosen
    /// rather than assumed.
    /// </summary>
    private static void RedirectWithMessage(HttpContext ctx, string target, string message) =>
        ctx.Response.Redirect($"{target}{(target.Contains('?') ? '&' : '?')}{RouteConstants.MsgQuery}="
            + Uri.EscapeDataString(message));

    /// <summary>
    /// The same, back to the Account section. That page renders a message only
    /// when it is paired with the <c>err=</c> label naming what failed - the
    /// pair <see cref="EntraAuthEndpoints"/> redirects with - so the label is
    /// added here rather than left to seven call sites to remember.
    /// </summary>
    private static void RedirectToAccount(HttpContext ctx, string message) =>
        RedirectWithMessage(ctx, $"{AccountReposSection}?{RouteConstants.ErrQuery}=GitHub", message);

    /// <summary>
    /// The same, without the <c>err=</c> field label. For the things that went
    /// nowhere rather than went wrong - an abandoned handshake, a link the
    /// person came back to twice - where naming a field would invent a form
    /// they never filled in.
    /// </summary>
    private static void RedirectToAccountPlain(HttpContext ctx, string message) =>
        RedirectWithMessage(ctx, AccountReposSection, message);

    /// <summary>
    /// Mints a single-use <c>state</c> for one handshake: Data Protection
    /// ciphertext over <c>orgId|userId|nonce|issuedAt</c>, paired with a cache
    /// entry the callback consumes. Returns <see langword="null"/> when there is
    /// no signed-in user to bind it to.
    /// </summary>
    private static string? TryCreateState(
        string purpose,
        IDataProtectionProvider protection,
        IMemoryCache cache,
        IOrganizationContext org,
        TimeProvider clock,
        string? returnTo = null)
    {
        var orgId = org.CurrentOrganizationId;
        var userId = org.CurrentUserId;
        if (orgId is null || userId is null) return null;

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // Single-use: the callback consumes the cache entry, so a replayed
        // (or shared) callback URL finds nothing and is refused.
        cache.Set(NonceCacheKey(purpose, nonce), true, StateLifetime);
        // The optional fifth segment is where the person should be offered next,
        // sealed with the rest so it cannot be edited on the way past GitHub.
        // The install handshake passes none and keeps its four-part shape.
        var payload = $"{orgId.Value}|{userId.Value}|{nonce}|{clock.GetUtcNow().ToUnixTimeSeconds()}";
        if (returnTo is not null) payload += $"|{returnTo}";
        return protection.CreateProtector(purpose).Protect(payload);
    }

    /// <summary>
    /// Validates the round-tripped <c>state</c> and burns it. Five things have
    /// to hold: it decrypts under <paramref name="purpose"/> (so we minted it,
    /// for this handshake and not the other), its nonce is still in the cache
    /// (so it has not been used), it is inside <see cref="StateLifetime"/>, and
    /// it names the organisation and user whose cookie is on this request — a
    /// callback URL pasted into someone else's session must not connect anything.
    /// </summary>
    private static bool TryConsumeState(
        string purpose,
        string? state,
        IDataProtectionProvider protection,
        IMemoryCache cache,
        IOrganizationContext org,
        TimeProvider clock) =>
        TryConsumeState(purpose, state, protection, cache, org, clock, out _);

    private static bool TryConsumeState(
        string purpose,
        string? state,
        IDataProtectionProvider protection,
        IMemoryCache cache,
        IOrganizationContext org,
        TimeProvider clock,
        out string? returnTo)
    {
        returnTo = null;
        if (string.IsNullOrWhiteSpace(state)) return false;

        string payload;
        try
        {
            payload = protection.CreateProtector(purpose).Unprotect(state);
        }
        catch (CryptographicException)
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length is not (4 or 5)) return false;
        if (!int.TryParse(parts[0], out var orgId)) return false;
        if (!int.TryParse(parts[1], out var userId)) return false;
        if (!long.TryParse(parts[3], out var issuedAt)) return false;

        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAt);
        if (age < TimeSpan.Zero || age > StateLifetime) return false;
        if (org.CurrentOrganizationId != orgId || org.CurrentUserId != userId) return false;

        var key = NonceCacheKey(purpose, parts[2]);
        if (!cache.TryGetValue(key, out bool _)) return false;
        cache.Remove(key);
        // Sealed by us, but still narrowed to the one marker we honour: a
        // future segment must never become a redirect target by accident.
        returnTo = parts.Length == 5 && parts[4] == AdminRepositoriesReturn ? AdminRepositoriesReturn : null;
        return true;
    }
}
