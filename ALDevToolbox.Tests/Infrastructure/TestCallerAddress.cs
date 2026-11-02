using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Gives each test its own caller address on a shared host.
///
/// <para>The test server sends every request without a remote address, so the
/// rate limiters that partition by caller put the whole class in one partition.
/// That is harmless until a class makes more calls than a limit allows - ten
/// anonymous registrations at <c>/oauth/register</c> in ten minutes - and then
/// whichever test comes later gets a 429 (#1604). A request that carries
/// <see cref="Header"/> arrives from the address it names instead, and
/// <see cref="Next"/> hands each test an address of its own.</para>
///
/// <para><see cref="SharedEndpointHost"/> installs the filter, so a class on a
/// shared host only needs to put its address on the clients it sends from.</para>
/// </summary>
public static class TestCallerAddress
{
    public const string Header = "X-Test-Connection-Address";

    private static int _last;

    /// <summary>An address from the 198.18.0.0/15 benchmarking range that no recent test has used.</summary>
    public static string Next()
    {
        var n = (int)((uint)Interlocked.Increment(ref _last) % (256 * 250));
        return $"198.18.{n / 250}.{n % 250 + 1}";
    }

    /// <summary>Makes every request <paramref name="client"/> sends arrive from <paramref name="address"/>.</summary>
    public static HttpClient From(this HttpClient client, string address)
    {
        client.DefaultRequestHeaders.Remove(Header);
        client.DefaultRequestHeaders.Add(Header, address);
        return client;
    }

    /// <summary>Installs the filter that honours <see cref="Header"/>.</summary>
    public static IServiceCollection AddTestCallerAddress(this IServiceCollection services) =>
        services.AddSingleton<IStartupFilter, AddressFilter>();

    /// <summary>Runs ahead of the app's pipeline, so the rate limiters partition on the named address.</summary>
    private sealed class AddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(nextMiddleware => ctx =>
            {
                var named = ctx.Request.Headers[Header].ToString();
                if (named.Length > 0) ctx.Connection.RemoteIpAddress = IPAddress.Parse(named);
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }
}
