using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Pins the pure artifact-index logic ported from BcContainerHelper's
/// <c>Get-BCArtifactUrl</c> — version parsing/selection, the blob→CDN URL shape,
/// the release-label format, and the manifest platformUrl read. No HTTP / DB, so
/// these run anywhere; <see cref="BcArtifactService"/> layers the network on top.
/// </summary>
public sealed class BcArtifactIndexTests
{
    // A trimmed country index in Microsoft's real shape: an array of records
    // carrying Version + CreationTime.
    private const string CountryJson = """
    [
      { "Version": "28.2.50931.51727", "CreationTime": "2026-06-10T00:00:00Z" },
      { "Version": "28.1.49000.50000", "CreationTime": "2026-05-10T00:00:00Z" },
      { "Version": "27.5.40000.41000", "CreationTime": "2026-04-10T00:00:00Z" },
      { "Version": "28.2.50000.50100", "CreationTime": "2026-06-01T00:00:00Z" }
    ]
    """;

    private const string PlatformJson = """
    [
      { "Version": "28.2.50931.51727", "CreationTime": "2026-06-10T00:00:00Z" },
      { "Version": "28.1.49000.50000", "CreationTime": "2026-05-10T00:00:00Z" },
      { "Version": "28.2.50000.50100", "CreationTime": "2026-06-01T00:00:00Z" }
    ]
    """;

