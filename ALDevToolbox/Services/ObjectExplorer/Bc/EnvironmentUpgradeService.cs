using System.Globalization;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Planned upgrades (issue #984): a named wave of environments the upgrade team moves,
/// starts and checks together, as a header (<see cref="OeEnvironmentUpgrade"/>) with lines
/// (<see cref="OeEnvironmentUpgradeLine"/>). This service owns the header and its lines;
/// the actions themselves still go through <see cref="UpgradeActionService"/>, which stamps
/// the upgrade's id on each row it writes. See <c>.design/environment-updates.md</c>,
/// "Planned upgrades: a header with lines".
///
/// <para><b>The join is the guard</b>, as in <see cref="UpgradeFleetService"/>: every read
/// of lines goes through <see cref="ProjectAccess.VisibleProjectPredicate"/> on the line's
/// own <c>project_id</c>, and a line whose solution the caller cannot see is left out
/// rather than shown blank. The header itself is organisation-wide - anyone in the
/// organisation may create one - and the environment-updates grant is checked where lines
/// are touched: adding, removing, assigning and checking a line, and every header move that
/// changes all of its lines at once (done, reopen, leftovers, delete).</para>
/// </summary>
public sealed class EnvironmentUpgradeService
{
    /// <summary>The longest name an upgrade may have. Matches the column.</summary>
    public const int NameMaxLength = 200;

    /// <summary>The longest note on a line's check. Matches the column.</summary>
    public const int LineNoteMaxLength = 500;

    /// <summary>
    /// The longest note on the upgrade itself. The column is text; the cap keeps a pasted
    /// email thread from becoming the page.
    /// </summary>
    public const int NoteMaxLength = 4000;

    /// <summary>What a target release looks like: Major.Minor, e.g. <c>28.5</c>.</summary>
    private static readonly Regex MajorMinorPattern = new(@"^\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly UpgradeFleetService _fleet;
    private readonly TimeProvider _clock;
    private readonly ILogger<EnvironmentUpgradeService> _logger;

    public EnvironmentUpgradeService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        UpgradeFleetService fleet,
        TimeProvider clock,
        ILogger<EnvironmentUpgradeService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _fleet = fleet;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; a planned upgrade was changed outside an authenticated request.");

    // ── Reading ─────────────────────────────────────────────────────────

    /// <summary>
    /// The open upgrades, each with its lines counted by derived state. Soonest planned slot
    /// first, then the newest; an upgrade with no slot goes after the ones that have one.
    /// Only lines the caller can see are counted.
    /// </summary>
    public async Task<List<EnvironmentUpgradeSummary>> ListOpenAsync(CancellationToken ct = default)
    {
        var headers = await _db.OeEnvironmentUpgrades.AsNoTracking()
            .Where(u => u.ClosedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = headers
            .OrderBy(u => u.PlannedAt is null)
            .ThenBy(u => u.PlannedAt)
            .ThenByDescending(u => u.CreatedAt)
            .ToList();
        return await SummariseAsync(ordered, ct).ConfigureAwait(false);
    }

    /// <summary>How many upgrades are marked done: the Archive's count, without reading their lines.</summary>
    public Task<int> CountArchivedAsync(CancellationToken ct = default) =>
        _db.OeEnvironmentUpgrades.AsNoTracking().CountAsync(u => u.ClosedAt != null, ct);

    /// <summary>
    /// The upgrades marked done, most recently closed first, optionally narrowed to those
    /// whose name or target release contains <paramref name="search"/> (case-insensitive).
    /// </summary>

    public async Task<List<EnvironmentUpgradeSummary>> ListArchivedAsync(string? search = null, CancellationToken ct = default)
    {
        var query = _db.OeEnvironmentUpgrades.AsNoTracking().Where(u => u.ClosedAt != null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower(CultureInfo.InvariantCulture);
            query = query.Where(u => u.Name.ToLower().Contains(term) || u.TargetVersion.ToLower().Contains(term));
        }

        var headers = await query
            .OrderByDescending(u => u.ClosedAt)
            .ThenByDescending(u => u.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        return await SummariseAsync(headers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One upgrade with its lines, or null when there is no such upgrade. Each line carries
    /// its environment's fleet row (from <see cref="UpgradeFleetService"/>, never a second
    /// read of the mirror), its derived state, the last action taken on it from this
    /// upgrade, who is to check it, and the check itself. Lines whose environment the caller
    /// cannot see - or that Business Central no longer reports - are left out.
    /// </summary>
    public async Task<EnvironmentUpgradeDetail?> GetAsync(int upgradeId, CancellationToken ct = default)
    {
        var header = await _db.OeEnvironmentUpgrades.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == upgradeId, ct).ConfigureAwait(false);
        if (header is null) return null;

        var lines = await BuildLinesAsync([header], ct).ConfigureAwait(false);
        var mine = lines.GetValueOrDefault(header.Id, []);
        var acted = await ActedUponAsync([header.Id], ct).ConfigureAwait(false);
        return new EnvironmentUpgradeDetail(Summarise(header, mine, acted.Contains(header.Id)), mine);
    }

    /// <summary>
    /// Which open upgrade holds each environment, keyed by environment id, so the picker can
    /// show an environment as taken - naming the upgrade - before anyone ticks it, rather than
    /// learning it from <see cref="AddLinesAsync"/>'s refusal afterwards. Only lines the caller
    /// can see are read: the same visibility join as every other line read here. An
    /// environment is on one open upgrade at a time (the filtered unique index), so each key
    /// has one owner.
    /// </summary>
    public async Task<Dictionary<int, UpgradeLineOwner>> ListOpenLineOwnersAsync(CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        var owners = await _db.OeEnvironmentUpgradeLines.AsNoTracking()
            .Where(l => l.IsOpen)
            .Where(l => _db.OeProjects.Where(visible)
                .Any(p => p.Id == l.ProjectId && p.DeletedAt == null))
            .Select(l => new { l.EnvironmentId, l.UpgradeId, UpgradeName = l.Upgrade!.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // ToDictionary would throw on a duplicate; the index rules one out, but a read that
        // races a reopen is not worth an exception on a page that only draws a lock.
        var result = new Dictionary<int, UpgradeLineOwner>();
        foreach (var o in owners)
        {
            result.TryAdd(o.EnvironmentId, new UpgradeLineOwner(o.UpgradeId, o.UpgradeName));
        }
        return result;
    }

    private async Task<List<EnvironmentUpgradeSummary>> SummariseAsync(
        List<OeEnvironmentUpgrade> headers, CancellationToken ct)
    {
        if (headers.Count == 0) return [];
        var lines = await BuildLinesAsync(headers, ct).ConfigureAwait(false);
        var acted = await ActedUponAsync(headers.Select(h => h.Id).ToList(), ct).ConfigureAwait(false);
        return headers.Select(h => Summarise(h, lines.GetValueOrDefault(h.Id, []), acted.Contains(h.Id))).ToList();
    }

    /// <summary>
    /// Which of <paramref name="upgradeIds"/> any action row carries - the fact
    /// <see cref="DeleteAsync"/> refuses on, read the same way: every action, whether or
    /// not its line is visible to the caller or its environment still in the fleet. Only
    /// the ids come back; the rows stay behind the organisation filter like any read here.
    /// </summary>
    private async Task<HashSet<int>> ActedUponAsync(List<int> upgradeIds, CancellationToken ct) =>
        (await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.UpgradeId != null && upgradeIds.Contains(a.UpgradeId.Value))
            .Select(a => a.UpgradeId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false))
        .ToHashSet();

    private static EnvironmentUpgradeSummary Summarise(
        OeEnvironmentUpgrade h, List<EnvironmentUpgradeLineRow> lines, bool anythingStarted)
    {
        var counts = lines
            .GroupBy(l => l.State)
            .ToDictionary(g => g.Key, g => g.Count());
        return new EnvironmentUpgradeSummary(
            h.Id, h.Name, h.TargetVersion, h.PlannedAt, h.Note,
            h.CreatedBy, h.CreatedAt, h.ClosedAt, h.ClosedBy, h.UpdatedAt,
            EnvironmentUpgradeLineState.DeriveStatus(h.ClosedAt is not null, lines.Select(l => l.State)),
            counts, lines.Count)
        {
            AnythingStarted = anythingStarted,
        };
    }

    /// <summary>
    /// The visible lines of <paramref name="headers"/>, keyed by upgrade, each with its state.
    /// Three reads whatever the number of upgrades: the lines (through the visibility join),
    /// the action rows carrying these upgrades' ids, and the fleet.
    /// </summary>
    private async Task<Dictionary<int, List<EnvironmentUpgradeLineRow>>> BuildLinesAsync(
        IReadOnlyList<OeEnvironmentUpgrade> headers, CancellationToken ct)
    {
        var ids = headers.Select(h => h.Id).ToList();
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        var lines = await _db.OeEnvironmentUpgradeLines.AsNoTracking()
            .Where(l => ids.Contains(l.UpgradeId))
            .Where(l => _db.OeProjects.Where(visible)
                .Any(p => p.Id == l.ProjectId && p.DeletedAt == null))
            .Select(l => new
            {
                l.Id,
                l.UpgradeId,
                l.EnvironmentId,
                l.AssigneeUserId,
                AssigneeName = l.AssigneeUser == null
                    ? null
                    : l.AssigneeUser.DisplayName != "" ? l.AssigneeUser.DisplayName : l.AssigneeUser.Email,
                l.CheckedAt,
                l.CheckedBy,
                l.Note,
                l.AddedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (lines.Count == 0) return new();

        var actions = (await _db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.UpgradeId != null && ids.Contains(a.UpgradeId.Value))
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(a => (a.UpgradeId!.Value, a.EnvironmentId));

        // The fleet is the mirror as the Upgrades page reads it, deleted-but-restorable
        // environments included: a line's environment deleted mid-wave is still worth seeing.
        var fleet = (await _fleet.ListFleetAsync(includeSoftDeleted: true, ct).ConfigureAwait(false))
            .ToDictionary(r => r.EnvironmentId);
        var byId = headers.ToDictionary(h => h.Id);

        var result = new Dictionary<int, List<EnvironmentUpgradeLineRow>>();
        foreach (var line in lines)
        {
            if (!fleet.TryGetValue(line.EnvironmentId, out var row)) continue;
            var header = byId[line.UpgradeId];
            var own = actions[(line.UpgradeId, line.EnvironmentId)].ToList();
            var state = EnvironmentUpgradeLineState.Derive(row, own, line.CheckedAt is not null, header.TargetVersion);
            var last = own
                .OrderByDescending(a => a.RequestedAt)
                .ThenByDescending(a => a.Id)
                .FirstOrDefault();

            if (!result.TryGetValue(line.UpgradeId, out var list))
            {
                result[line.UpgradeId] = list = [];
            }
            list.Add(new EnvironmentUpgradeLineRow(
                line.Id, row, state,
                last is null ? null : UpgradeActionRow.From(last) with { UpgradeName = header.Name },
                line.AssigneeUserId, line.AssigneeName,
                line.CheckedAt, line.CheckedBy, line.Note, line.AddedAt));
        }

        // Ordered like the fleet: by customer, Production first, then name.
        foreach (var list in result.Values)
        {
            list.Sort((a, b) =>
            {
                var byProject = StringComparer.OrdinalIgnoreCase.Compare(a.Environment.ProjectName, b.Environment.ProjectName);
                if (byProject != 0) return byProject;
                var byProduction = b.Environment.IsProduction.CompareTo(a.Environment.IsProduction);
                return byProduction != 0
                    ? byProduction
                    : StringComparer.OrdinalIgnoreCase.Compare(a.Environment.EnvironmentName, b.Environment.EnvironmentName);
            });
        }
        return result;
    }

    // ── The header ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates an open upgrade with no lines and returns its id. Anyone in the organisation
    /// may create one; adding environments to it is where the environment-updates grant is
    /// checked.
    /// </summary>
    /// <param name="targetVersion">The release the wave goes to, as Major.Minor (<c>28.5</c>).</param>
    /// <param name="plannedAt">The slot the team has in mind, if any. Advisory.</param>
    public async Task<int> CreateAsync(
        string? name, string? targetVersion, DateTimeOffset? plannedAt, string? note, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var details = ValidateDetails(name, targetVersion, note);

        var now = _clock.GetUtcNow().UtcDateTime;
        var createdBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct).ConfigureAwait(false);
        var upgrade = new OeEnvironmentUpgrade
        {
            OrganizationId = orgId,
            Name = details.Name,
            TargetVersion = details.TargetVersion,
            PlannedAt = plannedAt?.UtcDateTime,
            Note = details.Note,
            CreatedByUserId = _orgContext.CurrentUserId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.OeEnvironmentUpgrades.Add(upgrade);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} created planned upgrade {UpgradeId} ({UpgradeName}, target {TargetVersion}).",
            _orgContext.CurrentUserId, upgrade.Id, upgrade.Name, upgrade.TargetVersion);
        return upgrade.Id;
    }

    /// <summary>Changes the name, target release, planned slot and note. Same rules as <see cref="CreateAsync"/>; refused once the upgrade is done.</summary>
    public async Task UpdateDetailsAsync(
        int upgradeId, string? name, string? targetVersion, DateTimeOffset? plannedAt, string? note,
        CancellationToken ct = default)
    {
        RequireOrganizationId();
        var details = ValidateDetails(name, targetVersion, note);
        var upgrade = await LoadOpenAsync(upgradeId, ct).ConfigureAwait(false);

        if (upgrade.TargetVersion != details.TargetVersion)
        {
            // A new target is a new "ready to check" to send, even if the old one went out.
            // Cleared in the same save as the new target: cleared earlier, a sweep in
            // between would announce the old target again and stamp it for the new one.
            var pending = await _db.OeEnvironmentUpgradeLines
                .Where(l => l.UpgradeId == upgradeId && l.CheckedAt == null && l.UpdatedNotifiedAt != null)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var line in pending) line.UpdatedNotifiedAt = null;
        }
        upgrade.Name = details.Name;
        upgrade.TargetVersion = details.TargetVersion;
        upgrade.PlannedAt = plannedAt?.UtcDateTime;
        upgrade.Note = details.Note;
        upgrade.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} updated planned upgrade {UpgradeId} ({UpgradeName}, target {TargetVersion}).",
            _orgContext.CurrentUserId, upgradeId, upgrade.Name, upgrade.TargetVersion);
    }

    /// <summary>
    /// Marks the upgrade done: it leaves the open list for the archive, and its environments
    /// are free to go on another open upgrade. Returns how many of its lines were still
    /// unchecked, so the page can say so in its confirmation. Needs the environment-updates
    /// grant on every solution the upgrade touches.
    /// </summary>
    public async Task<int> CloseAsync(int upgradeId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var upgrade = await LoadWithLinesAsync(upgradeId, ct).ConfigureAwait(false);
        if (upgrade.ClosedAt is not null)
        {
            throw Refusal("Upgrade", "This upgrade is already marked done.");
        }
        await EnsureCanManageAllAsync(upgrade.Lines, ct).ConfigureAwait(false);

        var now = _clock.GetUtcNow().UtcDateTime;
        upgrade.ClosedAt = now;
        upgrade.ClosedByUserId = _orgContext.CurrentUserId;
        upgrade.ClosedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct).ConfigureAwait(false);
        upgrade.UpdatedAt = now;
        foreach (var line in upgrade.Lines) line.IsOpen = false;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var uncheckedCount = upgrade.Lines.Count(l => l.CheckedAt is null);
        _logger.LogInformation(
            "User {UserId} marked planned upgrade {UpgradeId} done ({LineCount} environments, {Unchecked} unchecked).",
            _orgContext.CurrentUserId, upgradeId, upgrade.Lines.Count, uncheckedCount);
        return uncheckedCount;
    }

    /// <summary>
    /// Takes a done upgrade back out of the archive. Refused when any of its environments has
    /// meanwhile gone on another open upgrade - an environment is on one open upgrade at a
    /// time - naming them, so the person knows what to take off where.
    /// </summary>
    public async Task ReopenAsync(int upgradeId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var upgrade = await LoadWithLinesAsync(upgradeId, ct).ConfigureAwait(false);
        if (upgrade.ClosedAt is null)
        {
            throw Refusal("Upgrade", "This upgrade is already open.");
        }
        await EnsureCanManageAllAsync(upgrade.Lines, ct).ConfigureAwait(false);

        var environmentIds = upgrade.Lines.Select(l => l.EnvironmentId).ToList();
        var taken = await OpenElsewhereAsync(environmentIds, upgradeId, ct).ConfigureAwait(false);
        if (taken.Count > 0)
        {
            throw Refusal("Upgrade",
                $"{Describe(taken)} Take them off first, or start a new upgrade for what is left.");
        }

        upgrade.ClosedAt = null;
        upgrade.ClosedByUserId = null;
        upgrade.ClosedBy = null;
        upgrade.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        // A line that reached the target while the upgrade was closed is old news, not a
        // "ready to check" to send now.
        var versions = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => environmentIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Version, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var line in upgrade.Lines)
        {
            line.IsOpen = true;
            if (line.UpdatedNotifiedAt is null
                && EnvironmentUpgradeLineState.IsOnTarget(versions.GetValueOrDefault(line.EnvironmentId), upgrade.TargetVersion))
            {
                line.UpdatedNotifiedAt = now;
            }
        }
        await SaveGuardingOpenIndexAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} reopened planned upgrade {UpgradeId}.", _orgContext.CurrentUserId, upgradeId);
    }

    /// <summary>
    /// Starts the second pass: a new open upgrade with the same target release and note,
    /// whose lines are this upgrade's environments that failed or were never started
    /// (<see cref="EnvironmentUpgradeLineState.IsLeftover"/>). Returns the new upgrade's id.
    ///
    /// <para>The source must be done first. Its environments are held by it while it is
    /// open, and one environment is on one open upgrade at a time, so a second open upgrade
    /// cannot take them without quietly taking them off the first.</para>
    /// </summary>
    public async Task<int> CreateFromLeftoversAsync(int upgradeId, string? name, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var trimmedName = ValidateName(name);

        var source = await _db.OeEnvironmentUpgrades.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == upgradeId, ct).ConfigureAwait(false)
            ?? throw Gone();
        if (source.ClosedAt is null)
        {
            throw Refusal("Upgrade",
                "Mark this upgrade done first. Its environments stay on it while it is open, so they can't move to a new one yet.");
        }

        var lines = (await BuildLinesAsync([source], ct).ConfigureAwait(false)).GetValueOrDefault(source.Id, []);
        var leftovers = lines.Where(l => EnvironmentUpgradeLineState.IsLeftover(l.State)).ToList();
        if (leftovers.Count == 0)
        {
            throw Refusal("Upgrade",
                "Every environment on this upgrade is updated, checked or still running, so there is nothing left over to carry on with.");
        }

        foreach (var projectId in leftovers.Select(l => l.Environment.ProjectId).Distinct())
        {
            await _access.EnsureCanManageEnvironmentUpdatesAsync(projectId, ct).ConfigureAwait(false);
        }

        var taken = await OpenElsewhereAsync(leftovers.Select(l => l.Environment.EnvironmentId).ToList(), null, ct)
            .ConfigureAwait(false);
        if (taken.Count > 0)
        {
            throw Refusal("Upgrade", $"{Describe(taken)} Take them off first, then try again.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var createdBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct).ConfigureAwait(false);
        var upgrade = new OeEnvironmentUpgrade
        {
            OrganizationId = orgId,
            Name = trimmedName,
            TargetVersion = source.TargetVersion,
            Note = source.Note,
            CreatedByUserId = _orgContext.CurrentUserId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            Lines = leftovers.Select(l => new OeEnvironmentUpgradeLine
            {
                OrganizationId = orgId,
                EnvironmentId = l.Environment.EnvironmentId,
                ProjectId = l.Environment.ProjectId,
                IsOpen = true,
                AddedAt = now,
            }).ToList(),
        };
        _db.OeEnvironmentUpgrades.Add(upgrade);
        await SaveGuardingOpenIndexAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} created planned upgrade {UpgradeId} ({UpgradeName}) from the {LeftoverCount} leftovers of upgrade {SourceUpgradeId}.",
            _orgContext.CurrentUserId, upgrade.Id, upgrade.Name, leftovers.Count, upgradeId);
        return upgrade.Id;
    }

    /// <summary>
    /// Deletes an upgrade outright - only while nothing has been done from it. Once an
    /// action carries its id it is part of the record, and marking it done is the way out.
    /// </summary>
    public async Task DeleteAsync(int upgradeId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var upgrade = await LoadWithLinesAsync(upgradeId, ct).ConfigureAwait(false);

        var acted = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .AnyAsync(a => a.UpgradeId == upgradeId, ct).ConfigureAwait(false);
        if (acted)
        {
            throw Refusal("Upgrade",
                "Something has already been done from this upgrade, so it stays on the record. Mark it done instead.");
        }
        await EnsureCanManageAllAsync(upgrade.Lines, ct).ConfigureAwait(false);

        _db.OeEnvironmentUpgrades.Remove(upgrade);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} deleted planned upgrade {UpgradeId} ({UpgradeName}, {LineCount} environments).",
            _orgContext.CurrentUserId, upgradeId, upgrade.Name, upgrade.Lines.Count);
    }

    // ── Lines ───────────────────────────────────────────────────────────

    /// <summary>
    /// Puts environments on an open upgrade. Each environment is judged on its own, so one
    /// that cannot go on does not cost the others: it is refused when the caller cannot see
    /// it, cannot manage its solution's updates, when Business Central no longer has it or
    /// has deleted it, or when it is already on another open upgrade (which the refusal
    /// names). One already on this upgrade is passed over quietly. Refusals come back keyed
    /// by environment id; only a problem with the upgrade itself throws.
    /// </summary>
    public async Task<AddUpgradeLinesResult> AddLinesAsync(
        int upgradeId, IEnumerable<int> environmentIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(environmentIds);
        var orgId = RequireOrganizationId();
        var wanted = environmentIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            throw Refusal("Environments", "Pick at least one environment to add.");
        }
        var upgrade = await LoadOpenAsync(upgradeId, ct).ConfigureAwait(false);

        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var environments = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => wanted.Contains(e.Id))
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
            .Select(e => new
            {
                e.Id, e.ProjectId, e.Name, e.MissingSince, e.SoftDeletedOn, e.Status,
                ProjectName = e.Project!.Name,
            })
            .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        var existing = await _db.OeEnvironmentUpgradeLines.AsNoTracking()
            .Where(l => wanted.Contains(l.EnvironmentId) && (l.UpgradeId == upgradeId || l.IsOpen))
            .Select(l => new { l.EnvironmentId, l.UpgradeId, UpgradeName = l.Upgrade!.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var added = new List<int>();
        var alreadyOn = new List<int>();
        var refused = new Dictionary<string, string>();
        var grant = new Dictionary<int, bool>();
        var now = _clock.GetUtcNow().UtcDateTime;

        foreach (var environmentId in wanted)
        {
            var key = environmentId.ToString(CultureInfo.InvariantCulture);
            if (!environments.TryGetValue(environmentId, out var env) || env.MissingSince is not null)
            {
                refused[key] = "That environment no longer exists, or you can't see its solution.";
                continue;
            }
            if (existing.Any(l => l.EnvironmentId == environmentId && l.UpgradeId == upgradeId))
            {
                alreadyOn.Add(environmentId);
                continue;
            }
            if (env.SoftDeletedOn is not null || BcEnvironmentStatus.IsSoftDeleted(env.Status))
            {
                refused[key] = $"{env.Name} ({env.ProjectName}) has been deleted in Business Central, so it can't be updated.";
                continue;
            }
            if (existing.FirstOrDefault(l => l.EnvironmentId == environmentId) is { } other)
            {
                refused[key] = $"{env.Name} ({env.ProjectName}) is already on the open upgrade \"{other.UpgradeName}\". Take it off that one first.";
                continue;
            }
            if (!grant.TryGetValue(env.ProjectId, out var mayAct))
            {
                grant[env.ProjectId] = mayAct =
                    await _access.CanManageEnvironmentUpdatesAsync(env.ProjectId, ct).ConfigureAwait(false);
            }
            if (!mayAct)
            {
                refused[key] = $"You can't manage updates for {env.ProjectName}.";
                continue;
            }

            _db.OeEnvironmentUpgradeLines.Add(new OeEnvironmentUpgradeLine
            {
                OrganizationId = orgId,
                UpgradeId = upgradeId,
                EnvironmentId = environmentId,
                ProjectId = env.ProjectId,
                IsOpen = true,
                AddedAt = now,
            });
            added.Add(environmentId);
        }

        if (added.Count > 0)
        {
            upgrade.UpdatedAt = now;
            await SaveGuardingOpenIndexAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "User {UserId} added {Added} environments to planned upgrade {UpgradeId} ({AlreadyOn} already on it, {Refused} refused).",
            _orgContext.CurrentUserId, added.Count, upgradeId, alreadyOn.Count, refused.Count);
        return new AddUpgradeLinesResult(added, alreadyOn, refused);
    }

    /// <summary>
    /// Takes an environment off an open upgrade - only while nothing has been done to it
    /// from this upgrade. After that the line is part of the record.
    /// </summary>
    public async Task RemoveLineAsync(int lineId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var line = await LoadLineForWriteAsync(lineId, ct).ConfigureAwait(false);

        var acted = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .AnyAsync(a => a.UpgradeId == line.UpgradeId && a.EnvironmentId == line.EnvironmentId, ct)
            .ConfigureAwait(false);
        if (acted)
        {
            throw Refusal("Line",
                "Something has already been done to this environment from this upgrade, so it stays on it.");
        }

        _db.OeEnvironmentUpgradeLines.Remove(line);
        line.Upgrade!.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} took environment {EnvironmentId} off planned upgrade {UpgradeId}.",
            _orgContext.CurrentUserId, line.EnvironmentId, line.UpgradeId);
    }

    /// <summary>Sets who is to check a line after its update, or nobody with a null <paramref name="userId"/>.</summary>
    public async Task AssignAsync(int lineId, int? userId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        if (userId is { } assignee
            && !await _db.Users.AsNoTracking().AnyAsync(u => u.Id == assignee, ct).ConfigureAwait(false))
        {
            throw Refusal("Assignee", "That person isn't in this organisation.");
        }
        var line = await LoadLineForWriteAsync(lineId, ct).ConfigureAwait(false);

        if (userId is not null && line.AssigneeUserId != userId && line.CheckedAt is null)
        {
            // The new checker has not been told, so the next sweep tells them if it is ready.
            line.UpdatedNotifiedAt = null;
        }
        line.AssigneeUserId = userId;
        line.Upgrade!.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} assigned environment {EnvironmentId} on planned upgrade {UpgradeId} to user {AssigneeUserId}.",
            _orgContext.CurrentUserId, line.EnvironmentId, line.UpgradeId, userId);
    }

    /// <summary>
    /// Ticks or unticks a line's after-upgrade check and sets its note in one write. Ticking
    /// stamps the acting person and the time - once: ticking a line that is already ticked
    /// keeps the stamp of whoever did the check. Unticking clears both. The note replaces
    /// the stored one, trimmed, at most <see cref="LineNoteMaxLength"/> characters; blank
    /// clears it. A page that changes only one of the two uses
    /// <see cref="SetCheckedAsync(int, bool, CancellationToken)"/> or
    /// <see cref="SetLineNoteAsync"/>, so it never writes back a value it read earlier.
    /// </summary>
    public Task SetCheckedAsync(int lineId, bool isChecked, string? note, CancellationToken ct = default) =>
        WriteCheckAsync(lineId, isChecked, setNote: true, note, ct);

    /// <summary>
    /// Ticks or unticks a line's check and leaves its note as stored - the tick in the lines,
    /// which must not write back a note somebody changed since the page last read it.
    /// Same stamping rule as <see cref="SetCheckedAsync(int, bool, string?, CancellationToken)"/>.
    /// </summary>
    public Task SetCheckedAsync(int lineId, bool isChecked, CancellationToken ct = default) =>
        WriteCheckAsync(lineId, isChecked, setNote: false, null, ct);

    /// <summary>
    /// Sets a line's note and leaves the tick, its stamp included, as stored: saving "reports
    /// OK" never ticks a line somebody unticked meanwhile, nor re-stamps a ticked one with
    /// the writer. Trimmed, at most <see cref="LineNoteMaxLength"/> characters; blank clears.
    /// </summary>
    public Task SetLineNoteAsync(int lineId, string? note, CancellationToken ct = default) =>
        WriteCheckAsync(lineId, isChecked: null, setNote: true, note, ct);

    /// <param name="isChecked">The tick to set, or null to leave it as stored.</param>
    /// <param name="setNote">False leaves the stored note alone.</param>
    private async Task WriteCheckAsync(int lineId, bool? isChecked, bool setNote, string? note, CancellationToken ct)
    {
        RequireOrganizationId();
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (setNote && trimmedNote is { Length: > LineNoteMaxLength })
        {
            throw Refusal("Note", $"Keep the note to {LineNoteMaxLength} characters or fewer.");
        }
        var line = await LoadLineForWriteAsync(lineId, ct).ConfigureAwait(false);

        var now = _clock.GetUtcNow().UtcDateTime;
        // A line already ticked keeps its stamp: ticking it again is not a second check.
        if (isChecked == true && line.CheckedAt is null)
        {
            line.CheckedAt = now;
            line.CheckedByUserId = _orgContext.CurrentUserId;
            line.CheckedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct).ConfigureAwait(false);
        }
        else if (isChecked == false)
        {
            line.CheckedAt = null;
            line.CheckedByUserId = null;
            line.CheckedBy = null;
        }
        if (setNote) line.Note = trimmedNote;
        line.Upgrade!.UpdatedAt = now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} set the check on environment {EnvironmentId} on planned upgrade {UpgradeId} ({CheckState}, note {NoteChange}).",
            _orgContext.CurrentUserId, line.EnvironmentId, line.UpgradeId,
            isChecked switch { true => "checked", false => "unchecked", null => "tick unchanged" },
            setNote ? "set" : "unchanged");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed record Details(string Name, string TargetVersion, string? Note);

    /// <summary>The header's rules, all reported at once so the form can mark every field.</summary>
    private static Details ValidateDetails(string? name, string? targetVersion, string? note)
    {
        var errors = new Dictionary<string, string>();
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0)
        {
            errors["Name"] = "Give the upgrade a name, e.g. 28.5 in November 2026.";
        }
        else if (trimmedName.Length > NameMaxLength)
        {
            errors["Name"] = $"Keep the name to {NameMaxLength} characters or fewer.";
        }

        var version = NormaliseTargetVersion(targetVersion);
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            errors["TargetVersion"] = "Choose the Business Central version the upgrade goes to, e.g. 28.5.";
        }
        else if (version is null)
        {
            errors["TargetVersion"] = "Write the version as major.minor, e.g. 28.5.";
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > NoteMaxLength })
        {
            errors["Note"] = $"Keep the note to {NoteMaxLength} characters or fewer.";
        }

        if (errors.Count > 0) throw new PlanValidationException(errors);
        return new Details(trimmedName, version!, trimmedNote);
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw Refusal("Name", "Give the upgrade a name, e.g. 28.5 in November 2026.");
        if (trimmed.Length > NameMaxLength) throw Refusal("Name", $"Keep the name to {NameMaxLength} characters or fewer.");
        return trimmed;
    }

    /// <summary>
    /// A target release as Major.Minor with leading zeros dropped ("28.05" is "28.5"), or null
    /// when it is not one. The numeric per-segment compare the mirror uses
    /// (<see cref="ProjectConnectionService.CompareVersions"/>) reads it either way; storing
    /// one spelling keeps the archive search and the page consistent.
    /// </summary>
    internal static string? NormaliseTargetVersion(string? targetVersion)
    {
        var trimmed = targetVersion?.Trim() ?? string.Empty;
        if (!MajorMinorPattern.IsMatch(trimmed)) return null;
        var parts = trimmed.Split('.');
        return string.Create(CultureInfo.InvariantCulture,
            $"{int.Parse(parts[0], CultureInfo.InvariantCulture)}.{int.Parse(parts[1], CultureInfo.InvariantCulture)}");
    }

    private async Task<OeEnvironmentUpgrade> LoadOpenAsync(int upgradeId, CancellationToken ct)
    {
        var upgrade = await _db.OeEnvironmentUpgrades
            .FirstOrDefaultAsync(u => u.Id == upgradeId, ct).ConfigureAwait(false)
            ?? throw Gone();
        if (upgrade.ClosedAt is not null) throw ClosedRefusal();
        return upgrade;
    }

    /// <summary>The header with every line, visible or not: done, reopen and delete act on all of them.</summary>
    private async Task<OeEnvironmentUpgrade> LoadWithLinesAsync(int upgradeId, CancellationToken ct) =>
        await _db.OeEnvironmentUpgrades
            .Include(u => u.Lines)
            .FirstOrDefaultAsync(u => u.Id == upgradeId, ct).ConfigureAwait(false)
        ?? throw Gone();

    /// <summary>
    /// One line, tracked, for a write: reached through the visibility join (a line from a
    /// solution the caller cannot see reads as one that does not exist), on an open
    /// upgrade, with the grant checked on its solution.
    /// </summary>
    private async Task<OeEnvironmentUpgradeLine> LoadLineForWriteAsync(int lineId, CancellationToken ct)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var line = await _db.OeEnvironmentUpgradeLines
            .Include(l => l.Upgrade)
            .Where(l => l.Id == lineId)
            .Where(l => _db.OeProjects.Where(visible)
                .Any(p => p.Id == l.ProjectId && p.DeletedAt == null))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw Refusal("Line", "That environment is no longer on the upgrade. Reload the upgrade and try again.");
        if (line.Upgrade!.ClosedAt is not null) throw ClosedRefusal();
        await _access.EnsureCanManageEnvironmentUpdatesAsync(line.ProjectId, ct).ConfigureAwait(false);
        return line;
    }

    /// <summary>
    /// The grant on every solution among <paramref name="lines"/>, visible or not - a move
    /// that changes all of an upgrade's lines needs the right to change each of them.
    /// </summary>
    private async Task EnsureCanManageAllAsync(IEnumerable<OeEnvironmentUpgradeLine> lines, CancellationToken ct)
    {
        foreach (var projectId in lines.Select(l => l.ProjectId).Distinct())
        {
            await _access.EnsureCanManageEnvironmentUpdatesAsync(projectId, ct).ConfigureAwait(false);
        }
    }

    private sealed record TakenEnvironment(string EnvironmentName, string ProjectName, string UpgradeName);

    /// <summary>Which of <paramref name="environmentIds"/> are on an open upgrade other than <paramref name="exceptUpgradeId"/>.</summary>
    private async Task<List<TakenEnvironment>> OpenElsewhereAsync(
        List<int> environmentIds, int? exceptUpgradeId, CancellationToken ct) =>
        await _db.OeEnvironmentUpgradeLines.AsNoTracking()
            .Where(l => l.IsOpen && environmentIds.Contains(l.EnvironmentId)
                        && (exceptUpgradeId == null || l.UpgradeId != exceptUpgradeId))
            .OrderBy(l => l.Environment!.Name)
            .Select(l => new TakenEnvironment(l.Environment!.Name, l.Project!.Name, l.Upgrade!.Name))
            .ToListAsync(ct).ConfigureAwait(false);

    private static string Describe(List<TakenEnvironment> taken)
    {
        var parts = taken.Select(t => $"{t.EnvironmentName} ({t.ProjectName}) is on \"{t.UpgradeName}\"");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Saves, turning a clash on the one-open-upgrade index into words. The service checks
    /// first, so this only fires when two people put the same environment on two upgrades
    /// at the same moment.
    /// </summary>
    private async Task SaveGuardingOpenIndexAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw Refusal("Environments",
                "Another upgrade took one of these environments a moment ago. Reload and try again.");
        }
    }

    private static PlanValidationException Refusal(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });

    private static PlanValidationException Gone() =>
        Refusal("Upgrade", "That upgrade no longer exists. It may have been deleted.");

    private static PlanValidationException ClosedRefusal() =>
        Refusal("Upgrade", "This upgrade is marked done. Reopen it to change it.");
}

