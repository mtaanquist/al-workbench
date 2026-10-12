using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Data.Configurations;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// What the Account page needs to show about one person's GitHub link, without
/// any of the secret half.
/// </summary>
/// <param name="IsLinked">A link row exists for this user.</param>
/// <param name="Login">The GitHub login as it was at link time; display only.</param>
/// <param name="GitHubUserId">GitHub's stable numeric id, which is what we match on.</param>
/// <param name="IsOrgMember">
/// Whether the user was in the organisation's connected GitHub organisation last
/// time we asked. Null when there is no connected organisation, or GitHub would
/// not say - the row renders that as "we do not know" rather than as "no".
/// </param>
/// <param name="LinkedAt">When the link was made.</param>
/// <param name="NeedsRelink">
/// The stored credentials can no longer be used - a revoked authorisation, an
/// expired refresh token, or a key ring that lost the keys - so the only way
/// forward is to link again. The row says so instead of failing later inside a
/// feature.
/// </param>
public sealed record GitHubLinkStatus(
    bool IsLinked,
    string? Login,
    long? GitHubUserId,
    bool? IsOrgMember,
    DateTime? LinkedAt,
    bool NeedsRelink)
{
    /// <summary>The state of someone who has never linked.</summary>
    public static readonly GitHubLinkStatus NotLinked = new(false, null, null, null, null, false);
}

/// <summary>
/// Whether the acting user may connect a given installation of the App to their
/// organisation - the gate on <see cref="GitHubConnectionService.ConnectAsync"/>.
/// </summary>
public enum GitHubInstallationClaim
{
    /// <summary>The user has not linked a GitHub account, so nothing can be proven about them.</summary>
    NotLinked,

    /// <summary>The link exists but its credentials no longer work; they have to link again.</summary>
    LinkUnusable,

    /// <summary>GitHub could not be reached or refused to answer. Nothing may be concluded, so nothing is allowed.</summary>
    Unknown,

    /// <summary>GitHub did not list this installation among the ones the user administers.</summary>
    NotTheirs,

    /// <summary>GitHub confirmed the user administers this installation.</summary>
    Confirmed,
}

/// <summary>
/// The per-user half of the GitHub integration (issue #621): the account link
/// itself, the token pair behind it, and the three questions every later
/// feature asks about what one person may see.
///
/// <para><strong>This is a link, not a sign-in.</strong> Nothing here issues a
/// cookie or creates a <see cref="User"/>. Microsoft Entra ID remains the one
/// federated sign-in; a GitHub row in <c>user_external_logins</c> only ever
/// authorises, and every query that means "can sign in with Microsoft" filters
/// the provider out (see <see cref="UserExternalLogin"/>).</para>
///
/// <para><strong>Which credential acts.</strong> Reads and writes inside
/// somebody else's repository go out on <em>that person's</em> token, so GitHub
/// enforces their own permissions natively and we never have to get a
/// permission gate right ourselves. The installation token stays for org-level
/// acts. See <c>.design/github-integration.md</c>.</para>
///
/// <para><strong>Tenant fence.</strong> Every read and write is pinned to the
/// acting user through <see cref="IOrganizationContext"/>, and the query filter
/// on <c>user_external_logins</c> (nav-based, via the owning user's
/// organisation) scopes it further. No <c>IgnoreQueryFilters()</c> call: every
/// route into this service runs inside the user's own authenticated session.</para>
/// </summary>
public sealed class GitHubAccessService
{
    /// <summary>Provider discriminator stamped on the rows this service owns.</summary>
    public const string ProviderName = "github";

    /// <summary>
    /// Constant <c>issuer</c> for GitHub rows. GitHub has no tenant, but the
    /// <c>(provider, issuer, subject)</c> unique index predates this milestone
    /// and a constant keeps its shape without a schema change.
    /// </summary>
    public const string IssuerValue = "github.com";

    /// <summary>Data Protection purpose for a user's GitHub user-to-server access token.</summary>
    public const string AccessTokenProtectionPurpose = "ALDevToolbox.GitHubUserLink.AccessToken";

    /// <summary>Data Protection purpose for the refresh token that mints the next access token.</summary>
    public const string RefreshTokenProtectionPurpose = "ALDevToolbox.GitHubUserLink.RefreshToken";

