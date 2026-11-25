using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Tests.Builders;

/// <summary>Helpers for constructing <see cref="Module"/> rows in tests.</summary>
public static class ModuleBuilder
{
    public const int DefaultOrganizationId = TestDb.DefaultOrgId;

    public static Module Default(string key = "test-module", string name = "Test Module", int? idRangeSize = null, int organizationId = DefaultOrganizationId, string? extensionName = null) => new()
    {
        OrganizationId = organizationId,
        Key = key,
        Name = name,
        // PascalCase default derived from the display name so existing call
        // sites don't have to be touched. Tests asserting on the folder name
        // should set extensionName explicitly.
        ExtensionName = extensionName ?? PascalCaseFromName(name),
        IdRangeSize = idRangeSize,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static string PascalCaseFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Module";
        var letters = name.Where(c => char.IsLetterOrDigit(c)).ToArray();
        return letters.Length == 0 ? "Module" : new string(letters);
    }

    public static Module WithDependency(this Module module, string id, string name, string publisher, string version)
    {
        module.Dependencies.Add(new ModuleDependency
        {
            OrganizationId = module.OrganizationId,
            Ordering = module.Dependencies.Count,
            DepId = id,
            DepName = name,
            DepPublisher = publisher,
            DepVersion = version,
        });
        return module;
    }
}
