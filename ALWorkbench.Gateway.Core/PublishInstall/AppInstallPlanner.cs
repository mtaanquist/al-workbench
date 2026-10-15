using System.Text.Json;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway.Core.PublishInstall;

/// <summary>One version of the app as <c>Get-NAVAppInfo -Tenant</c> lists it.</summary>
/// <param name="Scope"><c>Global</c> or <c>Tenant</c>; null when the version does not say.</param>
/// <param name="Installed">Installed on the tenant; null when unknown.</param>
/// <param name="DataVersion">The version the tenant's data is at, from <c>-TenantSpecificProperties</c>; null when there is none or it is unknown.</param>
public sealed record AppVersionRow(Version Version, string? Scope, bool? Installed, Version? DataVersion);

/// <summary>Another tenant on the same server, and which versions of the app it has installed.</summary>
public sealed record OtherTenant(string Id, IReadOnlyList<Version> Installed);

/// <summary>
/// What <see cref="PublishInstallScript.Inspect"/> found. Read defensively, like the
/// inventory: Windows PowerShell 5.1 sometimes wraps an array as
/// <c>{"value": [...], "Count": n}</c> and writes a one-item array as the item.
/// </summary>
/// <param name="Versions">Every version of the app the tenant sees.</param>
/// <param name="OtherTenants">The server's other tenants; null when they could not be read (or were not asked for).</param>
/// <param name="OtherTenantsUnknown">Why they could not be read.</param>
/// <param name="Parameters">Which optional cmdlet parameters this Business Central version has, by name.</param>
public sealed record AppInspection(
    bool TenantSpecificProperties,
    IReadOnlyList<AppVersionRow> Versions,
    IReadOnlyList<OtherTenant>? OtherTenants,
    string? OtherTenantsUnknown,
    IReadOnlyDictionary<string, bool> Parameters)
{
    /// <summary>Reads the script's output. Throws <see cref="FormatException"/> when it is not the shape the script writes.</summary>
    public static AppInspection FromScriptOutput(JsonNode? output)
    {
        if (Unwrap(output) is JsonArray { Count: > 0 } array) output = array[0];
        if (output is not JsonObject root) throw new FormatException("The gateway could not read what Get-NAVAppInfo returned.");

        var versions = new List<AppVersionRow>();
        foreach (var row in Items(root["versions"]).OfType<JsonObject>())
        {
            if (!AppVersions.TryParse(Text(row["version"]), out var version)) continue;
            Version? data = null;
            if (AppVersions.TryParse(Text(row["dataVersion"]), out var parsed) && parsed > new Version(0, 0, 0, 0)) data = parsed;
            versions.Add(new AppVersionRow(version, Text(row["scope"]), Bool(row["isInstalled"]), data));
        }

        List<OtherTenant>? others = null;
        if (root["otherTenants"] is { } othersNode)
        {
            others = [];
            foreach (var tenant in Items(othersNode).OfType<JsonObject>())
            {
                var installed = Items(tenant["installed"])
                    .Select(v => AppVersions.TryParse(Text(v), out var parsedVersion) ? parsedVersion : null)
                    .OfType<Version>()
                    .ToList();
                others.Add(new OtherTenant(Text(tenant["id"]) ?? string.Empty, installed));
            }
        }

        var parameters = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (root["parameters"] is JsonObject flags)
        {
            foreach (var (name, value) in flags) parameters[name] = Bool(value) ?? false;
        }
        return new AppInspection(Bool(root["tenantSpecificProperties"]) ?? false, versions, others, Text(root["otherTenantsUnknown"]), parameters);
    }

    private static JsonNode? Unwrap(JsonNode? node) =>
        node is JsonObject wrapped && wrapped["value"] is JsonArray inner && wrapped.ContainsKey("Count") ? inner : node;

    /// <summary>An array's items; a lone object or value as one item; nothing for null.</summary>
    private static IEnumerable<JsonNode?> Items(JsonNode? node)
    {
        switch (Unwrap(node))
        {
            case null:
                break;
            case JsonArray array:
                foreach (var item in array) yield return item;
                break;
            case var single:
                yield return single;
                break;
        }
    }

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool? Bool(JsonNode? node) => Text(node)?.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };
}

/// <summary>
/// One step of an app's plan, in the order it is reported: either one
/// <see cref="PublishInstallScript.Apply"/> runs, or one decided against, with the reason.
/// </summary>
/// <param name="Step">One of <see cref="GatewayPublishInstallSteps"/>.</param>
/// <param name="Run">What <see cref="PublishInstallScript.Apply"/> runs; null for a step not run.</param>
/// <param name="Version">The version it acts on, where it is not the app's own.</param>
/// <param name="SkipReason">Why it is not run.</param>
public sealed record PlannedStep(string Step, AppStep? Run, string? Version = null, string? SkipReason = null);

/// <summary>What to do with one app on the tenant.</summary>
/// <param name="Outcome"><see cref="GatewayPublishInstallOutcomes.Installed"/>, <c>Upgraded</c> or <c>AlreadyInstalled</c>; null when refused.</param>
/// <param name="From">The version installed before, or the data version, for an upgrade.</param>
/// <param name="Refusal">Why the app cannot go on this tenant; nothing is run.</param>
public sealed record AppPlan(string? Outcome, Version? From, IReadOnlyList<PlannedStep> Steps, string? Refusal = null)
{
    public IEnumerable<AppStep> ToRun => Steps.Select(s => s.Run).OfType<AppStep>();
}

