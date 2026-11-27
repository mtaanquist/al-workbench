using System.IO.Compression;

namespace ALDevToolbox.Services.ObjectExplorer.Import.Packages;

/// <summary>
/// Picks the layout for a third-party upload: the first registered vendor layout
/// that matches, else <see cref="GenericPackageLayout"/>. Singleton; the layouts
/// are stateless.
/// </summary>
public sealed class ThirdPartyPackageLayouts
{
    private readonly IReadOnlyList<IThirdPartyPackageLayout> _layouts;

    public ThirdPartyPackageLayouts(IEnumerable<IThirdPartyPackageLayout> layouts)
    {
        // The generic layout matches everything, so it goes last whatever the
        // registration order, and is added if nobody registered it.
        var vendor = layouts.Where(l => l is not GenericPackageLayout).ToList();
        vendor.Add(new GenericPackageLayout());
        _layouts = vendor;
    }

    /// <summary>The layout that will import <paramref name="archive"/>.</summary>
    public IThirdPartyPackageLayout Resolve(ZipArchive archive) => _layouts.First(l => l.Matches(archive));
}
