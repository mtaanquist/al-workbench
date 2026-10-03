namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One person's choice to follow, or stop following, one solution (issue #1048).
/// Followers hear about the solution's own events, such as Business Central update
/// dates. The owner and the people on the solution's People list follow without a
/// row; a row with <see cref="Following"/> false is how they opt out, and a row with
/// it true is how anyone else opts in. See <c>.design/notifications.md</c>.
/// </summary>
public class OeProjectFollower
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>True to follow, false to stop following a solution this person would otherwise follow by default.</summary>
    public bool Following { get; set; }

    public DateTime UpdatedAt { get; set; }
}
