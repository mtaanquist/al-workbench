namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Origin of a backup row — distinguishes scheduled (background) runs from
/// SiteAdmin-triggered ad-hoc ones in the audit log and on the backups page.
/// </summary>
public enum BackupKind
{
    Scheduled,
    AdHoc,
}

/// <summary>
/// One row per <c>pg_dump</c> file under the backups directory. The file on
/// disk is the source of truth for restorability; the DB row carries the
/// metadata the <c>/site-admin/backups</c> page renders without stat-ing
/// every file on every load.
/// </summary>
public class Backup
{
    public int Id { get; set; }

    /// <summary>
    /// Filename inside the backups directory — no path separators. Format
    /// is <c>aldevtoolbox-YYYYMMDDTHHMMSSZ-{adhoc|scheduled}.dump</c>; the
    /// service generates the name and validates that the filename never
    /// escapes the backups directory.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Size of the <c>pg_dump</c> file in bytes at write time.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>UTC instant the backup was written.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// FK to the <c>users</c> row that triggered an ad-hoc backup. Null for
    /// scheduled backups — the scheduler runs without an HTTP context.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public BackupKind Kind { get; set; }

    /// <summary>
    /// When <see langword="true"/>, retention pruning skips this backup. The
    /// flag is the only customer-facing way to keep a backup beyond the
    /// retention window.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>UTC instant the backup was uploaded to the off-site bucket, or null when it hasn't been.</summary>
    public DateTime? OffsiteUploadedAt { get; set; }

    /// <summary>The S3 object key under which the backup was uploaded, or null when not uploaded.</summary>
    public string? OffsiteObjectKey { get; set; }

    /// <summary>
    /// Set on a dump downloaded with Recover: the id of the other installation
    /// that made it. Restoring such a row is only allowed on a fresh
    /// installation, and it is the restore, not the download, that makes this
    /// installation take over the other one's backups (#1322).
    /// </summary>
    public string? RecoveredFromDeploymentId { get; set; }
}
