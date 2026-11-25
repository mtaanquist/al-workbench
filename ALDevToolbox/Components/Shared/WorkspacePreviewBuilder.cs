using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Builds the whole New Workspace live preview: the org logo, the template's
/// required and ticked extensions, the selected modules, and the workspace-root
/// files and folders. Kept out of the page so <c>PreviewZipParityTests</c> can
/// hold this exact assembly against a generated ZIP - the preview is a second
/// implementation of <c>WorkspaceZipBuilder</c>'s layout, and
/// <c>.design/generation-engine.md</c> promises the two agree.
/// </summary>
public static class WorkspacePreviewBuilder
{
    /// <param name="ranges">
    /// Id-range labels keyed by extension folder name, shown beside each row.
    /// Display only; they do not change which paths appear.
    /// </param>
    public static PreviewNode Build(
        string rootName,
        RuntimeTemplate? template,
        IReadOnlyCollection<string> selectedExtensionPaths,
        IEnumerable<Module>? modules,
        IReadOnlyCollection<string> selectedModuleKeys,
        OrganizationConfig? orgConfig,
        bool includeExamples,
        IReadOnlyDictionary<string, string>? ranges = null)
    {
        var children = new List<PreviewNode>();

        // The org logo lands at the path the admin set on
        // /admin/configuration/defaults' "Default logo path" (stripped of
        // any extension-relative ../ prefix). No logo uploaded means no
        // logo node at all - fresh orgs don't see a phantom .assets folder.
        if (orgConfig?.Logo is not null)
        {
            var logoExt = orgConfig.Logo.ContentType switch
            {
                "image/svg+xml" => "svg",
                _ => "png",
            };
            var logoPath = WorkspaceZipBuilder.NormaliseLogoPath(orgConfig.Settings.DefaultLogo, logoExt);
            PreviewTreeBuilder.GraftFile(children, logoPath);
        }

        // Split the template's included files by scope once: workspace-root
        // grafts into the workspace tree; per-extension is duplicated inside
        // each extension folder by ExtensionPreviewBuilder. This is exactly
        // what WorkspaceZipBuilder does at emit time.
        var workspaceRootPaths = new List<string>();
        var perExtensionPaths = new List<string>();
        if (template is not null && orgConfig is not null)
        {
            var byId = orgConfig.Files.ToDictionary(f => f.Id);
            foreach (var joined in template.IncludedFiles.OrderBy(j => j.Ordering))
            {
                if (!byId.TryGetValue(joined.OrganizationFileId, out var file)) continue;
                if (file.Scope == OrganizationFileScope.EveryExtension)
                {
                    perExtensionPaths.Add(file.Path);
                }
                else
                {
                    workspaceRootPaths.Add(file.Path);
                }
            }
        }

        if (template is not null)
        {
            foreach (var ext in template.WorkspaceExtensions.OrderBy(e => e.Ordering))
            {
                if (!ext.Required && !selectedExtensionPaths.Contains(ext.Path)) continue;
                children.Add(PreviewNode.Extension(
                    ext.Path,
                    ExtensionPreviewBuilder.BuildContents(ext.Folders, includeExamples, perExtensionPaths))
                    with { Meta = ranges?.GetValueOrDefault(ext.Path) });
            }
        }

        if (modules is not null)
        {
            foreach (var module in modules.Where(m => selectedModuleKeys.Contains(m.Key)))
            {
                // Module-cloned extension folder name mirrors GenerationService:
                // the module's ExtensionName (PascalCase) is what lands in the
                // ZIP. Module.Key stays as the admin slug / dep ref target.
                children.Add(PreviewNode.Extension(
                    module.ExtensionName,
                    ExtensionPreviewBuilder.BuildContents(module.ExtensionFolders, includeExamples, perExtensionPaths))
                    with { Meta = ranges?.GetValueOrDefault(module.ExtensionName) });
            }
        }

        // Workspace-root side-cars + workspace-root-scoped org files, then
        // sort. Shared with the New Extension and Template Detail previews so
        // all three stay identical - see PreviewTreeBuilder.BuildWorkspacePreview.
        return PreviewTreeBuilder.BuildWorkspacePreview(
            rootName, children, WorkspaceConfigService.FileName, workspaceRootPaths,
            template?.RootFolders.OrderBy(f => f.Ordering).Select(f => f.Path));
    }
}
