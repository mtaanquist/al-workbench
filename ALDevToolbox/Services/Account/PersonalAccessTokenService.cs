using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ALDevToolbox.Services.Email;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Issues and validates Personal Access Tokens (PATs) — the bearer
/// credential the MCP server (and any future non-interactive caller) uses
/// in place of the browser auth cookie. The plain-text token is returned
/// exactly once at <see cref="IssueAsync"/>; only its SHA-256 hash is
/// persisted, so a database snapshot does not yield usable credentials.
/// </summary>
public sealed class PersonalAccessTokenService
{
    /// <summary>Prefix every issued token carries. Identifies PATs in logs and lets us recognise them in <c>Authorization</c> headers.</summary>
    public const string TokenPrefix = "aldt_pat_";

    /// <summary>Length of the random portion (base64url chars) after the prefix. 32 chars ≈ 192 bits of entropy.</summary>
    public const int RandomPortionLength = 32;

    /// <summary>How long a freshly-issued token lives by default when the caller doesn't specify an expiry.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// The row state a token should be drawn with: <c>active</c>, <c>revoked</c>
    /// or <c>expired</c>. Both token tables — the owner's own on /account and the
    /// cross-org one on /site-admin/connections/access-tokens — feed it to
    /// <c>RowStateIcon</c>, and they must never disagree about what "dead" means.
    /// </summary>
    public static string RowState(PersonalAccessToken token, DateTime utcNow)
    {
        if (token.RevokedAt is not null) return "revoked";
        if (token.ExpiresAt is { } expiresAt && expiresAt <= utcNow) return "expired";
        return "active";
    }

    /// <summary>True while a token can still authenticate a request.</summary>
    public static bool IsActiveAt(PersonalAccessToken token, DateTime utcNow)
        => RowState(token, utcNow) == "active";

    /// <summary>Skip writing <see cref="PersonalAccessToken.LastUsedAt"/> if it was already touched within this window — avoids write amplification under heavy MCP traffic.</summary>
    private static readonly TimeSpan LastUsedUpdateThrottle = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<PersonalAccessTokenService> _logger;

    private readonly IEmailService _email;
    private readonly EmailRenderer _emailRenderer;

    public PersonalAccessTokenService(
        AppDbContext db, TimeProvider clock, IEmailService email, EmailRenderer emailRenderer,
        ILogger<PersonalAccessTokenService> logger)
    {
        _db = db;
        _clock = clock;
        _email = email;
        _emailRenderer = emailRenderer;
        _logger = logger;
    }

