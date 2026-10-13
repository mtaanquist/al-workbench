using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The wipe-and-replace TOML restore on
/// <see cref="OrganizationConfigTomlImporter"/>: settings, always-included
/// files and the logo are all replaced by what the TOML carries. The full
/// export-then-import round trip lives in <see cref="ConfigExportImportRoundTripTests"/>.
/// </summary>
public sealed class OrganizationConfigTomlImportTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ImportFromToml_replaces_settings_files_and_logo()
    {
        // Seed an initial state we expect to be wiped, then import a TOML
        // carrying a different config and verify the post-state matches.
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveSettingsAsync(new OrganizationSettingsInput("OldPub", 90000, 90999, "old", "old"));
            await svc.SaveFilesAsync(new[]
            {
                new OrganizationFileInput(null, "old.txt", "stale", false),
            });
        }

        var pngBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var toml = $$"""
            [settings]
            default_publisher = "NewPub"
            default_id_range_from = 50000
            default_id_range_to = 50999
            default_brief = "imported"
            default_core_description = "imported desc"

            [logo]
            content_type = "image/png"
            content_base64 = "{{pngBase64}}"

            [[file]]
            path = "fresh.txt"
            content = "hello"
            mustache_enabled = false
            """;

        await using (var ctx = _db.NewContext())
        {
            var importer = _db.NewOrganizationConfigTomlImporter(ctx);
            await importer.ImportFromTomlAsync(toml);
        }

        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            var snapshot = await svc.GetCurrentAsync();
            snapshot.Settings.DefaultPublisher.Should().Be("NewPub");
            snapshot.Settings.DefaultIdRangeFrom.Should().Be(50000);
            snapshot.Files.Should().ContainSingle(f => f.Path == "fresh.txt" && f.Content == "hello");
            snapshot.Files.Should().NotContain(f => f.Path == "old.txt");
            snapshot.Logo.Should().NotBeNull();
            snapshot.Logo!.ContentType.Should().Be("image/png");
            snapshot.Logo.Content.Should().Equal(0x89, 0x50, 0x4E, 0x47);
        }
    }

    [Fact]
    public async Task ImportFromToml_keeps_url_and_logo_path_when_the_toml_predates_them()
    {
        await using (var ctx = _db.NewContext())
        {
            await _db.NewOrganizationConfigService(ctx).SaveSettingsAsync(new OrganizationSettingsInput(
                "CRONUS", 50000, 50999, string.Empty, string.Empty,
                DefaultUrl: "https://cronus.com", DefaultLogo: "../.assets/logo.png"));
        }

        await using (var ctx = _db.NewContext())
        {
            await _db.NewOrganizationConfigTomlImporter(ctx).ImportFromTomlAsync("""
                [settings]
                default_publisher = "CRONUS"
                default_id_range_from = 50000
                default_id_range_to = 50999
                """);
        }

        await using (var ctx = _db.NewContext())
        {
            var settings = (await _db.NewOrganizationConfigService(ctx).GetCurrentAsync()).Settings;
            settings.DefaultUrl.Should().Be("https://cronus.com");
            settings.DefaultLogo.Should().Be("../.assets/logo.png");
        }
    }

    [Fact]
    public async Task ImportFromToml_clears_url_and_logo_path_when_the_toml_carries_them_empty()
    {
        await using (var ctx = _db.NewContext())
        {
            await _db.NewOrganizationConfigService(ctx).SaveSettingsAsync(new OrganizationSettingsInput(
                "CRONUS", 50000, 50999, string.Empty, string.Empty,
                DefaultUrl: "https://cronus.com", DefaultLogo: "../.assets/logo.png"));
        }

        await using (var ctx = _db.NewContext())
        {
            await _db.NewOrganizationConfigTomlImporter(ctx).ImportFromTomlAsync("""
                [settings]
                default_publisher = "CRONUS"
                default_id_range_from = 50000
                default_id_range_to = 50999
                default_url = ""
                default_logo = ""
                """);
        }

        await using (var ctx = _db.NewContext())
        {
            var settings = (await _db.NewOrganizationConfigService(ctx).GetCurrentAsync()).Settings;
            settings.DefaultUrl.Should().BeNull();
            settings.DefaultLogo.Should().BeNull();
        }
    }

    [Fact]
    public async Task ImportFromToml_rejects_a_logo_path_that_leaves_the_workspace()
    {
        await using var ctx = _db.NewContext();
        var act = () => _db.NewOrganizationConfigTomlImporter(ctx).ImportFromTomlAsync("""
            [settings]
            default_publisher = "CRONUS"
            default_id_range_from = 50000
            default_id_range_to = 50999
            default_logo = "../../logo.png"
            """);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(OrganizationSettingsInput.DefaultLogo));
    }

    [Fact]
    public async Task ImportFromToml_keeps_template_opt_ins_for_files_it_still_carries()
    {
        // #1216: re-importing a file the TOML still carries must update the
        // row in place. Re-creating it under a new id cascades away every
        // template's opt-in link to it.
        int keptFileId;
        int templateId;
        await using (var ctx = _db.NewContext())
        {
            await _db.NewOrganizationConfigService(ctx).SaveFilesAsync(new[]
            {
                new OrganizationFileInput(null, "kept.txt", "before", false),
                new OrganizationFileInput(null, "dropped.txt", "gone", false),
            });
        }
        await using (var ctx = _db.NewContext())
        {
            var files = ctx.OrganizationFiles.Where(f => f.Path == "kept.txt" || f.Path == "dropped.txt").ToList();
            keptFileId = files.Single(f => f.Path == "kept.txt").Id;
            var template = TemplateBuilder.Default("import-keeps-links", organizationId: TestDb.DefaultOrgId);
            for (var i = 0; i < files.Count; i++)
            {
                template.IncludedFiles.Add(new RuntimeTemplateIncludedFile
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    OrganizationFileId = files[i].Id,
                    Ordering = i,
                });
            }
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            templateId = template.Id;
        }

        await using (var ctx = _db.NewContext())
        {
            await _db.NewOrganizationConfigTomlImporter(ctx).ImportFromTomlAsync("""
                [settings]
                default_publisher = "CRONUS"
                default_id_range_from = 50000
                default_id_range_to = 50999

                [[file]]
                path = "kept.txt"
                content = "after"
                mustache_enabled = true

                [[file]]
                path = "added.txt"
                content = "new"
                mustache_enabled = false
                """);
        }

        await using (var ctx = _db.NewContext())
        {
            var kept = ctx.OrganizationFiles.Single(f => f.Path == "kept.txt");
            kept.Id.Should().Be(keptFileId);
            kept.Content.Should().Be("after");
            kept.MustacheEnabled.Should().BeTrue();
            ctx.OrganizationFiles.Should().Contain(f => f.Path == "added.txt");
            ctx.OrganizationFiles.Should().NotContain(f => f.Path == "dropped.txt");

            var links = ctx.Set<RuntimeTemplateIncludedFile>()
                .Where(j => j.RuntimeTemplateId == templateId)
                .Select(j => j.OrganizationFileId)
                .ToList();
            links.Should().Equal(keptFileId);
        }
    }
}
