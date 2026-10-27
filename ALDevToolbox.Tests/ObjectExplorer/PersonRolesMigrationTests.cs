using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The migration that lets a person hold several roles on a solution (issue #1462)
/// keeps the one role each person already had.
/// </summary>
public sealed class PersonRolesMigrationTests : IDisposable
{
    private const string Previous = "20270220000000_WidenTranslationMemoryRankIndex";
    private const string Target = "20270221000000_PersonRoles";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Each_person_keeps_their_one_role_as_their_only_role()
    {
        int projectId;
        await using (var ctx = _db.NewContext())
        {
            await ctx.GetService<IMigrator>().MigrateAsync(Previous);
            ctx.Users.Add(new User
            {
                Id = 900, OrganizationId = TestDb.DefaultOrgId, Email = "anne@cronus.example", PasswordHash = "x",
                DisplayName = "Anne", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
            projectId = project.Id;

            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO oe_project_people (organization_id, project_id, user_id, role, created_at)
                VALUES ({TestDb.DefaultOrgId}, {projectId}, 900, 'ProjectLeader', now())
                """);

            await ctx.GetService<IMigrator>().MigrateAsync(Target);
        }

        await using var read = _db.NewContext();
        var person = await read.OeProjectPeople.AsNoTracking().SingleAsync(p => p.ProjectId == projectId);
        person.Roles.Should().Equal(ProjectPersonRole.ProjectLeader);
    }
}
