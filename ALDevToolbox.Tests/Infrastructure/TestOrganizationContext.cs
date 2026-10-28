using ALDevToolbox.Services;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// The identity <see cref="TestDb.OrgContext"/> acts under. Unlike the
/// production <see cref="AmbientOrganizationContext"/>, whether the acting org
/// is the system org is not a separate setting: it is true exactly when the
/// acting org is the constant <see cref="TestDb.SystemOrgId"/>. That constant
/// stands in for the org row a real sign-in reads the flag from, and
/// <see cref="TestDbOrganizationsTests"/> pins that the two agree. A test that
/// wants system-org behaviour switches to <see cref="TestDb.SystemOrgId"/>; it
/// cannot claim the system org is a tenant, or a tenant is the system org (#1441).
///
/// <para>It ignores <see cref="AmbientOrganizationScope"/>: the acting org is
/// whatever the test set. Production off-request code reads the ambient scope,
/// so a worker or scheduler that loops over organisations needs an off-request
/// <see cref="HttpOrganizationContext"/> in its test, not this one.</para>
/// </summary>
public sealed class TestOrganizationContext : IOrganizationContext
{
    public int? CurrentOrganizationId { get; set; }
    public int? CurrentUserId { get; set; }
    public bool IsSiteAdmin { get; set; }
    public bool IsSystemOrganization => CurrentOrganizationId == TestDb.SystemOrgId;
    public int OrganizationIdForFilter => CurrentOrganizationId ?? 0;
}
