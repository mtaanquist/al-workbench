using System.Net;
using System.Text;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Pins what <see cref="TestCallerAddress"/> is for (#1604): on a shared host,
/// calls from different tests' addresses land in different rate-limit
/// partitions, and calls from one address still share one.
/// </summary>
public sealed class TestCallerAddressTests : IClassFixture<SharedEndpointHost>
{
    /// <summary>Anonymous registrations one caller may make in the window.</summary>
    private const int RegistrationLimit = 10;

    private readonly EndpointFactory _factory;

    public TestCallerAddressTests(SharedEndpointHost host) => _factory = host.Factory;

    [Fact]
    public async Task Each_address_gets_its_own_rate_limit_partition()
    {
        var crowded = TestCallerAddress.Next();
        using var client = _factory.CreateClient().From(crowded);
        for (var i = 0; i < RegistrationLimit; i++)
        {
            using var allowed = await RegisterAsync(client);
            allowed.StatusCode.Should().Be(HttpStatusCode.Created, $"registration {i + 1} is within the limit");
        }

        using var refused = await RegisterAsync(client);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the same address is still one partition");

        using var other = _factory.CreateClient().From(TestCallerAddress.Next());
        using var fresh = await RegisterAsync(other);
        fresh.StatusCode.Should().Be(HttpStatusCode.Created,
            "another test's address does not share the first one's count");
    }

    [Fact]
    public void Addresses_stay_in_the_benchmarking_range_and_do_not_repeat_soon()
    {
        var range = IPNetwork.Parse("198.18.0.0/15");
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            var address = TestCallerAddress.Next();
            range.Contains(IPAddress.Parse(address)).Should().BeTrue(address);
            seen.Add(address).Should().BeTrue($"{address} was handed out twice");
        }
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client) =>
        client.PostAsync("/oauth/register", new StringContent(
            """{"redirect_uris":["http://127.0.0.1:41999/callback"],"client_name":"CRONUS test client"}""",
            Encoding.UTF8, "application/json"));
}
