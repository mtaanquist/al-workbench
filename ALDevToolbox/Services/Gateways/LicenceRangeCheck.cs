using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
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
/// <param name="Message">The sentence: <c>Table 50120 "Rating" is outside the licence (50000-50099)</c>.</param>
public sealed record LicenceCheckFinding(string ObjectType, int ObjectId, string? ObjectName, string Message);

/// <summary>
/// A build's objects compared with the licence of one linked on-premises environment
/// (#1370). Stored on the build (<c>oe_project_builds.licence_check</c>) for the Solution's
/// linked Production tiers, and worked out again against the target before a deployment.
/// </summary>
/// <param name="Reason">Why the licence was not checked, as a sentence; null unless <see cref="Outcome"/> is not checked.</param>
/// <param name="Findings">The objects outside the licence, at most <see cref="LicenceRangeCheck.MaxStoredFindings"/> when stored.</param>
/// <param name="OutsideCount">How many objects are outside the licence, all of them, even when <see cref="Findings"/> was cut.</param>
/// <param name="CheckedObjectCount">How many of the build's objects the licence was asked about.</param>
/// <param name="TierVersion">The service tier's version as the gateway read it, which decides the BC 14 enum rule.</param>
public sealed record LicenceCheck(
    int EnvironmentId,
    string EnvironmentName,
    string Outcome,
    string? Reason,
    IReadOnlyList<LicenceCheckFinding> Findings,
    int OutsideCount,
    int CheckedObjectCount,
    string? TierVersion,
    DateTime CheckedAt)
{
    [JsonIgnore] public bool IsPassed => Outcome == LicenceCheckOutcome.Passed;
    [JsonIgnore] public bool IsOutside => Outcome == LicenceCheckOutcome.OutsideLicence;
    [JsonIgnore] public bool IsNotChecked => Outcome == LicenceCheckOutcome.NotChecked;

    /// <summary>One sentence for a build, a deployment log or an agent.</summary>
    public string Summary => Outcome switch
    {
        LicenceCheckOutcome.Passed => CheckedObjectCount == 0
            ? $"Licence checked against {EnvironmentName}: this build has no objects the licence has to cover."
            : $"Licence checked against {EnvironmentName}: every object in this build is inside the licence.",
        LicenceCheckOutcome.OutsideLicence => OutsideCount == 1
            ? $"1 object in this build is outside the licence on {EnvironmentName}."
            : $"{OutsideCount} objects in this build are outside the licence on {EnvironmentName}.",
        _ => $"Licence not checked for {EnvironmentName}: {Reason}",
    };

    /// <summary>
    /// The deployment's refusal: the effect, every object outside the licence, and what to
    /// do next. Only for <see cref="LicenceCheckOutcome.OutsideLicence"/>.
    /// </summary>
    public string RefusalFor(int buildId) =>
        (OutsideCount == 1
            ? $"Build #{buildId} can't be deployed to {EnvironmentName}: 1 object is outside the customer's licence there, so nothing was sent. "
            : $"Build #{buildId} can't be deployed to {EnvironmentName}: {OutsideCount} objects are outside the customer's licence there, so nothing was sent. ")
        + string.Join("; ", Findings.Select(f => f.Message))
        + (Findings.Count < OutsideCount ? $"; and {OutsideCount - Findings.Count} more" : string.Empty)
        + ". Ask the customer to add these objects to their licence, or give them ids the licence covers, then deploy again.";
}

/// <summary>
/// Compares the objects a build produced with the object ranges in the licence the
/// gateway read from an on-premises service tier (#1370). Every on-premises Business
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
/// gateway is offline, the tier or its tenant is no longer reported, the licence has not
/// been read for an hour, its text was cut or its ranges could not be read, the tier's
/// version is unknown, or the build's objects were never indexed, the answer is
/// <see cref="LicenceCheckOutcome.NotChecked"/> with the reason, never a silent pass.</para>
/// </summary>
public static class LicenceRangeCheck
{
    /// <summary>Findings kept per environment on a build; the count is kept whole.</summary>
    public const int MaxStoredFindings = 200;

    /// <summary>Ranges named in one finding before "and N more".</summary>
    private const int RangesShown = 4;

