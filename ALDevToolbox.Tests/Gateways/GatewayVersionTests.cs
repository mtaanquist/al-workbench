using ALDevToolbox.Services.Gateways;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Gateway versions (#1363): how two compare, release candidates and previews
/// included, how a reported version lands on the Gateways page (current, out of
/// date, unsupported), and the download that matches a release of AL Workbench.
/// See <c>.design/on-prem-gateway.md</c>, "Versions and updates".
/// </summary>
public sealed class GatewayVersionTests
{
    [Theory]
    [InlineData("11.19.0", "11.19.1")]
    [InlineData("11.19.9", "11.20.0")]
    [InlineData("11.20.0", "12.0.0")]
    [InlineData("9.9.9", "10.0.0")] // numbers, not text: "9" < "10"
    [InlineData("12.0.0-rc.1", "12.0.0")] // a release candidate comes before its release
    [InlineData("12.0.0-rc.2", "12.0.0-rc.10")] // numeric parts compare as numbers
    [InlineData("12.0.0-alpha", "12.0.0-beta")]
    [InlineData("12.0.0-rc", "12.0.0-rc.1")] // fewer parts first when the rest are equal
    [InlineData("12.0.0-1", "12.0.0-alpha")] // numbers before words
    [InlineData("11.19.0", "12.0.0-rc.1")] // a candidate for the next major is still newer
    public void Versions_are_ordered_the_semantic_versioning_way(string older, string newer)
    {
        var a = GatewayVersion.Parse(older);
        var b = GatewayVersion.Parse(newer);

        (a < b).Should().BeTrue();
        (b > a).Should().BeTrue();
        a.CompareTo(b).Should().BeNegative();
        b.CompareTo(a).Should().BePositive();
    }

    [Theory]
    [InlineData("12.0.0", "v12.0.0")]
    [InlineData("12.0.0", "12.0.0+abc1234")] // build metadata is not part of the order
    [InlineData("12.0.0-rc.1", " 12.0.0-rc.1 ")]
    public void The_same_version_written_differently_is_equal(string left, string right)
    {
        GatewayVersion.Parse(left).Should().Be(GatewayVersion.Parse(right));
        (GatewayVersion.Parse(left) == GatewayVersion.Parse(right)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("12.0")]
    [InlineData("12.0.0.0")]
    [InlineData("12.0.x")]
    [InlineData("12.0.0-")]
    [InlineData("12.0.0-rc..1")]
    [InlineData("12.0.0-rc_1")]
    [InlineData("-1.0.0")]
    [InlineData("01.0.0")] // leading zeros are not allowed in numeric parts
    [InlineData("0.0.0-staging.0123456")] // why staging writes "g0123456"
    [InlineData("99999999999.0.0")]
    public void Anything_else_is_not_a_version(string? text)
    {
        GatewayVersion.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.0.0-dev", true)]
    [InlineData("0.0.0-staging.ga1b2c3d", true)]
    [InlineData("11.19.0", false)]
    [InlineData("12.0.0-rc.1", false)]
    public void Major_version_zero_is_a_preview(string text, bool preview)
    {
        GatewayVersion.Parse(text).IsPreview.Should().Be(preview);
    }

    [Theory]
    [InlineData("11.19.0-rc.1", "11.19.0", true)]
    [InlineData("11.19.0", "11.19.0", true)]
    [InlineData("11.20.0-alpha", "11.19.0", true)]
    [InlineData("11.18.9", "11.19.0", false)]
    [InlineData("11.18.99-rc.1", "11.19.0", false)]
    public void A_release_candidate_of_the_minimum_satisfies_it(string version, string minimum, bool satisfies)
    {
        GatewayVersion.Parse(version).SatisfiesMinimum(GatewayVersion.Parse(minimum)).Should().Be(satisfies);
    }

    [Fact]
    public void The_minimum_is_a_release_version()
    {
        var minimum = GatewayVersion.Parse(GatewayProtocol.MinimumGatewayVersion);

        minimum.IsPreview.Should().BeFalse();
        minimum.PreRelease.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the page's states

    [Theory]
    [InlineData("12.1.0", GatewayVersionState.Current)]
    [InlineData("12.2.0", GatewayVersionState.Current)] // newer than AL Workbench is not a problem
    [InlineData("12.1.0+abc1234", GatewayVersionState.Current)]
    [InlineData("12.1.0-rc.1", GatewayVersionState.OutOfDate)] // the candidate before this release
    [InlineData("12.0.3", GatewayVersionState.OutOfDate)]
    [InlineData(GatewayProtocol.MinimumGatewayVersion, GatewayVersionState.OutOfDate)] // still accepted
    [InlineData("11.18.9", GatewayVersionState.Unsupported)]
    [InlineData("1.0.0", GatewayVersionState.Unsupported)]
    [InlineData("11.19.0-rc.1", GatewayVersionState.OutOfDate)] // accepted, but older than 12.1.0
    [InlineData("0.0.0-dev", GatewayVersionState.Preview)]
    [InlineData("0.0.0-staging.ga1b2c3d", GatewayVersionState.Preview)]
    [InlineData(null, GatewayVersionState.Unknown)]
    [InlineData("not a version", GatewayVersionState.Unknown)]
    public void A_release_workbench_lists_each_gateway_by_how_its_version_compares(string? reported, GatewayVersionState expected)
    {
        var build = new GatewayBuild("12.1.0", null, signed: true);

        build.StateOf(reported).Should().Be(expected);
    }

    [Theory]
    [InlineData("12.0.0", GatewayVersionState.Current)]
    [InlineData("11.18.9", GatewayVersionState.Unsupported)]
    [InlineData("0.0.0-dev", GatewayVersionState.Preview)]
    public void A_local_workbench_has_no_version_to_be_out_of_date_against_but_still_knows_the_minimum(string reported, GatewayVersionState expected)
    {
        var build = new GatewayBuild(null, null, signed: false);

        build.StateOf(reported).Should().Be(expected);
        build.ExpectedVersion.Should().BeNull();
    }

    [Theory]
    [InlineData("11.18.9", true)]
    [InlineData("11.18.9-rc.1", true)]
    [InlineData("1.0.0", true)]
    [InlineData("11.19.0-rc.1", false)] // a release candidate of the minimum carries its change
    [InlineData("11.19.1-rc.1", false)]
    [InlineData(GatewayProtocol.MinimumGatewayVersion, false)]
    [InlineData("13.0.0", false)]
    [InlineData("0.0.0-dev", false)] // a preview is never refused for its version
    [InlineData(null, false)] // nor is a request that names none
    [InlineData("rubbish", false)]
    public void Only_a_release_below_the_minimum_is_refused(string? reported, bool refused)
    {
        GatewayBuild.IsBelowMinimum(reported).Should().Be(refused);
    }

    // ---------------------------------------------------------------- the download

    [Fact]
    public void A_release_links_the_gateway_attached_to_the_same_release_with_its_hash()
    {
        var hash = new string('a', 32) + new string('0', 32);

        var download = new GatewayBuild("v12.1.0", hash, signed: true).Download;

        download.Should().NotBeNull();
        download!.Version.Should().Be("12.1.0");
        download.Url.Should().Be("https://github.com/mtaanquist/al-workbench/releases/download/v12.1.0/ALWorkbench.Gateway.exe");
        download.ChecksumUrl.Should().Be("https://github.com/mtaanquist/al-workbench/releases/download/v12.1.0/ALWorkbench.Gateway.exe.sha256");
        download.Sha256.Should().Be(hash.ToUpperInvariant(), "shown the way PowerShell's Get-FileHash prints it");
        download.Signed.Should().BeTrue();
    }

    [Fact]
    public void A_release_candidate_links_its_own_release()
    {
        new GatewayBuild("12.1.0-rc.2", null, signed: false).Download!.Url
            .Should().Be("https://github.com/mtaanquist/al-workbench/releases/download/v12.1.0-rc.2/ALWorkbench.Gateway.exe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0-staging.ga1b2c3d")]
    [InlineData("not a version")]
    public void A_local_or_staging_workbench_offers_no_download(string? version)
    {
        new GatewayBuild(version, new string('a', 64), signed: true).Download.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_missing_or_malformed_hash_is_not_shown(string? hash)
    {
        new GatewayBuild("12.1.0", hash, signed: true).Download!.Sha256.Should().BeNull();
    }

    [Fact]
    public void An_unstamped_assembly_is_a_local_build()
    {
        var build = GatewayBuild.FromAssembly(typeof(GatewayVersionTests).Assembly);

        build.Download.Should().BeNull();
        build.ExpectedVersion.Should().BeNull();
    }
}
