using ALDevToolbox.Endpoints;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The cookies behind the "shown only once" pages: a new access token, a fresh
/// set of recovery codes, an invite link. The page's promise rests on the
/// first read deleting the cookie, and on a value that was tampered with or
/// sealed for a different page reading as nothing.
/// </summary>
public sealed class OneShotCookieTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    [Fact]
    public void An_access_token_reads_once_and_the_read_deletes_its_cookie()
    {
        var pat = new EndpointHelpers.OneShotPat(42, "alwb_pat_secret", "CRONUS laptop", Now, Now.AddDays(30));
        var issued = new DefaultHttpContext();
        EndpointHelpers.SetOneShotPatCookie(issued, _protection, pat);

        var reader = Carrying(EndpointHelpers.OneShotPatCookieName, CookieValue(issued, EndpointHelpers.OneShotPatCookieName));

        EndpointHelpers.ReadAndClearOneShotPatCookie(reader, _protection).Should().Be(pat);
        DeletesCookie(reader, EndpointHelpers.OneShotPatCookieName).Should().BeTrue();
    }

    [Fact]
    public void Recovery_codes_read_once_and_the_read_deletes_their_cookie()
    {
        var issued = new DefaultHttpContext();
        EndpointHelpers.SetOneShotRecoveryCodesCookie(issued, _protection, ["AAAA-BBBB", "CCCC-DDDD"]);

        var reader = Carrying(EndpointHelpers.OneShotRecoveryCodesCookieName,
            CookieValue(issued, EndpointHelpers.OneShotRecoveryCodesCookieName));

        EndpointHelpers.ReadAndClearOneShotRecoveryCodesCookie(reader, _protection)
            .Should().Equal("AAAA-BBBB", "CCCC-DDDD");
        DeletesCookie(reader, EndpointHelpers.OneShotRecoveryCodesCookieName).Should().BeTrue();
    }

    [Fact]
    public void An_invite_link_reads_once_and_the_read_deletes_its_cookie()
    {
        const string url = "https://workbench.cronus.example/invite?token=abc";
        var issued = new DefaultHttpContext();
        EndpointHelpers.SetOneShotInviteCookie(issued, _protection, url);

        var reader = Carrying(EndpointHelpers.OneShotInviteCookieName,
            CookieValue(issued, EndpointHelpers.OneShotInviteCookieName));

        EndpointHelpers.ReadAndClearOneShotInviteCookie(reader, _protection).Should().Be(url);
        DeletesCookie(reader, EndpointHelpers.OneShotInviteCookieName).Should().BeTrue();
    }

    [Fact]
    public void A_value_sealed_for_another_page_reads_as_nothing()
    {
        // Recovery codes planted under the access-token cookie's name.
        var issued = new DefaultHttpContext();
        EndpointHelpers.SetOneShotRecoveryCodesCookie(issued, _protection, ["AAAA-BBBB"]);

        var reader = Carrying(EndpointHelpers.OneShotPatCookieName,
            CookieValue(issued, EndpointHelpers.OneShotRecoveryCodesCookieName));

        EndpointHelpers.ReadAndClearOneShotPatCookie(reader, _protection).Should().BeNull();
        DeletesCookie(reader, EndpointHelpers.OneShotPatCookieName).Should().BeTrue("a bad value is cleared too");
    }

    [Fact]
    public void A_tampered_value_reads_as_nothing()
    {
        var issued = new DefaultHttpContext();
        EndpointHelpers.SetOneShotInviteCookie(issued, _protection, "https://workbench.cronus.example/invite?token=abc");
        var value = CookieValue(issued, EndpointHelpers.OneShotInviteCookieName);
        var tampered = value[..^2] + (value[^2] == 'A' ? 'B' : 'A') + value[^1];

        var reader = Carrying(EndpointHelpers.OneShotInviteCookieName, tampered);

        EndpointHelpers.ReadAndClearOneShotInviteCookie(reader, _protection).Should().BeNull();
    }

    [Fact]
    public void No_cookie_reads_as_nothing_and_writes_nothing()
    {
        var reader = new DefaultHttpContext();

        EndpointHelpers.ReadAndClearOneShotPatCookie(reader, _protection).Should().BeNull();
        reader.Response.Headers.SetCookie.Should().BeEmpty();
    }

    private static DefaultHttpContext Carrying(string name, string value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{name}={Uri.EscapeDataString(value)}";
        return ctx;
    }

    private static string CookieValue(HttpContext issued, string name)
    {
        var header = issued.Response.Headers.SetCookie.Single(h => h!.StartsWith(name + "=", StringComparison.Ordinal))!;
        var start = name.Length + 1;
        return Uri.UnescapeDataString(header[start..header.IndexOf(';')]);
    }

    /// <summary>True when the response tells the browser to drop <paramref name="name"/>: an empty value dated in the past.</summary>
    private static bool DeletesCookie(HttpContext reader, string name) =>
        reader.Response.Headers.SetCookie.Any(h =>
            h!.StartsWith(name + "=;", StringComparison.Ordinal)
            && h.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
}
