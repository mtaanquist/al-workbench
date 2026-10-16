namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// Where an environment row comes from. See <c>.design/on-prem-gateway.md</c>,
/// "An on-premises environment is an OeProjectEnvironment row".
/// </summary>
public enum OeEnvironmentKind
{
    /// <summary>Business Central online, read from the Admin Center API. Every row that existed before #1366 is one.</summary>
    Online,

    /// <summary>
    /// A service tier (and tenant) an on-premises gateway reports, linked to the Solution
    /// by an org Admin. Its facts are written by the gateway's inventory, never by the
    /// Admin Center refresh.
    /// </summary>
    OnPremises,
}

/// <summary>
/// A Business Central SaaS environment belonging to a <see cref="OeProject"/>
/// (customer), fetched from the BC Admin Center API and cached so release
/// pipelines can target it without re-typing a name. Refresh is a <em>stable
/// upsert</em> keyed by <c>(ProjectId, Name)</c> — the row id survives a refresh, and
/// so does anything configured on it (the update window), so a release pipeline's FK
/// never dangles. An environment the customer has since deleted is not hard-removed
/// (a release pipeline may still point at it); it is stamped
/// <see cref="MissingSince"/> and surfaced as "no longer present". Org-scoped.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public class OeProjectEnvironment
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>
    /// Online (the default) or a linked on-premises service tier. Every reader decides
    /// whether on-premises rows belong: the Admin Center refresh, the Upgrades page and
    /// Microsoft's update notices leave them out. See <c>.design/on-prem-gateway.md</c>,
    /// "Readers of OeProjectEnvironments".
    /// </summary>
    public OeEnvironmentKind Kind { get; set; } = OeEnvironmentKind.Online;

    /// <summary>
    /// The service tier an on-premises row is linked to; null for an online row. Kept when
    /// the tier is unlinked (<see cref="MissingSince"/> set), so linking the same tier to
    /// the same Solution again revives the row.
    /// </summary>
    public int? GatewayInstanceId { get; set; }
    public GatewayInstance? GatewayInstance { get; set; }

    /// <summary>
    /// The Business Central tenant on <see cref="GatewayInstance"/> (e.g. <c>default</c>, which
    /// is what a single-tenant server is stored as). Not the Microsoft Entra tenant, which is
    /// <see cref="AadTenantId"/>. Null for an online row.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>The environment name (e.g. <c>Production</c>) — keys the automation API URL and, with <see cref="ProjectId"/>, identifies the row across refreshes.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Environment type as reported by the Admin Center API (e.g. <c>Production</c> / <c>Sandbox</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>When this environment was last seen in a fetch.</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>Set when a refresh no longer returns this environment (the customer deleted it), or, for an on-premises row, when an Admin unlinked the tier ("no longer linked"). Cleared if it reappears or is linked again. The row is retained so any release pipeline pointing at it can show "no longer present" rather than break. Distinct from <see cref="SoftDeletedOn"/>: a soft-deleted environment still comes back from the API, a hard-deleted one vanishes from it.</summary>
    public DateTime? MissingSince { get; set; }

    // ── Fetched detail from the Admin Center API ──────────────────────────────
    // All nullable and all rewritten by every refresh. Enum-ish values are stored
    // verbatim as the API returned them — Microsoft's casing differs per endpoint,
    // so nothing here is normalised. Rows fetched before these columns existed carry
    // nulls until the next Refresh.

    /// <summary>The environment's display name in the admin center. Often equals <see cref="Name"/>.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>The application family the environment belongs to, verbatim from the API. Used to address it in later admin-center calls instead of assuming one.</summary>
    public string? ApplicationFamily { get; set; }

    /// <summary>Lifecycle status (<c>Active</c>, <c>Upgrading</c>, <c>SoftDeleted</c>, ...). A delivery is refused when this isn't publishable; see <c>BcEnvironmentStatus</c>.</summary>
    public string? Status { get; set; }

    /// <summary>When <see cref="Status"/> was last read. A status is only as good as its age, and the delivery run re-reads it live before uploading.</summary>
    public DateTime? StatusFetchedAt { get; set; }

    /// <summary>The environment's country/localisation code (e.g. <c>DK</c>).</summary>
    public string? CountryCode { get; set; }

    /// <summary>The Entra tenant the environment actually lives in — catches a connection pointed at the wrong tenant.</summary>
    public Guid? AadTenantId { get; set; }

    /// <summary>Deep link to the environment's web client, behind the "Open in Business Central" action.</summary>
    public string? WebClientLoginUrl { get; set; }

    /// <summary>Azure region the environment runs in.</summary>
    public string? LocationName { get; set; }

    /// <summary>Azure geography. The by-name response omits it, so a live re-read leaves the cached value alone.</summary>
    public string? GeoName { get; set; }

    /// <summary>The update ring the environment is on.</summary>
    public string? RingName { get; set; }

    /// <summary>How AppSource app updates are applied to this environment.</summary>
    public string? AppSourceAppsUpdateCadence { get; set; }

    /// <summary>The environment's Business Central version.</summary>
    public string? Version { get; set; }

    /// <summary>Start of the grace period before Microsoft enforces the next major update.</summary>
    public DateTime? GracePeriodStartDate { get; set; }

    /// <summary>When Microsoft starts enforcing the next update.</summary>
    public DateTime? EnforcedUpdatePeriodStartDate { get; set; }

    /// <summary>When the customer soft-deleted the environment. It still returns from the API until hard deletion; see <see cref="MissingSince"/>.</summary>
    public DateTime? SoftDeletedOn { get; set; }

    /// <summary>When the soft-deleted environment is scheduled to be removed for good.</summary>
    public DateTime? HardDeletePendingOn { get; set; }

    /// <summary>Why the environment was deleted, as reported by the API.</summary>
    public string? DeleteReason { get; set; }

    /// <summary>
    /// Start of the recurring daily <em>delivery window</em> — the time of day this
    /// environment prefers to receive <em>our</em> deliveries, in the project's
    /// <see cref="OeProject.BcTimeZone"/>. A commercial arrangement with the customer,
    /// enforced by our own worker. <c>null</c> (with <see cref="UpdateWindowEnd"/>) means
    /// "no window — deliver any time" (the normal Sandbox case). It is a
    /// <strong>default, not a lock</strong>: it seeds the prefilled schedule time; the
    /// user can override.
    /// <para>
    /// <b>Not</b> Microsoft's platform-update window — that is mirrored separately into
    /// the <c>BcUpdateWindow*</c> fields below, and neither is derived from the other.
    /// </para>
    /// User config, preserved across refreshes. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public TimeOnly? UpdateWindowStart { get; set; }

    /// <summary>End of the daily delivery window (may wrap past midnight, e.g. 22:00–06:00). Null together with <see cref="UpdateWindowStart"/> = no window.</summary>
    public TimeOnly? UpdateWindowEnd { get; set; }

    // ── Microsoft's platform-update window, mirrored (read-only context) ──────────
    //
    // Fetched from settings/upgrade so a consultant can see when Microsoft patches this
    // environment before choosing a delivery slot above. Never used to drive a delivery.

    /// <summary>Start of Microsoft's update window, in <see cref="BcUpdateWindowTimeZoneId"/>. Null when the environment has none, or it hasn't been fetched.</summary>
    public TimeOnly? BcUpdateWindowStart { get; set; }

    /// <summary>End of Microsoft's update window. Null together with <see cref="BcUpdateWindowStart"/>.</summary>
    public TimeOnly? BcUpdateWindowEnd { get; set; }

    /// <summary>
    /// The <em>Windows</em> time-zone id Microsoft's window is expressed in (e.g.
    /// <c>Romance Standard Time</c>), stored verbatim because it is the only form the
    /// update-settings endpoint accepts back.
    /// </summary>
    public string? BcUpdateWindowTimeZoneId { get; set; }

    /// <summary>
    /// The same zone as an IANA id (e.g. <c>Europe/Paris</c>), converted once at fetch
    /// time. Display code uses this one: handing a raw Windows id to
    /// <c>TimeZoneInfo.FindSystemTimeZoneById</c> throws on Linux. Null when the id has
    /// no mapping, in which case display falls back to the project's own zone.
    /// </summary>
    public string? BcUpdateWindowTimeZoneIana { get; set; }

    /// <summary>
    /// When the mirror last succeeded. Stamped only on a successful read, so a failed
    /// per-environment fetch leaves the previous answer and its age visible rather than
    /// silently blanking it.
    /// </summary>
    public DateTime? BcUpdateWindowFetchedAt { get; set; }

    // ── Microsoft's next platform update, mirrored ───────────────────────────────
    //
    // The one update out of the environment's updates list that answers "when does this
    // customer move?": the selected one when the customer has picked a slot, else the
    // newest available one they could pick. Cached so a fleet-wide page can list a
    // hundred environments without a hundred live round trips; the full list stays a
    // live fetch on the environment panel. Values are verbatim from the API — casing is
    // Microsoft's and is interpreted at the parse seam, not here.
    // See .design/saas-delivery.md and issue #657.

    /// <summary>The target platform version of the mirrored update (e.g. <c>27.6</c>). Null when the environment has no update to show.</summary>
    public string? BcNextUpdateVersion { get; set; }

    /// <summary>The API's <c>targetVersionType</c> (major / minor), verbatim.</summary>
    public string? BcNextUpdateType { get; set; }

    /// <summary>The API's <c>updateStatus</c>, verbatim — not normalised, because Microsoft's spelling differs per endpoint and localized text must never drive logic.</summary>
    public string? BcNextUpdateStatus { get; set; }

    /// <summary>When the update is scheduled to run, as the customer (or Microsoft) has it set. Null when no date is chosen yet.</summary>
    public DateTime? BcNextUpdateDate { get; set; }

    /// <summary>The latest date the update can still be pushed out to. The ceiling the fleet page's "push to latest" action aims at.</summary>
    public DateTime? BcNextUpdateLatestDate { get; set; }

    /// <summary>True when the update is set to run regardless of Microsoft's update window (<see cref="BcUpdateWindowStart"/>) — i.e. as soon as possible.</summary>
    public bool? BcNextUpdateIgnoresWindow { get; set; }

    /// <summary>
    /// When this mirror last succeeded. Stamped on every successful read, including one
    /// that found no update at all (which clears the six columns above) — so an empty
    /// mirror reads as "there is nothing scheduled" rather than "never read".
    /// </summary>
    public DateTime? BcNextUpdateFetchedAt { get; set; }

    /// <summary>
    /// Every version Business Central offers this environment (its <c>available</c>
    /// updates), newest first, from the same read as the columns above. The next-update
    /// columns keep one version; the Upgrades page's "change the next version" action
    /// needs all of them, to list what can be picked and to say which environments are
    /// not offered a version yet (issue #960). Null means the list has never been read
    /// (a row mirrored before this column existed); empty means nothing is on offer.
    /// </summary>
    public List<string>? BcOfferedVersions { get; set; }

    /// <summary>
    /// The environment's database size in kilobytes, as Business Central last reported it.
    /// Null until read, or when Business Central could not work it out. The allowance it
    /// counts against is the tenant's - <see cref="OeProject.BcStorageQuotaKb"/>.
    /// </summary>
    public long? BcDatabaseKb { get; set; }

    // ── Licence expiry notices (#1369), on-premises only ─────────────────────────

    /// <summary>
    /// The licence expiry date the last expiry notice was sent for. A different expiry
    /// (a new licence) starts the thresholds again; null until a notice has gone out.
    /// </summary>
    public DateOnly? LicenseExpiryNoticeFor { get; set; }

    /// <summary>
    /// The smallest threshold, in days before <see cref="LicenseExpiryNoticeFor"/>, a notice
    /// has gone out for (30, 7, or 0 for "expired"). Claimed with a compare-and-set, so each
    /// threshold sends once however many workers sweep. Null with <see cref="LicenseExpiryNoticeFor"/>.
    /// </summary>
    public int? LicenseExpiryNoticeDays { get; set; }
}
