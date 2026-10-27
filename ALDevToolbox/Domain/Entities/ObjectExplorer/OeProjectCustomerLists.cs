namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

// The short hand-kept lists on a solution's Customer tab. See
// .design/solution-customer-info.md. Each is a handful of rows per solution, read far
// more often than written.

/// <summary>Who a <see cref="OeProjectContact"/> is, from the customer's side of the table or ours.</summary>
public enum ProjectContactType
{
    Customer,
    HostingPartner,
    MicrosoftPartner,
    Internal,
}

/// <summary>Someone to call or write to about this customer.</summary>
public class OeProjectContact
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    public ProjectContactType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>What one of our people does for a customer.</summary>
public enum ProjectPersonRole
{
    Consultant,
    Developer,
    ProjectLeader,
    Architect,
}

/// <summary>
/// One of our own users who knows this customer, and what about. Deliberately not a
/// team: <see cref="OeProjectTeam"/> says who may change the solution, this says who to ask.
/// </summary>
public class OeProjectPerson
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// What they are on this customer, at least one. Several when someone is, say, both
    /// a developer and the project leader; notifications go by role (#1462, #1463).
    /// </summary>
    public List<ProjectPersonRole> Roles { get; set; } = new();

    /// <summary>Free text: "finance, warehouse".</summary>
    public string? Areas { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Which way data flows through a <see cref="OeProjectIntegration"/>, seen from Business Central.</summary>
public enum ProjectIntegrationDirection
{
    Inbound,
    Outbound,
    Both,
}

/// <summary>Another system the customer's Business Central talks to.</summary>
public class OeProjectIntegration
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    public string Name { get; set; } = string.Empty;
    public ProjectIntegrationDirection Direction { get; set; }

    public DateTime CreatedAt { get; set; }
}
