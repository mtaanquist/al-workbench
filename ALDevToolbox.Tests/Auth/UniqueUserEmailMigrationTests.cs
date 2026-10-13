using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The migration that makes user emails unique deployment-wide (issue #1207)
/// has to settle addresses already held by more than one account first, or it
/// cannot create its index on a database that has them.
/// </summary>
public sealed class UniqueUserEmailMigrationTests : IDisposable
{
    private const string Previous = "20270120000000_AddStagedReleaseRepositoryForeignKey";
    private const string Target = "20270121000000_UniqueUserEmail";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Duplicates_keep_the_oldest_account_and_the_newer_ones_are_disabled_and_renamed()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var ctx = _db.NewContext())
        {
            await ctx.GetService<IMigrator>().MigrateAsync(Previous);
            ctx.Users.AddRange(
                NewUser(800, TestDb.OtherOrgId, "dup@example.com", created.AddDays(1)),
                NewUser(801, TestDb.DefaultOrgId, "dup@example.com", created),
                NewUser(802, TestDb.DefaultOrgId, "single@example.com", created));
            await ctx.SaveChangesAsync();

            await ctx.GetService<IMigrator>().MigrateAsync(Target);
        }

        await using var read = _db.NewContext();
        var users = await read.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id >= 800 && u.Id <= 802).ToDictionaryAsync(u => u.Id);
        users[801].Email.Should().Be("dup@example.com");
        users[801].Status.Should().Be(UserStatus.Active);
        users[800].Email.Should().Be("duplicate-800+dup@example.com");
        users[800].Status.Should().Be(UserStatus.Disabled);
        users[800].CredentialsChangedAt.Should().NotBeNull();
        users[802].Email.Should().Be("single@example.com");
        users[802].Status.Should().Be(UserStatus.Active);
    }

    private static User NewUser(int id, int orgId, string email, DateTime createdAt) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = "Someone",
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = createdAt,
    };
}
