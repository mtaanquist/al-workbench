using System.Security.Claims;
using ALDevToolbox.Services.Account;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The freshness rule behind step-up: which sign-in methods count as strong,
/// how the stamps ride the cookie's properties and mirror onto claims, and
/// the window a strong moment stays good for. Pure: no DB, no host.
/// </summary>
public sealed class StepUpAuthTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(SignInMethod.Totp, true)]
    [InlineData(SignInMethod.EmailCode, true)]
    [InlineData(SignInMethod.RecoveryCode, true)]
    [InlineData(SignInMethod.PasskeyVerified, true)]
    [InlineData(SignInMethod.Entra, true)]
    [InlineData(SignInMethod.Password, false)]
    [InlineData(SignInMethod.Passkey, false)]
    [InlineData(SignInMethod.MagicLink, false)]
    [InlineData(SignInMethod.EntraSso, false)]
    public void Only_second_factor_methods_are_strong(SignInMethod method, bool strong)
    {
        StepUpAuth.IsStrong(method).Should().Be(strong);
    }

    [Fact]
    public void A_fresh_sign_in_earns_the_moment_only_for_a_strong_method()
    {
        StepUpAuth.StrongMomentFor(SignInMethod.Totp, Now).Should().Be(Now);
        StepUpAuth.StrongMomentFor(SignInMethod.Password, Now).Should().BeNull();
        StepUpAuth.StrongMomentFor(SignInMethod.EntraSso, Now).Should().BeNull(
            "Microsoft without auth_time may have reused its own session");
    }

    [Fact]
    public void Stamp_writes_exactly_what_it_is_given()
    {
        var strong = new AuthenticationProperties();
        StepUpAuth.Stamp(strong, SignInMethod.Totp, Now);
        strong.Items[StepUpAuth.MethodKey].Should().Be("Totp");
        StepUpAuth.StrongAuthAt(strong).Should().Be(Now);

        // A re-issue of the same session passes the moment it already had,
        // whatever the method: a password change must neither refresh nor
        // lose an earlier step-up.
        var reissued = new AuthenticationProperties();
        StepUpAuth.Stamp(reissued, SignInMethod.Totp, Now.AddHours(-8));
        StepUpAuth.StrongAuthAt(reissued).Should().Be(Now.AddHours(-8));

        var none = new AuthenticationProperties();
        none.Items[StepUpAuth.StrongAuthAtKey] = "stale";
        StepUpAuth.Stamp(none, SignInMethod.Password, null);
        StepUpAuth.StrongAuthAt(none).Should().BeNull("a null moment removes an earlier stamp");
    }

    [Fact]
    public void Stamps_mirror_onto_claims_and_replace_stale_copies()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(StepUpAuth.MethodClaim, "Password"),
            new Claim(StepUpAuth.StrongAuthAtClaim, "garbage"),
        }, "test");
        var props = new AuthenticationProperties();
        StepUpAuth.Stamp(props, SignInMethod.Entra, Now);

        StepUpAuth.ApplyClaims(identity, props);

        identity.FindAll(StepUpAuth.MethodClaim).Should().ContainSingle().Which.Value.Should().Be("Entra");
        StepUpAuth.Method(new ClaimsPrincipal(identity)).Should().Be(SignInMethod.Entra);
        StepUpAuth.StrongAuthAt(new ClaimsPrincipal(identity)).Should().Be(Now);
    }

    [Fact]
    public void Fresh_inside_the_window_and_stale_after_it()
    {
        StepUpAuth.IsFresh(Now.AddMinutes(-14), Now).Should().BeTrue();
        StepUpAuth.IsFresh(Now - StepUpAuth.Window, Now).Should().BeTrue("the window is inclusive");
        StepUpAuth.IsFresh(Now - StepUpAuth.Window - TimeSpan.FromSeconds(1), Now).Should().BeFalse();
        StepUpAuth.IsFresh((DateTime?)null, Now).Should().BeFalse("a session that never had a second factor is never fresh");
    }

    [Fact]
    public void A_stamp_from_the_future_is_not_fresh()
    {
        // A forged or mis-clocked stamp far ahead of now must not buy an
        // arbitrarily long window; a few minutes of skew is tolerated.
        StepUpAuth.IsFresh(Now.AddMinutes(2), Now).Should().BeTrue();
        StepUpAuth.IsFresh(Now.AddHours(1), Now).Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_claim_reads_as_no_strong_moment()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(StepUpAuth.StrongAuthAtClaim, "not a date") }, "test"));

        StepUpAuth.IsFresh(principal, Now).Should().BeFalse();
    }
}
