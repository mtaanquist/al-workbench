namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One Sealed link: a secret someone encrypted in their browser to hand to a person
/// outside the workbench, opened from a link that works a set number of times before
/// it expires. See <c>.design/sealed.md</c>.
///
/// <para>Nothing here can recover the secret. The key is in the part of the link after
/// <c>#</c>, which browsers never send, so the row holds ciphertext and two hashes the
/// recipient's browser proves it can match before a view is spent. The ciphertext is
/// cleared as soon as the link closes (used up, revoked, expired or locked); the rest of
/// the row stays for <see cref="Services.Sealed.SealedLinkService.RetentionAfterClose"/>
/// so the sender can see what happened to it.</para>
/// </summary>
public class SealedLink
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>
    /// The random, URL-safe id in <c>/s/{token}</c>: 16 bytes from a CSPRNG, base64url.
    /// Not the key - knowing it opens nothing without the part after <c>#</c>.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The sender's own note, shown only to them and their admins. Never the secret.</summary>
    public string? Label { get; set; }

    /// <summary>AES-GCM ciphertext with its tag. Null once the link has closed.</summary>
    public byte[]? Ciphertext { get; set; }

    /// <summary>The 12-byte AES-GCM nonce.</summary>
    public byte[] Iv { get; set; } = [];

    /// <summary>
    /// SHA-256 of a value the recipient's browser derives from the key in the link.
    /// A reveal must present it, so a link with its key cut off (or no key at all)
    /// cannot spend a view.
    /// </summary>
    public byte[] LinkCheckHash { get; set; } = [];

    /// <summary>The passphrase's PBKDF2 salt, when the sender set one; rendered into the recipient page.</summary>
    public byte[]? PassphraseSalt { get; set; }

    /// <summary>SHA-256 of the passphrase proof the recipient's browser derives, when the sender set one.</summary>
    public byte[]? PassphraseCheckHash { get; set; }

    /// <summary>Wrong passphrases so far; the link locks at <see cref="Services.Sealed.SealedLinkService.MaxPassphraseAttempts"/>.</summary>
    public int FailedPassphraseAttempts { get; set; }

    /// <summary>How many times the secret may be shown, 1 to <see cref="Services.Sealed.SealedLinkService.MaxViewsLimit"/>.</summary>
    public int MaxViews { get; set; } = 1;

    /// <summary>How many times it has been shown.</summary>
    public int ViewCount { get; set; }

    /// <summary>How many of <see cref="ViewCount"/> the sender has been notified about.</summary>
    public int NotifiedViewCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>When the link stopped working, for whichever <see cref="ClosedReason"/>. Null while open.</summary>
    public DateTime? ClosedAt { get; set; }

    public SealedLinkClosedReason? ClosedReason { get; set; }

    /// <summary>True once the sender has been told the link locked after wrong passphrases.</summary>
    public bool LockoutNotified { get; set; }

    public List<SealedLinkEvent> Events { get; set; } = new();
}

/// <summary>Why a <see cref="SealedLink"/> stopped working. Stored as text.</summary>
public enum SealedLinkClosedReason
{
    /// <summary>Shown as many times as the sender allowed.</summary>
    UsedUp,
    /// <summary>The sender (or an admin) revoked it.</summary>
    Revoked,
    /// <summary>Reached its expiry time unopened, or not opened as many times as allowed.</summary>
    Expired,
    /// <summary>Too many wrong passphrases.</summary>
    LockedOut,
}

/// <summary>
/// Something a recipient did with a <see cref="SealedLink"/>: opened it, or typed a
/// wrong passphrase. Kept with the link, and deleted with it, so the sender can see
/// when and from where it was opened.
/// </summary>
public class SealedLinkEvent
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }

    public int SealedLinkId { get; set; }
    public SealedLink? SealedLink { get; set; }

    public SealedLinkEventKind Kind { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>The recipient's address as the app saw it, behind the trusted proxy. Null when unknown.</summary>
    public string? IpAddress { get; set; }
}

/// <summary>The kind of <see cref="SealedLinkEvent"/>. Stored as text.</summary>
public enum SealedLinkEventKind
{
    Opened,
    WrongPassphrase,
}
