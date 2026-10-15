using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The parameters of an <c>import-licence</c> command (#1369), typed, for AL Workbench to
/// build the command with and for the tests to build it the same way. The licence itself
/// is the command's one file. On the wire they are the plain object
/// <see cref="GatewayCommandMessage.Parameters"/>; <see cref="ToParameters"/> writes it.
/// See <c>.design/on-prem-gateway.md</c>, "Licences".
/// </summary>
public sealed record GatewayImportLicenceParameters
{
    /// <summary>The name of <see cref="LicenceTarget"/> in the parameters.</summary>
    public const string LicenceTargetParameter = "licenceTarget";

    /// <summary>The database's publishing tier: its computer.</summary>
    public string Machine { get; init; } = string.Empty;

    /// <summary>The database's publishing tier: its server instance.</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>The tenant the environment is; null on a single-tenant server (tenant <c>default</c>). Used for a <c>Tenant</c> import.</summary>
    public string? Tenant { get; init; }

    /// <summary>
    /// Where the person was told the licence goes, one of <see cref="GatewayLicenceTargets"/>:
    /// the database's <c>licence_target</c> as the gateway last reported it. The gateway
    /// refuses the command when its settings now say anything else, so what runs is what
    /// was confirmed.
    /// </summary>
    public string LicenceTarget { get; init; } = string.Empty;

    /// <summary>The command's parameters object.</summary>
    public JsonObject ToParameters()
    {
        var parameters = new JsonObject
        {
            [GatewayCommandParameters.Machine] = Machine,
            [GatewayCommandParameters.Instance] = Instance,
            [LicenceTargetParameter] = LicenceTarget,
        };
        if (Tenant is not null) parameters[GatewayCommandParameters.Tenant] = Tenant;
        return parameters;
    }
}

/// <summary>
/// Where <c>Import-NAVServerLicense -Database</c> puts a licence, spelt as the cmdlet and the
/// policy file's <c>licence_target</c> spell it.
/// </summary>
public static class GatewayLicenceTargets
{
    /// <summary>The application database the tiers run on (on a single-tenant server, the one database).</summary>
    public const string NavDatabase = "NavDatabase";

    /// <summary>The server's master database, for every tenant of a multitenant server.</summary>
    public const string Master = "Master";

    /// <summary>The cmdlet's own default for the server.</summary>
    public const string Default = "Default";

    /// <summary>One tenant's database (<c>-Tenant</c>).</summary>
    public const string Tenant = "Tenant";

    /// <summary>Every target, compared exactly, case included.</summary>
    public static readonly IReadOnlyList<string> All = [NavDatabase, Master, Default, Tenant];
}

/// <summary>
/// Which licence file format a Business Central version imports, and the size a licence
/// file may be. One rule shared by AL Workbench, which refuses the upload, and the gateway,
/// which refuses the command: <c>.flf</c> before Business Central 22, <c>.bclicense</c> from
/// 22. Whether 22 and later still take an <c>.flf</c> is to be checked against a real server;
/// until then the rule is as written in <c>.design/on-prem-gateway.md</c>, "Licences".
/// </summary>
public static class GatewayLicenceFormats
{
    /// <summary>The licence format before Business Central 22 (and in every NAV version).</summary>
    public const string Flf = ".flf";

    /// <summary>The licence format from Business Central 22.</summary>
    public const string BcLicense = ".bclicense";

    /// <summary>The first major version that imports <see cref="BcLicense"/> instead of <see cref="Flf"/>.</summary>
    public const int FirstBcLicenseMajor = 22;

    /// <summary>The largest licence file taken. Real ones are a few kilobytes.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The extension a tier on <paramref name="major"/> imports.</summary>
    public static string For(int major) => major >= FirstBcLicenseMajor ? BcLicense : Flf;

    /// <summary>The file's extension when it is one of the two, lower case; null otherwise.</summary>
    public static string? FormatOf(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension is Flf or BcLicense ? extension : null;
    }

    /// <summary>
    /// Why a tier running <paramref name="version"/> cannot import <paramref name="fileName"/>,
    /// naming both; null when it can. An unknown version refuses: the rule cannot be applied.
    /// </summary>
    public static string? Refusal(string fileName, Version? version)
    {
        var format = FormatOf(fileName);
        if (format is null)
        {
            return $"{fileName} is not a licence file. Business Central licences end in {Flf} (before Business Central {FirstBcLicenseMajor}) or {BcLicense} (from {FirstBcLicenseMajor}).";
        }
        if (version is null)
        {
            return "The service tier's Business Central version is not known yet, so whether it can import this licence cannot be checked. Wait for the gateway's next reading, then try again.";
        }
        var expected = For(version.Major);
        if (format == expected) return null;
        return $"{fileName} is a {format} licence, but the service tier runs Business Central {version.Major} ({version}), which imports {expected} licences. "
            + (expected == BcLicense
                ? $"Ask Microsoft or the partner for the licence in the {BcLicense} format."
                : $"Versions before Business Central {FirstBcLicenseMajor} need the licence in the {Flf} format.");
    }
}

/// <summary>
/// What an <c>import-licence</c> reports in <see cref="GatewayCommandResultRequest.Result"/>
/// (#1369): where the licence went, whether it was imported, how the restart group went and
/// whether the inventory was read again afterwards.
/// </summary>
public sealed record GatewayImportLicenceResult
{
    /// <summary>Bumped when the shape changes.</summary>
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>One of <see cref="GatewayLicenceTargets"/>: where the licence was (or would have been) imported.</summary>
    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    /// <summary>True when <c>Import-NAVServerLicense</c> succeeded.</summary>
    [JsonPropertyName("imported")]
    public bool Imported { get; init; }

    /// <summary>The restart group, tier by tier; null when it never got that far (the import failed).</summary>
    [JsonPropertyName("restart")]
    public GatewayRestartGroupResult? Restart { get; init; }

    /// <summary>True when every tier of the group was read again and sent to AL Workbench after the restart.</summary>
    [JsonPropertyName("refreshed")]
    public bool Refreshed { get; init; }

    /// <summary>Why the reading afterwards did not happen or did not go through, when it did not.</summary>
    [JsonPropertyName("refreshMessage")]
    public string? RefreshMessage { get; init; }

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Options)!.AsObject();

    /// <summary>Reads a result back; null when it is not one.</summary>
    public static GatewayImportLicenceResult? FromJson(JsonObject? result)
    {
        if (result is null) return null;
        try
        {
            return result.Deserialize<GatewayImportLicenceResult>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
