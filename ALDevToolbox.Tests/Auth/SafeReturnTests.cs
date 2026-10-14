using ALDevToolbox.Endpoints;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The open-redirect guard behind every <c>?return=</c>: same-site paths only,
/// plus a plain element-id fragment so a step-up can come back to the card it
/// left from.
/// </summary>
public sealed class SafeReturnTests
{
    [Theory]
    [InlineData("/account/security?ok=confirmed-passkey#passkeys", "/account/security?ok=confirmed-passkey#passkeys")]
    [InlineData("/account/security", "/account/security")]
    [InlineData("/pipelines/deployments/7?tab=history", "/pipelines/deployments/7?tab=history")]
    public void A_same_site_path_comes_back_unchanged(string requested, string expected) =>
        EndpointHelpers.ResolveSafeReturn(requested).Should().Be(expected);

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("//evil.example/#passkeys")]
    [InlineData("https://evil.example/#passkeys")]
    [InlineData("#passkeys")]
    [InlineData("")]
    public void Anything_that_could_leave_the_site_goes_home(string requested) =>
        EndpointHelpers.ResolveSafeReturn(requested).Should().Be("/");

    [Theory]
    [InlineData("/account/security#<script>", "/account/security")]
    [InlineData("/account/security#a/b", "/account/security")]
    [InlineData("/account/security#", "/account/security")]
    public void A_fragment_that_is_not_a_plain_id_is_dropped(string requested, string expected) =>
        EndpointHelpers.ResolveSafeReturn(requested).Should().Be(expected);
}
