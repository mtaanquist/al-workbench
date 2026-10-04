namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A planned upgrade: a named wave ("28.5 in November 2026") that groups the environments
/// the upgrade team moves, starts and checks together. The header of a header-and-lines
/// pair; its environments are <see cref="OeEnvironmentUpgradeLine"/> rows. Issue #984 and
/// <c>.design/environment-updates.md</c>, "Planned upgrades: a header with lines".
///
/// <para>Its status (Planned, In progress, Updated, Done) is never stored: it is derived
/// from the lines, which are derived from the mirror and the action rows that carry this
/// upgrade's id. Only "done" is a fact somebody states, and that is <see cref="ClosedAt"/>.</para>
///
/// <para>No soft delete. An upgrade can be deleted outright only while nothing has been sent
/// from it; after that it is part of the record and the way out is marking it done.</para>
/// </summary>
public class OeEnvironmentUpgrade
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>What the team calls the wave, e.g. "28.5 in November 2026".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The Business Central release the wave takes its environments to, as Major.Minor
    /// (<c>28.5</c>). A line reads as updated once its environment is on this release or a
    /// later one, compared numerically per segment.
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    /// <summary>The slot the team has in mind for the wave (UTC), if any. Advisory: nothing fires from it.</summary>
    public DateTime? PlannedAt { get; set; }

    /// <summary>Free text for the team: what was agreed, who to ring.</summary>
    public string? Note { get; set; }

    /// <summary>Who created the upgrade. Nullable (<c>ON DELETE SET NULL</c>) so the record outlives the account.</summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>The creator in the audit log's <c>"name &lt;email&gt;"</c> form, copied in at creation, like <see cref="OeEnvironmentUpgradeAction.RequestedBy"/>.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When somebody marked the wave done (UTC); null while it is open. Closing moves it to
    /// the archive and releases its environments for another open upgrade.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public int? ClosedByUserId { get; set; }
    public User? ClosedByUser { get; set; }

    /// <summary>Who marked it done, in the same denormalised form as <see cref="CreatedBy"/>.</summary>
    public string? ClosedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<OeEnvironmentUpgradeLine> Lines { get; set; } = new();
}

/// <summary>
/// One environment on a planned upgrade (<see cref="OeEnvironmentUpgrade"/>): who checks it
/// after the update, and whether they have. Its state word (Planned, Booked, Running, ...) is
/// derived, not stored; see <c>EnvironmentUpgradeLineState</c>.
/// </summary>
public class OeEnvironmentUpgradeLine
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int UpgradeId { get; set; }
    public OeEnvironmentUpgrade? Upgrade { get; set; }

    public int EnvironmentId { get; set; }
    public OeProjectEnvironment? Environment { get; set; }

    /// <summary>
    /// The environment's solution, denormalised so a list of lines can be joined through
    /// <c>ProjectAccess.VisibleProjectPredicate</c> without going via the environment -
    /// the same reason <see cref="OeEnvironmentUpgradeAction.ProjectId"/> carries it.
    /// </summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>
    /// True while the parent upgrade is open - a copy of <c>upgrade.ClosedAt == null</c>,
    /// kept in step by closing and reopening. It exists only so the one-open-upgrade-per-
    /// environment rule can be a filtered unique index on this table: an index cannot
    /// filter on a column of another table.
    /// </summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>Who is to check this environment after its update; null for nobody yet.</summary>
    public int? AssigneeUserId { get; set; }
    public User? AssigneeUser { get; set; }

    /// <summary>When somebody ticked the after-upgrade check (UTC); null while unchecked.</summary>
    public DateTime? CheckedAt { get; set; }

    public int? CheckedByUserId { get; set; }
    public User? CheckedByUser { get; set; }

    /// <summary>Who checked it, in the audit log's <c>"name &lt;email&gt;"</c> form, so the tick still names them after the account is gone.</summary>
    public string? CheckedBy { get; set; }

    /// <summary>A short note from the check ("posting OK, reports OK"). At most 500 characters.</summary>
    public string? Note { get; set; }

    public DateTime AddedAt { get; set; }

    /// <summary>
    /// When the person checking this environment was told it is on the target version
    /// (UTC, issue #1047); null until then. Stamped so they are told once, whichever
    /// read noticed the new version.
    /// </summary>
    public DateTime? UpdatedNotifiedAt { get; set; }
}
