using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The order several uploaded apps install in: dependencies first by the manifests that
/// could be read, and a package whose manifest is hidden (an encrypted vendor app) keeps
/// the place the person chose it in.
/// </summary>
public sealed class UploadAppDialogOrderTests
{
    private static readonly Guid Core = Guid.NewGuid();
    private static readonly Guid Connector = Guid.NewGuid();
    private static readonly Guid Reports = Guid.NewGuid();

    private static UploadAppDialog.UploadFile File(string name, Guid? id, params Guid[] dependsOn) => new(
        new byte[] { 1 }, name,
        id is null ? null : new AppManifest(id.Value, name, "Vendor", "1.0.0.0", null, null, false, false, false,
            dependsOn.Select(d => new AppDependency(d, "dep", "Vendor", "1.0.0.0")).ToList()));

    [Fact]
    public void Dependencies_come_first_whatever_order_the_files_were_chosen_in()
    {
        var ordered = UploadAppDialog.InstallOrder(new[]
        {
            File("Reports.app", Reports, Connector),
            File("Connector.app", Connector, Core),
            File("Core.app", Core),
        });

        ordered.Select(f => f.FileName).Should().Equal("Core.app", "Connector.app", "Reports.app");
    }

    [Fact]
    public void An_app_whose_manifest_cannot_be_read_keeps_its_place()
    {
        var ordered = UploadAppDialog.InstallOrder(new[]
        {
            File("Encrypted.app", null),
            File("Connector.app", Connector, Core),
            File("Core.app", Core),
        });

        ordered.Select(f => f.FileName).Should().Equal("Encrypted.app", "Core.app", "Connector.app");
    }

    [Fact]
    public void A_dependency_outside_the_selection_is_no_constraint()
    {
        var ordered = UploadAppDialog.InstallOrder(new[]
        {
            File("Connector.app", Connector, Guid.NewGuid()),
            File("Core.app", Core),
        });

        ordered.Select(f => f.FileName).Should().Equal("Connector.app", "Core.app");
    }
}
