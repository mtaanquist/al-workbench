using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Pure parsing / formatting helpers for Microsoft's Business Central artifact
/// index — the logic ported from BcContainerHelper's <c>Get-BCArtifactUrl</c> /
/// <c>QueryArtifactsFromIndex</c>. Kept free of HTTP and the database so the
/// version-selection and URL/label rules are unit-testable against a captured
/// index sample. <see cref="BcArtifactService"/> owns the network and DB sides.
///
/// <para>
/// Microsoft serves a small JSON index per type+country at
/// <c>https://{host}/{type}/indexes/{country}.json</c> — an array of records
/// each carrying a <c>Version</c> and a <c>CreationTime</c>. A sibling
/// <c>platform.json</c> lists the versions that also have a platform artifact;
/// an application build is only usable when its version appears there too.
/// </para>
///
/// <para>
/// Two <em>channels</em> share these helpers. <see cref="BcArtifactChannel.Release"/>
/// is the public storage's OnPrem type — the builds Microsoft has shipped.
/// <see cref="BcArtifactChannel.Preview"/> is the insider storage's Sandbox type:
/// the pre-release builds of upcoming versions BcContainerHelper reaches with
/// <c>-storageAccount bcinsider</c>. The insider storage serves no OnPrem type
/// (its OnPrem indexes return 403), and its Sandbox artifacts carry the same
/// <c>Applications.&lt;country&gt;/</c> + <c>Extensions/</c> layout as the public
/// OnPrem ones, so one walker covers both. See <c>.design/object-explorer.md</c>,
/// "Preview builds".
/// </para>
/// </summary>
public static class BcArtifactIndex
{
    /// <summary>The public storage's artifact type: shipped builds with loose <c>.app</c> files the Object Explorer can walk.</summary>
    public const string OnPremType = "onprem";

    /// <summary>The insider storage's artifact type. Pre-release builds are only published as Sandbox artifacts.</summary>
    public const string SandboxType = "sandbox";

    /// <summary>
    /// Oldest major we offer for import. BC 15 (Oct 2019) is the first release
    /// to run the AL-on-server architecture and ship loose <c>.app</c> files;
    /// everything below it is C/AL, a different runtime the AL walker can't read
    /// (the C/AL TXT ingest path is a separate, manual flow). Indexes still list
    /// the 14.x-and-older builds, so we floor them out here.
    /// </summary>
    public const int MinimumAlMajor = 15;

    /// <summary>
    /// Azure blob host BcContainerHelper nominally resolves to. Microsoft has
    /// since disabled anonymous access to it (it returns 403), so we don't fetch
    /// from it — it's kept as the rewrite source for <see cref="ToCdnUrl"/> and
    /// as a trusted host for <see cref="IsTrustedArtifactHost"/>.
    /// </summary>
    public const string BlobHost = "bcartifacts.blob.core.windows.net";

    /// <summary>The Front Door host the index and downloads are actually served from.</summary>
    public const string DefaultCdnHost = "bcartifacts-exdbf9fwegejdqak.b02.azurefd.net";

    /// <summary>Azure blob host behind the insider storage; a rewrite source for <see cref="ToCdnUrl"/> and a trusted host, never fetched directly.</summary>
    public const string InsiderBlobHost = "bcinsider.blob.core.windows.net";

    /// <summary>The Front Door host the insider (pre-release) indexes and downloads are served from — anonymously, like the public one.</summary>
    public const string DefaultInsiderCdnHost = "bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net";

    /// <summary>
    /// Front Door CDN host for the index and download URLs. Defaults to
    /// <see cref="DefaultCdnHost"/>; overridable via the <c>BC_ARTIFACT_CDN_HOST</c>
    /// env var so a Microsoft rotation of the opaque Front Door identifier is a
    /// config change + restart rather than a rebuild. There is no dynamic
    /// discovery — BcContainerHelper itself hardcodes the same value.
    /// </summary>
    public static readonly string CdnHost = ResolveHost("BC_ARTIFACT_CDN_HOST", DefaultCdnHost);

    /// <summary>
    /// Front Door host for the insider storage; the pre-release counterpart of
    /// <see cref="CdnHost"/>, overridable via <c>BC_INSIDER_CDN_HOST</c>.
    /// </summary>
    public static readonly string InsiderCdnHost = ResolveHost("BC_INSIDER_CDN_HOST", DefaultInsiderCdnHost);

