using ALDevToolbox.Services;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// The identity <see cref="TestDb.OrgContext"/> acts under. Unlike the
/// production <see cref="AmbientOrganizationContext"/>, whether the acting org
/// is the system org is not a separate setting: it follows from which org is
/// acting, the way a real sign-in reads it off the org row. A test that wants
/// system-org behaviour switches to <see cref="TestDb.SystemOrgId"/>; it cannot
/// claim the system org is a tenant, or a tenant is the system org (#1441).
/// </summary>
public sealed class TestOrganizationContext : IOrganizationContext
{
    public int? CurrentOrganizationId { get; set; }
    public int? CurrentUserId { get; set; }
    public bool IsSiteAdmin { get; set; }
    public bool IsSystemOrganization => CurrentOrganizationId == TestDb.SystemOrgId;
    public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
}
