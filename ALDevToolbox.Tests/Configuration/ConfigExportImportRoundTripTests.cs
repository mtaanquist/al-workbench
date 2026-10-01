using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// Round-trip test required by Milestone P3.14's "Export → wipe org → import"
/// done-when bullet. Exports the per-org configuration block from one
/// organisation, drops every relevant row, then re-imports and asserts the
/// post-state matches what was exported.
/// </summary>
public sealed class ConfigExportImportRoundTripTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Round_trip_preserves_settings_files_and_logo()
    {
        // Initial state: two files, one logo, customised settings.
        var logoBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xAA, 0xBB };
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveSettingsAsync(new OrganizationSettingsInput(
                "RoundTrip",
                40000, 40999,
                "round-trip brief",
                "round-trip core description",
                DefaultUrl: "https://cronus.com",
                DefaultLogo: "../.assets/images/logo.png"));
            // Customise the workspace JSON template so the round-trip
            // assertion below catches a regression in the export/import path
            // for Issue #61's new column.
            await svc.SaveCodeWorkspaceJsonAsync("""
                {
                  "settings": {
                    "al.ruleSetPath": "../.assets/rulesets/RoundTrip.ruleset.json"
                  }
                }
                """);
            await svc.SaveFilesAsync(new[]
            {
                new OrganizationFileInput(null, ".editorconfig", "indent=tab", false),
                new OrganizationFileInput(null, "docs/onboarding.md", "Welcome {{publisher}}", true),
            });
            await _db.NewOrganizationBrandingService(ctx).UploadLogoAsync("image/png", logoBytes);
        }

        // Export: pull organization-config.toml out of the archive.
        string toml;
        await using (var ctx = _db.NewContext())
        {
            var export = new ExportService(ctx, _db.OrgContext, new FolderTreeHydrator(ctx), NullLogger<ExportService>.Instance);
            var archive = await export.ExportAllAsync();
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("organization-config.toml");
            entry.Should().NotBeNull("the export must include the per-org config block");
            using var reader = new StreamReader(entry!.Open());
            toml = await reader.ReadToEndAsync();
        }

        // Wipe.
        await using (var ctx = _db.NewContext())
        {
            await ctx.OrganizationFiles.ExecuteDeleteAsync();
            await ctx.OrganizationAssets.ExecuteDeleteAsync();
            await ctx.OrganizationSettings.ExecuteDeleteAsync();
        }

        // Re-import.
        await using (var ctx = _db.NewContext())
        {
            var importer = _db.NewOrganizationConfigTomlImporter(ctx);
            await importer.ImportFromTomlAsync(toml);
        }

        // Verify the post-state matches.
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var snapshot = await svc.GetCurrentAsync();
            snapshot.Settings.DefaultPublisher.Should().Be("RoundTrip");
            snapshot.Settings.DefaultIdRangeFrom.Should().Be(40000);
            snapshot.Settings.DefaultUrl.Should().Be("https://cronus.com");
            snapshot.Settings.DefaultLogo.Should().Be("../.assets/images/logo.png");
            snapshot.Settings.DefaultIdRangeTo.Should().Be(40999);
            snapshot.Settings.DefaultBrief.Should().Be("round-trip brief");
            snapshot.Settings.DefaultCoreDescription.Should().Be("round-trip core description");
            snapshot.Settings.CodeWorkspaceJson.Should().Contain("RoundTrip.ruleset.json");
            snapshot.Files.Select(f => (f.Path, f.Content, f.MustacheEnabled))
                .Should().BeEquivalentTo(new[]
                {
                    (".editorconfig", "indent=tab", false),
                    ("docs/onboarding.md", "Welcome {{publisher}}", true),
                });
            snapshot.Logo.Should().NotBeNull();
            snapshot.Logo!.ContentType.Should().Be("image/png");
            snapshot.Logo.Content.Should().Equal(logoBytes);
        }
    }
}