    /// <summary>
    /// The AL object kinds checked, as <c>oe_module_objects.kind</c> stores them, with the
    /// licence's word for the type and the word people read.
    /// </summary>
    private static readonly Dictionary<string, (string LicenceType, string Display)> FullObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["table"] = ("Table", "Table"),
        ["page"] = ("Page", "Page"),
        ["report"] = ("Report", "Report"),
        ["codeunit"] = ("Codeunit", "Codeunit"),
        ["query"] = ("Query", "Query"),
        ["xmlport"] = ("XMLport", "XMLport"),
        ["permissionset"] = ("PermissionSet", "Permission set"),
    };

    /// <summary>The kinds read from a build: the full objects, and enums for the BC 14 rule.</summary>
    private static readonly string[] KindsRead = [.. FullObjects.Keys, "enum"];

    /// <summary>Permission words that grant nothing. Anything else in the permissions text grants something.</summary>
    private static readonly HashSet<string> NoPermission = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "no", "0", "n/a", "na",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The objects of <paramref name="objects"/> that <paramref name="ranges"/> do not cover
    /// on a tier running <paramref name="tierVersion"/>, in type then id order. Pure: the
    /// whole rule lives here, and both the build and the deployment call it.
    /// </summary>
    public static (IReadOnlyList<LicenceCheckFinding> Outside, int Checked) Evaluate(
        IEnumerable<LicensedObject> objects, IEnumerable<LicenceRange> ranges, Version tierVersion)
    {
        var granting = ranges.Where(r => Grants(r.Permissions)).ToList();
        var byType = granting
            .GroupBy(r => r.ObjectType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.FromId).ToList(), StringComparer.OrdinalIgnoreCase);
        // A licence that lists no permission set ranges at all says nothing about them:
        // older licences predate permission set objects, and BC has not been seen to ask.
        var permissionSetsLicensed = ranges.Any(r => string.Equals(r.ObjectType, "PermissionSet", StringComparison.OrdinalIgnoreCase));
        var bc14 = tierVersion.Major <= 14;

        var outside = new List<LicenceCheckFinding>();
        var checkedCount = 0;
        var seen = new HashSet<(string, int)>();
        foreach (var o in objects
                     .OrderBy(o => KindOrder(o.Kind))
                     .ThenBy(o => o.Id))
        {
            if (!seen.Add((o.Kind.ToLowerInvariant(), o.Id))) continue;
            string licenceType, display;
            bool enumOnBc14 = false;
            if (FullObjects.TryGetValue(o.Kind, out var full))
            {
                if (full.LicenceType == "PermissionSet" && !permissionSetsLicensed) continue;
                (licenceType, display) = full;
            }
            else if (bc14 && string.Equals(o.Kind, "enum", StringComparison.OrdinalIgnoreCase))
            {
                (licenceType, display, enumOnBc14) = ("Table", "Enum", true);
            }
            else
            {
                continue;
            }

            checkedCount++;
            var covering = byType.TryGetValue(licenceType, out var list) ? list : [];
            if (covering.Any(r => r.FromId <= o.Id && o.Id <= r.ToId)) continue;

            var named = string.IsNullOrWhiteSpace(o.Name) ? string.Empty : $" \"{o.Name}\"";
            var message = enumOnBc14
                ? $"Enum {o.Id}{named} needs table {o.Id} in the licence (BC 14)"
                : $"{display} {o.Id}{named} is outside the licence ({RangesText(covering, display)})";
            outside.Add(new LicenceCheckFinding(display, o.Id, string.IsNullOrWhiteSpace(o.Name) ? null : o.Name, message));
        }
        return (outside, checkedCount);
    }

    /// <summary>
    /// Whether a range with these permissions counts as licensed. The parser keeps the
    /// permissions as the text after the two ids, trimmed, so which column a word stood in
    /// (read, insert, modify, delete, execute) is lost; the rule is therefore "any
    /// permission": no text at all (a layout that prints none, as the <c>.bclicense</c>
    /// samples do) counts, and so does any word but a "none" or a run of dashes.
    /// </summary>
    public static bool Grants(string? permissions)
    {
        if (string.IsNullOrWhiteSpace(permissions)) return true;
        foreach (var word in permissions.Split([' ', '\t', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (NoPermission.Contains(word)) continue;
            if (word.All(c => c is '-' or '.' or '_')) continue;
            return true;
        }
        return false;
    }

    /// <summary>The licensed ranges of one type, as a finding names them.</summary>
    private static string RangesText(IReadOnlyList<LicenceRange> ranges, string display)
    {
        if (ranges.Count == 0) return $"it licenses no {display.ToLowerInvariant()} ids";
        var shown = ranges.Take(RangesShown).Select(r => r.FromId == r.ToId
            ? r.FromId.ToString(CultureInfo.InvariantCulture)
            : $"{r.FromId.ToString(CultureInfo.InvariantCulture)}-{r.ToId.ToString(CultureInfo.InvariantCulture)}");
        var text = string.Join(", ", shown);
        return ranges.Count > RangesShown ? $"{text} and {ranges.Count - RangesShown} more" : text;
    }

    private static int KindOrder(string kind) =>
        Array.FindIndex(KindsRead, k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : KindsRead.Length;

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// The build's checks against each linked on-premises Production environment of its
    /// Solution, for the build to keep. Empty for an online Solution, one with no linked
    /// Production tier, and a preview build (it is never deployed).
    /// </summary>
    public static async Task<List<LicenceCheck>> ForBuildAsync(AppDbContext db, int buildId, DateTime now, CancellationToken ct)
    {
        var build = await db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => new { b.ProjectId, b.ReleaseId, b.BcTarget, Hosting = b.Project!.HostingType })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (build is null || ProjectBuildTarget.IsPreview(build.BcTarget) || build.Hosting == ProjectHostingType.MicrosoftCloud) return [];

        var environments = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == build.ProjectId && e.Kind == OeEnvironmentKind.OnPremises
                        && e.MissingSince == null && e.GatewayInstanceId != null && e.Type == "Production")
            .OrderBy(e => e.Name)
            .Select(e => e.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (environments.Count == 0) return [];

        var objects = await LoadObjectsAsync(db, build.ReleaseId, ct).ConfigureAwait(false);
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
        var objects = await LoadObjectsAsync(db, releaseId, ct).ConfigureAwait(false);
        return await CheckAsync(db, environmentId, objects, now, ct).ConfigureAwait(false);
    }

    /// <summary>Null when the build's objects were never indexed (a build staged from a GitHub release, or one whose index was removed).</summary>
    private static async Task<List<LicensedObject>?> LoadObjectsAsync(AppDbContext db, int? releaseId, CancellationToken ct)
    {
        if (releaseId is not { } release) return null;
        if (!await db.OeModules.AsNoTracking().AnyAsync(m => m.ReleaseId == release, ct).ConfigureAwait(false)) return null;
        var kinds = KindsRead;
        return await db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == release && o.ObjectId != null && kinds.Contains(o.Kind))
            .Select(o => new LicensedObject(o.Kind, o.ObjectId!.Value, o.Name))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static async Task<LicenceCheck?> CheckAsync(
        AppDbContext db, int environmentId, List<LicensedObject>? objects, DateTime now, CancellationToken ct)
    {
        var env = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.MissingSince,
                e.TenantId,
                e.GatewayInstanceId,
                Hosting = e.Project!.HostingType,
                Machine = e.GatewayInstance == null ? null : e.GatewayInstance.Machine,
                Instance = e.GatewayInstance == null ? null : e.GatewayInstance.Name,
                Version = e.GatewayInstance == null ? null : e.GatewayInstance.Version,
                InstanceMissing = e.GatewayInstance != null && e.GatewayInstance.MissingSince != null,
                TenantMissing = e.GatewayInstance != null
                                && e.GatewayInstance.Tenants.Any(t => t.TenantId == e.TenantId && t.MissingSince != null),
                GatewayState = e.GatewayInstance == null ? (Domain.Entities.GatewayState?)null : e.GatewayInstance.Gateway!.State,
                LastSeenAt = e.GatewayInstance == null ? null : e.GatewayInstance.Gateway!.LastSeenAt,
                RevokedAt = e.GatewayInstance == null ? null : e.GatewayInstance.Gateway!.RevokedAt,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (env is null || env.Hosting == ProjectHostingType.MicrosoftCloud) return null;

        LicenceCheck NotChecked(string reason) =>
            new(env.Id, env.Name, LicenceCheckOutcome.NotChecked, reason, [], 0, 0, env.Version, now);

        var tier = $"{env.Machine} / {env.Instance}";
        if (env.GatewayInstanceId is not { } instanceId || env.MissingSince is not null)
            return NotChecked($"{env.Name} is no longer linked to a service tier.");
        var status = GatewayService.StatusOf(env.GatewayState ?? Domain.Entities.GatewayState.Pending, env.LastSeenAt, env.RevokedAt, now);
        if (status != GatewayStatus.Online)
            return NotChecked(status == GatewayStatus.Offline
                ? $"the gateway for {tier} is offline, so the licence it last read may be out of date."
                : $"the gateway for {tier} is not connected, so its licence can't be read.");
        if (env.InstanceMissing || env.TenantMissing)
            return NotChecked($"the gateway no longer reports {tier}.");

        var licence = await db.GatewayInstanceLicences.AsNoTracking()
            .Where(l => l.InstanceId == instanceId)
            .Select(l => new
            {
                l.ReadAt,
                l.Truncated,
                Ranges = l.Ranges.Select(r => new LicenceRange(r.ObjectType, r.FromId, r.ToId, r.Permissions)).ToList(),
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (licence is null)
            return NotChecked($"the gateway has not read the licence on {tier} yet.");
        if (now - licence.ReadAt > OnPremisesTierFacts.StaleAfter)
            return NotChecked($"the licence on {tier} has not been read in the last hour, so it may be out of date.");
        if (licence.Truncated)
            return NotChecked($"the licence on {tier} was too long for the gateway to send whole.");
        if (licence.Ranges.Count == 0)
            return NotChecked($"the object ranges in the licence on {tier} could not be read.");
        if (OnPremisesLicenceService.ParseVersion(env.Version) is not { } version)
            return NotChecked($"the version of {tier} is not known yet.");
        if (objects is null)
            return NotChecked("this build's objects were not indexed, so there is nothing to compare with the licence.");

        var (outside, checkedCount) = Evaluate(objects, licence.Ranges, version);
        return new LicenceCheck(env.Id, env.Name,
            outside.Count == 0 ? LicenceCheckOutcome.Passed : LicenceCheckOutcome.OutsideLicence,
            null, outside, outside.Count, checkedCount, env.Version, now);
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
