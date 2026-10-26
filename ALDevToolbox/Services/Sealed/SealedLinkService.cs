using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Sealed;

/// <summary>What the sender's browser sends to create a link: everything base64url, nothing readable.</summary>
/// <param name="Ciphertext">AES-GCM ciphertext with its tag.</param>
/// <param name="Iv">The 12-byte nonce.</param>
/// <param name="LinkCheck">A 32-byte value derived from the key, which a reveal must present.</param>
/// <param name="PassphraseSalt">The 16-byte PBKDF2 salt, when there is a passphrase.</param>
/// <param name="PassphraseCheck">A 32-byte value derived from the passphrase, when there is one.</param>
/// <param name="ExpiresInHours">One of <see cref="SealedLinkService.ExpiryChoices"/>.</param>
/// <param name="MaxViews">1 to <see cref="SealedLinkService.MaxViewsLimit"/>.</param>
/// <param name="Label">The sender's own note, never shown to the recipient.</param>
public sealed record SealedLinkInput(
    string Ciphertext,
    string Iv,
    string LinkCheck,
    string? PassphraseSalt,
    string? PassphraseCheck,
    int ExpiresInHours,
    int MaxViews,
    string? Label);

/// <summary>One recipient event as the sender's list shows it.</summary>
public sealed record SealedLinkEventRow(SealedLinkEventKind Kind, DateTime OccurredAt, string? IpAddress);

/// <summary>One link in the sender's list.</summary>
public sealed record SealedLinkRow(
    int Id,
    string? Label,
    string CreatedBy,
    int CreatedByUserId,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    int ViewCount,
    int MaxViews,
    bool HasPassphrase,
    DateTime? ClosedAt,
    SealedLinkClosedReason? ClosedReason,
    IReadOnlyList<SealedLinkEventRow> Events)
{
    /// <summary>Still opens. A link past its expiry reads as closed before the sweep has caught up with it.</summary>
    public bool IsOpenAt(DateTime now) => ClosedAt is null && ExpiresAt > now && ViewCount < MaxViews;
}

/// <summary>What the recipient page needs before anything is revealed.</summary>
/// <param name="PassphraseSalt">Base64url, or null when the link has no passphrase.</param>
public sealed record SealedLinkSummary(string OrganizationName, string SenderName, DateTime ExpiresAt, int ViewsLeft, string? PassphraseSalt);

/// <summary>How a reveal went.</summary>
public enum SealedRevealStatus
{
    /// <summary>Shown; <see cref="SealedRevealResult.Ciphertext"/> is set.</summary>
    Revealed,
    /// <summary>No such link, or it has closed, or the link's key part is wrong. One answer for all of them.</summary>
    Gone,
    /// <summary>The passphrase was wrong; <see cref="SealedRevealResult.AttemptsLeft"/> says how many tries remain.</summary>
    WrongPassphrase,
    /// <summary>That wrong passphrase was the last one allowed: the link is now closed.</summary>
    LockedOut,
}

/// <summary>The outcome of <see cref="SealedLinkService.RevealAsync"/>.</summary>
public sealed record SealedRevealResult(
    SealedRevealStatus Status,
    byte[]? Ciphertext = null,
    byte[]? Iv = null,
    int ViewsLeft = 0,
    int AttemptsLeft = 0);

/// <summary>
/// Sealed: share a secret through a link that opens a set number of times and then
/// stops working. The secret is encrypted in the sender's browser and decrypted in the
/// recipient's; this service only ever holds ciphertext. See <c>.design/sealed.md</c>.
///
/// <para>The sender's half (create, list, revoke) runs under the normal organisation
/// filter. The recipient's half (<see cref="GetForRecipientAsync"/>,
/// <see cref="RevealAsync"/>) has no account and so no organisation: it finds the link
/// by its random token alone, crossing the tenant fence under category 1 (approved by
/// the maintainer for this tool, see CLAUDE.md). Every one of those reads and writes is
/// pinned to that one token or to the id it resolved to.</para>
/// </summary>
public sealed class SealedLinkService
{
    /// <summary>How long a link may live, in hours: an hour, a day, a week.</summary>
    public static readonly IReadOnlyList<int> ExpiryChoices = [1, 24, 168];

