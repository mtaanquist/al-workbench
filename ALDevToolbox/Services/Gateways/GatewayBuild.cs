using System.Reflection;
using ALDevToolbox.Services.Operations;
using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Services.Gateways;

/// <summary>How a gateway's reported version compares with this AL Workbench.</summary>
public enum GatewayVersionState
{
    /// <summary>The version this AL Workbench offers for download, or newer; or nothing to compare against.</summary>
    Current,
    /// <summary>Older than this AL Workbench, but still accepted. It keeps working; updating is advised.</summary>
    OutOfDate,
    /// <summary>Older than <see cref="GatewayProtocol.MinimumGatewayVersion"/>: every request it makes is refused.</summary>
    Unsupported,
    /// <summary>A local or staging build (major version 0). Never compared with a release.</summary>
    Preview,
    /// <summary>It has not reported a version, or reported something that is not one.</summary>
    Unknown,
}

/// <summary>
/// The gateway download that goes with this release of AL Workbench: the asset
/// release.yml attached to the same GitHub release.
/// </summary>
/// <param name="Version">The release, which the gateway shares with AL Workbench.</param>
/// <param name="Url">The program itself.</param>
/// <param name="ChecksumUrl">A <c>sha256sum</c>-style file beside it.</param>
/// <param name="Sha256">The program's SHA-256 in upper-case hex, as PowerShell's <c>Get-FileHash</c> prints it; null when the image was not stamped with one.</param>
/// <param name="Signed">Whether the release job signed it. The page says when a release is not code-signed yet.</param>
public sealed record GatewayDownload(string Version, string Url, string ChecksumUrl, string? Sha256, bool Signed);

/// <summary>
/// What this AL Workbench knows about the gateway that matches it: the version it
/// expects (its own: a gateway is released with every release, under the same tag),
/// where to download that build and its SHA-256, and how a gateway's reported version
/// compares. Singleton; it reads build-time stamps only. See
/// <c>.design/on-prem-gateway.md</c>, "Versions and updates".
///
/// <para>release.yml publishes and signs the gateway first, then passes its hash and
/// whether it was signed into the image build (<c>GATEWAY_SHA256</c> /
/// <c>GATEWAY_SIGNED</c>), which the csproj turns into assembly metadata the way
/// <see cref="BuildInfo"/>'s release stamp is. A local or staging build has no release
/// version, so it offers no download: there is no release to link to.</para>
/// </summary>
public sealed class GatewayBuild
{
    /// <summary>The asset name on every release. Fixed, so updating is replacing one file the service already points at.</summary>
    public const string ExecutableName = "ALWorkbench.Gateway.exe";

    private static readonly GatewayVersion Minimum = GatewayVersion.Parse(GatewayProtocol.MinimumGatewayVersion);

    private readonly GatewayVersion? _expected;

    /// <param name="workbenchVersion">This AL Workbench's release version, or null for a local or staging build.</param>
    /// <param name="sha256">The gateway program's SHA-256 as stamped by the release job, in any case.</param>
    /// <param name="signed">Whether the release job signed it.</param>
    public GatewayBuild(string? workbenchVersion, string? sha256, bool signed)
    {
        if (GatewayVersion.TryParse(workbenchVersion, out var parsed) && !parsed.IsPreview)
        {
            _expected = parsed;
            var tag = "v" + parsed;
            var baseUrl = $"{BuildInfo.RepositoryUrl}/releases/download/{tag}/{ExecutableName}";
            Download = new GatewayDownload(parsed.ToString(), baseUrl, baseUrl + ".sha256", NormaliseHash(sha256), signed);
        }
    }

    /// <summary>From this build's own stamps.</summary>
    public static GatewayBuild FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Value(string key) => metadata.FirstOrDefault(m => m.Key == key)?.Value;
        return new GatewayBuild(
            BuildInfo.Create(Value("ReleaseVersion"), null)?.Version,
            Value("GatewaySha256"),
            string.Equals(Value("GatewaySigned"), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The download for this release, or null when this AL Workbench is not a release build.</summary>
    public GatewayDownload? Download { get; }

    /// <summary>The version a gateway should run: this AL Workbench's own, or null for a local or staging build.</summary>
    public string? ExpectedVersion => _expected?.ToString();

    /// <summary>
    /// Out of date when older than this AL Workbench; unsupported when older than the
    /// minimum, which also applies on a local or staging AL Workbench that has no
    /// version of its own to compare with.
    /// </summary>
    public GatewayVersionState StateOf(string? reportedVersion)
    {
        if (!GatewayVersion.TryParse(reportedVersion, out var reported)) return GatewayVersionState.Unknown;
        if (reported.IsPreview) return GatewayVersionState.Preview;
        if (!reported.SatisfiesMinimum(Minimum)) return GatewayVersionState.Unsupported;
        if (_expected is not null && reported < _expected) return GatewayVersionState.OutOfDate;
        return GatewayVersionState.Current;
    }

    /// <summary>
    /// True when <paramref name="reportedVersion"/> is a release older than
    /// <see cref="GatewayProtocol.MinimumGatewayVersion"/>; a release candidate of the
    /// minimum is not (<see cref="GatewayVersion.SatisfiesMinimum"/>). A preview build, a missing
    /// version and one that does not parse are not refused for their version.
    /// </summary>
    public static bool IsBelowMinimum(string? reportedVersion) =>
        GatewayVersion.TryParse(reportedVersion, out var reported) && !reported.IsPreview && !reported.SatisfiesMinimum(Minimum);

    private static string? NormaliseHash(string? sha256)
    {
        var value = sha256?.Trim();
        return value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
    }
}
