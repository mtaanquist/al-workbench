namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A tenant boundary. Every editable entity in the system carries an
/// <c>OrganizationId</c> and queries scope to a single organisation; cross-org
/// reads are blocked by EF query filters and cross-org writes by service
/// guards (see <c>.design/auth-and-audit.md</c>).
/// </summary>
public class Organization
{
    public int Id { get; set; }

    /// <summary>Display name (e.g. <c>CRONUS</c>). Free text.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-safe identifier the signup form matches against
    /// (<c>^[a-z0-9-]+$</c>). Unique across the deployment.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Pending organisations exist while their first signup is awaiting
    /// approval. They have no users until the bootstrap admin (or another
    /// admin in another org, in a future flow) approves the signup.
    /// </summary>
    public bool IsPending { get; set; }

    /// <summary>
    /// True for the singleton "system" organisation that hosts the canonical
    /// templates, modules and application versions other orgs fork from via
    /// <see cref="Services.TemplateImportService"/>. The Default org is
    /// stamped <c>IsSystem = true</c> by the migration; the partial unique
    /// index on this column refuses a second system org. Regular orgs start
    /// empty and pull from the system catalogue on demand.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Per-organisation override of the storage quota in megabytes. Null
    /// means "fall back to <see cref="SystemSettings.DefaultStorageQuotaMb"/>";
    /// if both are null the organisation has no quota (∞). See the storage
    /// admin section of <c>.design/domain-model.md</c>.
    /// </summary>
    public int? StorageQuotaMb { get; set; }

    /// <summary>
    /// Per-organisation opt-out for the MCP server. Defaults to true so an org
    /// in a deployment with MCP enabled site-wide gets MCP automatically; admins
    /// can flip it off from <c>/admin/configuration/mcp</c>. Has no effect when
    /// the site-wide <see cref="SystemSettings.McpEnabled"/> is false.
    /// </summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>
    /// Tools this organisation has switched off, stored as
    /// <see cref="Domain.Tools.ToolKey"/> names. Empty by default — an org sees
    /// every site-enabled tool until an admin turns one off on
    /// <c>/admin/administration/tools</c>. Has no effect on a tool already
    /// disabled site-wide (that one is hidden regardless). MCP isn't listed
    /// here; it keeps its own <see cref="McpEnabled"/> flag.
    /// </summary>
    public List<string> DisabledTools { get; set; } = new();

    /// <summary>
    /// Tools this organisation wants a recent multi-factor sign-in for, stored
    /// as <see cref="Domain.Tools.ToolKey"/> names. Empty by default. A member
    /// opening one of these tools' pages, or calling one of its writing MCP
    /// tools, must have completed a second factor (authenticator app, email
    /// code, recovery code, passkey with user verification, or a fresh Microsoft
    /// sign-in) within <c>StepUpAuth.Window</c>; otherwise they are sent to
    /// confirm it is them first. See ".design/auth-and-audit.md", "Step-up for
    /// sensitive tools".
    /// </summary>
    public List<string> StepUpTools { get; set; } = new();

    public DateTime CreatedAt { get; set; }
}
