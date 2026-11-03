using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Migration;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Migration;

/// <summary>A folder under the system temp folder, removed again on dispose.</summary>
internal sealed class MigrationTempDir : IDisposable
{
    public MigrationTempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mig-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A sub-folder, created.</summary>
    public string Sub(string relative)
    {
        var dir = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Writes the files a migration test reads.</summary>
internal static class MigrationTestFiles
{
    /// <summary>Writes an app.json into <paramref name="dir"/> (creating it), with optional dependencies as (id, name, publisher).</summary>
    public static void WriteApp(string dir, string id, string name, string publisher = "CRONUS",
        string version = "1.0.0.0", params (string Id, string Name, string Publisher)[] dependencies)
    {
        Directory.CreateDirectory(dir);
        var deps = string.Join(",", dependencies.Select(d =>
            $$"""{ "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "{{d.Publisher}}", "version": "1.0.0.0" }"""));
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              // comments and trailing commas are allowed in app.json
              "id": "{{id}}",
              "name": "{{name}}",
              "publisher": "{{publisher}}",
              "version": "{{version}}",
              "dependencies": [ {{deps}} ],
            }
            """);
    }

    public static void WriteFile(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}

/// <summary>The template and services a migration test builds a workspace with.</summary>
internal static class MigrationTestServices
{
    public const string TemplateKey = "runtime-test";

    /// <summary>Seeds the test template, opted into every organisation file the fixture has (.gitignore, README, ruleset, app.json).</summary>
    public static async Task SeedTemplateAsync(TestDb db)
    {
        await using var ctx = db.NewContext();
        var template = TemplateBuilder.Default(TemplateKey);
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
        var fileIds = ctx.OrganizationFiles
            .Where(f => f.OrganizationId == template.OrganizationId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.Id)
            .ToList();
        for (var i = 0; i < fileIds.Count; i++)
        {
            ctx.Set<RuntimeTemplateIncludedFile>().Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = template.OrganizationId,
                RuntimeTemplateId = template.Id,
                OrganizationFileId = fileIds[i],
                Ordering = i,
            });
        }
        await ctx.SaveChangesAsync();
    }

    public static MigrationWorkspaceBuilder NewBuilder(TestDb db, AppDbContext ctx) => new(
        db.NewGenerationService(ctx),
        new WorkspaceConfigService(ctx),
        new ProcessRunner(),
        NullLogger<MigrationWorkspaceBuilder>.Instance);

    /// <summary>A target on the test template for the customer "CRONUS A/S".</summary>
    public static MigrationTarget Target(bool addTemplateExtensions = false) => new(
        TemplateKey,
        "Test Runtime",
        "CRONUS A/S",
        addTemplateExtensions,
        PlanBuilder.WorkspacePlan(workspaceName: "CRONUS A/S", includeExamples: false) with
        {
            TenantId = Guid.NewGuid().ToString(),
        });
}