/// <summary>
/// A planned upgrade as the list shows it: the header, its derived status, and its visible
/// lines counted by state.
/// </summary>
/// <param name="LineCounts">Visible lines per derived state; a state with no lines is absent.</param>
/// <param name="LineCount">How many visible lines the upgrade has.</param>
public sealed record EnvironmentUpgradeSummary(
    int Id,
    string Name,
    string TargetVersion,
    DateTime? PlannedAt,
    string? Note,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    string? ClosedBy,
    DateTime UpdatedAt,
    UpgradeStatus Status,
    IReadOnlyDictionary<UpgradeLineState, int> LineCounts,
    int LineCount)
{
    /// <summary>True once somebody marked it done.</summary>
    public bool IsClosed => ClosedAt is not null;

    /// <summary>How many visible lines are in <paramref name="state"/>.</summary>
    public int Count(UpgradeLineState state) => LineCounts.GetValueOrDefault(state);

    /// <summary>How many visible lines have been checked.</summary>
    public int CheckedCount => Count(UpgradeLineState.Checked);

    /// <summary>
    /// True once anything has been started from the upgrade - a date moved, an update
    /// started or booked, even a booking cancelled before it was sent - on any of its lines,
    /// visible to the caller or not. Exactly what <see cref="EnvironmentUpgradeService.DeleteAsync"/>
    /// refuses on, so the list can hold Delete back before the click.
    /// </summary>
    public bool AnythingStarted { get; init; }
}

