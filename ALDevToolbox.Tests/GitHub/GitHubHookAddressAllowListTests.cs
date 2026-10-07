using System.Net;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Auth;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// Who may deliver to the webhook once GitHub's published hook ranges are loaded
/// (#1201), and who may while they are not.
/// </summary>
public sealed class GitHubHookAddressAllowListTests
{
    /// <summary>The shape of GitHub's <c>hooks</c> list: IPv4 and IPv6 ranges.</summary>
    internal static readonly string[] HookRanges =
        ["192.30.252.0/22", "185.199.108.0/22", "140.82.112.0/20", "143.55.64.0/20", "2a0a:a440::/29", "2606:50c0::/32"];

    private static GitHubHookAddressAllowList Loaded(TimeProvider? clock = null)
    {
        var list = new GitHubHookAddressAllowList(clock ?? TimeProvider.System);
        list.Replace(GitHubHookAddressAllowList.Parse(HookRanges).Ranges);
        return list;
    }

    [Theory]
    [InlineData("192.30.252.1")]
    [InlineData("140.82.115.250")]
    [InlineData("2606:50c0::17")]
    [InlineData("2a0a:a447:ffff::1")]
    public void An_address_inside_a_published_range_is_allowed(string address)
    {
        Loaded().Check(IPAddress.Parse(address)).Should().Be(GitHubHookAddressVerdict.Allowed);
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("140.82.128.0")]
    [InlineData("2001:db8::1")]
    [InlineData("2606:50c1::1")]
    public void An_address_outside_every_range_is_refused(string address)
    {
        Loaded().Check(IPAddress.Parse(address)).Should().Be(GitHubHookAddressVerdict.Refused);
    }

    [Fact]
    public void An_ipv4_address_mapped_into_ipv6_is_judged_as_the_ipv4_address()
    {
        var list = Loaded();
        list.Check(IPAddress.Parse("::ffff:192.30.252.1")).Should().Be(GitHubHookAddressVerdict.Allowed,
            "a dual-stack listener reports an IPv4 sender this way");
        list.Check(IPAddress.Parse("::ffff:203.0.113.7")).Should().Be(GitHubHookAddressVerdict.Refused);
    }

    [Fact]
    public void Before_any_list_is_loaded_every_sender_is_let_through()
    {
        var list = new GitHubHookAddressAllowList(TimeProvider.System);

        list.LoadedAt.Should().BeNull();
        list.Check(IPAddress.Parse("203.0.113.7")).Should().Be(GitHubHookAddressVerdict.NotLoaded);
        list.Check(null).Should().Be(GitHubHookAddressVerdict.NotLoaded);
    }

    [Fact]
    public void Once_a_list_is_loaded_a_sender_with_no_address_is_refused()
    {
        Loaded().Check(null).Should().Be(GitHubHookAddressVerdict.Refused);
    }

    [Fact]
    public void An_empty_list_is_never_put_in_use()
    {
        var list = Loaded();
        var replace = () => list.Replace([]);

        replace.Should().Throw<ArgumentException>();
        list.Check(IPAddress.Parse("192.30.252.1")).Should().Be(GitHubHookAddressVerdict.Allowed,
            "the list that was in use is still in use");
    }

    [Fact]
    public void Entries_that_are_not_ranges_are_set_aside_without_losing_the_rest()
    {
        var (ranges, invalid) = GitHubHookAddressAllowList.Parse(
            ["192.30.252.0/22", "not-a-range", "10.0.0.1/33", "198.51.100.4", " 2606:50c0::/32 "]);

        ranges.Select(r => r.ToString()).Should().Equal("192.30.252.0/22", "198.51.100.4/32", "2606:50c0::/32");
        invalid.Should().Equal("not-a-range", "10.0.0.1/33");
    }

    [Fact]
    public void Refusal_warnings_are_rate_limited_and_count_what_went_unlogged()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var list = Loaded(clock);

        list.ShouldWarnRefusal(out var first).Should().BeTrue();
        first.Should().Be(0);
        list.ShouldWarnRefusal(out _).Should().BeFalse();
        list.ShouldWarnRefusal(out _).Should().BeFalse();

        clock.Advance(GitHubHookAddressAllowList.WarningInterval);
        list.ShouldWarnRefusal(out var later).Should().BeTrue();
        later.Should().Be(2, "two refusals were swallowed in between");
    }

    [Fact]
    public void The_not_loaded_warning_is_rate_limited()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var list = new GitHubHookAddressAllowList(clock);

        list.ShouldWarnUnloaded().Should().BeTrue();
        list.ShouldWarnUnloaded().Should().BeFalse();
        clock.Advance(GitHubHookAddressAllowList.WarningInterval);
        list.ShouldWarnUnloaded().Should().BeTrue();
    }
}
