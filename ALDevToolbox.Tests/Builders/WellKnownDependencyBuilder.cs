using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Tests.Builders;

/// <summary>Helpers for constructing <see cref="WellKnownDependency"/> rows in tests.</summary>
public static class WellKnownDependencyBuilder
{
    public const int DefaultOrganizationId = TestDb.DefaultOrgId;

    public static WellKnownDependency ForNav(string id, string name, string version = "24.0.0.0", int ordering = 0, int organizationId = DefaultOrganizationId) => new()
    {
        OrganizationId = organizationId,
        DepId = id,
        DepName = name,
        DepPublisher = "ForNAV",
        DepVersionDefault = version,
        Ordering = ordering,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