    private static string ResolveHost(string envVar, string fallback)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? fallback : fromEnv.Trim();
    }

    /// <summary>The host a channel's indexes and downloads live on.</summary>
    public static string HostFor(BcArtifactChannel channel) =>
        channel == BcArtifactChannel.Preview ? InsiderCdnHost : CdnHost;

    /// <summary>The artifact type segment a channel is published under.</summary>
    public static string TypeFor(BcArtifactChannel channel) =>
        channel == BcArtifactChannel.Preview ? SandboxType : OnPremType;

    /// <summary>URL of the per-country index for a channel.</summary>
    public static string CountryIndexUrl(string country, BcArtifactChannel channel = BcArtifactChannel.Release) =>
        $"https://{HostFor(channel)}/{TypeFor(channel)}/indexes/{country.Trim().ToLowerInvariant()}.json";

    /// <summary>URL of the platform index for a channel.</summary>
    public static string PlatformIndexUrl(BcArtifactChannel channel = BcArtifactChannel.Release) =>
        $"https://{HostFor(channel)}/{TypeFor(channel)}/indexes/platform.json";

    /// <summary>URL of the countries index for a channel (used to validate a configured country).</summary>
    public static string CountriesIndexUrl(BcArtifactChannel channel = BcArtifactChannel.Release) =>
        $"https://{HostFor(channel)}/{TypeFor(channel)}/indexes/countries.json";

    /// <summary>
    /// Parses a country index (and optional platform index) into the available
    /// versions, newest first. When <paramref name="platformJson"/> is supplied,
    /// application versions without a matching platform artifact are dropped —
    /// they can't be downloaded into a walkable set. Unparseable version strings
    /// are skipped rather than throwing.
    /// </summary>
    public static IReadOnlyList<string> ParseVersions(string countryJson, string? platformJson)
    {
        var countryVersions = ReadVersions(countryJson);

        HashSet<string>? platformVersions = null;
        if (!string.IsNullOrWhiteSpace(platformJson))
        {
            platformVersions = new HashSet<string>(ReadVersions(platformJson), StringComparer.OrdinalIgnoreCase);
        }

        return countryVersions
            .Where(v => platformVersions is null || platformVersions.Contains(v))
            .Where(IsAlEra)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(ToComparableVersion)
            .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Picks a version from <paramref name="available"/> (assumed newest-first):
    /// <paramref name="requested"/> <see langword="null"/> returns the newest;
    /// a full four-part version returns the exact match; a <c>Major.Minor</c>
    /// prefix returns the newest build of that minor. Returns <see langword="null"/>
    /// when nothing matches.
    /// </summary>
    public static string? SelectVersion(IReadOnlyList<string> available, string? requested)
    {
        if (available.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(requested)) return available[0];

        var trimmed = requested.Trim();
        var exact = available.FirstOrDefault(v => string.Equals(v, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Treat the request as a Major.Minor (or any leading-segment) prefix and
        // pick the newest matching build — `available` is already newest-first.
        var prefix = trimmed.EndsWith('.') ? trimmed : trimmed + ".";
        return available.FirstOrDefault(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Major.Minor of a dotted version, e.g. <c>28.2.50931.51727</c> → <c>28.2</c>. Falls back to the raw string when it has fewer than two segments.</summary>
    public static string ToMajorMinor(string version)
    {
        var segments = (version ?? string.Empty).Split('.');
        return segments.Length >= 2 ? $"{segments[0]}.{segments[1]}" : (version ?? string.Empty);
    }

    /// <summary>
    /// The auto-import / artifact release label: "Business Central {Major}.{Minor} ({CC})",
    /// e.g. <c>Business Central 28.2 (DK)</c>. The country code is upper-cased.
    /// Display only — dedup keys on <see cref="FormatDedupKey"/>, not this.
    /// </summary>
    public static string FormatLabel(string version, string country, bool prerelease = false) =>
        $"Business Central {ToMajorMinor(version)} ({country.Trim().ToUpperInvariant()})" + (prerelease ? PreviewLabelSuffix : "");

    /// <summary>The word a pre-release label ends in, so a preview reads as one wherever the label alone is shown.</summary>
    public const string PreviewLabelSuffix = " Preview";

    /// <summary>
    /// The label without its <see cref="PreviewLabelSuffix"/>, for the places
    /// that put a "Preview" pill right beside it (release cards, the admin list,
    /// the detail head) - the stored label keeps the suffix so pickers, the
    /// palette and MCP still say it on their own. Any other label is returned as is.
    /// </summary>
    public static string StripPreviewSuffix(string label) =>
        label.EndsWith(PreviewLabelSuffix, StringComparison.Ordinal) ? label[..^PreviewLabelSuffix.Length] : label;

    /// <summary>
    /// The explicit dedup key for a first-party OnPrem artifact release:
    /// <c>bc-onprem:{Major}.{Minor}:{cc}</c> (country lower-cased), e.g.
    /// <c>bc-onprem:28.2:dk</c>. Keys at the same Major.Minor + country granularity
    /// the daily sweep always deduped at — it's the successor to matching on the
    /// display label, freeing the label to be a pure display string. See
    /// <c>.design/roadmap.md</c> ("Harden first-party dedup, then free the label").
    /// </summary>
    public static string FormatDedupKey(string version, string country, bool prerelease = false) =>
        $"{(prerelease ? PreviewDedupPrefix : ReleaseDedupPrefix)}:{ToMajorMinor(version)}:{country.Trim().ToLowerInvariant()}";

    /// <summary>Dedup-key prefix of a shipped (public OnPrem) release.</summary>
    public const string ReleaseDedupPrefix = "bc-onprem";

    /// <summary>
    /// Dedup-key prefix of a pre-release (insider Sandbox) import:
    /// <c>bc-insider:{Major}.{Minor}:{cc}</c>. Distinct from the shipped key so a
    /// preview and the release that supersedes it can coexist for the moment the
    /// sweep swaps them, and so "is 29.0 imported?" never answers yes because of a
    /// preview.
    /// </summary>
    public const string PreviewDedupPrefix = "bc-insider";

    /// <summary>Reads the Major.Minor and country back out of a key <see cref="FormatDedupKey"/> produced, or null for any other key.</summary>
    public static (string MajorMinor, string Country)? ParseDedupKey(string? dedupKey)
    {
        if (string.IsNullOrWhiteSpace(dedupKey)) return null;
        var parts = dedupKey.Split(':');
        if (parts.Length != 3 || (parts[0] != ReleaseDedupPrefix && parts[0] != PreviewDedupPrefix)) return null;
        return (parts[1], parts[2]);
    }

    /// <summary>
    /// Builds the application-artifact download URL on the CDN host, e.g.
    /// <c>https://{cdn}/onprem/28.2.50931.51727/dk</c> — the shape
    /// <c>Get-BCArtifactUrl</c> returns.
    /// </summary>
    public static string BuildApplicationUrl(string version, string country, BcArtifactChannel channel = BcArtifactChannel.Release) =>
        $"https://{HostFor(channel)}/{TypeFor(channel)}/{version}/{country.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Picks the pre-release builds worth importing out of an insider index
    /// (<paramref name="previewVersions"/>, newest first): for every major above
    /// <paramref name="newestReleasedMajor"/>, the newest build of the lowest
    /// minor listed — the version that will ship next as that major. Preview
    /// builds of later minors on the current major (a 28.6 while 28.5 is the
    /// newest release) are deliberately left out: the request was for upcoming
    /// majors, and one preview per major keeps the catalogue readable. Returned
    /// lowest major first so 29.0 is queued before 30.0.
    /// </summary>
    public static IReadOnlyList<string> SelectPreviewVersions(IReadOnlyList<string> previewVersions, int newestReleasedMajor)
    {
        return previewVersions
            .Select(v => (Version: v, Parsed: ToComparableVersion(v)))
            .Where(x => x.Parsed.Major > newestReleasedMajor)
            .GroupBy(x => x.Parsed.Major)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var lowestMinor = g.Min(x => x.Parsed.Minor);
                // `previewVersions` is newest-first, so the first hit is the newest build of that minor.
                return g.First(x => x.Parsed.Minor == lowestMinor).Version;
            })
            .ToList();
    }

    /// <summary>Major of a dotted version, or null when it doesn't start with a number.</summary>
    public static int? ToMajor(string? version)
    {
        var major = version?.Split('.', 2)[0];
        return int.TryParse(major, out var n) ? n : null;
    }

    /// <summary>
    /// Rewrites a Microsoft artifact URL onto the active Front Door host —
    /// <see cref="CdnHost"/>, or <see cref="InsiderCdnHost"/> when the URL names
    /// an insider (<c>bcinsider</c>) host. The manifest's <c>platformUrl</c> comes
    /// back pointing at the (now 403-ing) blob host — or could point at a stale
    /// Front Door host — so we normalise it onto the host we know serves
    /// anonymously before downloading. Mirrors BcContainerHelper's
    /// <c>ReplaceCDN</c>. URLs on a non-Microsoft artifact host (or already on
    /// the right Front Door host) are returned unchanged.
    /// </summary>
    public static string ToCdnUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        var host = uri.Host.ToLowerInvariant();
        var target = host.StartsWith("bcinsider", StringComparison.Ordinal) ? InsiderCdnHost : CdnHost;
        if (host == target.ToLowerInvariant()) return url;

        // Only rewrite recognised Microsoft artifact hosts; anything else is left
        // alone (and would be refused by IsTrustedArtifactHost on download).
        var isArtifactHost = host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal)
            || host.EndsWith(".azureedge.net", StringComparison.Ordinal)
            || host.EndsWith(".azurefd.net", StringComparison.Ordinal);
        if (!isArtifactHost) return url;

        return $"{uri.Scheme}://{target}{uri.PathAndQuery}";
    }

    /// <summary>
    /// Derives the platform-artifact URL from an application-artifact URL by
    /// convention: replace the trailing country segment with the literal
    /// <c>platform</c> (e.g. <c>…/onprem/28.2.50931.51034/dk</c> →
    /// <c>…/onprem/28.2.50931.51034/platform</c>). Mirrors BcContainerHelper's
    /// <c>Download-Artifacts</c> fallback for when the application manifest
    /// carries no <c>platformUrl</c> (the common case for these artifacts).
    /// Returns <see langword="null"/> if the URL can't be parsed.
    /// </summary>
    public static string? DerivePlatformUrl(string applicationUrl)
    {
        if (!Uri.TryCreate(applicationUrl, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash <= 0) return null;
        return $"{uri.Scheme}://{uri.Host}{path[..lastSlash]}/platform";
    }

    /// <summary>
    /// True when <paramref name="host"/> is one of Microsoft's fixed artifact
    /// hosts. Used to vet both the URLs we build and the <c>platformUrl</c> we
    /// read out of a downloaded manifest before fetching it, so a tampered
    /// manifest can't redirect the download elsewhere. The SSRF guard on the
    /// HttpClient is the second layer.
    /// </summary>
    public static bool IsTrustedArtifactHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().ToLowerInvariant();
        return string.Equals(host, BlobHost, StringComparison.Ordinal)
            || string.Equals(host, InsiderBlobHost, StringComparison.Ordinal)
            || string.Equals(host, CdnHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, InsiderCdnHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".azurefd.net", StringComparison.Ordinal)
            || host.EndsWith(".azureedge.net", StringComparison.Ordinal)
            || host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal);
    }

    /// <summary>Reads the <c>platformUrl</c> string out of an artifact's <c>manifest.json</c> body, or null when absent/unparseable.</summary>
    public static string? ReadPlatformUrl(string manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "platformUrl", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var url = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(url) ? null : url;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to null — the caller imports the application artifact alone.
        }
        return null;
    }

    /// <summary>Parses the countries index into the set of available country codes (lower-cased).</summary>
    public static IReadOnlyCollection<string> ParseCountries(string countriesJson)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(countriesJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(countriesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var c = el.GetString();
                        if (!string.IsNullOrWhiteSpace(c)) result.Add(c.Trim().ToLowerInvariant());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Treat an unparseable countries index as "unknown" — callers skip validation.
        }
        return result;
    }

    /// <summary>
    /// Reads the <c>Version</c> string off each record in an index array. The
    /// records also carry <c>CreationTime</c>, which we don't need for selection
    /// (the version itself sorts deterministically). Tolerates a bare string
    /// array too.
    /// </summary>
    private static IEnumerable<string> ReadVersions(string json)
    {
        var versions = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return versions;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return versions; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return versions;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) versions.Add(s);
                        break;
                    case JsonValueKind.Object:
                        foreach (var prop in el.EnumerateObject())
                        {
                            if (string.Equals(prop.Name, "Version", StringComparison.OrdinalIgnoreCase)
                                && prop.Value.ValueKind == JsonValueKind.String)
                            {
                                var v = prop.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(v)) versions.Add(v);
                                break;
                            }
                        }
                        break;
                }
            }
        }
        return versions;
    }

    /// <summary>Parses a dotted version for ordering; unparseable strings sort last.</summary>
    private static Version ToComparableVersion(string version) =>
        Version.TryParse(version, out var v) ? v : new Version(0, 0);

    /// <summary>
    /// True when the version's major is <see cref="MinimumAlMajor"/> or newer —
    /// i.e. an AL-on-server build the walker can consume. Unparseable or
    /// majorless versions are excluded.
    /// </summary>
    private static bool IsAlEra(string version)
    {
        var major = version?.Split('.', 2)[0];
        return int.TryParse(major, out var n) && n >= MinimumAlMajor;
    }
}

/// <summary>
/// Which of Microsoft's two artifact storages a query or download targets. See
/// <see cref="BcArtifactIndex"/>.
/// </summary>
public enum BcArtifactChannel
{
    /// <summary>Shipped builds: the public storage's OnPrem type.</summary>
    Release,
    /// <summary>Pre-release builds of upcoming versions: the insider storage's Sandbox type.</summary>
    Preview,
}
