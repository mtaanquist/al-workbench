using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// One allocated AL object-id range, and the extension it belongs to.
/// <see cref="Path"/> is the template extension's path or the module's
/// <c>ExtensionName</c> — i.e. the folder that lands in the ZIP, which is also
/// what the preview tree labels its rows with.
/// </summary>
public readonly record struct AllocatedIdRange(string Path, int From, int To)
{
    /// <summary>
    /// How the range reads on a preview row: <c>ID 50100-50199</c>. The handoff's
    /// generator screen puts exactly this in <c>.tree__meta</c> — see
    /// <c>PageGenerator.dc.html</c>, whose sample rows carry
    /// <c>'ID ' + from + '-' + (from + 99)</c>.
    /// </summary>
    public string Label => $"ID {From}-{To}";
}

/// <summary>
/// Decides which AL object-id range each generated extension gets.
///
/// <para>Extracted from <c>GenerationService</c> for #546, which needed the New
/// Workspace preview to show the ranges it is about to allocate. The preview
/// could have replayed the walk itself — it already iterates the same two lists
/// in the same order — and that is precisely why it must not: the pages carry
/// comments promising the preview matches the ZIP ("this is exactly what
/// WorkspaceZipBuilder does at emit time"), and a second copy of a cursor walk
/// is how such a promise quietly stops being true. One caller became two, so
/// the rule moved out to where both can reach it.</para>
///
/// <para>The walk itself is unchanged, and deliberately so: the numbers it
/// produces are baked into generated <c>app.json</c> files that customers build
/// against, and eighteen assertions across the generation tests read them back
/// out of the ZIP.</para>
///
/// <para>The one rule that has moved since is #730: a template's
/// <c>ModuleIdRangeStart</c> says where the module range starts <em>relative to
/// that template's own Core range</em>, not at a fixed absolute id. When a
/// workspace moves the Core range, everything after it moves by the same
/// amount, preserving whatever gap the template author left between the end of
/// Core and the start of the module range. Leaving Core at the template's own
/// default shifts by zero, so those baked-in numbers are unchanged.</para>
/// </summary>
public static class IdRangeAllocator
{
    /// <summary>
    /// Allocates in emit order: the template's declared extensions first
    /// (required, then the optional ones the user ticked), then one clone per
    /// selected catalogue module.
    /// </summary>
    /// <param name="template">Supplies the auto-allocate start and the default slice size.</param>
    /// <param name="selectedExtensionPaths">Optional template extensions the user ticked.</param>
    /// <param name="modules">Selected catalogue modules, already in display order.</param>
    /// <param name="coreIdRangeFrom">The plan's Core range start — claimed by the first extension that declares no range of its own.</param>
    /// <param name="coreIdRangeTo">The plan's Core range end. Also decides where the
    /// module cursor starts: it is shifted by <c>coreIdRangeTo - template.CoreIdRangeTo</c>
    /// so the layout after Core follows the Core range wherever the workspace put it (#730).</param>
    public static List<AllocatedIdRange> Allocate(
        RuntimeTemplate template,
        IReadOnlyCollection<string> selectedExtensionPaths,
        IReadOnlyList<Module> modules,
        int coreIdRangeFrom,
        int coreIdRangeTo)
    {
        var selectedOptional = new HashSet<string>(selectedExtensionPaths, StringComparer.Ordinal);
        var allocated = new List<AllocatedIdRange>();

        // ID-range cursor: starts at the template's first auto-allocate slot
        // (ModuleIdRangeStart), shifted by however far this workspace moved the
        // end of the Core range away from the template's own (#730), and walks
        // forward. The first extension consumes the Core range from the plan
        // when it has no explicit ids; subsequent unannotated extensions take a
        // slice from the cursor.
        var cursor = ModuleRangeStart(template, coreIdRangeTo);
        var firstAuto = true;

        foreach (var ext in template.WorkspaceExtensions.OrderBy(e => e.Ordering))
        {
            if (!ext.Required && !selectedOptional.Contains(ext.Path)) continue;

            var (from, to, advanced) = ResolveTemplateRange(
                ext, template, coreIdRangeFrom, coreIdRangeTo, firstAuto, cursor);
            cursor = advanced;
            if (ext.IdRangeFrom is null && ext.IdRangeTo is null) firstAuto = false;

            allocated.Add(new AllocatedIdRange(ext.Path, from, to));
        }

        foreach (var module in modules)
        {
            // A typed size wins; a blank one follows the copied repository's
            // app.json, then the template (#1631).
            var size = module.IdRangeSize ?? module.SourceIdRangeSize ?? template.ModuleIdRangeSize;
            var from = cursor;
            var to = from + size - 1;
            cursor = to + 1;
            allocated.Add(new AllocatedIdRange(module.ExtensionName, from, to));
        }

        return allocated;
    }

    /// <summary>
    /// Where the first auto-allocated slot after the Core range begins for a
    /// workspace whose Core range ends at <paramref name="coreIdRangeTo"/>.
    ///
    /// <para>The template author's <see cref="RuntimeTemplate.ModuleIdRangeStart"/>
    /// is read relative to that template's own <see cref="RuntimeTemplate.CoreIdRangeTo"/>,
    /// so the gap between the two (usually zero — modules start right after Core)
    /// survives a workspace moving, widening or narrowing the Core range (#730).
    /// The shift keys on the range's <em>end</em>, so widening Core also pushes
    /// what follows out of its way.</para>
    ///
    /// <para>Can come out below 1 for a large downward move; the plan validation
    /// in <c>GenerationService</c> refuses such a plan against this same method
    /// rather than emitting negative object ids.</para>
    /// </summary>
    public static int ModuleRangeStart(RuntimeTemplate template, int coreIdRangeTo) =>
        template.ModuleIdRangeStart + (coreIdRangeTo - template.CoreIdRangeTo);

    private static (int From, int To, int Cursor) ResolveTemplateRange(
        WorkspaceExtension ext,
        RuntimeTemplate template,
        int coreIdRangeFrom,
        int coreIdRangeTo,
        bool firstAuto,
        int cursor)
    {
        // Explicit on the extension: use verbatim, don't move the cursor.
        if (ext.IdRangeFrom is int explicitFrom && ext.IdRangeTo is int explicitTo)
        {
            return (explicitFrom, explicitTo, cursor);
        }
        // First unannotated template extension: take the plan's Core range.
        if (firstAuto)
        {
            return (coreIdRangeFrom, coreIdRangeTo, cursor);
        }
        // Subsequent unannotated extensions: slice the template's module range.
        var size = template.ModuleIdRangeSize;
        return (cursor, cursor + size - 1, cursor + size);
    }
}
