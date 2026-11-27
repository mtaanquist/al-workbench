using System.IO.Compression;

namespace ALDevToolbox.Services.ObjectExplorer.Import.Packages;

/// <summary>
/// The fallback layout: any ZIP of <c>.app</c> files (with <c>.Source.zip</c>
/// siblings, or a zipped AL workspace) is imported as uploaded, into one release.
/// Always matches, so it is asked last.
/// </summary>
public sealed class GenericPackageLayout : IThirdPartyPackageLayout
{
    public string Name => "Generic";

    public bool Matches(ZipArchive archive) => true;

    public PackagePlan Plan(ZipArchive archive) => new PackagePlan.AsUploaded();
}
