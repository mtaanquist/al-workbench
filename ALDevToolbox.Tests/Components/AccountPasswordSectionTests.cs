using ALDevToolbox.Components.Pages;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Password card on the account page: a member of an organisation that
/// only allows Microsoft sign-in is told so up front instead of being offered
/// a form the server will refuse (#1455).
/// </summary>
public sealed class AccountPasswordSectionTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public AccountPasswordSectionTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Offers_the_change_password_form_when_password_sign_in_is_allowed()
    {
        var cut = _ctx.Render<AccountPasswordSection>(p => p.Add(c => c.PasswordSignInDisabled, false));

        cut.FindAll("form[action='/auth/account/password']").Should().ContainSingle();
        cut.Find("button[type=submit]").TextContent.Should().Contain("Update password");
    }

    [Fact]
    public void Says_the_person_signs_in_with_Microsoft_instead_of_showing_the_form()
    {
        var cut = _ctx.Render<AccountPasswordSection>(p => p.Add(c => c.PasswordSignInDisabled, true));

        cut.FindAll("form").Should().BeEmpty();
        cut.FindAll("input[type=password]").Should().BeEmpty();
        cut.Find(".card__sub").TextContent.Should().Contain("signs in with Microsoft");
    }
}
