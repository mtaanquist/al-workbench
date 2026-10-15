using System.Text;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.PublishInstall;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// A Business Central server behind the fake process runner: answers
/// <see cref="PublishInstallScript.Examine"/> and <see cref="PublishInstallScript.Inspect"/>
/// from its state, as those scripts would report it, and carries out
/// <see cref="PublishInstallScript.Apply"/>'s steps on it, as the cmdlets would. What the
/// real cmdlets return with and without <c>-TenantSpecificProperties</c> is the business of
/// the stand-in administration tools in <c>PublishInstallSamples/</c>, run under pwsh. It reads
/// what the runner put on standard input exactly as the real bootstrap does (base64 of
/// the JSON envelope), so the tests see the parameters the gateway really sends. Every
/// operation and every applied step is recorded.
/// </summary>
internal sealed class FakeBusinessCentral : IProcessRunner
{
    /// <summary>A published version of an app, the tenant it was published to (null for every tenant), and its build's package id.</summary>
    public sealed record Published(string Name, Guid Id, Version Version, string? Tenant, Guid PackageId);

    public List<Published> PublishedApps { get; } = [];

    /// <summary>Installed version per (tenant, app name).</summary>
    public Dictionary<(string Tenant, string App), Version> Installed { get; } = [];

    /// <summary>Data version per (tenant, app name): kept when an app is uninstalled.</summary>
    public Dictionary<(string Tenant, string App), Version> Data { get; } = [];

    public List<string> Tenants { get; } = ["default"];

    /// <summary>
    /// False plays Business Central 14: no <c>-TenantSpecificProperties</c> (so the inspect
    /// script reads installed state from <c>Get-NAVAppTenant</c>, and has no data versions),
    /// no <c>-Tenant</c> on <c>Unpublish-NAVApp</c>.
    /// </summary>
    public bool Current { get; set; } = true;

    /// <summary>
    /// Plays a version with neither <c>-TenantSpecificProperties</c> nor
    /// <c>Get-NAVAppTenant</c>: the inspect script reports every installed state as unknown,
    /// and the other tenants as unreadable.
    /// </summary>
    public bool InstalledStateUnknown { get; set; }

    /// <summary>Why the other tenants could not be read, when set; the inspect script then leaves them out.</summary>
    public string? OtherTenantsUnknown { get; set; }

    /// <summary>False plays a version whose <c>Get-NAVAppInfo</c> reports no <c>PackageId</c>.</summary>
    public bool ReportsPackageId { get; set; } = true;

    /// <summary>Changes what Business Central reads from a file (<c>Get-NAVAppInfo -Path</c>), for files it reads differently from the gateway.</summary>
    public Func<AppFileInfo, AppFileInfo>? BusinessCentralReads { get; set; }

    /// <summary>False plays a version whose <c>Get-NAVAppInfo</c> has no <c>-Path</c>.</summary>
    public bool ReadsFiles { get; set; } = true;

    public bool SignatureValid { get; set; } = true;

    /// <summary>Null plays a computer where Windows cannot read the signature at all.</summary>
    public string? SignatureError { get; set; }

    public string SignerSubject { get; set; } = "CN=CRONUS Partner, O=CRONUS Partner, C=DK";

    public string SignerThumbprint { get; set; } = "3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E";

    /// <summary>The apply process runs past its time limit and is killed, after starting two steps.</summary>
    public bool ApplyTimesOut { get; set; }

    /// <summary>Makes one step of one app fail with this error, e.g. ("sync", "CRONUS Tea").</summary>
    public Dictionary<(string Kind, string App), string> Failures { get; } = [];

    /// <summary>Each operation run: its name and its parameters.</summary>
    public List<(string Operation, JsonObject Parameters)> Runs { get; } = [];

    /// <summary>Each step carried out, as one readable line, e.g. "publish CRONUS Coffee 1.4.0.0 Tenant default skip=True".</summary>
    public List<string> Steps { get; } = [];

    public void Publish(string name, string version, string? tenant = null, Guid? id = null, Guid? packageId = null) =>
        PublishedApps.Add(new Published(name, id ?? AppFiles.CoffeeId, Version.Parse(version), tenant, packageId ?? Guid.NewGuid()));

