using System.Security.Claims;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The release-import background worker has no <c>HttpContext</c>, so it relies
/// on <see cref="AmbientOrganizationScope"/> feeding the submitting user's org
/// into <see cref="HttpOrganizationContext"/>. These guard that fallback —
/// and, critically, that a real request's claims always win over any ambient
/// value so the mechanism can't be used to widen a request's reach.
/// </summary>
public sealed class AmbientOrganizationScopeTests
{
    [Fact]
    public void Falls_back_to_ambient_when_no_http_context()
    {
        var ctx = new HttpOrganizationContext(new HttpContextAccessor());

        ctx.CurrentOrganizationId.Should().BeNull("nothing is in scope yet");

        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(
            OrganizationId: 42, UserId: 7, IsSiteAdmin: true, IsSystemOrganization: false)))
        {
            ctx.CurrentOrganizationId.Should().Be(42);
            ctx.CurrentUserId.Should().Be(7);
            ctx.IsSiteAdmin.Should().BeTrue();
            ctx.OrganizationIdForFilter.Should().Be(42);
        }

        ctx.CurrentOrganizationId.Should().BeNull("the scope was disposed");
    }

    [Fact]
    public void Http_claims_win_over_ambient()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(HttpOrganizationContext.OrganizationIdClaim, "9"),
                    new Claim(HttpOrganizationContext.UserIdClaim, "11"),
                }, authenticationType: "test")),
            },
        };
        var ctx = new HttpOrganizationContext(accessor);

        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(
            OrganizationId: 42, UserId: 7, IsSiteAdmin: true, IsSystemOrganization: false)))
        {
            ctx.CurrentOrganizationId.Should().Be(9, "a real request's claim must override the ambient fallback");
            ctx.CurrentUserId.Should().Be(11, "the request's user claim must override the ambient user");
            ctx.IsSiteAdmin.Should().BeFalse("the request has no site_admin claim, so the ambient flag must not leak in");
        }
    }

    [Fact]
    public void Org_and_user_fall_back_to_ambient_claim_by_claim()
    {
        // Pinned as it behaves: unlike the SiteAdmin flag, the org and user ids fall
        // back one claim at a time, so a request missing its user claim reads the
        // ambient user. A real sign-in cookie always carries both, which is why this
        // is noted rather than changed; changing it should flip this test on purpose.
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(HttpOrganizationContext.OrganizationIdClaim, "9"),
                }, authenticationType: "test")),
            },
        };
        var ctx = new HttpOrganizationContext(accessor);

        ctx.CurrentUserId.Should().BeNull("nothing is in scope yet");
        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(
            OrganizationId: 42, UserId: 7, IsSiteAdmin: true, IsSystemOrganization: false)))
        {
            ctx.CurrentOrganizationId.Should().Be(9);
            ctx.CurrentUserId.Should().Be(7);
        }
    }

    [Fact]
    public void Nested_scopes_restore_the_previous_value()
    {
        var ctx = new HttpOrganizationContext(new HttpContextAccessor());

        using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(1, null, false, false)))
        {
            ctx.CurrentOrganizationId.Should().Be(1);
            using (AmbientOrganizationScope.Enter(new AmbientOrganizationScope.OrganizationIdentity(2, null, false, false)))
            {
                ctx.CurrentOrganizationId.Should().Be(2);
            }
            ctx.CurrentOrganizationId.Should().Be(1, "disposing the inner scope restores the outer org");
        }
    }
}
