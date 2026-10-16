using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>The stored words for <see cref="LicenceCheck.Outcome"/>.</summary>
public static class LicenceCheckOutcome
{
    /// <summary>Every checked object is inside the licence.</summary>
    public const string Passed = "passed";

    /// <summary>At least one checked object is outside the licence.</summary>
    public const string OutsideLicence = "outside_licence";

    /// <summary>The licence could not be compared: not read yet, out of date, or unreadable. <see cref="LicenceCheck.Reason"/> says why.</summary>
    public const string NotChecked = "not_checked";
}

/// <summary>One object of a build that the licence does not cover.</summary>
/// <param name="ObjectType">The AL object type as people read it: <c>Table</c>, <c>Page</c>, <c>Enum</c>.</param>
/// <param name="Message">The sentence: <c>Table 50120 "Rating" is not in the licensed table range 50000-50099</c>.
/// Stored rather than rebuilt, because it names the licensed ranges as they were when it was checked.</param>
public sealed record LicenceCheckFinding(string ObjectType, int ObjectId, string? ObjectName, string Message);

/// <summary>
/// A build's objects compared with the licence of one linked on-premises environment
/// (#1370). Stored on the build (<c>oe_project_builds.licence_check</c>) for the Solution's
/// linked Production tiers and every tier a deployment pipeline of the build's pipeline
/// targets, and worked out again against the target before a deployment.
/// </summary>
/// <param name="Reason">Why the licence was not checked and what to do, as sentences; null unless <see cref="Outcome"/> is not checked.</param>
/// <param name="Findings">The objects outside the licence, at most <see cref="LicenceRangeCheck.MaxStoredFindings"/> when stored.</param>
/// <param name="OutsideCount">How many objects are outside the licence, all of them, even when <see cref="Findings"/> was cut.</param>
/// <param name="CheckedObjectCount">How many of the build's objects the licence was asked about.</param>
/// <param name="TierVersion">The service tier's version as AL Workbench read it, which decides the BC 14 enum rule.</param>
/// <param name="CheckedAt">When the build was compared with the licence.</param>
/// <param name="LicenceReadAt">When AL Workbench last read the licence it was compared with; null when it never has.</param>
public sealed record LicenceCheck(
    int EnvironmentId,
    string EnvironmentName,
    string Outcome,
    string? Reason,
    IReadOnlyList<LicenceCheckFinding> Findings,
    int OutsideCount,
    int CheckedObjectCount,
    string? TierVersion,
    DateTime CheckedAt,
    DateTime? LicenceReadAt = null)
{
    [JsonIgnore] public bool IsPassed => Outcome == LicenceCheckOutcome.Passed;
    [JsonIgnore] public bool IsOutside => Outcome == LicenceCheckOutcome.OutsideLicence;
    [JsonIgnore] public bool IsNotChecked => Outcome == LicenceCheckOutcome.NotChecked;

    /// <summary>One sentence (two when not checked) for a build, a deployment log or an agent; the consequence first.</summary>
    public string Summary => Outcome switch
    {
        LicenceCheckOutcome.Passed => CheckedObjectCount == 0
            ? $"Licence checked against {EnvironmentName}: this build has no objects the licence has to cover."
            : $"Licence checked against {EnvironmentName}: every object in this build is inside the licence.",
        LicenceCheckOutcome.OutsideLicence => OutsideCount == 1
            ? $"Can't be deployed to {EnvironmentName}: 1 object is outside the customer's licence."
            : $"Can't be deployed to {EnvironmentName}: {OutsideCount} objects are outside the customer's licence.",
        _ => $"Licence not checked for {EnvironmentName}. {Reason}",
    };

    /// <summary>
    /// The deployment's refusal: the effect, the objects outside the licence (the first
    /// <see cref="LicenceRangeCheck.MaxStoredFindings"/>, then how many more), and what to do
    /// next. Only for <see cref="LicenceCheckOutcome.OutsideLicence"/>.
    /// </summary>
    public string RefusalFor(int buildId)
    {
        var named = Findings.Take(LicenceRangeCheck.MaxStoredFindings).ToList();
        return (OutsideCount == 1
                   ? $"Build #{buildId} can't be deployed to {EnvironmentName}: 1 object is outside the customer's licence there, so nothing was sent. "
                   : $"Build #{buildId} can't be deployed to {EnvironmentName}: {OutsideCount} objects are outside the customer's licence there, so nothing was sent. ")
               + string.Join("; ", named.Select(f => f.Message))
               + (named.Count < OutsideCount ? $"; and {OutsideCount - named.Count} more" : string.Empty)
               + (OutsideCount == 1
                   ? ". Ask the customer to add it to their licence, or renumber it to an object ID the licence covers, then deploy again."
                   : ". Ask the customer to add these objects to their licence, or renumber them to object IDs the licence covers, then deploy again.");
    }
}

