using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Tolerance and validation coverage for
/// <see cref="WorkspaceConfigService.ParseAsync"/>. Mirrors
/// <see cref="TemplateTomlMapperToleranceTests"/>: hand-written TOML that
/// exercises each failure mode the import form has to render inline next to
/// the file-input.
/// </summary>
public sealed class WorkspaceConfigServiceToleranceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Empty_input_fails_with_a_field_keyed_error_not_a_tomlyn_stack_trace()
    {
        using var ctx = _db.NewContext();
        var svc = new WorkspaceConfigService(ctx);

        Func<Task> act = () => svc.ParseAsync("   ");
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("File");
    }

    [Fact]
    public async Task Unknown_top_level_keys_are_tolerated()
    {
        await SeedTemplateAsync("runtime-15");
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion}}
            kind = "workspace"
            future_field = "ignored"

            [workspace]
            template = "runtime-15"
            name = "Acme"
            application_version = "24.0.0.0"
            runtime_version = "15"
            core_id_range_from = 1
            core_id_range_to = 99
            include_examples = true
            extension_prefix = "ACME"
            """;
        using var ctx = _db.NewContext();

        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Kind.Should().Be(WorkspaceConfigService.WorkspaceKind);
        parsed.Workspace!.WorkspaceName.Should().Be("Acme");
    }

    [Fact]
    public async Task Missing_extensions_section_yields_an_empty_list_not_null()
    {
        await SeedTemplateAsync("runtime-15");
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion}}
            kind = "workspace"

            [workspace]
            template = "runtime-15"
            name = "X"
            application_version = "24.0.0.0"
            runtime_version = "15"
            core_id_range_from = 1
            core_id_range_to = 99
            include_examples = true
            extension_prefix = "X"
            """;
        using var ctx = _db.NewContext();

        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Extensions.Should().NotBeNull();
        parsed.Extensions.Should().BeEmpty();
        parsed.Workspace!.SelectedExtensionPaths.Should().BeEmpty();
        parsed.Workspace.SelectedModuleKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Newer_schema_version_fails_with_a_schema_version_keyed_error()
    {
        using var ctx = _db.NewContext();
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion + 1}}
            kind = "workspace"
            [workspace]
            template = "runtime-15"
            name = "X"
            """;

        Func<Task> act = () => new WorkspaceConfigService(ctx).ParseAsync(toml);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("SchemaVersion");
    }

    [Fact]
    public async Task Unknown_kind_fails_with_a_kind_keyed_error()
    {
        using var ctx = _db.NewContext();
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion}}
            kind = "neither"
            """;

        Func<Task> act = () => new WorkspaceConfigService(ctx).ParseAsync(toml);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Kind");
    }

    [Fact]
    public async Task Workspace_pointing_at_a_deleted_template_fails_with_a_template_keyed_error()
    {
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-soft");
            template.DeletedAt = DateTime.UtcNow;
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
        }
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion}}
            kind = "workspace"

            [workspace]
            template = "runtime-soft"
            name = "X"
            application_version = "24.0.0.0"
            runtime_version = "15"
            core_id_range_from = 1
            core_id_range_to = 99
            include_examples = true
            extension_prefix = "X"
            """;
        using var ctx = _db.NewContext();

        Func<Task> act = () => new WorkspaceConfigService(ctx).ParseAsync(toml);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("Template");
    }

    /// <summary>
    /// #1328: the saved module list is history. A key that names no module, or
    /// a module deleted since the workspace was generated, is kept as saved
    /// rather than refused, so an extension can still be added beside the others.
    /// </summary>
    [Fact]
    public async Task Workspace_listing_unknown_or_deleted_module_keys_is_read_back_with_them()
    {
        await SeedTemplateAsync("runtime-15");
        await using (var seed = _db.NewContext())
        {
            var retired = ModuleBuilder.Default("retired");
            retired.DeletedAt = DateTime.UtcNow;
            seed.Modules.AddRange(ModuleBuilder.Default("live"), retired);
            await seed.SaveChangesAsync();
        }
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion}}
            kind = "workspace"

            [workspace]
            template = "runtime-15"
            name = "X"
            application_version = "24.0.0.0"
            runtime_version = "15"
            core_id_range_from = 1
            core_id_range_to = 99
            include_examples = true
            extension_prefix = "X"
            modules = ["live", "retired", "ghost"]
            """;
        using var ctx = _db.NewContext();

        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Workspace!.SelectedModuleKeys.Should().Equal("live", "retired", "ghost");
    }

    [Fact]
    public async Task Extension_with_malformed_extension_id_drops_the_row_silently()
    {
        await SeedTemplateAsync("runtime-15");
        var toml = $$"""
            schema_version = {{WorkspaceConfigService.CurrentSchemaVersion}}
            kind = "workspace"

            [workspace]
            template = "runtime-15"
            name = "X"
            application_version = "24.0.0.0"
            runtime_version = "15"
            core_id_range_from = 1
            core_id_range_to = 99
            include_examples = true
            extension_prefix = "X"

            [[workspace.extensions]]
            kind = "core"
            key = ""
            id = "not-a-guid"
            name = "X Core"
            folder = "Core"
            publisher = "X"
            id_range_from = 1
            id_range_to = 99
            """;
        using var ctx = _db.NewContext();

        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Extensions.Should().BeEmpty(
            "malformed GUID rows are dropped silently so older configs don't fail validation");
    }

    private async Task SeedTemplateAsync(string key)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default(key));
        await ctx.SaveChangesAsync();
    }
}
