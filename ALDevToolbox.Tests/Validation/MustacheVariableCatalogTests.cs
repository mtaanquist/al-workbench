using System.IO.Compression;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Validation;

/// <summary>
/// Guards Issue #61's single-source-of-truth for mustache placeholders.
/// <see cref="MustacheVariableCatalog"/> is read both by the generator (the
/// names that resolve to non-empty values) and the admin UI hint, so if either
/// drifts the workspace JSON or the always-included files would substitute
/// inconsistently with what admins see.
/// </summary>
public sealed class MustacheVariableCatalogTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Catalogue_has_no_duplicate_names()
    {
        var names = MustacheVariableCatalog.All.Select(v => v.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Admin_facing_subset_excludes_volatile_and_per_file_only_vars()
    {
        var adminNames = MustacheVariableCatalog.ForAdminContent
            .Select(v => v.Name).ToHashSet();

        // Volatile / per-file vars must NOT be in the admin-facing hint —
        // {{guid}} resolves to a new GUID per substitution and {{namespace}}
        // needs a folder context that admin-edited org-wide files don't have.
        adminNames.Should().NotContain("guid");
        adminNames.Should().NotContain("namespace");
        adminNames.Should().NotContain("name");
        adminNames.Should().NotContain("module_name");

        // The placeholders that do make sense in org-wide content stay in.
        adminNames.Should().Contain("workspace_name");
        adminNames.Should().Contain("short_name");
        adminNames.Should().Contain("publisher");
        adminNames.Should().Contain("extension_prefix");
        adminNames.Should().Contain("affix");
        adminNames.Should().Contain("tenant_id");
    }

    [Fact]
    public async Task Every_catalogue_name_resolves_through_generation()
    {
        // Run all catalogue placeholders through the workspace JSON pipeline
        // (which calls SubstituteMustache) and verify the generator recognises
        // each one — i.e. the output no longer contains the literal {{name}}.
        // The catalogue must stay in lock-step with GenerationService's switch.
        var template = TemplateBuilder.Default();
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        // One key per variable so the probe never has to escape JSON quotes —
        // the value is plain text containing only the substituted token.
        var probeLines = MustacheVariableCatalog.All
            .Select(v => $"        \"probe_{v.Name}\": \"<{{{{{v.Name}}}}}>\"");
        var adminJson = "{\n  \"settings\": {\n" + string.Join(",\n", probeLines) + "\n  }\n}";
        await using (var ctx = _db.NewContext())
        {
            var svc = _db.NewOrganizationConfigService(ctx);
            await svc.SaveCodeWorkspaceJsonAsync(adminJson);
        }

        using var zip = await GenerateAsync();
        var entry = zip.GetEntry("AcmeCustomer/AcmeCustomer.code-workspace");
        var content = ReadEntry(entry!);
        foreach (var v in MustacheVariableCatalog.All)
        {
            content.Should().NotContain("{{" + v.Name + "}}",
                $"the generator should substitute '{{{{{v.Name}}}}}' — if this fails, " +
                "either remove the variable from the catalogue or add it to GenerationService.SubstituteMustache.");
        }

        // Gone is not enough: a renderer that blanked every name would pass the
        // loop above. These are always set for a workspace, so each must render
        // to something.
        foreach (var name in new[] { "name", "workspace_name", "customer_name", "short_name", "workspace_folder", "publisher", "extension_prefix", "affix", "guid" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(content, $"\"probe_{name}\"\\s*:\\s*\"<([^>]*)>\"");
            match.Success.Should().BeTrue("the probe for {0} should be in the file", name);
            match.Groups[1].Value.Should().NotBeEmpty("{{{{{0}}}}} always has a value in a workspace", name);
        }
    }

    // ===== helpers =====

    private async Task<ZipArchive> GenerateAsync()
    {
        var ctx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        var service = new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
        var archive = await service.GenerateWorkspaceAsync(PlanBuilder.WorkspacePlan());
        return new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
