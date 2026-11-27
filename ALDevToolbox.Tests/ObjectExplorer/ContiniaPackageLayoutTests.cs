using System.IO.Compression;
using ALDevToolbox.Services.ObjectExplorer.Import.Packages;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Continia package layout (#1802), over the folder tree Continia ships
/// (Apps/BC x.y/&lt;product&gt;/*.app, Apps/Source/*-Source.zip, Apps/Scripts).
/// </summary>
public sealed class ContiniaPackageLayoutTests
{
    private const string Root = "CBA 29.1.1.257932 - W1/Apps/";

    /// <summary>The package from the issue, trimmed to one app per product folder.</summary>
    internal static readonly string[] PackageEntries =
    [
        Root + "BC 29.0 (BC 2026 Wave 2)/Install.ps1",
        Root + "BC 29.0 (BC 2026 Wave 2)/Banking/Continia Banking 29.1.1.257932 - 29.0.app",
        Root + "BC 29.0 (BC 2026 Wave 2)/Banking/Continia Banking - CSV Import 29.1.1.257932 - 29.0.app",
        Root + "BC 29.0 (BC 2026 Wave 2)/Base/Continia Business Foundation 29.1.0.254675 - 29.0.app",
        Root + "BC 29.0 (BC 2026 Wave 2)/Core/Core 29.1.0.244450 - 29.0 (BC 2026 Wave 2).app",
        Root + "BC 29.0 (BC 2026 Wave 2)/Finance/CFI 29.0.0.214343 - 29.0 (BC 2026 Wave 2).app",
        Root + "BC 29.1 (BC 2026 Wave 2 CU1)/Install.ps1",
        Root + "BC 29.1 (BC 2026 Wave 2 CU1)/Banking/Continia Banking 29.1.1.257932 - 29.1.app",
        Root + "BC 29.1 (BC 2026 Wave 2 CU1)/Banking/Continia Banking - CSV Import 29.1.1.257932 - 29.1.app",
        Root + "BC 29.1 (BC 2026 Wave 2 CU1)/Base/Continia Business Foundation 29.1.0.254675 - 29.1.app",
        Root + "BC 29.1 (BC 2026 Wave 2 CU1)/Core/Core 29.1.0.244450 - 29.1 (BC 2026 Wave 2 CU1).app",
        Root + "Scripts/ContiniaAppHelper.psm1",
        Root + "Scripts/productDependencies.json",
        Root + "Source/CFI 29.0.0.214343 - 29 (BC 2026 Wave 2) Source.zip",
        Root + "Source/Continia Banking 29.1.1.257932 - 29.0-Source.zip",
        Root + "Source/Continia Banking - CSV Import 29.1.1.257932 - 29.0-Source.zip",
    ];

    /// <summary>A ZIP with one entry per path, each holding its own path as content.</summary>
    internal static byte[] Zip(IEnumerable<string> paths)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in paths)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(path);
            }
        }
        return buffer.ToArray();
    }

    private static ZipArchive Open(IEnumerable<string> paths) =>
        new(new MemoryStream(Zip(paths)), ZipArchiveMode.Read);

    [Fact]
    public void The_continia_package_matches()
    {
        using var archive = Open(PackageEntries);
        new ContiniaPackageLayout().Matches(archive).Should().BeTrue();
    }

    [Fact]
    public void A_plain_zip_of_apps_does_not_match_and_falls_to_the_generic_layout()
    {
        using var archive = Open(["applications/Contoso_Thing_1.0.0.0.app", "applications/Thing.Source.zip"]);
        new ContiniaPackageLayout().Matches(archive).Should().BeFalse();

        var layouts = new ThirdPartyPackageLayouts([new ContiniaPackageLayout()]);
        layouts.Resolve(archive).Should().BeOfType<GenericPackageLayout>();
        layouts.Resolve(archive).Plan(archive).Should().BeOfType<PackagePlan.AsUploaded>();
    }

    [Fact]
    public void Each_business_central_folder_becomes_its_own_group_in_version_order()
    {
        using var archive = Open(PackageEntries);
        var plan = new ContiniaPackageLayout().Plan(archive).Should().BeOfType<PackagePlan.Split>().Subject;

        plan.Groups.Select(g => g.BcVersion).Should().Equal("29.0", "29.1");
        plan.Groups[0].Apps.Should().HaveCount(5);
        plan.Groups[1].Apps.Should().HaveCount(4, "Finance only ships for 29.0");
        plan.Groups.SelectMany(g => g.Apps).Should().OnlyContain(a => a.App.Name.EndsWith(".app"),
            "installers and scripts are not imported");
    }

    [Fact]
    public void Source_zips_pair_by_name_and_version_across_business_central_folders()
    {
        using var archive = Open(PackageEntries);
        var plan = (PackagePlan.Split)new ContiniaPackageLayout().Plan(archive);

        string? SourceOf(int group, string appPrefix) =>
            plan.Groups[group].Apps.Single(a => a.App.Name.StartsWith(appPrefix + " 2")).Source?.Name;

        SourceOf(0, "Continia Banking").Should().Be("Continia Banking 29.1.1.257932 - 29.0-Source.zip");
        SourceOf(0, "Continia Banking - CSV Import").Should().Be("Continia Banking - CSV Import 29.1.1.257932 - 29.0-Source.zip");
        SourceOf(0, "CFI").Should().Be("CFI 29.0.0.214343 - 29 (BC 2026 Wave 2) Source.zip",
            "\"29\" and \"29.0\" are the same version, and a space before Source counts too");
        SourceOf(1, "Continia Banking").Should().Be("Continia Banking 29.1.1.257932 - 29.0-Source.zip",
            "the 29.1 build of the same app version shares the 29.0 source");
        SourceOf(0, "Core").Should().BeNull("no source ships for Core");
    }

    [Fact]
    public void A_source_built_for_the_same_business_central_version_wins()
    {
        using var archive = Open(
        [
            Root + "BC 29.1 (CU1)/Banking/Continia Banking 29.1.1.257932 - 29.1.app",
            Root + "Source/Continia Banking 29.1.1.257932 - 29.0-Source.zip",
            Root + "Source/Continia Banking 29.1.1.257932 - 29.1-Source.zip",
        ]);
        var plan = (PackagePlan.Split)new ContiniaPackageLayout().Plan(archive);

        plan.Groups.Single().Apps.Single().Source!.Name.Should().Be("Continia Banking 29.1.1.257932 - 29.1-Source.zip");
    }

    [Fact]
    public void The_product_folder_is_kept_with_each_app()
    {
        using var archive = Open(PackageEntries);
        var plan = (PackagePlan.Split)new ContiniaPackageLayout().Plan(archive);

        plan.Groups[0].Apps.Select(a => a.ProductGroup).Distinct()
            .Should().BeEquivalentTo(["Banking", "Base", "Core", "Finance"]);
    }

    [Fact]
    public void A_folder_named_with_the_major_version_alone_counts_as_its_dot_zero()
    {
        using var archive = Open(
        [
            Root + "BC 29 (BC 2026 Wave 2)/Core/Core 29.1.0.244450 - 29.0.app",
            Root + "BC29.1/Core/Core 29.1.0.244450 - 29.1.app",
            Root + "Source/Core/Core 29.1.0.244450 - 29.0-Source.zip",
        ]);
        var plan = (PackagePlan.Split)new ContiniaPackageLayout().Plan(archive);

        plan.Groups.Select(g => g.BcVersion).Should().Equal("29.0", "29.1");
        plan.Groups.Should().OnlyContain(g => g.Apps.Single().Source != null,
            "a source zip in a folder below Source pairs too");
    }

    [Fact]
    public void An_absurd_version_in_a_folder_name_is_not_a_business_central_folder()
    {
        using var archive = Open([Root + "BC 99999999999.0/Core/Core 29.1.0.244450 - 29.0.app"]);
        new ContiniaPackageLayout().Matches(archive).Should().BeFalse();
    }

    [Fact]
    public void Backslash_separated_entries_are_read_the_same()
    {
        using var archive = Open(PackageEntries.Select(p => p.Replace('/', '\\')));
        var plan = new ContiniaPackageLayout().Plan(archive).Should().BeOfType<PackagePlan.Split>().Subject;
        plan.Groups.Should().HaveCount(2);
    }
    /// <summary>
    /// The Document Capture and Expense Management package: the folder is "App",
    /// not "Apps", there is no wrapper folder, and installers, docs and sample
    /// files sit beside it.
    /// </summary>
    internal static readonly string[] DocumentCapturePackage =
    [
        "setup.exe",
        "App/BC 29.0 (BC 2026 Wave 2)/Install.ps1",
        "App/BC 29.0 (BC 2026 Wave 2)/Base/Continia AI - Core 29.1.0.244431 - 29.0.app",
        "App/BC 29.0 (BC 2026 Wave 2)/Core/Core 29.1.0.244450 - 29.0 (BC 2026 Wave 2).app",
        "App/BC 29.0 (BC 2026 Wave 2)/Document Capture/DC 29.1.1.254656 - 29.0 (BC 2026 Wave 2).app",
        "App/BC 29.0 (BC 2026 Wave 2)/Document Capture/DC on-premises 29.1.1.254656 - 29.0 (BC 2026 Wave 2).app",
        "App/BC 29.0 (BC 2026 Wave 2)/Expense Management/EM 29.1.0.250661 - 29.0 (BC 2026 Wave 2).app",
        "App/BC 29.1 (BC 2026 Wave 2 CU1)/Base/Continia AI - Core 29.1.0.244431 - 29.1.app",
        "App/BC 29.1 (BC 2026 Wave 2 CU1)/Core/Core 29.1.0.244450 - 29.1 (BC 2026 Wave 2 CU1).app",
        "App/BC 29.1 (BC 2026 Wave 2 CU1)/Document Capture/DC 29.1.1.254656 - 29.1 (BC 2026 Wave 2 CU1).app",
        "App/BC 29.1 (BC 2026 Wave 2 CU1)/Document Capture/DC on-premises 29.1.1.254656 - 29.1 (BC 2026 Wave 2 CU1).app",
        "App/BC 29.1 (BC 2026 Wave 2 CU1)/Expense Management/EM 29.1.0.250661 - 29.1 (BC 2026 Wave 2 CU1).app",
        "App/Scripts/excludeApps.json",
        "App/Scripts/Misc/Install-UI.ps1",
        "App/Source/DC 29.1.1.254656 - 29 (BC 2026 Wave 2) Source.zip",
        "App/Source/DC on-premises 29.1.1.254656 - 29 (BC 2026 Wave 2) Source.zip",
        "App/Source/EM 29.1.0.250661 - 29 (BC 2026 Wave 2) Source.zip",
        "Documentation/Document Capture/0.0 - Documentation and Resources.htm",
        "Software/File Service/Continia File Service.msi",
        "Software/Service Tier/Files/GdPicture.NET.14.dll",
        "Support Files/Document Capture/Test PDF/W1 - BC online - Purchase Invoice 1 - First Up Consultant - 44444444.pdf",
    ];

    [Fact]
    public void A_package_with_an_App_folder_and_installers_beside_it_splits_the_same_way()
    {
        using var archive = Open(DocumentCapturePackage);
        new ContiniaPackageLayout().Matches(archive).Should().BeTrue();

        var plan = new ContiniaPackageLayout().Plan(archive).Should().BeOfType<PackagePlan.Split>().Subject;
        plan.Groups.Select(g => g.BcVersion).Should().Equal("29.0", "29.1");
        plan.Groups.Should().AllSatisfy(g => g.Apps.Should().HaveCount(5));

        var byName = plan.Groups[1].Apps.ToDictionary(a => Path.GetFileName(a.App.FullName));
        byName["DC 29.1.1.254656 - 29.1 (BC 2026 Wave 2 CU1).app"].Source!.Name
            .Should().Be("DC 29.1.1.254656 - 29 (BC 2026 Wave 2) Source.zip");
        byName["DC on-premises 29.1.1.254656 - 29.1 (BC 2026 Wave 2 CU1).app"].Source!.Name
            .Should().Be("DC on-premises 29.1.1.254656 - 29 (BC 2026 Wave 2) Source.zip");
        byName["Core 29.1.0.244450 - 29.1 (BC 2026 Wave 2 CU1).app"].Source.Should().BeNull("this package ships no source for Core");
        byName["EM 29.1.0.250661 - 29.1 (BC 2026 Wave 2 CU1).app"].ProductGroup.Should().Be("Expense Management");
    }
}
