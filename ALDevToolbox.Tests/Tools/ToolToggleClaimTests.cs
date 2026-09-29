using System.Security.Claims;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// The per-org disabled-tools opt-out rides on the <c>org_disabled_tools</c>
/// auth claim, built in <see cref="EndpointHelpers.BuildIdentity"/> and read
/// back by <see cref="EndpointHelpers.ReadDisabledTools"/>. These pin the
/// round-trip, including MCP being folded in from the org's McpEnabled flag.
/// </summary>
public sealed class ToolToggleClaimTests
{
    private static User UserIn(Organization org) => new()
    {
        Id = 7,
        OrganizationId = org.Id,
        Organization = org,
        Email = "a@example.com",
        DisplayName = "A",
        PasswordHash = "x",
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    private static ClaimsPrincipal PrincipalFor(Organization org) =>
        new(EndpointHelpers.BuildIdentity(UserIn(org)));

    [Fact]
    public void No_disabled_tools_yields_empty_set()
    {
        var org = new Organization { Id = 1, Name = "CRONUS", McpEnabled = true };

        EndpointHelpers.ReadDisabledTools(PrincipalFor(org)).Should().BeEmpty();
    }

    [Fact]
    public void Org_disabled_tools_round_trip_through_the_claim()
    {
        var org = new Organization
        {
            Id = 1,
            Name = "CRONUS",
            McpEnabled = true,
            DisabledTools = ToolCatalog.Format(new[] { ToolKey.Projects, ToolKey.Cookbook }),
        };

        EndpointHelpers.ReadDisabledTools(PrincipalFor(org))
            .Should().BeEquivalentTo(new[] { ToolKey.Projects, ToolKey.Cookbook });
    }

    [Fact]
    public void Step_up_tools_round_trip_through_their_own_claim()
    {
        var org = new Organization
        {
            Id = 1,
            Name = "CRONUS",
            McpEnabled = true,
            StepUpTools = ToolCatalog.Format(new[] { ToolKey.Releases, ToolKey.Templates }),
        };

        EndpointHelpers.ReadStepUpTools(PrincipalFor(org))
            .Should().BeEquivalentTo(new[] { ToolKey.Releases, ToolKey.Templates });
        EndpointHelpers.ReadStepUpTools(new ClaimsPrincipal(new ClaimsIdentity()))
            .Should().BeEmpty("a bearer principal carries no such claim");
    }

    [Fact]
    public void Mcp_off_folds_into_the_disabled_set()
    {
        var org = new Organization { Id = 1, Name = "CRONUS", McpEnabled = false };

        EndpointHelpers.ReadDisabledTools(PrincipalFor(org)).Should().Contain(ToolKey.Mcp);
    }

    [Fact]
    public void Read_returns_empty_when_claim_absent()
    {
        // A principal with no org_disabled_tools claim (e.g. PAT / OAuth).
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        EndpointHelpers.ReadDisabledTools(principal).Should().BeEmpty();
    }
}
