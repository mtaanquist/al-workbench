using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Services.Operations;

/// <summary>
/// A stable, per-deployment identifier persisted next to the Data Protection /
/// OAuth key material on the <c>app-keys</c> volume. Used to fingerprint
/// whole-DB dumps uploaded off-site so a restore can refuse a dump that
/// originated from a <em>different</em> deployment sharing the same bucket /
/// prefix — the gap that made the off-site catalogue's "download then restore"
/// path able to clobber the entire database with a neighbour's data.
///
/// <para>
/// The id is a random opaque token; it is not a secret in the cryptographic
/// sense, but because it lives only on the (already sensitive) key volume an
/// attacker who can write objects to the bucket can't easily forge a matching
/// stamp. When the volume isn't writable we fall back to a process-ephemeral
/// id and mark <see cref="IsPersistent"/> false so the restore check can skip
/// enforcement rather than spuriously refusing legitimate dumps after a
/// restart.
/// </para>
///
/// <para>
/// A host rebuilt after a disaster gets a new id, so the lost installation's
/// backups read as another deployment's. <see cref="Trust"/> records the lost
/// installation's id beside this one (#1322); see
/// <c>docs/offsite-backups.md</c> for the recovery that uses it.
/// </para>
/// </summary>
public sealed class DeploymentIdentity
{
    public const string FileName = "deployment-id";

    /// <summary>
    /// File beside <see cref="FileName"/> listing, one per line, the ids of
    /// earlier installations this one was rebuilt from (#1322). Their stamps
    /// count as this deployment's own; see <see cref="Trust"/>.
    /// </summary>
    public const string TrustedFileName = "deployment-id.trusted";

    private readonly string? _directory;
    private readonly Lock _trustLock = new();
    private HashSet<string> _trusted;

    /// <summary>The opaque deployment identifier stamped onto off-site dumps.</summary>
    public string Id { get; }

    /// <summary>
    /// True when <see cref="Id"/> was loaded from / written to durable storage
    /// and therefore survives a restart. False when we fell back to an
    /// in-memory id because the key directory wasn't writable.
    /// </summary>
    public bool IsPersistent { get; }

    /// <summary>
    /// The ids of the installations this one took over in a disaster recovery
    /// (<see cref="Trust"/>). Their off-site objects are treated as this
    /// deployment's own: they can be downloaded and restored, and retention
    /// prunes them.
    /// </summary>
    public IReadOnlyCollection<string> TrustedFormerIds
    {
        get { lock (_trustLock) return _trusted.ToList(); }
    }

    /// <summary>
    /// True when <paramref name="stampedId"/> names another deployment and this
    /// one's id is durable enough to say so. A missing stamp is a legacy upload
    /// and is not foreign; an ephemeral id would call this deployment's own
    /// earlier stamps foreign after a restart, so it never refuses. The id of
    /// an installation this one was recovered from is not foreign either.
    /// </summary>
    public bool IsForeign(string? stampedId) =>
        IsPersistent
        && !string.IsNullOrEmpty(stampedId)
        && !IsOwn(stampedId);

    /// <summary>
    /// True when <paramref name="stampedId"/> is this deployment's id or the id
    /// of an installation it was recovered from. False for a missing stamp.
    /// </summary>
    public bool IsOwn(string? stampedId)
    {
        if (string.IsNullOrEmpty(stampedId)) return false;
        if (string.Equals(stampedId, Id, StringComparison.Ordinal)) return true;
        lock (_trustLock) return _trusted.Contains(stampedId);
    }

    /// <summary>
    /// Records <paramref name="formerId"/> as the id of an installation this one
    /// is being rebuilt from, so its off-site backups stop being refused as
    /// another deployment's and retention treats them as ours. Persisted beside
    /// the id on the key volume, so it survives the whole-database restore that
    /// follows (which replaces every table, settings included).
    ///
    /// <para>
    /// The id itself is deliberately not replaced by the former one: the
    /// passkey sign-in keys its synthetic credential ids off it, and this
    /// installation's own uploads keep their own stamp.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The id is not shaped like one this app writes.</exception>
    public void Trust(string formerId)
    {
        if (string.IsNullOrWhiteSpace(formerId) || formerId.Length > 128
            || !formerId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("Not a deployment id.", nameof(formerId));
        }
        lock (_trustLock)
        {
            if (string.Equals(formerId, Id, StringComparison.Ordinal) || _trusted.Contains(formerId)) return;
            if (IsPersistent && _directory is not null)
            {
                var path = Path.Combine(_directory, TrustedFileName);
                File.AppendAllText(path, formerId + "\n");
                TightenPermissions(path, logger: null);
            }
            _trusted = new HashSet<string>(_trusted, StringComparer.Ordinal) { formerId };
        }
    }

    private DeploymentIdentity(string id, bool isPersistent, string? directory, IEnumerable<string> trusted)
    {
        Id = id;
        IsPersistent = isPersistent;
        _directory = directory;
        _trusted = new HashSet<string>(trusted, StringComparer.Ordinal);
    }

    public static DeploymentIdentity LoadOrCreate(string directory, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var trusted = ReadTrusted(directory);
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return new DeploymentIdentity(existing, isPersistent: true, directory, trusted);
                }
            }

            var fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            TightenPermissions(path, logger);
            logger.LogInformation("Generated and persisted deployment identity at {Path}.", path);
            return new DeploymentIdentity(fresh, isPersistent: true, directory, trusted);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex,
                "Deployment-identity directory '{Directory}' not writable. Using an in-memory id; off-site dump provenance checks will be skipped until it is persistable.",
                directory);
            return new DeploymentIdentity("ephemeral-" + Guid.NewGuid().ToString("N"), isPersistent: false, directory: null, []);
        }
    }

    /// <summary>One id per line; blank lines and <c>#</c> comments are skipped, so an operator can annotate the file by hand.</summary>
    private static List<string> ReadTrusted(string directory)
    {
        var path = Path.Combine(directory, TrustedFileName);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    private static void TightenPermissions(string path, ILogger? logger)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger?.LogWarning(ex, "Could not tighten file permissions on {Path}.", path);
        }
    }
}
