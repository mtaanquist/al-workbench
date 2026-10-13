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
/// </summary>
public sealed class DeploymentIdentity
{
    public const string FileName = "deployment-id";

    /// <summary>The opaque deployment identifier stamped onto off-site dumps.</summary>
    public string Id { get; }

    /// <summary>
    /// True when <see cref="Id"/> was loaded from / written to durable storage
    /// and therefore survives a restart. False when we fell back to an
    /// in-memory id because the key directory wasn't writable.
    /// </summary>
    public bool IsPersistent { get; }

    /// <summary>
    /// True when <paramref name="stampedId"/> names another deployment and this
    /// one's id is durable enough to say so. A missing stamp is a legacy upload
    /// and is not foreign; an ephemeral id would call this deployment's own
    /// earlier stamps foreign after a restart, so it never refuses.
    /// </summary>
    public bool IsForeign(string? stampedId) =>
        IsPersistent
        && !string.IsNullOrEmpty(stampedId)
        && !string.Equals(stampedId, Id, StringComparison.Ordinal);

    private DeploymentIdentity(string id, bool isPersistent)
    {
        Id = id;
        IsPersistent = isPersistent;
    }

    public static DeploymentIdentity LoadOrCreate(string directory, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing))
                {
                    return new DeploymentIdentity(existing, isPersistent: true);
                }
            }

            var fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    logger.LogWarning(ex, "Could not tighten file permissions on {Path}.", path);
                }
            }
            logger.LogInformation("Generated and persisted deployment identity at {Path}.", path);
            return new DeploymentIdentity(fresh, isPersistent: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex,
                "Deployment-identity directory '{Directory}' not writable. Using an in-memory id; off-site dump provenance checks will be skipped until it is persistable.",
                directory);
            return new DeploymentIdentity("ephemeral-" + Guid.NewGuid().ToString("N"), isPersistent: false);
        }
    }
}