    /// <summary>The expiry the form starts on.</summary>
    public const int DefaultExpiryHours = 24;

    /// <summary>The most times one link may be opened.</summary>
    public const int MaxViewsLimit = 5;

    /// <summary>Wrong passphrases a link takes before it locks for good.</summary>
    public const int MaxPassphraseAttempts = 5;

    /// <summary>
    /// The largest secret, in UTF-8 bytes, the browser will seal. Far above a client
    /// secret or a certificate, and small enough that the sealed form fits in one
    /// message on the page's connection.
    /// </summary>
    public const int MaxSecretBytes = 16 * 1024;

    /// <summary>The sender's note, at most this many characters.</summary>
    public const int MaxLabelLength = 100;

    /// <summary>How long a closed link stays in the sender's list before it is deleted.</summary>
    public static readonly TimeSpan RetentionAfterClose = TimeSpan.FromDays(30);

    private const int TagBytes = 16;
    private const int IvBytes = 12;
    private const int CheckBytes = 32;
    private const int SaltBytes = 16;
    private const int TokenBytes = 16;

    private static readonly Regex TokenPattern = new("^[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<SealedLinkService> _logger;

    public SealedLinkService(
        AppDbContext db,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<SealedLinkService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Stores a sealed secret for the signed-in user and returns its token, for the
    /// browser to put in front of the key it kept to itself.
    /// </summary>
    public async Task<string> CreateAsync(SealedLinkInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var userId = RequireUserId();
        var orgId = RequireOrganizationId();

        var errors = new Dictionary<string, string>();
        var ciphertext = Decode(input.Ciphertext);
        var iv = Decode(input.Iv);
        var linkCheck = Decode(input.LinkCheck);
        if (ciphertext is null || iv is null || linkCheck is null
            || ciphertext.Length <= TagBytes || iv.Length != IvBytes || linkCheck.Length != CheckBytes)
        {
            errors["Secret"] = "The secret could not be sealed. Reload the page and try again.";
        }
        else if (ciphertext.Length > MaxSecretBytes + TagBytes)
        {
            errors["Secret"] = $"That is too long to send. Keep it under {MaxSecretBytes / 1024} KB.";
        }

        var hasSalt = !string.IsNullOrEmpty(input.PassphraseSalt);
        var hasCheck = !string.IsNullOrEmpty(input.PassphraseCheck);
        var salt = hasSalt ? Decode(input.PassphraseSalt) : null;
        var passphraseCheck = hasCheck ? Decode(input.PassphraseCheck) : null;
        if (hasSalt != hasCheck
            || (hasSalt && (salt is null || salt.Length != SaltBytes || passphraseCheck is null || passphraseCheck.Length != CheckBytes)))
        {
            errors["Passphrase"] = "The passphrase could not be used. Reload the page and try again.";
        }

        if (!ExpiryChoices.Contains(input.ExpiresInHours))
        {
            errors["ExpiresInHours"] = "Pick when the link expires: in an hour, a day or a week.";
        }
        if (input.MaxViews < 1 || input.MaxViews > MaxViewsLimit)
        {
            errors["MaxViews"] = $"A link can be opened from 1 to {MaxViewsLimit} times.";
        }

        var label = string.IsNullOrWhiteSpace(input.Label) ? null : input.Label.Trim();
        if (label is not null && label.Length > MaxLabelLength)
        {
            errors["Label"] = $"Keep the note under {MaxLabelLength} characters.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);

        var now = _clock.GetUtcNow().UtcDateTime;
        var link = new SealedLink
        {
            OrganizationId = orgId,
            CreatedByUserId = userId,
            Token = NewToken(),
            Label = label,
            Ciphertext = ciphertext,
            Iv = iv!,
            LinkCheckHash = SHA256.HashData(linkCheck!),
            PassphraseSalt = salt,
            PassphraseCheckHash = passphraseCheck is null ? null : SHA256.HashData(passphraseCheck),
            MaxViews = input.MaxViews,
            CreatedAt = now,
            ExpiresAt = now.AddHours(input.ExpiresInHours),
        };
        _db.SealedLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = now,
            ChangedBy = await AuditActor.ResolveAsync(_db, userId, ct),
            ChangedByUserId = userId,
            OrganizationId = orgId,
            EntityType = AuditEntityType.SealedLink,
            EntityId = link.Id,
            Action = AuditAction.Created,
            EntityName = label,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Sealed link {LinkId} created by user {UserId}: {MaxViews} view(s), expires {ExpiresAt:o}, passphrase {HasPassphrase}.",
            link.Id, userId, link.MaxViews, link.ExpiresAt, salt is not null);
        return link.Token;
    }

    /// <summary>
    /// The signed-in user's links, newest first; with <paramref name="everyone"/>, every
    /// link in the organisation, which only an Admin may ask for.
    /// </summary>
    public async Task<List<SealedLinkRow>> ListAsync(bool everyone = false, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        if (everyone && !await IsAdminAsync(userId, ct))
        {
            throw new InvalidOperationException("Only an Admin can list everyone's Sealed links.");
        }

        var query = _db.SealedLinks.AsNoTracking();
        if (!everyone) query = query.Where(l => l.CreatedByUserId == userId);

        var rows = await query
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id,
                l.Label,
                CreatedByName = l.CreatedByUser!.DisplayName,
                CreatedByEmail = l.CreatedByUser!.Email,
                l.CreatedByUserId,
                l.CreatedAt,
                l.ExpiresAt,
                l.ViewCount,
                l.MaxViews,
                HasPassphrase = l.PassphraseSalt != null,
                l.ClosedAt,
                l.ClosedReason,
                Events = l.Events
                    .OrderBy(e => e.OccurredAt)
                    .Select(e => new SealedLinkEventRow(e.Kind, e.OccurredAt, e.IpAddress))
                    .ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new SealedLinkRow(
                r.Id, r.Label,
                string.IsNullOrWhiteSpace(r.CreatedByName) ? r.CreatedByEmail : r.CreatedByName,
                r.CreatedByUserId, r.CreatedAt, r.ExpiresAt, r.ViewCount, r.MaxViews, r.HasPassphrase,
                r.ClosedAt, r.ClosedReason, r.Events))
            .ToList();
    }

    /// <summary>True when the signed-in user may see and revoke everyone's links.</summary>
    public Task<bool> CanSeeEveryoneAsync(CancellationToken ct = default) =>
        _orgContext.CurrentUserId is { } userId ? IsAdminAsync(userId, ct) : Task.FromResult(false);

    /// <summary>
    /// Stops a link working and drops its ciphertext. The sender may revoke their own
    /// links, an Admin anyone's. A link that has already closed is left as it is.
    /// </summary>
    public async Task RevokeAsync(int id, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var link = await _db.SealedLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Link"] = "That link no longer exists. Reload the page to see your links.",
            });
        if (link.CreatedByUserId != userId && !await IsAdminAsync(userId, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Link"] = "Only the person who created this link, or an Admin, can revoke it.",
            });
        }
        if (link.ClosedAt is not null) return;

        var now = _clock.GetUtcNow().UtcDateTime;
        // Conditional, so a reveal or the sweep that closed the link a moment ago keeps
        // its own reason instead of being relabelled Revoked.
        var revoked = await _db.SealedLinks
            .Where(l => l.Id == id && l.ClosedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Ciphertext, (byte[]?)null)
                .SetProperty(l => l.ClosedAt, now)
                .SetProperty(l => l.ClosedReason, SealedLinkClosedReason.Revoked), ct);
        if (revoked == 0) return;
        _db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = now,
            ChangedBy = await AuditActor.ResolveAsync(_db, userId, ct),
            ChangedByUserId = userId,
            OrganizationId = link.OrganizationId,
            EntityType = AuditEntityType.SealedLink,
            EntityId = link.Id,
            Action = AuditAction.Deleted,
            EntityName = link.Label,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Sealed link {LinkId} revoked by user {UserId}.", link.Id, userId);
    }

    /// <summary>
    /// What the recipient page shows before anything is revealed, or null when the link
    /// does not exist or no longer opens. Anonymous: see the class remarks.
    /// </summary>
    public async Task<SealedLinkSummary?> GetForRecipientAsync(string token, CancellationToken ct = default)
    {
        if (!IsWellFormedToken(token)) return null;
        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth, a Sealed link's recipient has no account): pinned to the link's random token.
        var link = await _db.SealedLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Token == token && l.ClosedAt == null && l.ExpiresAt > now)
            .Select(l => new
            {
                OrganizationName = l.Organization!.Name,
                SenderName = l.CreatedByUser!.DisplayName,
                SenderEmail = l.CreatedByUser!.Email,
                l.ExpiresAt,
                l.MaxViews,
                l.ViewCount,
                l.PassphraseSalt,
            })
            .FirstOrDefaultAsync(ct);
        if (link is null || link.ViewCount >= link.MaxViews) return null;
        return new SealedLinkSummary(
            link.OrganizationName,
            string.IsNullOrWhiteSpace(link.SenderName) ? link.SenderEmail : link.SenderName,
            link.ExpiresAt,
            link.MaxViews - link.ViewCount,
            link.PassphraseSalt is null ? null : Base64Url.EncodeToString(link.PassphraseSalt));
    }

    /// <summary>
    /// Spends one view and returns the ciphertext, when <paramref name="linkCheck"/>
    /// (and <paramref name="passphraseCheck"/>, if the link has a passphrase) match.
    /// Two reveals at once cannot both take the last view: the count only moves in a
    /// conditional update that one of them loses. Anonymous: see the class remarks.
    /// </summary>
    /// <param name="ipAddress">The recipient's address, recorded for the sender.</param>
    public async Task<SealedRevealResult> RevealAsync(
        string token, string? linkCheck, string? passphraseCheck, string? ipAddress, CancellationToken ct = default)
    {
        var gone = new SealedRevealResult(SealedRevealStatus.Gone);
        if (!IsWellFormedToken(token)) return gone;
        var check = Decode(linkCheck);
        if (check is null || check.Length != CheckBytes) return gone;

        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth, a Sealed link's recipient has no account): pinned to the link's random token.
        var link = await _db.SealedLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Token == token && l.ClosedAt == null && l.ExpiresAt > now)
            .Select(l => new
            {
                l.Id,
                l.OrganizationId,
                l.Ciphertext,
                l.Iv,
                l.LinkCheckHash,
                l.PassphraseCheckHash,
                l.FailedPassphraseAttempts,
                l.MaxViews,
                l.ViewCount,
            })
            .FirstOrDefaultAsync(ct);
        if (link?.Ciphertext is null || link.ViewCount >= link.MaxViews) return gone;

        // A link whose key part was cut off, or guessed, is the same as no link: it
        // must not spend a view or a passphrase attempt.
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(check), link.LinkCheckHash)) return gone;

        if (link.PassphraseCheckHash is not null)
        {
            var given = Decode(passphraseCheck);
            var right = given is { Length: CheckBytes }
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(given), link.PassphraseCheckHash);
            if (!right) return await RecordWrongPassphraseAsync(link.Id, link.OrganizationId, ipAddress, now, ct);
        }

        // Taking the view and, on the last one, closing the link and dropping the
        // ciphertext is one statement: a request aborted in between cannot leave a
        // used-up link holding its secret. Postgres re-checks the WHERE under the row
        // lock, so two reveals at once cannot both take the last view.
        // Fence category 1 (pre-auth, a Sealed link's recipient has no account): pinned to the id the token resolved to.
        var won = await _db.SealedLinks.IgnoreQueryFilters()
            .Where(l => l.Id == link.Id && l.ClosedAt == null && l.ExpiresAt > now
                && l.ViewCount < l.MaxViews && l.FailedPassphraseAttempts < MaxPassphraseAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ViewCount, l => l.ViewCount + 1)
                .SetProperty(l => l.Ciphertext, l => l.ViewCount + 1 >= l.MaxViews ? null : l.Ciphertext)
                .SetProperty(l => l.ClosedAt, l => l.ViewCount + 1 >= l.MaxViews ? now : l.ClosedAt)
                .SetProperty(l => l.ClosedReason, l => l.ViewCount + 1 >= l.MaxViews ? SealedLinkClosedReason.UsedUp : l.ClosedReason),
                CancellationToken.None);
        if (won == 0) return gone;

        // Read back after the update: another reveal may have taken a view meanwhile.
        // Fence category 1 (pre-auth, a Sealed link's recipient has no account): pinned to the id the token resolved to.
        var after = await _db.SealedLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == link.Id)
            .Select(l => new { l.ViewCount, l.MaxViews })
            .FirstAsync(CancellationToken.None);
        var viewsLeft = Math.Max(0, after.MaxViews - after.ViewCount);

        _db.SealedLinkEvents.Add(new SealedLinkEvent
        {
            OrganizationId = link.OrganizationId,
            SealedLinkId = link.Id,
            Kind = SealedLinkEventKind.Opened,
            OccurredAt = now,
            IpAddress = Truncate(ipAddress),
        });
        // The view is spent: record it even if the recipient has already gone.
        await _db.SaveChangesAsync(CancellationToken.None);

        _logger.LogInformation("Sealed link {LinkId} opened ({ViewsLeft} views left).", link.Id, viewsLeft);
        return new SealedRevealResult(
            SealedRevealStatus.Revealed,
            link.Ciphertext,
            link.Iv,
            ViewsLeft: viewsLeft);
    }

    private async Task<SealedRevealResult> RecordWrongPassphraseAsync(
        int linkId, int organizationId, string? ipAddress, DateTime now, CancellationToken ct)
    {
        // Counting the attempt and, on the last one, locking the link is one statement
        // whose WHERE Postgres re-checks under the row lock: a burst of guesses sent at
        // once still gets no more than MaxPassphraseAttempts between them.
        // Fence category 1 (pre-auth, a Sealed link's recipient has no account): pinned to the id the token resolved to.
        var counted = await _db.SealedLinks.IgnoreQueryFilters()
            .Where(l => l.Id == linkId && l.ClosedAt == null && l.FailedPassphraseAttempts < MaxPassphraseAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.FailedPassphraseAttempts, l => l.FailedPassphraseAttempts + 1)
                .SetProperty(l => l.Ciphertext, l => l.FailedPassphraseAttempts + 1 >= MaxPassphraseAttempts ? null : l.Ciphertext)
                .SetProperty(l => l.ClosedAt, l => l.FailedPassphraseAttempts + 1 >= MaxPassphraseAttempts ? now : l.ClosedAt)
                .SetProperty(l => l.ClosedReason, l => l.FailedPassphraseAttempts + 1 >= MaxPassphraseAttempts
                    ? SealedLinkClosedReason.LockedOut : l.ClosedReason),
                CancellationToken.None);
        if (counted == 0) return new SealedRevealResult(SealedRevealStatus.Gone);

        _db.SealedLinkEvents.Add(new SealedLinkEvent
        {
            OrganizationId = organizationId,
            SealedLinkId = linkId,
            Kind = SealedLinkEventKind.WrongPassphrase,
            OccurredAt = now,
            IpAddress = Truncate(ipAddress),
        });
        await _db.SaveChangesAsync(CancellationToken.None);

        // Fence category 1 (pre-auth, a Sealed link's recipient has no account): pinned to the id the token resolved to.
        var attempts = await _db.SealedLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == linkId)
            .Select(l => l.FailedPassphraseAttempts)
            .FirstOrDefaultAsync(ct);
        if (attempts >= MaxPassphraseAttempts)
        {
            _logger.LogWarning("Sealed link {LinkId} locked after {Attempts} wrong passphrases.", linkId, MaxPassphraseAttempts);
            return new SealedRevealResult(SealedRevealStatus.LockedOut);
        }
        return new SealedRevealResult(SealedRevealStatus.WrongPassphrase, AttemptsLeft: MaxPassphraseAttempts - attempts);
    }

    /// <summary>True for a string shaped like a token, so junk never reaches the database.</summary>
    public static bool IsWellFormedToken(string? token) => token is not null && TokenPattern.IsMatch(token);

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    private static byte[]? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Truncate(string? ip) => ip is { Length: > 64 } ? ip[..64] : ip;

    private async Task<bool> IsAdminAsync(int userId, CancellationToken ct) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Role == UserRole.Admin)
            .FirstOrDefaultAsync(ct);

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No signed-in user; a Sealed link belongs to one.");

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organisation in scope.");
}