    public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var envelope = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(spec.StandardInput!)))!.AsObject();
        var marker = envelope["marker"]!.GetValue<string>();
        var script = envelope["operation"]!.GetValue<string>();
        var parameters = envelope["parameters"]!.AsObject();
        JsonArray output;
        if (script == PublishInstallScript.Examine.Script)
        {
            Runs.Add(("examine", parameters));
            output = Examine(parameters);
        }
        else if (script == PublishInstallScript.Inspect.Script)
        {
            Runs.Add(("inspect", parameters));
            output = [Inspect(parameters)];
        }
        else if (script == PublishInstallScript.Apply.Script)
        {
            Runs.Add(("apply", parameters));
            if (ApplyTimesOut) return Task.FromResult(new ProcessOutcome(-1, "Publish-NAVApp, scope Tenant.\nSync-NAVApp, mode Add.\n", string.Empty, true));
            output = Apply(parameters);
        }
        else
        {
            throw new InvalidOperationException("The fake server does not know this operation.");
        }
        var stdout = $"Business Central Administration Shell\n{marker}\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(output.ToJsonString()))}\n{marker}\n";
        return Task.FromResult(new ProcessOutcome(0, stdout, string.Empty, false));
    }

    private IEnumerable<Published> VisibleTo(string tenant, Guid id) =>
        PublishedApps.Where(p => p.Id == id && (p.Tenant is null || p.Tenant == tenant));

    private JsonArray Examine(JsonObject parameters)
    {
        var results = new JsonArray();
        foreach (var file in parameters["files"]!.AsArray().OfType<JsonObject>())
        {
            var path = file["path"]!.GetValue<string>();
            var entry = new JsonObject { ["path"] = path };
            if (!ReadsFiles)
            {
                entry["appError"] = "Get-NAVAppInfo on this Business Central version cannot read an .app file";
            }
            else
            {
                var read = AppFile.Read(path);
                if (BusinessCentralReads is { } change) read = change(read);
                entry["app"] = new JsonObject
                {
                    ["id"] = read.Id.ToString("D"),
                    ["name"] = read.Name,
                    ["publisher"] = read.Publisher,
                    ["version"] = read.Version.ToString(),
                    ["packageId"] = ReportsPackageId ? read.PackageId.ToString("D") : null,
                };
            }
            if (file["signature"]!.GetValue<bool>())
            {
                if (SignatureError is { } error) entry["signatureError"] = error;
                else entry["signature"] = new JsonObject
                {
                    ["status"] = SignatureValid ? "Valid" : "HashMismatch",
                    ["statusMessage"] = SignatureValid ? "Signature verified." : "The contents of the file might have been changed.",
                    ["subject"] = SignerSubject,
                    ["thumbprint"] = SignerThumbprint,
                };
            }
            results.Add(entry);
        }
        return results;
    }

    private JsonObject Inspect(JsonObject parameters)
    {
        var tenant = parameters["tenant"]!.GetValue<string>();
        var id = Guid.Parse(parameters["appId"]!.GetValue<string>());
        var versions = new JsonArray();
        foreach (var p in VisibleTo(tenant, id))
        {
            versions.Add(new JsonObject
            {
                ["version"] = p.Version.ToString(),
                ["scope"] = p.Tenant is null ? "Global" : "Tenant",
                ["isInstalled"] = InstalledStateUnknown ? null : Installed.GetValueOrDefault((tenant, p.Name)) == p.Version ? "True" : "False",
                ["isPublished"] = "True",
                ["dataVersion"] = Current ? Data.GetValueOrDefault((tenant, p.Name))?.ToString() : null,
                ["syncState"] = Current ? "Synced" : null,
                ["packageId"] = ReportsPackageId ? p.PackageId.ToString("D") : null,
            });
        }
        JsonArray? others = null;
        string? othersUnknown = InstalledStateUnknown ? "this Business Central version does not say which versions a tenant has installed" : OtherTenantsUnknown;
        if (parameters["otherTenants"]!.GetValue<bool>() && othersUnknown is null)
        {
            others = [];
            foreach (var other in Tenants.Where(t => t != tenant))
            {
                var installed = new JsonArray();
                foreach (var p in VisibleTo(other, id).Where(p => Installed.GetValueOrDefault((other, p.Name)) == p.Version))
                {
                    installed.Add(p.Version.ToString());
                }
                others.Add(new JsonObject { ["id"] = other, ["installed"] = installed });
            }
        }
        return new JsonObject
        {
            ["schema"] = 2,
            ["tenantSpecificProperties"] = Current,
            ["installedFrom"] = InstalledStateUnknown ? null : Current ? "TenantSpecificProperties" : "Get-NAVAppTenant",
            ["parameters"] = new JsonObject
            {
                ["publishScope"] = true,
                ["publishTenant"] = true,
                ["unpublishTenant"] = Current,
            },
            ["versions"] = versions,
            ["otherTenants"] = others,
            ["otherTenantsUnknown"] = parameters["otherTenants"]!.GetValue<bool>() ? othersUnknown : null,
        };
    }

    private JsonArray Apply(JsonObject parameters)
    {
        var tenant = parameters["tenant"]!.GetValue<string>();
        var name = parameters["name"]!.GetValue<string>();
        var version = Version.Parse(parameters["version"]!.GetValue<string>());
        var id = PublishedApps.FirstOrDefault(p => p.Name == name)?.Id ?? AppIds.GetValueOrDefault(name, AppFiles.CoffeeId);
        var ran = new JsonArray();
        foreach (var step in parameters["steps"]!.AsArray().OfType<JsonObject>())
        {
            var kind = step["kind"]!.GetValue<string>();
            string? error = Failures.GetValueOrDefault((kind, name));
            string? stepVersion = null;
            if (error is null)
            {
                switch (kind)
                {
                    case "publish":
                        var scope = step["scope"]!.GetValue<string>();
                        if (VisibleTo(tenant, id).Any(p => p.Version == version)) error = $"{name} {version} is already published.";
                        else
                        {
                            PublishedApps.Add(new Published(name, id, version, scope == "Tenant" ? tenant : null, AppFile.Read(parameters["path"]!.GetValue<string>()).PackageId));
                            Steps.Add($"publish {name} {version} {scope} {tenant} skip={step["skipVerification"]!.GetValue<bool>()}");
                        }
                        break;
                    case "sync":
                        Steps.Add($"sync {name} {version} {tenant} {step["mode"]!.GetValue<string>()}");
                        break;
                    case "install":
                        Installed[(tenant, name)] = version;
                        Data[(tenant, name)] = version;
                        Steps.Add($"install {name} {version} {tenant}");
                        break;
                    case "upgrade":
                        Installed[(tenant, name)] = version;
                        Data[(tenant, name)] = version;
                        Steps.Add($"upgrade {name} {version} {tenant}");
                        break;
                    case "unpublish":
                        stepVersion = step["version"]!.GetValue<string>();
                        var previous = Version.Parse(stepVersion);
                        var tenantScoped = step["tenantScoped"]!.GetValue<bool>();
                        if (Tenants.Any(t => Installed.GetValueOrDefault((t, name)) == previous)) error = $"{name} {previous} is installed on a tenant and cannot be unpublished.";
                        else
                        {
                            PublishedApps.RemoveAll(p => p.Name == name && p.Version == previous);
                            Steps.Add($"unpublish {name} {previous}" + (tenantScoped && Current ? $" {tenant}" : string.Empty));
                        }
                        break;
                    default:
                        error = "The gateway has no step called " + kind + ".";
                        break;
                }
            }
            ran.Add(new JsonObject { ["kind"] = kind, ["version"] = stepVersion, ["ok"] = error is null, ["error"] = error });
            if (error is not null) break;
        }
        return ran;
    }

    /// <summary>App ids by name, for apps not published yet.</summary>
    public Dictionary<string, Guid> AppIds { get; } = [];
}

