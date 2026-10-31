using System.Net;
using ALDevToolbox.Services.OAuth;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// Pins <see cref="SsrfGuard.IsPubliclyRoutable"/>, the address filter that
/// stops the CIMD resolver's outbound fetch from reaching loopback,
/// link-local (incl. the cloud metadata service), private, CGNAT, and
/// unique-local destinations — the SSRF surface on <c>/oauth/authorize</c>.
/// </summary>
public sealed class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.31.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata service
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("fe80::1")]         // IPv6 link-local
    [InlineData("fc00::1")]         // IPv6 unique-local
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1")]  // IPv4-mapped private
    // #1576: multicast / reserved, and IPv6 forms that embed a private IPv4 address.
    [InlineData("224.0.0.1")]        // multicast
    [InlineData("239.255.255.250")]  // multicast (SSDP)
    [InlineData("240.0.0.1")]        // reserved
    [InlineData("64:ff9b::a9fe:a9fe")]  // NAT64 for 169.254.169.254
    [InlineData("64:ff9b::a00:1")]      // NAT64 for 10.0.0.1
    [InlineData("64:ff9b:1::808:808")]  // local-use NAT64 prefix
    [InlineData("2002:a9fe:a9fe::")]    // 6to4 for 169.254.169.254
    [InlineData("2002:c0a8:101::1")]    // 6to4 for 192.168.1.1
    [InlineData("::a9fe:a9fe")]         // IPv4-compatible 169.254.169.254
    [InlineData("::7f00:1")]            // IPv4-compatible 127.0.0.1
    public void Disallows_non_routable_addresses(string address)
    {
        SsrfGuard.IsPubliclyRoutable(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")] // example.com
    [InlineData("2606:4700:4700::1111")] // public IPv6 (Cloudflare)
    [InlineData("64:ff9b::808:808")]     // NAT64 for 8.8.8.8
    [InlineData("2002:808:808::1")]      // 6to4 for 8.8.8.8
    [InlineData("223.255.255.1")]        // last unicast /8 before multicast
    public void Allows_publicly_routable_addresses(string address)
    {
        SsrfGuard.IsPubliclyRoutable(IPAddress.Parse(address)).Should().BeTrue();
    }
}