/// <summary>The open upgrade an environment is on, for the picker's lock.</summary>
public sealed record UpgradeLineOwner(int UpgradeId, string UpgradeName);

/// <summary>One upgrade and its visible lines.</summary>
public sealed record EnvironmentUpgradeDetail(EnvironmentUpgradeSummary Upgrade, List<EnvironmentUpgradeLineRow> Lines);

/// <summary>
/// One environment on a planned upgrade: its fleet row from the mirror, its derived state,
/// the last action taken on it from this upgrade, who checks it, and the check.
/// </summary>
public sealed record EnvironmentUpgradeLineRow(
    int LineId,
    UpgradeFleetRow Environment,
    UpgradeLineState State,
    UpgradeActionRow? LastAction,
    int? AssigneeUserId,
    string? AssigneeName,
    DateTime? CheckedAt,
    string? CheckedBy,
    string? Note,
    DateTime AddedAt)
{
    /// <summary>True when the after-upgrade check is ticked.</summary>
    public bool IsChecked => CheckedAt is not null;
}

/// <summary>
/// What adding environments to an upgrade did: which went on, which were on it already, and
/// which were refused, keyed by environment id with the reason in words.
/// </summary>
public sealed record AddUpgradeLinesResult(
    IReadOnlyList<int> Added,
    IReadOnlyList<int> AlreadyOnIt,
    IReadOnlyDictionary<string, string> Refused);
