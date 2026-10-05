using ALDevToolbox.Endpoints;
using ALDevToolbox.Startup;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ALDevToolbox.Tests.Startup;

/// <summary>
/// The Microsoft step-up and "connect account" callbacks decide by the auth
/// cookie on the callback request. That cookie is SameSite=Lax, which a
/// browser sends on a top-level GET from login.microsoftonline.com but not on
/// the cross-site form_post the OIDC handler uses by default, so with
/// form_post every step-up came back as "didn't match your account".
/// </summary>
public class EntraCallbackResponseModeTests
{
    [Fact]
    public void Microsoft_returns_to_the_callback_with_a_get_so_the_auth_cookie_rides_along()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddAppAuthentication();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(EntraAuthEndpoints.AuthenticationScheme);

        options.ResponseType.Should().Be(OpenIdConnectResponseType.Code);
        options.ResponseMode.Should().Be(OpenIdConnectResponseMode.Query);
    }
}
