using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins the one-off rename of existing pipelines (the GeneratePipelineNames
/// migration). The fixture already ran it against an empty database, so this seeds
/// freely named pipelines, replays <see cref="GeneratePipelineNames.BackfillSql"/>,
/// and checks the names match what <see cref="PipelineNames"/> gives a pipeline
/// saved today. A clash keeps the old name, marked as typed.
/// </summary>
public sealed class GeneratePipelineNamesMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private const string Discovered = """
        [{"AppId":"{11111111-1111-1111-1111-111111111111}","Name":"CRONUS Sales","Publisher":"CRONUS","Version":"1.0.0.0","RepoUrl":"","RepoDisplayName":""},
         {"AppId":"22222222-2222-2222-2222-222222222222","Name":"CRONUS Base","Publisher":"CRONUS","Version":"1.0.0.0","RepoUrl":"","RepoDisplayName":""}]
        """;

    [Fact]
    public async Task Existing_pipelines_get_the_names_they_would_be_given_today()
    {
        const int org = TestDb.DefaultOrgId;
        var now = DateTime.UtcNow;
        OePipeline Build(int projectId, string name, string? branch, string? selection, DateTime? deleted = null) => new()
        {
            OrganizationId = org, ProjectId = projectId, Name = name, Branch = branch,
            RequestedAppIdsJson = selection, CreatedAt = now, UpdatedAt = now, DeletedAt = deleted,
        };

        int projectId;
        OePipeline everything, sales, two, sameAsEverything, broken, deleted;
        OeReleasePipeline fromSales, fromRepo, clashing, ofClashing;
        await using (var seed = _db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = org, Name = "CRONUS " + Guid.NewGuid().ToString("N"),
                DiscoveredExtensionsJson = Discovered, CreatedAt = now, UpdatedAt = now,
            };
            seed.OeProjects.Add(project);
            await seed.SaveChangesAsync();
            projectId = project.Id;

            everything = Build(projectId, "Default", null, null);
            sales = Build(projectId, "Prod", "main", """["11111111-1111-1111-1111-111111111111"]""");
            two = Build(projectId, "Test apps", "release/25.0",
                """["11111111-1111-1111-1111-111111111111","22222222-2222-2222-2222-222222222222"]""");
            // Same setup as `everything`, so it can't take the same name.
            sameAsEverything = Build(projectId, "Default copy", " ", "[]");
            broken = Build(projectId, "Odd", "main", "not json");
            deleted = Build(projectId, "Old", "main", null, now);
            seed.OePipelines.AddRange(everything, sales, two, sameAsEverything, broken, deleted);

            var repository = new OeProjectRepository
            {
                OrganizationId = org, ProjectId = projectId, Provider = Domain.ValueObjects.RepositoryProvider.GitHub,
                Url = "https://github.com/cronus-dk/" + Guid.NewGuid().ToString("N") + ".git", DisplayName = "cronus-apps",
            };
            var production = new OeProjectEnvironment { OrganizationId = org, ProjectId = projectId, Name = "Production", Type = "Production", FetchedAt = now };
            var sandbox = new OeProjectEnvironment { OrganizationId = org, ProjectId = projectId, Name = "UAT", Type = "Sandbox", FetchedAt = now };
            seed.OeProjectRepositories.Add(repository);
            seed.OeProjectEnvironments.AddRange(production, sandbox);
            await seed.SaveChangesAsync();

            OeReleasePipeline Deploy(string name, OePipeline? source, OeProjectEnvironment env) => new()
            {
                OrganizationId = org, ProjectId = projectId, Name = name,
                ArtifactSource = source is null ? ReleaseArtifactSource.GithubRelease : ReleaseArtifactSource.Build,
                BuildPipelineId = source?.Id, GithubReleaseRepositoryId = source is null ? repository.Id : null,
                ProjectEnvironmentId = env.Id, CreatedAt = now, UpdatedAt = now,
            };
            fromSales = Deploy("CRONUS to prod", sales, production);
            fromRepo = Deploy("Releases", null, sandbox);
            clashing = Deploy("Everything to UAT", everything, sandbox);
            ofClashing = Deploy("Everything to UAT again", everything, sandbox);
            seed.OeReleasePipelines.AddRange(fromSales, fromRepo, clashing, ofClashing);
            await seed.SaveChangesAsync();
        }

        await using (var run = _db.NewContext())
        {
            // Straight to the connection: ExecuteSqlRaw would read the SQL's '{}' as
            // format placeholders, which the migration itself never does.
            var connection = run.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = GeneratePipelineNames.BackfillSql;
            await command.ExecuteNonQueryAsync();
        }

        await using var read = _db.NewContext();
        var builds = await read.OePipelines.AsNoTracking().Where(p => p.ProjectId == projectId).ToDictionaryAsync(p => p.Id);
        builds[everything.Id].Name.Should().Be(PipelineNames.DefaultBranch);
        builds[sales.Id].Name.Should().Be("main (CRONUS Sales)");
        builds[two.Id].Name.Should().Be("release/25.0 (2 extensions)");
        builds[broken.Id].Name.Should().Be("main", "a selection that can't be read is treated as everything");
        builds[sameAsEverything.Id].Name.Should().Be("Default copy");
        builds[sameAsEverything.Id].NameIsCustom.Should().BeTrue();
        builds[everything.Id].NameIsCustom.Should().BeFalse();
        builds[deleted.Id].Name.Should().Be("Old", "deleted pipelines keep their names");

        var deployments = await read.OeReleasePipelines.AsNoTracking().Where(r => r.ProjectId == projectId).ToDictionaryAsync(r => r.Id);
        deployments[fromSales.Id].Name.Should().Be("main (CRONUS Sales) to Production");
        deployments[fromRepo.Id].Name.Should().Be("cronus-apps releases to UAT");
        deployments[clashing.Id].Name.Should().Be("Default branch to UAT");
        deployments[clashing.Id].NameIsCustom.Should().BeFalse();
        deployments[ofClashing.Id].Name.Should().Be("Everything to UAT again");
        deployments[ofClashing.Id].NameIsCustom.Should().BeTrue();
    }

    [Fact]
    public void The_SQL_and_the_service_name_build_pipelines_alike()
    {
        // The migration repeats PipelineNames in SQL; the cases above pin the SQL, these
        // pin the C# side to the same strings.
        var names = new Dictionary<string, string> { ["11111111-1111-1111-1111-111111111111"] = "CRONUS Sales" };
        PipelineNames.ForBuildPipeline(null, null, names).Should().Be("Default branch");
        PipelineNames.ForBuildPipeline("main", ["{11111111-1111-1111-1111-111111111111}"], names).Should().Be("main (CRONUS Sales)");
        PipelineNames.ForBuildPipeline("release/25.0", ["a", "b"], names).Should().Be("release/25.0 (2 extensions)");
        PipelineNames.ForBuildPipeline("main", ["c"], names).Should().Be("main (1 extension)");
        PipelineNames.ForDeploymentFromBuild("main (CRONUS Sales)", "Production").Should().Be("main (CRONUS Sales) to Production");
        PipelineNames.ForDeploymentFromReleases("cronus-apps", "UAT").Should().Be("cronus-apps releases to UAT");
        PipelineNames.ForBuildPipeline(new string('x', 250), null, names).Should().HaveLength(PipelineNames.MaxLength);
        PipelineNames.ForDeploymentFromBuild(new string('x', 200), "Production").Should()
            .HaveLength(PipelineNames.MaxLength).And.EndWith(" to Production");
    }
}
