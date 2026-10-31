using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Templates;

/// <summary>
/// Exercises <see cref="ModuleService.UpdateAsync"/>'s dependency reconciliation.
/// Mirrors the template reconciliation tests but for the smaller surface
/// — a regression here would silently churn or drop module dependencies in the
/// generated <c>app.json</c>.
/// </summary>
public sealed class ModuleServiceReconciliationTests : IDisposable
{
    private const string DepIdA = "11111111-1111-1111-1111-111111111111";
    private const string DepIdB = "22222222-2222-2222-2222-222222222222";
    private const string DepIdC = "33333333-3333-3333-3333-333333333333";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Update_reorders_dependencies_in_place_without_replacing_primary_keys()
    {
        int moduleId;
        int depAId, depBId;

        await using (var ctx = _db.NewContext())
        {
            var module = ModuleBuilder.Default("mod-reorder")
                .WithDependency(DepIdA, "Dep A", "Acme", "1.0.0.0")
                .WithDependency(DepIdB, "Dep B", "Acme", "1.0.0.0");
            ctx.Modules.Add(module);
            await ctx.SaveChangesAsync();
            moduleId = module.Id;
            depAId = module.Dependencies.Single(d => d.DepId == DepIdA).Id;
            depBId = module.Dependencies.Single(d => d.DepId == DepIdB).Id;
        }

        var input = ModuleInputFor(
            "mod-reorder",
            new ModuleDependencyInput(DepIdB, "Dep B", "Acme", "1.0.0.0"),
            new ModuleDependencyInput(DepIdA, "Dep A", "Acme", "1.0.0.0"));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(moduleId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var deps = await verify.ModuleDependencies
                .Where(d => d.ModuleId == moduleId)
                .OrderBy(d => d.Ordering)
                .ToListAsync();
            deps.Select(d => d.DepId).Should().Equal(DepIdB, DepIdA);
            // Reconciler reuses the same primary keys — row[0] mutates from
            // "Dep A" to "Dep B"; row[1] mutates the other way.
            deps[0].Id.Should().Be(depAId);
            deps[1].Id.Should().Be(depBId);
        }
    }

    [Fact]
    public async Task Update_removes_dependencies_dropped_from_the_input_list()
    {
        int moduleId;
        await using (var ctx = _db.NewContext())
        {
            var module = ModuleBuilder.Default("mod-shrink")
                .WithDependency(DepIdA, "Dep A", "Acme", "1.0.0.0")
                .WithDependency(DepIdB, "Dep B", "Acme", "1.0.0.0");
            ctx.Modules.Add(module);
            await ctx.SaveChangesAsync();
            moduleId = module.Id;
        }

        var input = ModuleInputFor(
            "mod-shrink",
            new ModuleDependencyInput(DepIdA, "Dep A", "Acme", "1.0.0.0"));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(moduleId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var deps = await verify.ModuleDependencies
                .Where(d => d.ModuleId == moduleId)
                .ToListAsync();
            deps.Should().HaveCount(1);
            deps[0].DepId.Should().Be(DepIdA);
        }
    }

    [Fact]
    public async Task Update_appends_new_dependencies_to_the_end_of_the_list()
    {
        int moduleId;
        await using (var ctx = _db.NewContext())
        {
            var module = ModuleBuilder.Default("mod-grow")
                .WithDependency(DepIdA, "Dep A", "Acme", "1.0.0.0");
            ctx.Modules.Add(module);
            await ctx.SaveChangesAsync();
            moduleId = module.Id;
        }

        var input = ModuleInputFor(
            "mod-grow",
            new ModuleDependencyInput(DepIdA, "Dep A", "Acme", "1.0.0.0"),
            new ModuleDependencyInput(DepIdC, "Dep C", "Acme", "1.0.0.0"));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(moduleId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var deps = await verify.ModuleDependencies
                .Where(d => d.ModuleId == moduleId)
                .OrderBy(d => d.Ordering)
                .ToListAsync();
            deps.Select(d => d.DepId).Should().Equal(DepIdA, DepIdC);
            deps[1].Ordering.Should().Be(1);
        }
    }

    [Fact]
    public async Task Update_persists_dependency_field_changes_in_place()
    {
        int moduleId, depId;
        await using (var ctx = _db.NewContext())
        {
            var module = ModuleBuilder.Default("mod-edit-dep")
                .WithDependency(DepIdA, "Old Name", "Old Publisher", "1.0.0.0");
            ctx.Modules.Add(module);
            await ctx.SaveChangesAsync();
            moduleId = module.Id;
            depId = module.Dependencies.Single().Id;
        }

        var input = ModuleInputFor(
            "mod-edit-dep",
            new ModuleDependencyInput(DepIdA, "New Name", "New Publisher", "2.0.0.0"));

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).UpdateAsync(moduleId, input);
        }

        await using (var verify = _db.NewContext())
        {
            var dep = await verify.ModuleDependencies.SingleAsync(d => d.Id == depId);
            dep.DepName.Should().Be("New Name");
            dep.DepPublisher.Should().Be("New Publisher");
            dep.DepVersion.Should().Be("2.0.0.0");
        }
    }

    [Fact]
    public async Task Creating_a_module_over_the_storage_quota_is_refused()
    {
        await _db.SetZeroStorageQuotaAsync();
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreateAsync(ModuleInputFor("quota-module"));

        (await act.Should().ThrowAsync<ALDevToolbox.Domain.ValueObjects.PlanValidationException>())
            .Which.Errors.Should().ContainKey("storage");
    }

    private ModuleService NewService(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx), _db.NewQuotaGuard(ctx));

    private static ModuleInput ModuleInputFor(string key, params ModuleDependencyInput[] deps) =>
        new(
            Key: key,
            Name: "Test Module",
            ExtensionName: "TestModule",
            IdRangeSize: null,
            Deprecated: false,
            Dependencies: deps,
            Folders: new List<FolderAuthoring>());
}