/// <summary>
/// A build's licence check as a list of builds carries it: the outcome per environment,
/// without the objects (#1370). <c>get_solution_build</c> and the build page carry those.
/// </summary>
public sealed record LicenceCheckRow(int EnvironmentId, string EnvironmentName, string Outcome, int OutsideCount, string Summary, DateTime CheckedAt)
{
    public static LicenceCheckRow From(LicenceCheck check) =>
        new(check.EnvironmentId, check.EnvironmentName, check.Outcome, check.OutsideCount, check.Summary, check.CheckedAt);
}

/// <summary>
/// Compares the objects a build produced with the object ranges in the licence AL
/// Workbench read from an on-premises service tier (#1370). Every on-premises Business
/// Central refuses to publish an extension whose objects its licence does not cover;
/// online never does for per-tenant extensions, so only on-premises environments are
/// checked. See <c>.design/on-prem-gateway.md</c>, "Licensed object ranges, as built".
///
/// <para><b>The rules.</b> Full objects only (table, page, report, codeunit, query,
/// xmlport, and a permission set where the licence lists permission set ranges at all),
/// each against the ranges of its own type. Enums, interfaces and every extension object
/// are never checked, except that on Business Central 14 an enum's id must fall in a
/// licensed <em>table</em> range. A range counts when the licence states any permission
/// for it, or none at all (see <see cref="Grants"/>).</para>
///
/// <para><b>Not checked rather than passed.</b> When the licence is not read yet, the
/// customer's server is not connected, the tier or its tenant is no longer reported, the
/// licence has not been read for an hour, its text was cut or only part of its ranges
/// could be read, the tier's version is unknown, or the build's objects can't be read,
/// the answer is <see cref="LicenceCheckOutcome.NotChecked"/> with the reason, never a
/// silent pass. The reasons are shown to a consultant as they are, so they name AL
/// Workbench and the customer's server, not the gateway.</para>
/// </summary>
public static partial class LicenceRangeCheck
{
    /// <summary>Findings kept per environment on a build, and named in a refusal; the count is kept whole.</summary>
    public const int MaxStoredFindings = 200;

    /// <summary>Ranges named in one finding before "and N more".</summary>
    private const int RangesShown = 4;

    /// <summary>What a not-checked reason that waits on the next read says to do.</summary>
    private const string ReadsAgain =
        "It reads it every 15 minutes while the customer's server is connected; check the environment page if this does not change.";

