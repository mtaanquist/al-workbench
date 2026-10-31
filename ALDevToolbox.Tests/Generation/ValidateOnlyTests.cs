using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// Covers the validate-only entry points the generator pages call before they
/// let their native POST through (#546).
///
/// The load-bearing property is <em>parity</em>: whatever
/// <c>GenerateWorkspaceAsync</c> / <c>GenerateExtensionAsync</c> would refuse,
/// the matching <c>Validate…Async</c> must report — and whatever they accept,
/// it must wave through. If the two ever disagree the page either blocks a
/// legal plan or waves through an illegal one, and the second case lands the
/// user on exactly the error page inline validation exists to avoid. Each
/// rejection test below therefore asserts *both* sides rather than trusting
/// that they share a code path.
/// </summary>
public sealed class ValidateOnlyTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ===== workspace =====

    [Fact]
    public async Task A_good_workspace_plan_reports_nothing()
    {
        await SeedTemplateAsync();

        var errors = await NewService().ValidateWorkspaceAsync(PlanBuilder.WorkspacePlan());

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "WorkspaceName")]
    [InlineData("!!!", "WorkspaceName")]             // nothing left to name a folder with
    [InlineData("\tCRONUS", "WorkspaceName")]        // no control characters
    public async Task A_bad_workspace_name_is_reported_and_refused(string name, string expectedKey)
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(workspaceName: name);

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey(expectedKey);
        await AssertGenerateRefusesAsync(plan, expectedKey);
    }

    [Fact]
    public async Task An_over_long_short_name_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(shortName: new string('A', 51));

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("ShortName");
        await AssertGenerateRefusesAsync(plan, "ShortName");
    }

    [Fact]
    public async Task An_extension_name_over_200_characters_is_reported_and_refused()
    {
        // AppSourceCop AS0047. The name only exists once the template has been
        // substituted, so the check runs inside the shared prepare step -
        // which is exactly what makes the page see it too. See
        // .design/customer-naming.md.
        // A typed prefix is capped on its own (#1615), so a long name now comes
        // from the template's own name pattern.
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Single().NameTemplate = "{{extension_prefix}} " + new string('C', 200);
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(template);
            ctx.Modules.Add(ModuleBuilder.Default());
            await ctx.SaveChangesAsync();
        }
        var plan = PlanBuilder.WorkspacePlan();

        var errors = await NewService().ValidateWorkspaceAsync(plan);
        errors.Should().ContainKey("ShortName");
        errors["ShortName"].Should().Be(
            "The extension name would be longer than 200 characters, which Business Central "
            + "refuses. Use a shorter short name.");
        await AssertGenerateRefusesAsync(plan, "ShortName");
    }

    [Fact]
    public async Task A_workspace_plan_carrying_a_short_name_reports_nothing()
    {
        await SeedTemplateAsync();

        var errors = await NewService().ValidateWorkspaceAsync(PlanBuilder.WorkspacePlan(shortName: "CRO"));

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CRONUS tenant")]
    [InlineData("8a4f1c3e1b2d4e6f9c1a3d5b7e9f2a4c")]
    public async Task A_tenant_id_that_is_not_a_guid_is_reported_and_refused(string tenantId)
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan() with { TenantId = tenantId };

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("TenantId");
        await AssertGenerateRefusesAsync(plan, "TenantId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("8a4f1c3e-1b2d-4e6f-9c1a-3d5b7e9f2a4c")]
    public async Task A_blank_or_well_formed_tenant_id_is_accepted(string tenantId)
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan() with { TenantId = tenantId };

        (await NewService().ValidateWorkspaceAsync(plan)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_inverted_core_id_range_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(coreFrom: 90999, coreTo: 90000);

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("CoreIdRangeTo");
        await AssertGenerateRefusesAsync(plan, "CoreIdRangeTo");
    }

    /// <summary>
    /// #730: everything after Core moves with the Core range, so a large enough
    /// downward move would put the first slot after Core below object id 1.
    /// Refused up front, keyed on the field the shift keys on so the New
    /// Workspace page renders it next to the range editor.
    /// </summary>
    [Fact]
    public async Task A_core_range_that_pushes_later_extensions_below_id_one_is_reported_and_refused()
    {
        // The default fixture leaves no gap after Core (modules start at
        // CoreIdRangeTo + 1), so its shifted start is always CoreIdRangeTo + 1
        // and can never fall below 1. Only a template whose module range starts
        // *before* the end of its own Core range can, so seed one of those:
        // with a module start 999 ids before CoreIdRangeTo, a workspace Core
        // range ending at 999 shifts by -90000 and lands the module start on 0.
        var template = TemplateBuilder.Default();
        template.ModuleIdRangeStart = 90000;
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }
        var plan = PlanBuilder.WorkspacePlan(coreFrom: 1, coreTo: 999);

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("CoreIdRangeTo");
        await AssertGenerateRefusesAsync(plan, "CoreIdRangeTo");
    }

    /// <summary>
    /// The other side of the same rule: a moved Core range that still leaves
    /// room for what follows is accepted, and the page must not block it.
    /// </summary>
    [Fact]
    public async Task A_moved_core_range_that_leaves_room_after_it_is_accepted()
    {
        await SeedTemplateAsync();

        var errors = await NewService().ValidateWorkspaceAsync(
            PlanBuilder.WorkspacePlan(coreFrom: 60000, coreTo: 60999));

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_that_does_not_exist_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.WorkspacePlan(templateKey: "no-such-template");

        (await NewService().ValidateWorkspaceAsync(plan)).Should().ContainKey("TemplateKey");
        await AssertGenerateRefusesAsync(plan, "TemplateKey");
    }

    /// <summary>
    /// The rule the page could not mirror in HTML and the reason this entry
    /// point has to load the template rather than just check the plan's shape:
    /// the overlap is between *resolved* ranges, which only exist once the
    /// template's extensions and the selected modules have been walked.
    /// </summary>
    [Fact]
    public async Task Overlapping_id_ranges_are_reported_and_refused()
    {
        // Two template extensions with explicit ranges that collide. Nothing
        // the plan carries can cause or avoid this - it is a property of the
        // template plus the selection, which is exactly why the check needs
        // the template loaded.
        var template = TemplateBuilder.Default();
        template.WorkspaceExtensions.Single().IdRangeFrom = 70000;
        template.WorkspaceExtensions.Single().IdRangeTo = 70999;
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Clash",
            NameTemplate = "Clash",
            Required = true,
            Ordering = 1,
            IdRangeFrom = 70500,
            IdRangeTo = 71499,
        });
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }
        var plan = PlanBuilder.WorkspacePlan();

        var errors = await NewService().ValidateWorkspaceAsync(plan);

        errors.Should().NotBeEmpty();
        errors.Keys.Should().Contain(k => k.Contains("IdRange"));
        await AssertGenerateRefusesAsync(plan, expectedKeyFragment: "IdRange");
    }

    /// <summary>
    /// A ticked optional extension that depends on an unticked one used to
    /// pass inline validation and only fail at ZIP emit, landing the user on
    /// the error page (#1266, L31). The page now hears about it first, keyed on
    /// the optional-extension checkboxes.
    /// </summary>
    [Fact]
    public async Task Depending_on_an_unticked_optional_extension_is_reported_and_refused()
    {
        await SeedTemplateWithOptionalPairAsync();
        var plan = PlanBuilder.WorkspacePlan() with { SelectedExtensionPaths = ["Test"] };

        var errors = await NewService().ValidateWorkspaceAsync(plan);

        errors.Should().ContainKey("SelectedExtensionPaths");
        errors["SelectedExtensionPaths"].Should().Contain("Test needs Fixtures");
        await AssertGenerateRefusesAsync(plan, "SelectedExtensionPaths");
    }

    [Fact]
    public async Task Ticking_the_extension_it_depends_on_as_well_is_accepted()
    {
        await SeedTemplateWithOptionalPairAsync();
        var plan = PlanBuilder.WorkspacePlan() with { SelectedExtensionPaths = ["Test", "Fixtures"] };

        (await NewService().ValidateWorkspaceAsync(plan)).Should().BeEmpty();
        var archive = await NewService().GenerateWorkspaceAsync(plan);
        await using var stream = archive.Stream;
        stream.Length.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The extension flow checks the template exists with a probe rather than
    /// loading it whole (L33); a deleted template still has to be refused.
    /// </summary>
    [Fact]
    public async Task An_extension_plan_on_a_deleted_template_is_reported()
    {
        var template = TemplateBuilder.Default();
        template.DeletedAt = DateTime.UtcNow;
        await using (var ctx = _db.NewContext())
        {
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        var errors = await NewService().ValidateExtensionAsync(PlanBuilder.ExtensionPlan());

        errors.Should().ContainKey("TemplateKey");
    }

    // ===== standalone extension =====

    [Fact]
    public async Task A_good_extension_plan_reports_nothing()
    {
        await SeedTemplateAsync();

        var errors = await NewService().ValidateExtensionAsync(PlanBuilder.ExtensionPlan());

        errors.Should().BeEmpty();
    }

    /// <summary>
    /// This used to assert a *space* was refused. Issue #520 deliberately widened
    /// <c>ExtensionNameRegex</c> to allow spaces — the name feeds app.json's
    /// "name", where spaces are wanted, and the folder name strips them — so a
    /// space is now the happy path, covered by
    /// <c>StandaloneExtensionGenerationTests</c>. What this test is actually for
    /// is that inline validation and generation agree about a bad name, so it
    /// keeps that shape with a name that is still invalid: "Bad$Name" trips the
    /// character class, matching the example in PlanValidationTests.
    /// </summary>
    [Fact]
    public async Task An_invalid_extension_name_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(extensionName: "Bad$Name");

        (await NewService().ValidateExtensionAsync(plan)).Should().ContainKey("ExtensionName");
        await AssertGenerateRefusesAsync(plan, "ExtensionName");
    }

    /// <summary>
    /// The other half of #520: a spaced name must pass inline validation, or the
    /// generator page would refuse a name the service happily generates.
    /// </summary>
    [Fact]
    public async Task An_extension_name_with_a_space_is_accepted()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(extensionName: "My Addon");

        (await NewService().ValidateExtensionAsync(plan)).Should().BeEmpty();
    }

    /// <summary>
    /// Publisher is not a form field — it comes from the org's defaults — so a
    /// blank one used to reject the submit with "Required." next to nothing at
    /// all. The message has to say where to go instead.
    /// </summary>
    [Fact]
    public async Task A_missing_org_publisher_is_reported_with_somewhere_to_go()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(publisher: "");

        var errors = await NewService().ValidateExtensionAsync(plan);

        errors.Should().ContainKey("Publisher");
        errors["Publisher"].Should().Contain("Administration").And.Contain("Defaults");
        errors["Publisher"].Should().NotBe("Required.");
    }

    [Fact]
    public async Task An_over_long_typed_prefix_is_reported_under_its_own_field()
    {
        await SeedTemplateAsync();
        await SetOrgSettingsAsync(new OrganizationSettings { ExtensionPrefixMode = ExtensionPrefixMode.PerWorkspace });
        var plan = PlanBuilder.WorkspacePlan(extensionPrefix: new string('P', 51));

        var errors = await NewService().ValidateWorkspaceAsync(plan);

        errors.Should().ContainKey("ExtensionPrefix").And.NotContainKey("ShortName");
        await AssertGenerateRefusesAsync(plan, "ExtensionPrefix");
    }

    /// <summary>
    /// Left alone, the prefix field posts what an empty field resolves to - with
    /// no short name, the customer's whole name, which may be up to 100
    /// characters. That is not a typed prefix and is not capped.
    /// </summary>
    [Fact]
    public async Task An_untouched_prefix_field_carrying_a_long_customer_name_is_accepted()
    {
        await SeedTemplateAsync();
        await SetOrgSettingsAsync(new OrganizationSettings { ExtensionPrefixMode = ExtensionPrefixMode.PerWorkspace });
        var customer = "CRONUS " + new string('A', 53);
        var plan = PlanBuilder.WorkspacePlan(workspaceName: customer, shortName: null, extensionPrefix: customer);

        (await NewService().ValidateWorkspaceAsync(plan)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_typed_prefix_of_fifty_characters_is_accepted()
    {
        await SeedTemplateAsync();
        await SetOrgSettingsAsync(new OrganizationSettings { ExtensionPrefixMode = ExtensionPrefixMode.PerWorkspace });

        (await NewService().ValidateWorkspaceAsync(PlanBuilder.WorkspacePlan(extensionPrefix: new string('P', 50))))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "Base Application", "Microsoft", "24.0.0.0", "DepId")]
    [InlineData("Base Application", "Base Application", "Microsoft", "24.0.0.0", "DepId")]
    [InlineData("437dbf0e-84ff-417a-965d-ed2bb9650972", " ", "Microsoft", "24.0.0.0", "DepName")]
    [InlineData("437dbf0e-84ff-417a-965d-ed2bb9650972", "Base Application", "", "24.0.0.0", "DepPublisher")]
    [InlineData("437dbf0e-84ff-417a-965d-ed2bb9650972", "Base Application", "Microsoft", "", "DepVersion")]
    public async Task A_dependency_missing_a_field_or_a_real_app_id_is_reported_and_refused(
        string id, string name, string publisher, string version, string field)
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(dependencies: new[] { new DependencyEntry(id, name, publisher, version) });

        (await NewService().ValidateExtensionAsync(plan)).Should().ContainKey($"Dependencies[0].{field}");
        await AssertGenerateRefusesAsync(plan, $"Dependencies[0].{field}");
    }

    [Fact]
    public async Task The_same_app_id_written_two_ways_is_a_duplicate()
    {
        await SeedTemplateAsync();
        var plan = PlanBuilder.ExtensionPlan(dependencies: new[]
        {
            new DependencyEntry("437dbf0e-84ff-417a-965d-ed2bb9650972", "Base Application", "Microsoft", "24.0.0.0"),
            new DependencyEntry("{437DBF0E-84FF-417A-965D-ED2BB9650972}", "Base Application", "Microsoft", "24.0.0.0"),
        });

        (await NewService().ValidateExtensionAsync(plan)).Should().ContainKey("Dependencies[1].DepId");
    }

    [Fact]
    public async Task A_duplicate_dependency_is_reported_and_refused()
    {
        await SeedTemplateAsync();
        var dep = new DependencyEntry("437dbf0e-84ff-417a-965d-ed2bb9650972", "Base Application", "Microsoft", "24.0.0.0");
        var plan = PlanBuilder.ExtensionPlan(dependencies: new[] { dep, dep });

        var errors = await NewService().ValidateExtensionAsync(plan);

        errors.Keys.Should().Contain(k => k.StartsWith("Dependencies["));
        await AssertGenerateRefusesAsync(plan, expectedKeyFragment: "Dependencies[");
    }

    // ===== helpers =====

    private async Task SetOrgSettingsAsync(OrganizationSettings settings)
    {
        await using var ctx = _db.NewContext();
        settings.OrganizationId = TemplateBuilder.DefaultOrganizationId;
        settings.UpdatedAt = DateTime.UtcNow;
        ctx.OrganizationSettings.Add(settings);
        await ctx.SaveChangesAsync();
    }

    private async Task AssertGenerateRefusesAsync(ProjectPlan plan, string expectedKeyFragment)
    {
        var act = async () => await NewService().GenerateWorkspaceAsync(plan);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains(expectedKeyFragment));
    }

    private async Task AssertGenerateRefusesAsync(StandaloneExtensionPlan plan, string expectedKeyFragment)
    {
        var act = async () => await NewService().GenerateExtensionAsync(plan);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Keys.Should().Contain(k => k.Contains(expectedKeyFragment));
    }

    private GenerationService NewService()
    {
        var ctx = _db.NewContext();
        var mustache = new ALDevToolbox.Services.Generation.MustacheRenderer(
            NullLogger<ALDevToolbox.Services.Generation.MustacheRenderer>.Instance);
        return new GenerationService(
            ctx,
            _db.NewOrganizationConfigService(ctx),
            new FolderTreeHydrator(ctx),
            _db.OrgContext,
            mustache,
            new ALDevToolbox.Services.Generation.WorkspaceZipBuilder(mustache, new WorkspaceConfigService(ctx)),
            NullLogger<GenerationService>.Instance);
    }

    /// <summary>The default template plus two optional extensions, Test depending on Fixtures.</summary>
    private async Task SeedTemplateWithOptionalPairAsync()
    {
        var template = TemplateBuilder.Default();
        var test = new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Test",
            NameTemplate = "Test",
            Required = false,
            Ordering = 1,
        };
        test.Dependencies.Add(new WorkspaceExtensionDependency
        {
            OrganizationId = template.OrganizationId,
            RefExtensionPath = "Fixtures",
            Ordering = 0,
        });
        template.WorkspaceExtensions.Add(test);
        template.WorkspaceExtensions.Add(new WorkspaceExtension
        {
            OrganizationId = template.OrganizationId,
            Path = "Fixtures",
            NameTemplate = "Fixtures",
            Required = false,
            Ordering = 2,
        });
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedTemplateAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(TemplateBuilder.Default());
        ctx.Modules.Add(ModuleBuilder.Default());
        await ctx.SaveChangesAsync();
    }
}
