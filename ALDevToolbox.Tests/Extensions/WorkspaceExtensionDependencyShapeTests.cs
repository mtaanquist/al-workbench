using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ALDevToolbox.Tests.Extensions;

/// <summary>
/// A template extension's dependency names exactly one of three things: a
/// sibling extension, a module, or a literal app. The service validates that
/// before saving; <c>ck_workspace_extension_dependencies_one_ref</c> is what
/// holds when a row arrives some other way (an import, a hand-written SQL fix).
/// </summary>
public sealed class WorkspaceExtensionDependencyShapeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_dependency_with_one_reference_saves()
    {
        await SaveWithDependencyAsync(new WorkspaceExtensionDependency { RefModuleKey = "payments" });

        await using var ctx = _db.NewContext();
        (await ctx.Set<WorkspaceExtensionDependency>().AsNoTracking().SingleAsync())
            .RefModuleKey.Should().Be("payments");
    }

    [Fact]
    public async Task A_dependency_with_two_references_is_refused()
    {
        var act = () => SaveWithDependencyAsync(new WorkspaceExtensionDependency
        {
            RefExtensionPath = "Core",
            LitId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
            LitName = "System Application",
            LitPublisher = "Microsoft",
            LitVersion = "27.0.0.0",
        });

        await ShouldViolateTheShapeCheckAsync(act);
    }

    [Fact]
    public async Task A_dependency_with_no_reference_is_refused()
    {
        var act = () => SaveWithDependencyAsync(new WorkspaceExtensionDependency());

        await ShouldViolateTheShapeCheckAsync(act);
    }

    private static async Task ShouldViolateTheShapeCheckAsync(Func<Task> act)
    {
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("ck_workspace_extension_dependencies_one_ref");
    }

    private async Task SaveWithDependencyAsync(WorkspaceExtensionDependency dependency)
    {
        var template = TemplateBuilder.Default();
        dependency.OrganizationId = template.OrganizationId;
        template.WorkspaceExtensions.Single().Dependencies.Add(dependency);

        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();
    }
}