    /// <summary>
    /// The AL object kinds checked, as <c>oe_module_objects.kind</c> and the symbol reader
    /// spell them, with the licence's word for the type, the word that starts a finding and
    /// the word that names its ranges.
    /// </summary>
    private static readonly Dictionary<string, (string LicenceType, string Display, string RangeWord)> FullObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["table"] = ("Table", "Table", "table"),
        ["page"] = ("Page", "Page", "page"),
        ["report"] = ("Report", "Report", "report"),
        ["codeunit"] = ("Codeunit", "Codeunit", "codeunit"),
        ["query"] = ("Query", "Query", "query"),
        ["xmlport"] = ("XMLport", "XMLport", "XMLport"),
        ["permissionset"] = ("PermissionSet", "Permission set", "permission set"),
    };

    private const string EnumKind = "enum";

    /// <summary>The kinds read from a build: the full objects, then enums for the BC 14 rule.</summary>
    private static readonly string[] KindsOrder = [.. FullObjects.Keys, EnumKind];

    private static readonly string[] FullKinds = [.. FullObjects.Keys];

    /// <summary>Permission words that grant nothing. Anything else in the permissions text grants something.</summary>
    private static readonly HashSet<string> NoPermission = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "no", "0", "n/a", "na", "false",
    };

    private static readonly char[] WordSeparators = [' ', '\t', ',', ';', '|'];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The objects of <paramref name="objects"/> that <paramref name="ranges"/> do not cover
    /// on a tier running <paramref name="tierVersion"/>, in type then id order. Pure: the
    /// whole rule lives here, and both the build and the deployment call it.
    /// </summary>
    public static (IReadOnlyList<LicenceCheckFinding> Outside, int Checked) Evaluate(
        IEnumerable<LicensedObject> objects, IEnumerable<LicenceRange> ranges, Version tierVersion)
    {
        var rangeList = ranges.ToList();
        var byType = rangeList
            .Where(r => Grants(r.Permissions))
            .GroupBy(r => r.ObjectType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.FromId).ToList(), StringComparer.OrdinalIgnoreCase);
        // A licence that lists no permission set ranges at all says nothing about them:
        // older licences predate permission set objects, and BC has not been seen to ask.
        var permissionSetsLicensed = rangeList.Any(r => string.Equals(r.ObjectType, "PermissionSet", StringComparison.OrdinalIgnoreCase));
        var bc14 = IsBc14(tierVersion);

        var outside = new List<LicenceCheckFinding>();
        var checkedCount = 0;
        var seen = new HashSet<(string, int)>();
        foreach (var o in objects
                     .OrderBy(o => KindOrder(o.Kind))
                     .ThenBy(o => o.Id))
        {
            if (!seen.Add((o.Kind.ToLowerInvariant(), o.Id))) continue;
            string licenceType, display, rangeWord;
            var enumOnBc14 = false;
            if (FullObjects.TryGetValue(o.Kind, out var full))
            {
                if (full.LicenceType == "PermissionSet" && !permissionSetsLicensed) continue;
                (licenceType, display, rangeWord) = full;
            }
            else if (bc14 && string.Equals(o.Kind, EnumKind, StringComparison.OrdinalIgnoreCase))
            {
                (licenceType, display, rangeWord, enumOnBc14) = ("Table", "Enum", "table", true);
            }
            else
            {
                continue;
            }

            checkedCount++;
            var covering = byType.TryGetValue(licenceType, out var list) ? list : [];
            if (covering.Any(r => r.FromId <= o.Id && o.Id <= r.ToId)) continue;

            var name = string.IsNullOrWhiteSpace(o.Name) ? null : o.Name;
            var subject = name is null ? $"{display} {o.Id}" : $"{display} {o.Id} \"{name}\"";
            var message = (covering.Count, enumOnBc14) switch
            {
                (0, false) => $"{subject} is not in the licence, which has no {rangeWord} ranges",
                (0, true) => $"{subject} is not in the licence, which has no table ranges for BC 14 to check enums against",
                (_, false) => $"{subject} is not in the licensed {rangeWord} {RangesText(covering)}",
                (_, true) => $"{subject} is not in the licensed table {RangesText(covering)}, which BC 14 checks enums against",
            };
            outside.Add(new LicenceCheckFinding(display, o.Id, name, message));
        }
        return (outside, checkedCount);
    }

    /// <summary>
    /// Whether a range with these permissions counts as licensed. The parser keeps the
    /// permissions as the text after the two ids, trimmed, so which column a word stood in
    /// (read, insert, modify, delete, execute) is lost; the rule is therefore "any
    /// permission". No text at all (a layout that prints none, as the <c>.bclicense</c>
    /// samples do) counts. Text written as labels and values (<c>Execute: No</c>) is judged
    /// by its values only, so a label never grants on its own; otherwise any word but a
    /// "none" or a run of dashes counts.
    /// </summary>
    public static bool Grants(string? permissions)
    {
        if (string.IsNullOrWhiteSpace(permissions)) return true;
        if (permissions.Contains(':', StringComparison.Ordinal))
        {
            var values = LabelledValue().Matches(permissions).Select(m => m.Groups["value"].Value).ToList();
            return values.Any(GrantingWord);
        }
        return permissions.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Any(GrantingWord);
    }

    private static bool GrantingWord(string word) =>
        !NoPermission.Contains(word) && !word.All(c => c is '-' or '.' or '_');

    [GeneratedRegex(@"[^:\s,;|]+\s*:\s*(?<value>[^\s,;|]+)", RegexOptions.None, 200)]
    private static partial Regex LabelledValue();

    /// <summary>Business Central 14 and earlier, where an enum takes a table id from the licence.</summary>
    private static bool IsBc14(Version tierVersion) => tierVersion.Major <= 14;

    /// <summary>The licensed ranges of one type, as a finding names them: "range 50000-50099" or "ranges 50000-50099, 50200-50249".</summary>
    private static string RangesText(IReadOnlyList<LicenceRange> ranges)
    {
        var shown = string.Join(", ", ranges.Take(RangesShown).Select(r => r.FromId == r.ToId
            ? r.FromId.ToString(CultureInfo.InvariantCulture)
            : $"{r.FromId.ToString(CultureInfo.InvariantCulture)}-{r.ToId.ToString(CultureInfo.InvariantCulture)}"));
        if (ranges.Count == 1) return $"range {shown}";
        return ranges.Count > RangesShown ? $"ranges {shown} and {ranges.Count - RangesShown} more" : $"ranges {shown}";
    }

    private static int KindOrder(string kind) =>
        Array.FindIndex(KindsOrder, k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : KindsOrder.Length;

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The build's checks for the build to keep: against each linked on-premises
    /// environment of its Solution that is Production or that a deployment pipeline drawing
    /// from the build's pipeline targets, so a build that an automatic deployment refused
    /// says why where people look at it. Empty for an online Solution, one with no such
    /// tier, and a preview build (it is never deployed).
    /// </summary>
    public static async Task<List<LicenceCheck>> ForBuildAsync(AppDbContext db, int buildId, DateTime now, CancellationToken ct)
    {
        var build = await db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => new { b.ProjectId, b.PipelineId, b.ReleaseId, b.BcTarget, Hosting = b.Project!.HostingType })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (build is null || ProjectBuildTarget.IsPreview(build.BcTarget) || build.Hosting == ProjectHostingType.MicrosoftCloud) return [];

        var pipelineId = build.PipelineId;
        var environments = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == build.ProjectId && e.Kind == OeEnvironmentKind.OnPremises
                        && e.MissingSince == null && e.GatewayInstanceId != null
                        && (e.Type == "Production"
                            || (pipelineId != null && db.OeReleasePipelines.Any(r =>
                                r.ProjectEnvironmentId == e.Id && r.DeletedAt == null && r.BuildPipelineId == pipelineId))))
            .OrderBy(e => e.Name)
            .Select(e => e.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (environments.Count == 0) return [];

        var objects = new BuildObjects(db, buildId, build.ReleaseId);
        var checks = new List<LicenceCheck>();
        foreach (var id in environments)
        {
            if (await CheckAsync(db, id, objects, now, ct).ConfigureAwait(false) is { } check)
                checks.Add(check);
        }
        return checks;
    }

    /// <summary>
    /// The build's objects against one environment's licence, for a deployment to it.
    /// Null when the environment is not on-premises (online is never checked) or not there.
    /// </summary>
    public static async Task<LicenceCheck?> ForDeploymentAsync(AppDbContext db, int environmentId, int buildId, DateTime now, CancellationToken ct)
    {
        var releaseId = await db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => b.ReleaseId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return await CheckAsync(db, environmentId, new BuildObjects(db, buildId, releaseId), now, ct).ConfigureAwait(false);
    }

    private static async Task<LicenceCheck?> CheckAsync(
        AppDbContext db, int environmentId, BuildObjects objects, DateTime now, CancellationToken ct)
    {
        var env = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.MissingSince,
                e.GatewayInstanceId,
                Hosting = e.Project!.HostingType,
                Machine = e.GatewayInstance == null ? null : e.GatewayInstance.Machine,
                Instance = e.GatewayInstance == null ? null : e.GatewayInstance.Name,
                Version = e.GatewayInstance == null ? null : e.GatewayInstance.Version,
                InstanceMissing = e.GatewayInstance != null && e.GatewayInstance.MissingSince != null,
                // As the inventory compares tenant ids: trimmed, in any case.
                TenantMissing = e.GatewayInstance != null && e.TenantId != null
                                && e.GatewayInstance.Tenants.Any(t => t.TenantId.Trim().ToLower() == e.TenantId.Trim().ToLower()
                                                                      && t.MissingSince != null),
                GatewayState = e.GatewayInstance == null ? (Domain.Entities.GatewayState?)null : e.GatewayInstance.Gateway!.State,
                LastSeenAt = e.GatewayInstance == null ? null : e.GatewayInstance.Gateway!.LastSeenAt,
                RevokedAt = e.GatewayInstance == null ? null : e.GatewayInstance.Gateway!.RevokedAt,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (env is null || env.Hosting == ProjectHostingType.MicrosoftCloud) return null;

        DateTime? readAt = null;
        LicenceCheck NotChecked(string reason) =>
            new(env.Id, env.Name, LicenceCheckOutcome.NotChecked, reason, [], 0, 0, env.Version, now, readAt);

        var tier = $"{env.Machine} / {env.Instance}";
        if (env.GatewayInstanceId is not { } instanceId || env.MissingSince is not null)
            return NotChecked($"{env.Name} is no longer linked to a service tier on the customer's server. An administrator can link it again on the environment page.");

        var licence = await db.GatewayInstanceLicences.AsNoTracking()
            .Where(l => l.InstanceId == instanceId)
            .Select(l => new
            {
                l.ReadAt,
                l.Truncated,
                // The text only when something could not be read, to learn whether a range was lost.
                Text = l.ParseProblems == null ? null : l.Text,
                Ranges = l.Ranges.Select(r => new LicenceRange(r.ObjectType, r.FromId, r.ToId, r.Permissions)).ToList(),
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        readAt = licence?.ReadAt;

        var status = GatewayService.StatusOf(env.GatewayState ?? Domain.Entities.GatewayState.Pending, env.LastSeenAt, env.RevokedAt, now);
        if (status != GatewayStatus.Online)
            return NotChecked(status == GatewayStatus.Offline
                ? $"The connection to the customer's server is offline, so the licence AL Workbench last read on {tier} may be out of date. It is read again once the server is connected; check the environment page if this does not change."
                : $"The customer's server is not connected to AL Workbench, so the licence on {tier} can't be read. Check the environment page.");
        if (env.InstanceMissing || env.TenantMissing)
            return NotChecked($"AL Workbench no longer sees {tier} on the customer's server. Check the environment page.");
        if (licence is null)
            return NotChecked($"AL Workbench has not read the licence on {tier} yet. {ReadsAgain}");
        if (now - licence.ReadAt > OnPremisesTierFacts.StaleAfter)
            return NotChecked($"AL Workbench last read the licence on {tier} more than an hour ago, so it may be out of date. {ReadsAgain}");
        if (licence.Truncated)
            return NotChecked($"The licence on {tier} was too long for AL Workbench to read whole. Check the licence on the environment page.");
        if (licence.Ranges.Count == 0)
            return NotChecked($"AL Workbench could not read the object ranges in the licence on {tier}. Check the licence on the environment page.");
        if (licence.Text is { } text && !GatewayLicenceParser.Parse(text).RangesComplete)
            return NotChecked($"AL Workbench could read only some of the object ranges in the licence on {tier}, so an object could look outside it when it is not. Check the licence on the environment page.");
        if (OnPremisesLicenceService.ParseVersion(env.Version) is not { } version)
            return NotChecked($"AL Workbench does not know the version of {tier} yet. It reads the version every 15 minutes while the customer's server is connected; check the environment page if this does not change.");

        var read = await objects.GetAsync(withEnums: IsBc14(version), ct).ConfigureAwait(false);
        if (read.Problem is { } problem) return NotChecked(problem);

        var (outside, checkedCount) = Evaluate(read.Objects, licence.Ranges, version);
        return new LicenceCheck(env.Id, env.Name,
            outside.Count == 0 ? LicenceCheckOutcome.Passed : LicenceCheckOutcome.OutsideLicence,
            null, outside, outside.Count, checkedCount, env.Version, now, licence.ReadAt);
    }

    /// <summary>
    /// A build's objects, read once however many environments are checked. From the Object
    /// Explorer index when the build has one; otherwise from the symbols in the build's own
    /// <c>.app</c> files (a build staged from a GitHub release, or one whose index was
    /// removed). Enums are read only for a BC 14 tier, the one rule that needs them.
    /// </summary>
    private sealed class BuildObjects
    {
        private readonly AppDbContext _db;
        private readonly int _buildId;
        private readonly int? _releaseId;
        private ObjectsRead? _read;

        public BuildObjects(AppDbContext db, int buildId, int? releaseId)
        {
            _db = db;
            _buildId = buildId;
            _releaseId = releaseId;
        }

        public async Task<ObjectsRead> GetAsync(bool withEnums, CancellationToken ct)
        {
            if (_read is { } done && (done.WithEnums || !withEnums))
                return withEnums || !done.WithEnums ? done : done.WithoutEnums();
            _read = await ReadAsync(withEnums, ct).ConfigureAwait(false);
            return _read;
        }

        private async Task<ObjectsRead> ReadAsync(bool withEnums, CancellationToken ct)
        {
            var kinds = withEnums ? KindsOrder : FullKinds;
            if (_releaseId is { } release
                && await _db.OeModules.AsNoTracking().AnyAsync(m => m.ReleaseId == release, ct).ConfigureAwait(false))
            {
                var indexed = await _db.OeModuleObjects.AsNoTracking()
                    .Where(o => o.Module!.ReleaseId == release && o.ObjectId != null && kinds.Contains(o.Kind))
                    .Select(o => new LicensedObject(o.Kind, o.ObjectId!.Value, o.Name))
                    .ToListAsync(ct).ConfigureAwait(false);
                return new ObjectsRead(indexed, null, withEnums);
            }

            var files = await _db.OeProjectBuildArtifacts.AsNoTracking()
                .Where(a => a.ProjectBuildId == _buildId)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.FileName })
                .ToListAsync(ct).ConfigureAwait(false);
            if (files.Count == 0)
                return new ObjectsRead([], "This build has no app files to compare with the licence.", withEnums);

            var objects = new List<LicensedObject>();
            foreach (var file in files)
            {
                // One file at a time, so a build of many apps never holds them all at once.
                var content = await _db.OeProjectBuildArtifacts.AsNoTracking()
                    .Where(a => a.Id == file.Id)
                    .Select(a => a.Content)
                    .FirstAsync(ct).ConfigureAwait(false);
                AppPackage package;
                try
                {
                    await using var stream = new MemoryStream(content, writable: false);
                    package = await AppPackageReader.ReadAsync(stream, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ObjectsRead([],
                        $"AL Workbench could not read the objects in {file.FileName}, so there is nothing to compare with the licence.", withEnums);
                }
                objects.AddRange(package.Symbols.Objects
                    .Where(o => o.ObjectId is not null && kinds.Contains(o.Kind, StringComparer.OrdinalIgnoreCase))
                    .Select(o => new LicensedObject(o.Kind, o.ObjectId!.Value, o.Name)));
            }
            return new ObjectsRead(objects, null, withEnums);
        }
    }

    /// <summary>The objects read from a build, or why they could not be.</summary>
    private sealed record ObjectsRead(List<LicensedObject> Objects, string? Problem, bool WithEnums)
    {
        public ObjectsRead WithoutEnums() =>
            this with { Objects = Objects.Where(o => !string.Equals(o.Kind, EnumKind, StringComparison.OrdinalIgnoreCase)).ToList(), WithEnums = false };
    }

    // ------------------------------------------------------------------ storing

    /// <summary>The checks as stored on the build, with each one's findings cut to <see cref="MaxStoredFindings"/>; null for none.</summary>
    public static string? Serialize(IReadOnlyList<LicenceCheck> checks) =>
        checks.Count == 0
            ? null
            : JsonSerializer.Serialize(
                checks.Select(c => c.Findings.Count > MaxStoredFindings ? c with { Findings = c.Findings.Take(MaxStoredFindings).ToList() } : c).ToList(),
                Json);

    /// <summary>The checks stored on a build; empty when there are none or the text does not read.</summary>
    public static IReadOnlyList<LicenceCheck> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<LicenceCheck>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>One object of a build as the licence check reads it: the AL kind, its id and name.</summary>
public sealed record LicensedObject(string Kind, int Id, string? Name);