    /// <summary>
    /// Refresh this far before the access token actually lapses, so a call that
    /// starts just under the wire does not finish with a dead token.
    /// </summary>
    private static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one answer about what a person may see is reused.
    ///
    /// <para>The design doc asks for per-request caching, and the reason is
    /// exactly right: a permission revoked on GitHub has to take effect on the
    /// next page load, not on the next deploy. But a Blazor Server scope is a
    /// <em>circuit</em>, which can outlive a working day, so an
    /// instance-lifetime dictionary would be the very staleness the rule bans.
    /// A short window instead collapses the burst of questions one page render
    /// asks - the repository picker checks every row - while guaranteeing the
    /// answer is re-asked long before anyone reloads.</para>
    /// </summary>
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly IOrganizationContext _orgContext;
    private readonly OrganizationConfigService _orgConfig;
    private readonly IDataProtector _accessProtector;
    private readonly IDataProtector _refreshProtector;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubAccessService> _logger;

    /// <summary>Answers from this instance, each valid for <see cref="AnswerWindow"/>. See its note.</summary>
    private readonly Dictionary<string, (bool Answer, DateTimeOffset AskedAt)> _answers = new(StringComparer.Ordinal);

    public GitHubAccessService(
        AppDbContext db,
        GitHubAppClient github,
        IOrganizationContext orgContext,
        OrganizationConfigService orgConfig,
        IDataProtectionProvider protectionProvider,
        TimeProvider clock,
        ILogger<GitHubAccessService> logger)
    {
        _db = db;
        _github = github;
        _orgContext = orgContext;
        _orgConfig = orgConfig;
        _accessProtector = protectionProvider.CreateProtector(AccessTokenProtectionPurpose);
        _refreshProtector = protectionProvider.CreateProtector(RefreshTokenProtectionPurpose);
        _clock = clock;
        _logger = logger;
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No user in scope; GitHubAccessService called outside an authenticated request.");

    // ── The link ────────────────────────────────────────────────────────────

    /// <summary>
    /// The acting user's GitHub link, or <see cref="GitHubLinkStatus.NotLinked"/>.
    /// Costs one query and never talks to GitHub, so the Account page renders
    /// even when GitHub is unreachable.
    /// </summary>
    public async Task<GitHubLinkStatus> GetLinkStatusAsync(CancellationToken ct = default)
    {
        var userId = _orgContext.CurrentUserId;
        if (userId is null) return GitHubLinkStatus.NotLinked;

        var row = await _db.UserExternalLogins.AsNoTracking()
            .FirstOrDefaultAsync(l => l.UserId == userId.Value && l.Provider == ProviderName, ct);
        if (row is null) return GitHubLinkStatus.NotLinked;

        return new GitHubLinkStatus(
            IsLinked: true,
            Login: row.DisplayIdentity,
            GitHubUserId: long.TryParse(row.Subject, out var id) ? id : null,
            IsOrgMember: row.IsOrgMember,
            LinkedAt: row.CreatedAt,
            NeedsRelink: !IsUsable(row));
    }

    /// <summary>
    /// Completes the OAuth handshake for the acting user: trades
    /// <paramref name="code"/> for a token pair, asks GitHub who it belongs to,
    /// and stores the link encrypted. Re-linking overwrites the existing row
    /// rather than adding a second one, so "link again" is always the way out
    /// of a broken link.
    ///
    /// <para>Field-keyed <see cref="PlanValidationException"/> on the one thing
    /// a person can act on themselves: the GitHub account they just signed in
    /// as already belongs to a colleague here.</para>
    /// </summary>
    /// <exception cref="GitHubAppNotConfiguredException">The deployment has no OAuth client id/secret.</exception>
    /// <exception cref="GitHubApiException">GitHub refused the exchange.</exception>
    public async Task LinkAsync(string code, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var tokens = await _github.ExchangeUserCodeAsync(code, ct);
        var identity = await _github.GetAuthenticatedUserAsync(tokens.AccessToken, ct);
        var subject = identity.Id.ToString();

        // Filtered: a signed-in user can only reach rows whose owner is in their
        // own organisation, and the predicate narrows that to themselves.
        var mine = await _db.UserExternalLogins
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == ProviderName, ct);

        // (provider, issuer, subject) is unique, so one GitHub account belongs to
        // one workbench user. Checking inside the filter deliberately: a clash with
        // someone in another organisation is not this user's business, and letting
        // the insert fail there surfaces as a plain refusal rather than telling
        // them a stranger exists.
        var takenByColleague = await _db.UserExternalLogins.AnyAsync(
            l => l.Provider == ProviderName && l.Subject == subject && l.UserId != userId, ct);
        if (takenByColleague)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["GitHubLink"] = $"The GitHub account {identity.Login} is already connected to someone else here. Sign in to GitHub as yourself, then try again.",
            });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var inserting = mine is null;
        if (mine is null)
        {
            mine = new UserExternalLogin
            {
                UserId = userId,
                Provider = ProviderName,
                Issuer = IssuerValue,
                CreatedAt = now,
            };
            _db.UserExternalLogins.Add(mine);
        }

        mine.Subject = subject;
        mine.DisplayIdentity = identity.Login;
        StoreTokens(mine, tokens);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (DbErrors.IsUniqueViolation(ex, UserExternalLoginConfiguration.IdentityIndexName))
        {
            // The colleague check above runs inside the tenant filter on purpose
            // (see its note), but the index it backstops is deployment-wide and
            // this table carries no organisation of its own. So the same GitHub
            // account held by someone in another organisation gets past the check
            // and loses here instead - on the insert, and on the re-link that
            // moves Subject onto an account another organisation claims.
            //
            // Translating it into the refusal the rest of this flow uses is what
            // keeps that from being the generic error page: the callback consumes
            // the handshake's one-shot state before calling in, so a 500 here
            // costs the user the retry as well as the answer. The message stays
            // deliberately vague - that a stranger exists elsewhere on this
            // server is not something to tell them.
            await UndoLinkAsync(mine, inserting, ct);
            _logger.LogWarning(ex,
                "User {UserId} tried to connect GitHub account {GitHubLogin} ({GitHubUserId}), which is already linked outside their organisation.",
                userId, identity.Login, identity.Id);
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["GitHubLink"] = $"The GitHub account {identity.Login} is already connected to another account. Sign in to GitHub as yourself, then try again.",
            });
        }

        // Best effort, and after the link is safely stored: knowing the answer is
        // a convenience on the Account row, not a condition of linking, and a
        // GitHub hiccup here must not undo the link the user just made.
        mine.IsOrgMember = await AskOrgMembershipAsync(tokens.AccessToken, identity.Login, ct);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "User {UserId} linked GitHub account {GitHubLogin} ({GitHubUserId}); org member: {IsOrgMember}.",
            userId, identity.Login, identity.Id, mine.IsOrgMember?.ToString() ?? "unknown");
    }

    /// <summary>
    /// Puts the change tracker back where it was after a link that could not be
    /// saved, so the refused attempt is not still pending on a context the rest
    /// of the request goes on using: a new row is forgotten, and an existing one
    /// is read back as the database still has it.
    /// </summary>
    private async Task UndoLinkAsync(UserExternalLogin row, bool inserting, CancellationToken ct)
    {
        if (inserting)
        {
            _db.Entry(row).State = EntityState.Detached;
            return;
        }
        await _db.Entry(row).ReloadAsync(ct);
    }

    /// <summary>
    /// Removes the acting user's link. The authorisation itself survives on
    /// GitHub until the person revokes it there, which is said on the page -
    /// deleting the row only stops the workbench holding a token.
    /// </summary>
    public async Task UnlinkAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var row = await _db.UserExternalLogins
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == ProviderName, ct);
        if (row is null)
        {
            _logger.LogInformation("User {UserId} asked to unlink GitHub but had no link.", userId);
            return;
        }

        _db.UserExternalLogins.Remove(row);
        await _db.SaveChangesAsync(ct);
        _answers.Clear();
        _logger.LogInformation("User {UserId} unlinked GitHub account {GitHubLogin}.", userId, row.DisplayIdentity);
    }

    // ── The questions every later feature asks ──────────────────────────────

    /// <summary>
    /// Whether <paramref name="userId"/> can see <paramref name="repoFullName"/>
    /// (<c>owner/name</c>) on GitHub, asked with that person's own token.
    /// <see langword="false"/> when they have no usable link, when GitHub says
    /// no, and when GitHub could not be asked - "we could not confirm" is never
    /// promoted to "yes". A caller that has something better to say about the
    /// last of those asks <see cref="TryCanAccessRepoAsync"/> instead.
    /// </summary>
    public async Task<bool> CanAccessRepoAsync(int userId, string repoFullName, CancellationToken ct = default) =>
        await TryCanAccessRepoAsync(userId, repoFullName, ct) ?? false;

    /// <summary>
    /// The same question as <see cref="CanAccessRepoAsync"/>, with the third
    /// answer kept: <see langword="null"/> means GitHub could not be asked (it
    /// did not answer, rate-limited the call, or failed), which is not the same as
    /// a no and is not remembered as one.
    /// </summary>
    public async Task<bool?> TryCanAccessRepoAsync(int userId, string repoFullName, CancellationToken ct = default)
    {
        var parts = (repoFullName ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        var key = $"repo:{userId}:{repoFullName!.ToLowerInvariant()}";
        if (TryRecall(key, out var remembered)) return remembered;

        var token = await ResolveUserTokenAsync(userId, ct);
        if (token is null) return Remember(key, false);

        try
        {
            return Remember(key, await _github.UserCanSeeRepositoryAsync(token, parts[0], parts[1], ct));
        }
        catch (Exception ex) when (IsGitHubUnreachable(ex, ct))
        {
            _logger.LogWarning(ex,
                "Could not check whether user {UserId} can see {RepoFullName}; GitHub gave no answer.",
                userId, repoFullName);
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="userId"/> can write to <paramref name="repoFullName"/>
    /// (<c>owner/name</c>) on GitHub, asked with that person's own token - the gate
    /// on publishing releases there, which itself runs on the installation token.
    /// <see langword="false"/> when they have no usable link or GitHub says no;
    /// <see langword="null"/> when GitHub could not be asked, which is not
    /// remembered. A renamed repository is followed to its new name.
    /// </summary>
    public async Task<bool?> TryCanPushRepoAsync(int userId, string repoFullName, CancellationToken ct = default)
    {
        var parts = (repoFullName ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        var key = $"push:{userId}:{repoFullName!.ToLowerInvariant()}";
        if (TryRecall(key, out var remembered)) return remembered;

        var token = await ResolveUserTokenAsync(userId, ct);
        if (token is null) return Remember(key, false);

        try
        {
            var access = await _github.GetUserRepositoryAccessAsync(token, parts[0], parts[1], ct);
            Remember($"repo:{userId}:{repoFullName.ToLowerInvariant()}", access.CanSee);
            return Remember(key, access.CanPush);
        }
        catch (Exception ex) when (IsGitHubUnreachable(ex, ct))
        {
            _logger.LogWarning(ex,
                "Could not check whether user {UserId} can write to {RepoFullName}; GitHub gave no answer.",
                userId, repoFullName);
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="userId"/> is a member of the organisation's
    /// connected GitHub organisation. <see langword="false"/> when the
    /// organisation has not connected one, when the user has no usable link,
    /// when GitHub says no, and when GitHub could not be asked - "we could not
    /// confirm" is never promoted to "yes". A caller that has something better
    /// to say about the last of those asks
    /// <see cref="TryGetOrgMembershipAsync"/> instead.
    /// </summary>
    public async Task<bool> IsOrgMemberAsync(int userId, CancellationToken ct = default) =>
        await TryGetOrgMembershipAsync(userId, ct) ?? false;

    /// <summary>
    /// The same question as <see cref="IsOrgMemberAsync"/>, with the third
    /// answer kept: <see langword="null"/> means GitHub could not be asked -
    /// no organisation is connected, the link is unusable, or the call did not
    /// come back - which is deliberately not the same as a definite no. The
    /// Account page words those two differently, and telling somebody an owner
    /// has to add them when GitHub simply timed out is advice they cannot act
    /// on.
    ///
    /// <para>Refreshes the stored answer on the link row so the Account page
    /// stays honest without a call of its own, and remembers only the answers
    /// GitHub actually gave: caching "could not ask" as a no would keep a
    /// single timeout in front of the user for the rest of the circuit, which
    /// is exactly what <see cref="CanAccessRepoAsync"/> avoids.</para>
    /// </summary>
    public async Task<bool?> TryGetOrgMembershipAsync(int userId, CancellationToken ct = default)
    {
        var orgLogin = (await _orgConfig.GetCurrentAsync(ct)).Settings.GitHubOrgLogin;
        if (string.IsNullOrWhiteSpace(orgLogin)) return null;

        var key = $"member:{userId}:{orgLogin}";
        if (TryRecall(key, out var remembered)) return remembered;

        var row = await _db.UserExternalLogins
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == ProviderName, ct);
        if (row is null) return Remember(key, false);

        // A link we cannot use is a settled no rather than an open question: the
        // Account row already tells that person to connect again, and nothing
        // about this organisation can be asked on their behalf until they do.
        var token = await ResolveTokenForAsync(row, ct);
        if (token is null) return Remember(key, false);

        var member = await AskOrgMembershipAsync(token, row.DisplayIdentity, ct);
        if (member is null) return null;

        if (row.IsOrgMember != member)
        {
            row.IsOrgMember = member;
            await _db.SaveChangesAsync(ct);
        }
        return Remember(key, member.Value);
    }

    /// <summary>
    /// Narrows <paramref name="repoFullNames"/> - typically everything the
    /// installation can see - to the ones <paramref name="userId"/> can see
    /// themselves, preserving the caller's order. One call to GitHub per
    /// repository, sequential: the picker's list is short and a burst of
    /// parallel calls is how you meet a secondary rate limit.
    /// </summary>
    public async Task<IReadOnlyList<string>> FilterAccessibleAsync(
        int userId, IEnumerable<string> repoFullNames, CancellationToken ct = default)
    {
        var candidates = repoFullNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];
        if (candidates.Count == 0) return [];

        // One resolve up front: no link means no repositories, and asking GitHub
        // once per row about a token we do not have is pure latency.
        if (await ResolveUserTokenAsync(userId, ct) is null)
        {
            _logger.LogInformation(
                "User {UserId} has no usable GitHub link, so none of the {RepositoryCount} repositories are offered.",
                userId, candidates.Count);
            return [];
        }

        var visible = new List<string>(candidates.Count);
        foreach (var name in candidates)
        {
            if (await CanAccessRepoAsync(userId, name, ct)) visible.Add(name);
        }

        _logger.LogInformation(
            "User {UserId} can see {VisibleCount} of {RepositoryCount} repositories in the connected organisation.",
            userId, visible.Count, candidates.Count);
        return visible;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may connect installation
    /// <paramref name="installationId"/> to their organisation, answered with
    /// that person's own token in two steps: <c>GET /user/installations</c>
    /// names the GitHub organisation the installation sits on, and
    /// <c>GET /user/memberships/orgs/{org}</c> says what they are in it. Only
    /// an <em>active owner</em> is
    /// <see cref="GitHubInstallationClaim.Confirmed"/>.
    ///
    /// <para>This is the gate that closes the installation-claim hole: the App
    /// JWT is authorised for every installation of the App, so the install
    /// callback cannot tell whose organisation came back from a hand-edited
    /// <c>installation_id</c>. Only the acting user's own credential can.</para>
    ///
    /// <para><strong>Why the second call.</strong> Being in
    /// <c>/user/installations</c> only means the person can reach one repository
    /// the installation covers - GitHub lists installations covering
    /// repositories they own, collaborate on, or see through an organisation.
    /// An outside collaborator on a single repository in a GitHub organisation
    /// that has installed the App would otherwise have been able to connect
    /// that organisation to their own workbench organisation, and mint its
    /// installation token for every repository the installation covers. The
    /// role is the thing that had to be checked, and only the memberships route
    /// reports it.</para>
    ///
    /// <para>An answer we could not get is
    /// <see cref="GitHubInstallationClaim.Unknown"/>, never a pass.</para>
    /// </summary>
    public async Task<GitHubInstallationClaim> CanAdministerInstallationAsync(
        int userId, long installationId, CancellationToken ct = default)
    {
        var row = await _db.UserExternalLogins
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == ProviderName, ct);
        if (row is null) return GitHubInstallationClaim.NotLinked;

        var token = await ResolveTokenForAsync(row, ct);
        if (token is null) return GitHubInstallationClaim.LinkUnusable;

        IReadOnlyList<GitHubUserInstallation> installations;
        try
        {
            installations = await _github.ListUserInstallationsAsync(token, ct);
        }
        catch (Exception ex) when (IsGitHubUnreachable(ex, ct))
        {
            _logger.LogWarning(ex,
                "Could not list the GitHub installations user {UserId} can reach; refusing the claim on installation {InstallationId}.",
                userId, installationId);
            return GitHubInstallationClaim.Unknown;
        }

        var installation = installations.FirstOrDefault(i => i.Id == installationId);
        if (installation is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to connect GitHub installation {InstallationId}, which GitHub does not list among the {InstallationCount} they can reach.",
                userId, installationId, installations.Count);
            return GitHubInstallationClaim.NotTheirs;
        }

        // A personal-account installation has no owners to be one of, and
        // ConnectAsync refuses it anyway. There is nothing to ask, so the claim
        // is refused rather than assumed.
        if (!installation.IsOrganization)
        {
            _logger.LogWarning(
                "User {UserId} tried to connect GitHub installation {InstallationId}, which sits on the {AccountType} account {AccountLogin}.",
                userId, installationId, installation.AccountType, installation.AccountLogin);
            return GitHubInstallationClaim.NotTheirs;
        }
        if (string.IsNullOrWhiteSpace(installation.AccountLogin))
        {
            _logger.LogWarning(
                "GitHub did not say which account installation {InstallationId} sits on, so user {UserId}'s claim on it cannot be checked.",
                installationId, userId);
            return GitHubInstallationClaim.Unknown;
        }

        GitHubOrgMembership? membership;
        try
        {
            membership = await _github.GetOrgMembershipAsync(token, installation.AccountLogin, ct);
        }
        catch (Exception ex) when (IsGitHubUnreachable(ex, ct))
        {
            _logger.LogWarning(ex,
                "Could not check user {UserId}'s role in {OrgLogin}; refusing the claim on installation {InstallationId}.",
                userId, installation.AccountLogin, installationId);
            return GitHubInstallationClaim.Unknown;
        }

        if (membership is { IsActiveAdmin: true }) return GitHubInstallationClaim.Confirmed;

        _logger.LogWarning(
            "User {UserId} tried to connect GitHub installation {InstallationId} on {OrgLogin}, where GitHub calls them {Role} ({State}).",
            userId, installationId, installation.AccountLogin,
            membership?.Role ?? "nothing", membership?.State ?? "no membership");
        return GitHubInstallationClaim.NotTheirs;
    }

    // ── Tokens ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The decrypted, in-date access token for <paramref name="userId"/>, or
    /// <see langword="null"/> when there is no link, the key ring can no longer
    /// read it, or the refresh token is gone too. Refreshing is transparent:
    /// callers never see the 8-hour lifetime.
    /// </summary>
    public async Task<string?> ResolveUserTokenAsync(int userId, CancellationToken ct = default)
    {
        var row = await _db.UserExternalLogins
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == ProviderName, ct);
        return row is null ? null : await ResolveTokenForAsync(row, ct);
    }

    /// <summary>
    /// The refresh half of <see cref="ResolveUserTokenAsync"/>, on a row the
    /// caller has already read (and which must be <em>tracked</em>, since a
    /// refresh writes the new pair back).
    /// </summary>
    private async Task<string?> ResolveTokenForAsync(UserExternalLogin row, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.AccessTokenEncrypted)) return null;

        var expiresAt = row.AccessTokenExpiresAt;
        var stillGood = expiresAt is null
            || expiresAt.Value - _clock.GetUtcNow().UtcDateTime > RefreshBefore;
        if (stillGood) return Decrypt(_accessProtector, row.AccessTokenEncrypted, row.UserId, "access");

        var refreshToken = string.IsNullOrEmpty(row.RefreshTokenEncrypted)
            ? null
            : Decrypt(_refreshProtector, row.RefreshTokenEncrypted, row.UserId, "refresh");
        if (refreshToken is null)
        {
            _logger.LogWarning(
                "The GitHub access token for user {UserId} has expired and there is no usable refresh token; they have to link again.",
                row.UserId);
            return null;
        }

        try
        {
            var refreshed = await _github.RefreshUserTokenAsync(refreshToken, ct);
            StoreTokens(row, refreshed);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Refreshed the GitHub access token for user {UserId}.", row.UserId);
            return refreshed.AccessToken;
        }
        catch (Exception ex) when (IsGitHubUnreachable(ex, ct))
        {
            _logger.LogWarning(ex, "Could not refresh the GitHub access token for user {UserId}.", row.UserId);
            return null;
        }
    }

    /// <summary>
    /// Encrypts a fresh pair onto the row. GitHub rotates the refresh token on
    /// every refresh, so both halves are written together or the next refresh
    /// fails with a token that has already been spent.
    /// </summary>
    private void StoreTokens(UserExternalLogin row, GitHubUserTokens tokens)
    {
        row.AccessTokenEncrypted = _accessProtector.Protect(tokens.AccessToken);
        row.RefreshTokenEncrypted = string.IsNullOrEmpty(tokens.RefreshToken)
            ? null
            : _refreshProtector.Protect(tokens.RefreshToken);
        row.AccessTokenExpiresAt = tokens.ExpiresAt?.UtcDateTime;
    }

    private string? Decrypt(IDataProtector protector, string cipher, int userId, string which)
    {
        try
        {
            return protector.Unprotect(cipher);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex,
                "Could not decrypt the GitHub {Which} token for user {UserId}; they have to link again.",
                which, userId);
            return null;
        }
    }

    /// <summary>
    /// A link is usable when we hold an access token that either does not
    /// expire or can still be renewed. Anything else is a link that will fail
    /// the first time a feature leans on it, which the Account row says up front.
    /// </summary>
    private bool IsUsable(UserExternalLogin row)
    {
        if (string.IsNullOrEmpty(row.AccessTokenEncrypted)) return false;
        if (row.AccessTokenExpiresAt is not DateTime expiresAt) return true;
        if (expiresAt - _clock.GetUtcNow().UtcDateTime > RefreshBefore) return true;
        return !string.IsNullOrEmpty(row.RefreshTokenEncrypted);
    }

    /// <summary>
    /// Asks GitHub whether <paramref name="login"/> is in the connected
    /// organisation. <see langword="null"/> means "we could not establish it" -
    /// no organisation connected, or GitHub would not answer - which is
    /// deliberately different from a definite no.
    /// </summary>
    private async Task<bool?> AskOrgMembershipAsync(string accessToken, string login, CancellationToken ct)
    {
        var orgLogin = (await _orgConfig.GetCurrentAsync(ct)).Settings.GitHubOrgLogin;
        if (string.IsNullOrWhiteSpace(orgLogin) || string.IsNullOrWhiteSpace(login)) return null;

        try
        {
            return await _github.UserIsOrgMemberAsync(accessToken, orgLogin, login, ct);
        }
        catch (Exception ex) when (IsGitHubUnreachable(ex, ct))
        {
            _logger.LogWarning(ex,
                "Could not check whether {GitHubLogin} is a member of the GitHub organisation {OrgLogin}.",
                login, orgLogin);
            return null;
        }
    }

    /// <summary>
    /// Whether an exception means "GitHub did not answer" rather than "this code
    /// is wrong". The subtle one is the client's 30-second timeout, which
    /// surfaces as <see cref="TaskCanceledException"/> - and so does the caller
    /// giving up, which must keep propagating. The caller's own token is what
    /// tells the two apart.
    /// </summary>
    private static bool IsGitHubUnreachable(Exception ex, CancellationToken ct) =>
        ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    // ── The short answer window (see AnswerWindow) ──────────────────────────

    private bool TryRecall(string key, out bool answer)
    {
        answer = false;
        if (!_answers.TryGetValue(key, out var entry)) return false;
        if (_clock.GetUtcNow() - entry.AskedAt > AnswerWindow)
        {
            _answers.Remove(key);
            return false;
        }
        answer = entry.Answer;
        return true;
    }

    private bool Remember(string key, bool answer)
    {
        _answers[key] = (answer, _clock.GetUtcNow());
        return answer;
    }
}
