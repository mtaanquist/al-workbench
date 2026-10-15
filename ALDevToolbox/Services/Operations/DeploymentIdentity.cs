using System.Globalization;
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
/// backups read as another deployment's. Restoring one of its dumps on a
/// still-fresh host records the lost installation's id as this one's
/// <see cref="FormerId"/> (#1322): its backups can then be restored here, but
/// retention never deletes them, and a SiteAdmin can forget it again. See
/// <c>docs/offsite-backups.md</c>.
/// </para>
/// </summary>
public sealed class DeploymentIdentity
{
    public const string FileName = "deployment-id";

    /// <summary>
    /// File beside <see cref="FileName"/> holding the former installation's id
    /// and when it was recorded, on one line (#1322).
    /// </summary>
    public const string FormerFileName = "deployment-id.former";

    private readonly string? _directory;
    private readonly Lock _formerLock = new();
    private FormerInstallation? _former;

    /// <summary>The opaque deployment identifier stamped onto off-site dumps.</summary>
    public string Id { get; }

    /// <summary>
    /// True when <see cref="Id"/> was loaded from / written to durable storage
    /// and therefore survives a restart. False when we fell back to an
    /// in-memory id because the key directory wasn't writable.
    /// </summary>
    public bool IsPersistent { get; }

    /// <summary>
    /// The installation this one was rebuilt from, if a dump of it has been
    /// restored here; <see langword="null"/> otherwise. At most one.
    /// </summary>
    public FormerInstallation? Former
    {
        get { lock (_formerLock) return _former; }
    }

    /// <summary>
    /// True when <paramref name="stampedId"/> names another deployment and this
    /// one's id is durable enough to say so. A missing stamp is a legacy upload
    /// and is not foreign; an ephemeral id would call this deployment's own
    /// earlier stamps foreign after a restart, so it never refuses. The
    /// installation this one was rebuilt from is not foreign either: its
    /// backups can be downloaded and restored here.
    /// </summary>
    public bool IsForeign(string? stampedId) =>
        IsPersistent
        && !string.IsNullOrEmpty(stampedId)
        && !string.Equals(stampedId, Id, StringComparison.Ordinal)
        && !IsFormer(stampedId);

    /// <summary>True when <paramref name="stampedId"/> is the <see cref="Former"/> installation's id.</summary>
    public bool IsFormer(string? stampedId) =>
        !string.IsNullOrEmpty(stampedId)
        && string.Equals(Former?.Id, stampedId, StringComparison.Ordinal);

    /// <summary>True when <paramref name="id"/> is shaped like an id this app writes.</summary>
    public static bool IsWellFormed(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Records <paramref name="formerId"/> as the installation this one was
    /// rebuilt from. Persisted beside the id on the key volume, so it survives
    /// the whole-database restore that triggers it (which replaces every table,
    /// settings included). A no-op when it is already the former id.
    ///
    /// <para>
    /// <see cref="Id"/> itself is deliberately not replaced: the passkey
    /// sign-in keys its synthetic credential ids off it, and this
    /// installation's own uploads keep their own stamp.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The id is not shaped like one this app writes.</exception>
    /// <exception cref="InvalidOperationException">A different former id is already recorded, or the id cannot be persisted.</exception>
    public void RecordFormer(string formerId, DateTime recordedAtUtc)
    {
        if (!IsWellFormed(formerId)) throw new ArgumentException("Not a deployment id.", nameof(formerId));
        lock (_formerLock)
        {
            if (string.Equals(_former?.Id, formerId, StringComparison.Ordinal)) return;
            if (_former is not null)
            {
                throw new InvalidOperationException("This installation has already taken over another installation's backups.");
            }
            if (!IsPersistent || _directory is null)
            {
                throw new InvalidOperationException("The key directory is not writable, so the former installation cannot be recorded.");
            }
            var path = Path.Combine(_directory, FormerFileName);
            File.WriteAllText(path, formerId + " " + recordedAtUtc.ToString("O", CultureInfo.InvariantCulture) + "\n");
            TightenPermissions(path, logger: null);
            _former = new FormerInstallation(formerId, recordedAtUtc);
        }
    }

    /// <summary>Forgets the former installation; its backups read as another installation's again.</summary>
    public void ForgetFormer()
    {
        lock (_formerLock)
        {
            if (_former is null) return;
            if (_directory is not null)
            {
                var path = Path.Combine(_directory, FormerFileName);
                if (File.Exists(path)) File.Delete(path);
            }
            _former = null;
        }
    }

    private DeploymentIdentity(string id, bool isPersistent, string? directory, FormerInstallation? former)
    {
        Id = id;
        IsPersistent = isPersistent;
        _directory = directory;
        _former = former;
    }

    public static DeploymentIdentity LoadOrCreate(string directory, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var former = ReadFormer(directory, logger);
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return new DeploymentIdentity(existing, isPersistent: true, directory, former);
                }
            }

            var fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            TightenPermissions(path, logger);
            logger.LogInformation("Generated and persisted deployment identity at {Path}.", path);
            return new DeploymentIdentity(fresh, isPersistent: true, directory, former);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex,
                "Deployment-identity directory '{Directory}' not writable. Using an in-memory id; off-site dump provenance checks will be skipped until it is persistable.",
                directory);
            return new DeploymentIdentity("ephemeral-" + Guid.NewGuid().ToString("N"), isPersistent: false, directory: null, former: null);
        }
    }

    /// <summary>
    /// Reads "<c>id</c> [<c>recorded-at</c>]". A malformed file is ignored with a
    /// warning rather than stopping the app; an operator who writes the id by
    /// hand may leave the date out.
    /// </summary>
    private static FormerInstallation? ReadFormer(string directory, ILogger logger)
    {
        var path = Path.Combine(directory, FormerFileName);
        if (!File.Exists(path)) return null;
        var parts = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !IsWellFormed(parts[0]))
        {
            logger.LogWarning("Ignoring {Path}: it does not hold a deployment id.", path);
            return null;
        }
        DateTime? at = parts.Length > 1
            && DateTime.TryParse(parts[1], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
        return new FormerInstallation(parts[0], at);
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

/// <summary>The installation this one was rebuilt from (#1322), and when that was recorded, if known.</summary>
public sealed record FormerInstallation(string Id, DateTime? RecordedAtUtc);
