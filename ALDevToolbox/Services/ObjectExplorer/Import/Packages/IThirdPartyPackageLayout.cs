using System.IO.Compression;

namespace ALDevToolbox.Services.ObjectExplorer.Import.Packages;

/// <summary>
/// One way a vendor lays out the ZIP it ships its apps in. A third-party
/// folder-ZIP upload asks each registered layout in turn whether it
/// <see cref="Matches"/>, and the first that does decides how the upload is
/// imported; <see cref="GenericPackageLayout"/> matches everything and is asked
/// last. A new vendor format is a new implementation registered in
/// <c>ObjectExplorerRegistration</c>, not a branch in the importer (#1802).
/// </summary>
public interface IThirdPartyPackageLayout
{
    /// <summary>A short name for logs, e.g. "Continia".</summary>
    string Name { get; }

    /// <summary>True when <paramref name="archive"/> is laid out the way this vendor ships.</summary>
    bool Matches(ZipArchive archive);

    /// <summary>How to import <paramref name="archive"/>; only called when <see cref="Matches"/> said yes.</summary>
    PackagePlan Plan(ZipArchive archive);
}

/// <summary>What a <see cref="IThirdPartyPackageLayout"/> decided to do with an upload.</summary>
public abstract record PackagePlan
{
    private PackagePlan() { }

    /// <summary>Import the ZIP as uploaded, into one release, exactly as before layouts existed.</summary>
    public sealed record AsUploaded : PackagePlan;

    /// <summary>
    /// Import each group as its own release. The groups are repackaged into one
    /// temporary ZIP each, so the background import pairs every app with the
    /// source zip the layout chose for it.
    /// </summary>
    public sealed record Split(IReadOnlyList<PackageReleaseGroup> Groups) : PackagePlan;
}

/// <summary>
/// The apps that go into one release. <paramref name="BcVersion"/> is the
/// Business Central <c>major.minor</c> they were built for ("29.1"), which picks
/// the Microsoft release the new one sits on.
/// </summary>
public sealed record PackageReleaseGroup(string BcVersion, IReadOnlyList<PackageApp> Apps);

/// <summary>One <c>.app</c> in a group, with the source zip paired to it when there is one.</summary>
public sealed record PackageApp(ZipArchiveEntry App, ZipArchiveEntry? Source, string? ProductGroup);