/// <summary>
/// Decides what <c>publish-install</c> does with one app, from what the tenant has of it.
/// See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises", steps 2 to 5.
/// <list type="bullet">
/// <item>This version installed: nothing, as for online (#931).</item>
/// <item>A newer version installed, or the tenant's data from a newer version: refused,
/// the gateway never downgrades.</item>
/// <item>An older version installed, or none installed but the tenant keeps data from an
/// older one: publish, sync, <c>Start-NAVAppDataUpgrade</c>.</item>
/// <item>Otherwise (never installed, or uninstalled with data at this very version):
/// publish, sync, <c>Install-NAVApp</c>.</item>
/// </list>
/// <para>Publishing is left out when this version is already published (a deployment
/// that failed after publishing, sent again). <c>-SkipVerification</c> only where the
/// policy file allows unsigned apps; where it does not, Windows checks the signature
/// first. When previous versions are to go, each one published to this tenant alone is
/// unpublished with <c>-Tenant</c>; one published for every tenant only when no other
/// tenant still has it installed, and otherwise left with the reason.</para>
/// </summary>
public static class AppInstallPlanner
{
    public static AppPlan Plan(AppInspection inspection, Version target, EffectiveInstance tier, string tenant, string syncMode, bool unpublishPrevious)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(tier);
        var installed = inspection.Versions.Where(v => v.Installed == true).Select(v => v.Version).Distinct().ToList();

        if (installed.Contains(target))
        {
            return new AppPlan(GatewayPublishInstallOutcomes.AlreadyInstalled, null, []);
        }
        if (installed.Where(v => v > target).Max() is { } newer)
        {
            return Refused($"version {newer} is installed on tenant '{tenant}', which is newer than {target}. The gateway does not install an older version.");
        }
        var data = inspection.Versions.Select(v => v.DataVersion).OfType<Version>().DefaultIfEmpty().Max();
        if (installed.Count == 0 && data is not null && data > target)
        {
            return Refused($"tenant '{tenant}' keeps data from version {data}, which is newer than {target}. The gateway does not install an older version.");
        }

        var from = installed.Where(v => v < target).DefaultIfEmpty().Max()
                   ?? (data is not null && data < target ? data : null);
        var steps = new List<PlannedStep>();
        if (!tier.AllowUnsigned)
        {
            steps.Add(new PlannedStep(GatewayPublishInstallSteps.Signature, new AppStep(PublishInstallScript.Kinds.Signature)));
        }
        if (inspection.Versions.Any(v => v.Version == target))
        {
            steps.Add(new PlannedStep(GatewayPublishInstallSteps.Publish, null,
                SkipReason: $"Version {target} is already published on this server, so it was not published again."));
        }
        else
        {
            var scope = tier.Scope == PublishScope.Global ? "Global" : "Tenant";
            steps.Add(new PlannedStep(GatewayPublishInstallSteps.Publish,
                new AppStep(PublishInstallScript.Kinds.Publish, Scope: scope, SkipVerification: tier.AllowUnsigned)));
        }
        steps.Add(new PlannedStep(GatewayPublishInstallSteps.Sync, new AppStep(PublishInstallScript.Kinds.Sync, Mode: syncMode)));
        steps.Add(from is null
            ? new PlannedStep(GatewayPublishInstallSteps.Install, new AppStep(PublishInstallScript.Kinds.Install))
            : new PlannedStep(GatewayPublishInstallSteps.Upgrade, new AppStep(PublishInstallScript.Kinds.Upgrade)));

        if (unpublishPrevious)
        {
            foreach (var previous in inspection.Versions.Where(v => v.Version < target).GroupBy(v => v.Version).OrderBy(g => g.Key))
            {
                steps.Add(Unpublish(previous.Key, previous.Select(v => v.Scope).FirstOrDefault(s => s is not null), inspection));
            }
        }
        return new AppPlan(from is null ? GatewayPublishInstallOutcomes.Installed : GatewayPublishInstallOutcomes.Upgraded, from, steps);
    }

    private static AppPlan Refused(string reason) => new(null, null, [], reason);

    private static PlannedStep Unpublish(Version previous, string? scope, AppInspection inspection)
    {
        var version = previous.ToString();
        if (string.Equals(scope, "Tenant", StringComparison.OrdinalIgnoreCase))
        {
            return new PlannedStep(GatewayPublishInstallSteps.Unpublish,
                new AppStep(PublishInstallScript.Kinds.Unpublish, Version: version, TenantScoped: true), version);
        }
        // Published for every tenant (or the version does not say, which is treated the
        // same way, the careful way): only once no other tenant still runs it.
        if (inspection.OtherTenants is null)
        {
            return new PlannedStep(GatewayPublishInstallSteps.Unpublish, null, version,
                $"Version {version} was left published: it is published for every tenant on this server, and the gateway could not tell whether another tenant still uses it ({inspection.OtherTenantsUnknown ?? "the other tenants could not be read"}).");
        }
        var users = inspection.OtherTenants.Where(t => t.Installed.Contains(previous)).Select(t => t.Id).ToList();
        if (users.Count > 0)
        {
            return new PlannedStep(GatewayPublishInstallSteps.Unpublish, null, version,
                $"Version {version} was left published: it is published for every tenant on this server, and {Tenants(users)} still {(users.Count == 1 ? "has" : "have")} it installed.");
        }
        return new PlannedStep(GatewayPublishInstallSteps.Unpublish,
            new AppStep(PublishInstallScript.Kinds.Unpublish, Version: version, TenantScoped: false), version);
    }

    private static string Tenants(IReadOnlyList<string> ids)
    {
        var shown = ids.Take(5).Select(id => $"'{id}'").ToList();
        var text = (ids.Count == 1 ? "tenant " : "tenants ") + string.Join(", ", shown);
        return ids.Count > shown.Count ? $"{text} and {ids.Count - shown.Count} more" : text;
    }
}