/// <summary>Finds every tier at one install folder, for the given version.</summary>
internal sealed class FixedTierLocator(string folder, Version? version = null) : IServiceTierLocator
{
    public ServiceTierInstall? Find(string instance) => new(folder, version ?? new Version(25, 4, 29661, 29927));
}

/// <summary>An <see cref="IGatewayPolicy"/> over a policy file's text, checked with the real <see cref="PolicyEnforcer"/>.</summary>
internal sealed class TextPolicy : IGatewayPolicy
{
    public TextPolicy(string toml, string machine = "CRONUS-BC01")
    {
        var parsed = GatewayPolicyParser.Parse(toml, machine);
        InForce = parsed.Policy ?? throw new InvalidOperationException(string.Join(" ", parsed.Errors));
    }

    public GatewayPolicy InForce { get; set; }

    public int Checks { get; private set; }

    public IReadOnlyList<string> Start() => [];

    public PolicySnapshot Refresh() => new(InForce.ToReport(GatewayPolicyStates.Valid, []), 1);

    public PolicyDecision Check(GatewayCommandMessage command)
    {
        Checks++;
        var request = PolicyRequest.FromCommand(command, out var problem);
        return request is null
            ? PolicyDecision.Refuse(problem!)
            : PolicyEnforcer.Check(InForce, request, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
    }
}