    /// <summary>
    /// Mints a new PAT for <paramref name="userId"/> in <paramref name="orgId"/>.
    /// The plain-text token in the return value is the only place it ever
    /// appears — the caller must show it to the user immediately and then
    /// discard it. Throws <see cref="PlanValidationException"/> when
    /// <paramref name="name"/> is empty or longer than 80 characters.
    /// <para>
    /// When <paramref name="noticeUrl"/> is given (the person's AI assistants
    /// tab), the owner is emailed that a token was made, with a link there:
    /// a token acts as them until it expires or is revoked, so, like a new
    /// passkey, one they did not make must not go unnoticed (#1627).
    /// </para>
    /// </summary>
    public async Task<IssuedToken> IssueAsync(
        int userId, int orgId, string name, DateTime? expiresAt, CancellationToken ct = default, string? noticeUrl = null)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            errors["Name"] = "Give the token a recognisable name.";
        }
        else if (trimmed.Length > 80)
        {
            errors["Name"] = "Name is too long (80 characters max).";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var now = _clock.GetUtcNow().UtcDateTime;
        if (expiresAt is { } exp && exp <= now)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ExpiresAt"] = "Expiry must be in the future.",
            });
        }

        var (plaintext, hash) = GenerateTokenAndHash();
        var row = new PersonalAccessToken
        {
            UserId = userId,
            OrganizationId = orgId,
            Name = trimmed,
            TokenHash = hash,
            TokenPrefix = plaintext[..12],
            Scopes = "mcp",
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        _db.PersonalAccessTokens.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Issued personal access token {TokenId} (prefix {TokenPrefix}) for user {UserId} in org {OrgId}, expires {ExpiresAt}",
            row.Id, row.TokenPrefix, userId, orgId, expiresAt);

        if (noticeUrl is not null) await NotifyCreatedAsync(userId, row, noticeUrl);
        return new IssuedToken(row.Id, plaintext, row.TokenPrefix, row.CreatedAt, row.ExpiresAt);
    }

    /// <summary>
    /// Queues the "an access token was created" email. A failure is logged,
    /// not thrown: the token is already saved and the person is about to copy
    /// it, as with the passkey-added email (<c>PasskeyService.NotifyAddedAsync</c>).
    /// </summary>
    private async Task NotifyCreatedAsync(int userId, PersonalAccessToken token, string tokensUrl)
    {
        try
        {
            // Filtered: the token's own user, inside the organisation it was made in.
            var owner = await _db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstAsync(CancellationToken.None);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var content = await AccessTokenCreatedEmail.RenderAsync(_emailRenderer,
                owner.DisplayName, token.Name,
                token.CreatedAt.ToString("d MMM yyyy, HH:mm 'UTC'", culture),
                token.ExpiresAt?.ToString("d MMM yyyy", culture),
                tokensUrl, CancellationToken.None);
            await _email.SendAsync(owner.Email, content, EmailPurpose.AccessTokenCreated, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue the access-token email for user {UserId}.", userId);
        }
    }

    /// <summary>
    /// Validates a bearer token and returns the claims to mount on the
    /// authenticated principal. Returns <c>null</c> for unknown, expired,
    /// revoked, or malformed tokens. Runs with
    /// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
    /// because no <see cref="IOrganizationContext"/> is mounted yet — the
    /// validator's job is to discover which one to mount.
    /// </summary>
    public async Task<PatPrincipal?> ValidateAsync(string plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext) || !plaintext.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = HashHex(plaintext);
        var row = await _db.PersonalAccessTokens
            // Fence category 1 (pre-auth routing): bearer-token validation discovers which org
            // to mount; pinned to the token hash.
            .IgnoreQueryFilters()
            .Include(p => p.User)
            .Include(p => p.Organization)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TokenHash == hash, ct);
        if (row is null || row.User is null || row.Organization is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (row.RevokedAt is not null || (row.ExpiresAt is { } exp && exp <= now))
        {
            return null;
        }
        if (row.User.Status != UserStatus.Active)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(row.TokenHash),
                Encoding.ASCII.GetBytes(hash)))
        {
            // Defensive: the unique-index lookup above already implies a match,
            // but the constant-time compare keeps the contract explicit.
            return null;
        }

        // Touch LastUsedAt — throttled so heavy MCP traffic doesn't write on every call.
        if (row.LastUsedAt is null || row.LastUsedAt < now - LastUsedUpdateThrottle)
        {
            try
            {
                await _db.PersonalAccessTokens
                    // Fence category 1 (pre-auth routing): last-used touch on the row just validated;
                    // pinned to p.Id == row.Id.
                    .IgnoreQueryFilters()
                    .Where(p => p.Id == row.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastUsedAt, now), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update last_used_at for personal access token {TokenId}", row.Id);
            }
        }

        return new PatPrincipal(
            TokenId: row.Id,
            UserId: row.UserId,
            OrganizationId: row.OrganizationId,
            DisplayName: row.User.DisplayName,
            Email: row.User.Email,
            Role: row.User.Role,
            IsSiteAdmin: row.User.IsSiteAdmin,
            IsSystemOrganization: row.Organization.IsSystem);
    }

    /// <summary>Lists all of <paramref name="userId"/>'s tokens (current and revoked), newest first.</summary>
    public async Task<IReadOnlyList<PersonalAccessToken>> ListForUserAsync(int userId, CancellationToken ct = default)
    {
        return await _db.PersonalAccessTokens
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Lists every token across every organisation, newest first. Caller
    /// must verify SiteAdmin authorisation; the service skips the org query
    /// filter so the SiteAdmin oversight page can see other tenants' tokens.
    /// </summary>
    public async Task<IReadOnlyList<PersonalAccessToken>> ListAllAsync(CancellationToken ct = default)
    {
        return await _db.PersonalAccessTokens
            // Fence category 2 (SiteAdmin cross-org console): only caller is
            // /site-admin/access-tokens, gated by [Authorize(Roles = SiteAdminRole)].
            .IgnoreQueryFilters()
            .Include(p => p.User)
            .Include(p => p.Organization)
            .OrderByDescending(p => p.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stamps the token's <see cref="PersonalAccessToken.RevokedAt"/>. No-op
    /// when the row doesn't exist (per the org query filter) or is already
    /// revoked. Pass <paramref name="ignoreOrgScope"/> = true from SiteAdmin
    /// paths where the actor isn't in the token's organisation.
    /// <para><paramref name="forUserId"/> scopes the revoke to one user's own
    /// tokens — the self-service path passes the caller's id so a member can't
    /// revoke another member's PAT in the same org by guessing its id (the org
    /// filter alone isn't enough; tokens are visible org-wide). See issue #375.</para>
    /// </summary>
    public async Task RevokeAsync(int id, bool ignoreOrgScope = false, int? forUserId = null, CancellationToken ct = default)
    {
        var query = _db.PersonalAccessTokens.AsQueryable();
        // Fence category 2 (SiteAdmin cross-org console): opt-in bypass; only the SiteAdmin
        // page passes ignoreOrgScope, self-service callers stay inside the filter.
        if (ignoreOrgScope) query = query.IgnoreQueryFilters();
        if (forUserId is int uid) query = query.Where(p => p.UserId == uid);
        var row = await query.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null || row.RevokedAt is not null) return;
        row.RevokedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Revoked personal access token {TokenId} (prefix {TokenPrefix})", row.Id, row.TokenPrefix);
    }

    /// <summary>Generates a fresh <c>aldt_pat_&lt;random&gt;</c> token and its hex SHA-256.</summary>
    private static (string Plaintext, string Hash) GenerateTokenAndHash()
    {
        // 24 random bytes → 32 base64url chars (no padding). 192 bits of entropy.
        Span<byte> raw = stackalloc byte[24];
        RandomNumberGenerator.Fill(raw);
        var random = Convert.ToBase64String(raw)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var plaintext = TokenPrefix + random;
        return (plaintext, HashHex(plaintext));
    }

    private static string HashHex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Return shape of <see cref="PersonalAccessTokenService.IssueAsync"/>.
/// <see cref="Plaintext"/> appears exactly once — the UI must show it then
/// discard it.
/// </summary>
public sealed record IssuedToken(int Id, string Plaintext, string Prefix, DateTime CreatedAt, DateTime? ExpiresAt);

/// <summary>
/// Resolved identity of a PAT after <see cref="PersonalAccessTokenService.ValidateAsync"/>.
/// The PAT auth handler turns this into a <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// with the same claim names <see cref="HttpOrganizationContext"/> already
/// reads from the auth cookie.
/// </summary>
public sealed record PatPrincipal(
    int TokenId,
    int UserId,
    int OrganizationId,
    string DisplayName,
    string Email,
    UserRole Role,
    bool IsSiteAdmin,
    bool IsSystemOrganization);
