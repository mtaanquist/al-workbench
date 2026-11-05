using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The migration that gives existing organisations the default repository standard
/// (the workflow that keeps test up to date with main) adds it once, after what
/// they already have, and never replaces a file an Admin put at the same path.
/// </summary>
public sealed class RefreshTestWorkflowMigrationTests : IDisposable
{
    private const string Previous = "20270301000000_MoveDateAfterUpdate";
    private const string Target = "20270302000000_SeedRefreshTestWorkflow";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Existing_organisations_get_the_workflow_after_their_own_files_and_keep_their_own_copy()
    {
        int otherOrgId;
        await using (var ctx = _db.NewContext())
        {
            await ctx.GetService<IMigrator>().MigrateAsync(Previous);
            var other = new Organization { Name = "Fabrikam", Slug = "refresh-test-migration", CreatedAt = DateTime.UtcNow };
            ctx.Organizations.Add(other);
            await ctx.SaveChangesAsync();
            otherOrgId = other.Id;

            ctx.GitHubRepositoryStandardFiles.AddRange(
                new GitHubRepositoryStandardFile
                {
                    OrganizationId = TestDb.DefaultOrgId, Path = "CODEOWNERS", Content = "* @cronus",
                    Ordering = 4, UpdatedAt = DateTime.UtcNow,
                },
                new GitHubRepositoryStandardFile
                {
                    OrganizationId = otherOrgId, Path = DefaultRepositoryStandardFiles.RefreshTestWorkflowPath,
                    Content = "name: our own", Ordering = 0, UpdatedAt = DateTime.UtcNow,
                });
            await ctx.SaveChangesAsync();

            await ctx.GetService<IMigrator>().MigrateAsync(Target);
        }

        await using var read = _db.NewContext();
        var files = await read.GitHubRepositoryStandardFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Path == DefaultRepositoryStandardFiles.RefreshTestWorkflowPath)
            .ToListAsync();

        var ours = files.Should().ContainSingle(f => f.OrganizationId == TestDb.DefaultOrgId).Subject;
        ours.Ordering.Should().Be(5, "it goes after the organisation's existing standards");
        // The migration carries its own copy of the body; today it matches the
        // constant new organisations get. If the constant changes later, this
        // assertion is the one to relax, not the migration.
        ours.Content.Should().Be(DefaultRepositoryStandardFiles.RefreshTestWorkflowContent);

        files.Single(f => f.OrganizationId == otherOrgId).Content
            .Should().Be("name: our own", "an Admin's file at the same path is kept");
    }
}