    [Fact]
    public void ParseVersions_orders_newest_first()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, platformJson: null);

        versions.Should().ContainInOrder(
            "28.2.50931.51727", "28.2.50000.50100", "28.1.49000.50000", "27.5.40000.41000");
    }

    [Fact]
    public void ParseVersions_drops_versions_without_a_platform_artifact()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        // 27.5.* has no platform entry, so it's filtered out.
        versions.Should().NotContain("27.5.40000.41000");
        versions.Should().ContainInOrder(
            "28.2.50931.51727", "28.2.50000.50100", "28.1.49000.50000");
    }

    [Fact]
    public void ParseVersions_drops_pre_AL_majors()
    {
        // 14.x and older are C/AL — a different runtime the AL walker can't read,
        // so they're floored out even though the index lists them.
        const string withLegacy = """
        [
          { "Version": "28.2.50931.51727", "CreationTime": "2026-06-10T00:00:00Z" },
          { "Version": "15.0.36560.0",     "CreationTime": "2019-10-01T00:00:00Z" },
          { "Version": "14.18.41442.0",    "CreationTime": "2020-06-01T00:00:00Z" },
          { "Version": "13.0.30609.0",     "CreationTime": "2019-04-01T00:00:00Z" }
        ]
        """;

        var versions = BcArtifactIndex.ParseVersions(withLegacy, platformJson: null);

        versions.Should().ContainInOrder("28.2.50931.51727", "15.0.36560.0");
        versions.Should().NotContain("14.18.41442.0");
        versions.Should().NotContain("13.0.30609.0");
    }

    [Fact]
    public void SelectVersion_null_picks_newest()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        BcArtifactIndex.SelectVersion(versions, requested: null).Should().Be("28.2.50931.51727");
    }

    [Fact]
    public void SelectVersion_exact_match_wins()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        BcArtifactIndex.SelectVersion(versions, "28.1.49000.50000").Should().Be("28.1.49000.50000");
    }

    [Fact]
    public void SelectVersion_major_minor_prefix_picks_newest_of_that_minor()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        // Two 28.2 builds — the newer wins.
        BcArtifactIndex.SelectVersion(versions, "28.2").Should().Be("28.2.50931.51727");
    }

    [Fact]
    public void SelectVersion_returns_null_when_nothing_matches()
    {
        var versions = BcArtifactIndex.ParseVersions(CountryJson, PlatformJson);

        BcArtifactIndex.SelectVersion(versions, "99.9").Should().BeNull();
        BcArtifactIndex.SelectVersion(Array.Empty<string>(), requested: null).Should().BeNull();
    }

    [Theory]
    [InlineData("28.2.50931.51727", "28.2")]
    [InlineData("27.5.40000.41000", "27.5")]
    [InlineData("28", "28")]
    public void ToMajorMinor_takes_the_first_two_segments(string version, string expected)
    {
        BcArtifactIndex.ToMajorMinor(version).Should().Be(expected);
    }

    [Fact]
    public void FormatLabel_uses_major_minor_and_upper_country()
    {
        BcArtifactIndex.FormatLabel("28.2.50931.51727", "dk")
            .Should().Be("Business Central 28.2 (DK)");
    }

    [Theory]
    [InlineData("28.2.50931.51727", "dk", "bc-onprem:28.2:dk")]
    [InlineData("28.2.50931.51727", "DK", "bc-onprem:28.2:dk")] // country lower-cased
    [InlineData("25.18.0.0", "w1", "bc-onprem:25.18:w1")]
    public void FormatDedupKey_is_major_minor_plus_lower_country(string version, string country, string expected)
    {
        // The migration backfill derives the same key from "Business Central
        // {Maj}.{Min} ({CC})" labels — these must stay in lockstep.
        BcArtifactIndex.FormatDedupKey(version, country).Should().Be(expected);
    }

    [Fact]
    public void FormatLabel_and_dedup_key_mark_a_preview_build()
    {
        BcArtifactIndex.FormatLabel("29.0.55227.0", "dk", prerelease: true)
            .Should().Be("Business Central 29.0 (DK) Preview");
        BcArtifactIndex.FormatDedupKey("29.0.55227.0", "DK", prerelease: true)
            .Should().Be("bc-insider:29.0:dk", "a preview never collides with, or satisfies, the shipped key");
    }

    [Theory]
    [InlineData("bc-onprem:28.2:dk", "28.2", "dk")]
    [InlineData("bc-insider:29.0:w1", "29.0", "w1")]
    public void ParseDedupKey_reads_back_what_FormatDedupKey_wrote(string key, string majorMinor, string country)
    {
        BcArtifactIndex.ParseDedupKey(key).Should().Be((majorMinor, country));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("symbols:appid:1.0")]
    [InlineData("bc-onprem:28.2")]
    public void ParseDedupKey_returns_null_for_other_keys(string? key)
    {
        BcArtifactIndex.ParseDedupKey(key).Should().BeNull();
    }

    [Fact]
    public void SelectPreviewVersions_picks_one_build_per_upcoming_major()
    {
        // The insider index as captured on 2026-09-29 with 28.5 the newest shipped
        // release: minor previews of the current major (28.6), the next major in
        // two minors (29.0, 29.1), and the one after (30.0). Newest first.
        var previews = new[]
        {
            "30.0.55227.0", "30.0.55110.0",
            "29.1.55200.0",
            "29.0.55190.0", "29.0.55000.0",
            "28.6.55100.0",
            "27.12.55050.0",
        };

        var picked = BcArtifactIndex.SelectPreviewVersions(previews, newestReleasedMajor: 28);

        picked.Should().Equal("29.0.55190.0", "30.0.55227.0");
    }

    [Fact]
    public void SelectPreviewVersions_is_empty_when_nothing_is_ahead_of_the_shipped_major()
    {
        BcArtifactIndex.SelectPreviewVersions(new[] { "28.6.55100.0", "28.5.54000.0" }, newestReleasedMajor: 28)
            .Should().BeEmpty();
        BcArtifactIndex.SelectPreviewVersions(Array.Empty<string>(), newestReleasedMajor: 28).Should().BeEmpty();
    }

    [Fact]
    public void Preview_channel_urls_target_the_insider_host_and_sandbox_type()
    {
        BcArtifactIndex.CountryIndexUrl("DK", BcArtifactChannel.Preview)
            .Should().Be($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/indexes/dk.json");
        BcArtifactIndex.PlatformIndexUrl(BcArtifactChannel.Preview)
            .Should().Be($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/indexes/platform.json");
        BcArtifactIndex.BuildApplicationUrl("29.0.55190.0", "dk", BcArtifactChannel.Preview)
            .Should().Be($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/29.0.55190.0/dk");
        // The default channel is unchanged: shipped builds, public host, onprem type.
        BcArtifactIndex.CountryIndexUrl("dk")
            .Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/indexes/dk.json");
    }

    [Fact]
    public void BuildApplicationUrl_targets_the_cdn_host()
    {
        var url = BcArtifactIndex.BuildApplicationUrl("28.2.50931.51727", "DK");

        url.Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/28.2.50931.51727/dk");
    }

    [Fact]
    public void CountryIndexUrl_targets_the_cdn_host_and_lowercases_country()
    {
        // Microsoft 403s the raw blob host; the index is fetched from the CDN.
        BcArtifactIndex.CountryIndexUrl("DK")
            .Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/indexes/dk.json");
    }

    [Theory]
    // Blob host → CDN (the manifest platformUrl case that was 403-ing).
    [InlineData("https://bcartifacts.blob.core.windows.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // Legacy edge host → CDN.
    [InlineData("https://bcartifacts.azureedge.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // A stale/other Front Door host → the active CDN.
    [InlineData("https://bcartifacts-stalehash.b02.azurefd.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // Already on the active CDN → unchanged.
    [InlineData("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform",
                "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51727/platform")]
    // Foreign host → untouched (download-time trust check then refuses it).
    [InlineData("https://evil.example.com/onprem/x/platform",
                "https://evil.example.com/onprem/x/platform")]
    // Insider blob host → the insider Front Door, never the public one (a
    // preview's platform artifact only exists on the insider storage).
    [InlineData("https://bcinsider.blob.core.windows.net/sandbox/29.0.55190.0/platform",
                "https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net/sandbox/29.0.55190.0/platform")]
    // Already on the insider Front Door → unchanged.
    [InlineData("https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net/sandbox/29.0.55190.0/platform",
                "https://bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net/sandbox/29.0.55190.0/platform")]
    public void ToCdnUrl_rewrites_only_microsoft_artifact_hosts(string input, string expected)
    {
        BcArtifactIndex.ToCdnUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("bcartifacts.blob.core.windows.net", true)]
    [InlineData("bcartifacts-exdbf9fwegejdqak.b02.azurefd.net", true)]
    [InlineData("bcinsider.blob.core.windows.net", true)]
    [InlineData("bcinsider-fvh2ekdjecfjd6gk.b02.azurefd.net", true)]
    [InlineData("evil.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrustedArtifactHost_only_allows_microsoft_artifact_hosts(string? host, bool expected)
    {
        BcArtifactIndex.IsTrustedArtifactHost(host).Should().Be(expected);
    }

    [Fact]
    public void DerivePlatformUrl_swaps_the_country_segment_for_platform()
    {
        BcArtifactIndex.DerivePlatformUrl("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51034/dk")
            .Should().Be("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/onprem/28.2.50931.51034/platform");
    }

    [Fact]
    public void DerivePlatformUrl_returns_null_for_an_unparseable_url()
    {
        BcArtifactIndex.DerivePlatformUrl("not a url").Should().BeNull();
    }

    [Fact]
    public void ReadPlatformUrl_extracts_the_platform_url_from_a_manifest()
    {
        const string manifest = """
        { "platformUrl": "https://bcartifacts.blob.core.windows.net/onprem/28.2.50931.51727/platform", "version": "28.2" }
        """;

        BcArtifactIndex.ReadPlatformUrl(manifest)
            .Should().Be("https://bcartifacts.blob.core.windows.net/onprem/28.2.50931.51727/platform");
    }

    [Fact]
    public void ReadPlatformUrl_returns_null_when_absent_or_unparseable()
    {
        BcArtifactIndex.ReadPlatformUrl("""{ "version": "28.2" }""").Should().BeNull();
        BcArtifactIndex.ReadPlatformUrl("not json").Should().BeNull();
        BcArtifactIndex.ReadPlatformUrl("").Should().BeNull();
    }

    [Fact]
    public void ParseCountries_reads_the_country_array_lowercased()
    {
        var countries = BcArtifactIndex.ParseCountries("""[ "W1", "dk", "DE" ]""");

        countries.Should().BeEquivalentTo("w1", "dk", "de");
    }
}
