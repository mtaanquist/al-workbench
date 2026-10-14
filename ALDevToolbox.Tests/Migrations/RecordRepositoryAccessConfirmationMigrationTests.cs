using ALDevToolbox.Data.Migrations;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migrations;

/// <summary>
/// Pins the deploy default of the RecordRepositoryAccessConfirmation migration: every
/// repository already on a solution counts as confirmed, so builds keep working, with
/// nobody recorded as having added or confirmed it. The fixture already ran the
/// migration against an empty database, so this seeds rows as they were before it and
/// replays <see cref="RecordRepositoryAccessConfirmation.BackfillSql"/>.
/// </summary>
public sealed class RecordRepositoryAccessConfirmationMigrationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Existing_repositories_are_confirmed_by_nobody_and_a_confirmed_one_keeps_its_time()
    {
        var earlier = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        int legacyId, confirmedId;
        await using (var seed = _db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var legacy = new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId, Provider = RepositoryProvider.AzureDevOps,
                Url = "https://dev.azure.com/cronus/bc/_git/extensions", DisplayName = "extensions",
            };
            var confirmed = new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId, Provider = RepositoryProvider.GitHub,
                Url = "https://github.com/cronus-dk/base-app", DisplayName = "base-app", AccessConfirmedAt = earlier,
            };
            project.Repositories.Add(legacy);
            project.Repositories.Add(confirmed);
            seed.OeProjects.Add(project);
            await seed.SaveChangesAsync();
            (legacyId, confirmedId) = (legacy.Id, confirmed.Id);
        }

        await using (var run = _db.NewContext())
        {
            await run.Database.ExecuteSqlRawAsync(RecordRepositoryAccessConfirmation.BackfillSql);
        }

        await using var read = _db.NewContext();
        var rows = await read.OeProjectRepositories.AsNoTracking().ToDictionaryAsync(r => r.Id);
        rows[legacyId].AccessConfirmedAt.Should().NotBeNull();
        rows[legacyId].AccessConfirmedByUserId.Should().BeNull();
        rows[legacyId].AddedByUserId.Should().BeNull();
        rows[confirmedId].AccessConfirmedAt.Should().Be(earlier);
    }
}
