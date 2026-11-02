using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Tests.Toml;

/// <summary>
/// Round-trip coverage for <see cref="WorkspaceConfigService"/>: a plan goes
/// <c>Plan → Build → ParseAsync → Plan</c> and every field a user would care
/// about survives the trip. Mirrors the
/// <see cref="TemplateTomlMapperRoundTripTests"/> shape.
/// </summary>
public sealed class WorkspaceConfigServiceRoundTripTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Workspace_round_trip_preserves_every_field_and_module_list()
    {
        await SeedTemplateAndModulesAsync("runtime-15", "payments", "warehouse");

        var plan = new ProjectPlan(
            TemplateKey: "runtime-15",
            WorkspaceName: "Acme Customer",
            ShortName: "AC",
            ExtensionPrefix: "ACME",
            Brief: "Brief copy",
            Description: "Longer description with [TOML]-unfriendly chars: \" ' \n line two",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15.2",
            CoreIdRangeFrom: 80000,
            CoreIdRangeTo: 80999,
            IncludeExamples: false,
            SelectedExtensionPaths: new List<string> { "Hotfix", "Reports" },
            SelectedModuleKeys: new List<string> { "payments", "warehouse" },
            TenantId: "7d3a1f5e-2b4c-4e8a-9f61-0c2d3e4f5a6b");

        var identities = new List<WorkspaceExtensionIdentity>
        {
            new(WorkspaceExtensionIdentity.CoreKind, null, Guid.NewGuid(), "ACME Core", "Core", "Acme", 80000, 80099),
            new(WorkspaceExtensionIdentity.ModuleKind, "payments", Guid.NewGuid(), "ACME Payments", "Payments", "Acme", 81000, 81099),
        };

        using var buildCtx = _db.NewContext();
        var svc = new WorkspaceConfigService(buildCtx);
        var toml = svc.BuildWorkspace(plan, identities);

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Kind.Should().Be(WorkspaceConfigService.WorkspaceKind);
        parsed.Extension.Should().BeNull();
        parsed.Workspace.Should().NotBeNull();
        parsed.Workspace!.TemplateKey.Should().Be(plan.TemplateKey);
        parsed.Workspace.WorkspaceName.Should().Be(plan.WorkspaceName);
        parsed.Workspace.ShortName.Should().Be(plan.ShortName);
        parsed.Workspace.ExtensionPrefix.Should().Be(plan.ExtensionPrefix);
        parsed.Workspace.Brief.Should().Be(plan.Brief);
        parsed.Workspace.Description.Should().Be(plan.Description);
        parsed.Workspace.ApplicationVersion.Should().Be(plan.ApplicationVersion);
        parsed.Workspace.RuntimeVersion.Should().Be(plan.RuntimeVersion);
        parsed.Workspace.CoreIdRangeFrom.Should().Be(plan.CoreIdRangeFrom);
        parsed.Workspace.CoreIdRangeTo.Should().Be(plan.CoreIdRangeTo);
        parsed.Workspace.IncludeExamples.Should().Be(plan.IncludeExamples);
        parsed.Workspace.SelectedExtensionPaths.Should().Equal(plan.SelectedExtensionPaths);
        parsed.Workspace.SelectedModuleKeys.Should().Equal(plan.SelectedModuleKeys);
        parsed.Workspace.TenantId.Should().Be(plan.TenantId);
        // The whole record too, so a field added to the plan later is caught
        // if it is left out of the file - but only once the plan above sets it
        // to a non-default value. An unset optional field round-trips as its
        // default either way, which is how TenantId went unchecked (#1440).
        parsed.Workspace.Should().BeEquivalentTo(plan, o => o.WithStrictOrdering());

        parsed.Extensions.Should().HaveCount(2);
        parsed.Extensions.Should().BeEquivalentTo(identities);
    }

    [Fact]
    public async Task Extension_round_trip_preserves_every_field_including_dependencies()
    {
        await SeedTemplateAsync("runtime-15");

        var plan = new StandaloneExtensionPlan(
            TemplateKey: "runtime-15",
            ExtensionName: "Acme Hotfix",
            Brief: "One-off bug-fix extension.",
            Description: "Multi-line\r\ndescription",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15.2",
            IdRangeFrom: 50000,
            IdRangeTo: 50099,
            IncludeExamples: true,
            Publisher: "Acme",
            Dependencies: new List<DependencyEntry>
            {
                new("11111111-1111-1111-1111-111111111111", "Base App", "Microsoft", "24.0.0.0"),
                new("22222222-2222-2222-2222-222222222222", "System App", "Microsoft", "24.0.0.0"),
            });

        using var buildCtx = _db.NewContext();
        var svc = new WorkspaceConfigService(buildCtx);
        var toml = svc.BuildExtension(plan);

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.Kind.Should().Be(WorkspaceConfigService.ExtensionKind);
        parsed.Workspace.Should().BeNull();
        parsed.Extension.Should().NotBeNull();
        parsed.Extension!.TemplateKey.Should().Be(plan.TemplateKey);
        parsed.Extension.ExtensionName.Should().Be(plan.ExtensionName);
        parsed.Extension.Brief.Should().Be(plan.Brief);
        parsed.Extension.Description.Should().Be(plan.Description);
        parsed.Extension.ApplicationVersion.Should().Be(plan.ApplicationVersion);
        parsed.Extension.RuntimeVersion.Should().Be(plan.RuntimeVersion);
        parsed.Extension.IdRangeFrom.Should().Be(plan.IdRangeFrom);
        parsed.Extension.IdRangeTo.Should().Be(plan.IdRangeTo);
        parsed.Extension.IncludeExamples.Should().Be(plan.IncludeExamples);
        parsed.Extension.Publisher.Should().Be(plan.Publisher);
        parsed.Extension.Dependencies.Should().BeEquivalentTo(plan.Dependencies);
    }

    [Fact]
    public async Task Build_emits_documented_header_so_users_recognise_the_file_in_a_diff()
    {
        await SeedTemplateAsync("runtime-15");
        var plan = new ProjectPlan(
            TemplateKey: "runtime-15",
            WorkspaceName: "X",
            ShortName: null,
            ExtensionPrefix: "X",
            Brief: "", Description: "",
            ApplicationVersion: "24.0.0.0",
            RuntimeVersion: "15.2",
            CoreIdRangeFrom: 1, CoreIdRangeTo: 2,
            IncludeExamples: true,
            SelectedExtensionPaths: Array.Empty<string>(),
            SelectedModuleKeys: Array.Empty<string>());
        using var buildCtx = _db.NewContext();
        var svc = new WorkspaceConfigService(buildCtx);

        var toml = svc.BuildWorkspace(plan, Array.Empty<WorkspaceExtensionIdentity>());

        toml.Should().StartWith("# AL Workbench project config.");
    }

    [Fact]
    public async Task A_config_written_before_short_names_existed_still_parses()
    {
        // Every workspace generated so far has a config with no short_name
        // key. It has to keep importing - and the customer name is the right
        // answer for it, which is the same fallback a blank short name gets.
        await SeedTemplateAsync("runtime-15");

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync("""
            schema_version = 1
            kind = "workspace"

            [workspace]
            template = "runtime-15"
            name = "CRONUS A/S"
            brief = ""
            description = ""
            application_version = "24.0.0.0"
            runtime_version = "15.2"
            core_id_range_from = 80000
            core_id_range_to = 80999
            include_examples = true
            extension_prefix = "CRO"
            """);

        parsed.Workspace.Should().NotBeNull();
        parsed.Workspace!.ShortName.Should().BeEmpty();
        parsed.Workspace.EffectiveShortName.Should().Be("CRONUS A/S");
    }

    /// <summary>
    /// #1615: the settings record the name the <c>.code-workspace</c> file was
    /// written under, so an extension added after the folder style changed
    /// rewrites that file instead of adding a second one.
    /// </summary>
    [Fact]
    public async Task The_code_workspace_file_name_round_trips()
    {
        await SeedTemplateAsync("runtime-15");
        using var buildCtx = _db.NewContext();
        var toml = new WorkspaceConfigService(buildCtx)
            .BuildWorkspace(MinimalPlan(), Array.Empty<WorkspaceExtensionIdentity>(), "CronusAS.code-workspace");

        toml.Should().Contain("code_workspace = \"CronusAS.code-workspace\"");
        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.CodeWorkspaceFileName.Should().Be("CronusAS.code-workspace");
    }

    [Fact]
    public async Task A_config_written_before_the_file_name_was_recorded_reads_it_as_unknown()
    {
        await SeedTemplateAsync("runtime-15");
        using var buildCtx = _db.NewContext();
        var toml = new WorkspaceConfigService(buildCtx)
            .BuildWorkspace(MinimalPlan(), Array.Empty<WorkspaceExtensionIdentity>());

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.CodeWorkspaceFileName.Should().BeNull();
    }

    /// <summary>
    /// The name is written back as a path in a ZIP and in a repository, so a
    /// hand-edited value that is anything but a plain file name at the root is
    /// ignored, and the name is derived as for an older file.
    /// </summary>
    [Theory]
    [InlineData("../evil.code-workspace")]
    [InlineData("sub/CronusAS.code-workspace")]
    [InlineData("sub\\CronusAS.code-workspace")]
    [InlineData("C:CronusAS.code-workspace")]
    [InlineData(".code-workspace")]
    [InlineData("CronusAS.json")]
    [InlineData("Cronus\u0001AS.code-workspace")]
    public async Task A_saved_file_name_that_is_not_a_plain_file_name_is_ignored(string saved)
    {
        await SeedTemplateAsync("runtime-15");
        using var buildCtx = _db.NewContext();
        var toml = new WorkspaceConfigService(buildCtx)
            .BuildWorkspace(MinimalPlan(), Array.Empty<WorkspaceExtensionIdentity>(), saved);

        await using var ctx = _db.NewContext();
        var parsed = await new WorkspaceConfigService(ctx).ParseAsync(toml);

        parsed.CodeWorkspaceFileName.Should().BeNull();
    }

    private static ProjectPlan MinimalPlan() => new(
        TemplateKey: "runtime-15",
        WorkspaceName: "CRONUS A/S",
        ShortName: null,
        ExtensionPrefix: "CRO",
        Brief: "", Description: "",
        ApplicationVersion: "24.0.0.0",
        RuntimeVersion: "15.2",
        CoreIdRangeFrom: 80000, CoreIdRangeTo: 80999,
        IncludeExamples: true,
        SelectedExtensionPaths: Array.Empty<string>(),
        SelectedModuleKeys: Array.Empty<string>());

    private async Task SeedTemplateAsync(string key)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default(key));
        await ctx.SaveChangesAsync();
    }

    private async Task SeedTemplateAndModulesAsync(string templateKey, params string[] moduleKeys)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default(templateKey));
        foreach (var moduleKey in moduleKeys)
        {
            ctx.Modules.Add(ModuleBuilder.Default(moduleKey, moduleKey));
        }
        await ctx.SaveChangesAsync();
    }
}
