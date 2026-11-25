using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Tests.Builders;

/// <summary>
/// Constructs <see cref="RuntimeTemplate"/> rows pre-populated with sensible
/// defaults so tests only spell out the fields they care about.
/// </summary>
/// <remarks>
/// Updated for the unified-extensions model: the legacy
/// <see cref="WithCoreFolder"/> / <see cref="WithExtensionFolder(Module, string, ValueTuple{string,string}[])"/> helpers seed
/// the equivalent <see cref="WorkspaceExtension"/> and
/// <see cref="ModuleExtensionFolder"/> rows so existing tests keep their shape
/// without having to know about the new tables. Tests that need finer control
/// of the recursive folder tree should construct the entities directly.
/// </remarks>
public static class TemplateBuilder
{
    public const int DefaultOrganizationId = TestDb.DefaultOrgId;
    public const string CoreExtensionPath = "Core";

    public static RuntimeTemplate Default(string key = "runtime-test", string runtime = "15", int organizationId = DefaultOrganizationId) => new()
    {
        OrganizationId = organizationId,
        Key = key,
        Runtime = runtime,
        Name = "Test Runtime",
        Description = "Synthetic template used in tests.",
        Defaults = new TemplateDefaults
        {
            Publisher = "Acme",
            Target = "Cloud",
            Application = "24.0.0.0",
            Platform = "1.0.0.0",
            Features = new List<string> { "TranslationFile" },
            SupportedLocales = new List<string> { "en-US" },
            Affix = "ACME",
            AffixType = AffixType.Prefix,
        },
        AppSourceCop = new AppSourceCopSettings
        {
            MandatoryPrefix = "ACME",
            SupportedCountries = new List<string> { "US" },
        },
        CoreIdRangeFrom = 90000,
        CoreIdRangeTo = 90999,
        ModuleIdRangeStart = 91000,
        ModuleIdRangeSize = 200,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        WorkspaceExtensions = new List<WorkspaceExtension>
        {
            new WorkspaceExtension
            {
                OrganizationId = organizationId,
                Path = CoreExtensionPath,
                NameTemplate = "{{extension_prefix}} Core",
                Required = true,
                Ordering = 0,
            },
        },
    };

    /// <summary>
    /// Adds a top-level folder to the template's required Core extension.
    /// Slash-separated paths are walked into the recursive folder tree; files
    /// attach to the leaf folder. Mirrors the legacy single-row helper while
    /// targeting the new schema.
    /// </summary>
    public static RuntimeTemplate WithCoreFolder(this RuntimeTemplate template, string path, params (string Path, string Content)[] files)
    {
        var ext = template.WorkspaceExtensions.FirstOrDefault(e => e.Path == CoreExtensionPath)
            ?? throw new InvalidOperationException("Template has no Core extension; call Default() first.");

        var leaf = WalkOrCreate(ext, path);
        for (var i = 0; i < files.Length; i++)
        {
            leaf.Files.Add(new WorkspaceExtensionFile
            {
                OrganizationId = template.OrganizationId,
                Ordering = i,
                Path = files[i].Path,
                Content = files[i].Content,
            });
        }
        return template;
    }

    /// <summary>
    /// Adds a top-level folder to a module's extension layout. Tests that
    /// previously used <c>WithModuleFolder</c> on the template now point at a
    /// specific module since the unified-extensions model decoupled the two.
    /// </summary>
    public static Module WithExtensionFolder(this Module module, string path, params (string Path, string Content)[] files)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        ModuleExtensionFolder? parent = null;
        foreach (var seg in segs)
        {
            var sibling = (parent?.Folders ?? module.ExtensionFolders)
                .FirstOrDefault(f => string.Equals(f.Path, seg, StringComparison.Ordinal));
            if (sibling is null)
            {
                sibling = new ModuleExtensionFolder
                {
                    OrganizationId = module.OrganizationId,
                    Path = seg,
                    ParentFolder = parent,
                    Ordering = (parent?.Folders ?? module.ExtensionFolders).Count,
                };
                (parent?.Folders ?? module.ExtensionFolders).Add(sibling);
            }
            parent = sibling;
        }
        var leaf = parent!;
        for (var i = 0; i < files.Length; i++)
        {
            leaf.Files.Add(new ModuleExtensionFile
            {
                OrganizationId = module.OrganizationId,
                Ordering = i,
                Path = files[i].Path,
                Content = files[i].Content,
            });
        }
        return module;
    }

    private static WorkspaceExtensionFolder WalkOrCreate(WorkspaceExtension extension, string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        WorkspaceExtensionFolder? parent = null;
        foreach (var seg in segs)
        {
            var siblings = parent?.Folders ?? extension.Folders;
            var sibling = siblings.FirstOrDefault(f => string.Equals(f.Path, seg, StringComparison.Ordinal));
            if (sibling is null)
            {
                sibling = new WorkspaceExtensionFolder
                {
                    OrganizationId = extension.OrganizationId,
                    Path = seg,
                    ParentFolder = parent,
                    Ordering = siblings.Count,
                };
                siblings.Add(sibling);
            }
            parent = sibling;
        }
        return parent!;
    }
}
