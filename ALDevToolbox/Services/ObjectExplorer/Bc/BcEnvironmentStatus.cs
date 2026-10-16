namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>What an environment's Admin Center <c>status</c> means for a delivery.</summary>
public enum BcEnvironmentReadiness
{
    /// <summary>The environment is <c>Active</c> — safe to publish to.</summary>
    Ready,

    /// <summary>Busy with an update or not up yet (<c>Upgrading</c>, <c>Preparing</c>, <c>NotReady</c>, <c>Recovering</c>). Retry later.</summary>
    Busy,

    /// <summary>On its way out (<c>Removing</c>, <c>SoftDeleting</c>, <c>SoftDeleted</c>). Retrying won't help.</summary>
    Deleting,

    /// <summary>Any <c>*Failed</c> status — the environment needs attention in Business Central.</summary>
    Failed,

    /// <summary>No status recorded, or one we don't recognise. Deliberately not a refusal — see <see cref="BcEnvironmentStatus"/>.</summary>
    Unknown,
}

/// <summary>
/// Classifies the Admin Center environment <c>status</c> string so both delivery
/// gates — at scheduling and again at claim time — refuse for the same reasons with
/// the same words. Comparisons are case-insensitive because Microsoft's casing is
/// inconsistent across endpoints, and an unrecognised (or absent) status is treated
/// as <see cref="BcEnvironmentReadiness.Unknown"/> and <em>allowed</em>: rows fetched
/// before this field was captured have no status, and a status Microsoft adds later
/// shouldn't silently block every release. See <c>.design/saas-delivery.md</c>.
/// </summary>
public static class BcEnvironmentStatus
{
    /// <summary>The one status that means "publish away".</summary>
    public const string Active = "Active";

    /// <summary>
    /// The status of an environment the customer has deleted and Microsoft is still
    /// keeping — recoverable until the hard delete. Every page that has to tell a
    /// deleted environment from a live one reads it through <see cref="IsSoftDeleted"/>
    /// rather than comparing the string itself.
    /// </summary>
    public const string SoftDeleted = "SoftDeleted";

    private static readonly string[] BusyStatuses = ["Upgrading", "Preparing", "NotReady", "Recovering"];
    private static readonly string[] DeletingStatuses = ["Removing", "SoftDeleting", SoftDeleted];

    /// <summary>
    /// True when Business Central reports the environment as deleted-but-recoverable.
    /// Compared case-insensitively, because the status is stored exactly as Microsoft
    /// spelled it (see <see cref="BcEnvironment"/>), and trimmed for the same reason.
    /// <para>
    /// This is the one lifecycle state the workbench splits out rather than merely
    /// describes: a soft-deleted environment cannot be published to, updated or
    /// rescheduled, so it is kept out of the working lists and shown on its own. See
    /// <c>.design/environment-updates.md</c>, "Deleted environments".
    /// </para>
    /// </summary>
    public static bool IsSoftDeleted(string? status) =>
        string.Equals(status?.Trim(), SoftDeleted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a plainly running environment - the one state that needs no word on
    /// screen, which is why the fleet tables and the command palette both leave it
    /// unsaid rather than writing "Running" on most rows.
    /// </summary>
    public static bool IsRunning(string? status) =>
        string.Equals(status, Active, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The state in words a consultant would use. Business Central's own tokens
    /// ("SoftDeleted") are API vocabulary and must not reach the screen, but an
    /// unrecognised one is still shown rather than swallowed - a state we don't know
    /// about is exactly the kind of thing someone needs to see.
    ///
    /// <para>Here rather than beside the table that first needed it
    /// (<c>Components/Shared/FleetRowState.cs</c>, which now calls this) because the
    /// command palette describes the same environment in a result row, and one
    /// environment must not be in two states depending on where you read it.</para>
    /// </summary>
    public static string StatusWord(string? status) =>
        (status ?? string.Empty).ToLowerInvariant() switch
        {
            "active" => "Running",
            // "Upgrading" is what Business Central actually reports while a platform
            // update runs; without it here a watched update read as a state nobody knew.
            "updating" or "upgrading" => "Update in progress",
            "preparing" => "Being prepared",
            "suspended" => "Suspended by Microsoft",
            // An on-premises service tier's own states (Get-NAVServerInstance); a running
            // one is stored as Active, so it reads as every other running environment.
            "stopped" => "Stopped",
            "starting" => "Starting",
            "stopping" => "Stopping",
            "softdeleted" => Humanise(status),
            "" => "State not reported",
            // Every documented failure ends in "Failed" (UpgradingFailed, ...); Classify
            // already knows them, so they must not read as a state nobody recognises.
            _ when Classify(status) == BcEnvironmentReadiness.Failed => Humanise(status),
            _ => "In a state we don't recognise",
        };

    /// <summary>Classifies a status string. Null/blank is <see cref="BcEnvironmentReadiness.Unknown"/>.</summary>
    public static BcEnvironmentReadiness Classify(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return BcEnvironmentReadiness.Unknown;
        var s = status.Trim();

        if (s.Equals(Active, StringComparison.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Ready;
        if (BusyStatuses.Contains(s, StringComparer.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Busy;
        if (DeletingStatuses.Contains(s, StringComparer.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Deleting;
        // Every failure status Microsoft documents ends in "Failed" (PreparingFailed,
        // UpgradingFailed, ...), so match the suffix rather than enumerating them.
        if (s.EndsWith("Failed", StringComparison.OrdinalIgnoreCase)) return BcEnvironmentReadiness.Failed;

        return BcEnvironmentReadiness.Unknown;
    }

    /// <summary>True when a delivery may go ahead (ready, or no status to judge by).</summary>
    public static bool CanPublish(string? status) =>
        Classify(status) is BcEnvironmentReadiness.Ready or BcEnvironmentReadiness.Unknown;

    /// <summary>
    /// The refusal message for a status that blocks publishing, naming the status and
    /// saying what to do next. Returns null when the status doesn't block.
    /// </summary>
    public static string? RefusalMessage(string environmentName, string? status)
    {
        var name = string.IsNullOrWhiteSpace(environmentName) ? "This environment" : $"'{environmentName}'";
        var shown = (status ?? string.Empty).Trim();
        return Classify(status) switch
        {
            BcEnvironmentReadiness.Busy =>
                $"Business Central reports {name} as {shown} right now, so it can't take an install. Wait for it to finish, then deploy again.",
            BcEnvironmentReadiness.Deleting =>
                $"Business Central reports {name} as {shown} — it's being removed. Pick another environment.",
            BcEnvironmentReadiness.Failed =>
                $"Business Central reports {name} as {shown} — the environment is in a failed state in Business Central. Sort it out in the admin center, then deploy again.",
            _ => null,
        };
    }

    /// <summary>
    /// Microsoft's status strings are machine-cased (<c>SoftDeleted</c>, <c>NotReady</c>).
    /// They're stored verbatim, but a space before each interior capital is all it takes
    /// to read as a sentence rather than a wire value. No translation table — a status
    /// Microsoft adds later humanises on its own.
    /// </summary>
    public static string Humanise(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return string.Empty;
        var s = status.Trim();
        // The one token with a settled name of its own: Business Central people say
        // "soft-deleted", hyphenated, and every page has to say the same word (#806).
        if (s.Equals("SoftDeleted", StringComparison.OrdinalIgnoreCase)) return "Soft-deleted";
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]) && !char.IsWhiteSpace(s[i - 1]))
            {
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(s[i]));
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}
