using ALDevToolbox.Domain.Entities;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Pins the two shortcuts the suite takes for speed (#1552): a seeded template
/// built once and cloned per test, and cheap password hashes the real sign-in
/// still accepts.
/// </summary>
public sealed class TestDbSeedTests
{
    private static int _seedRuns;

    private static async Task<int> SeedOneOrganizationAsync(TestDb db)
    {
        Interlocked.Increment(ref _seedRuns);
        await using var ctx = db.NewContext();
        var org = new Organization { Name = "Seeded", Slug = "seeded-template-org", CreatedAt = DateTime.UtcNow };
        ctx.Organizations.Add(org);
        await ctx.SaveChangesAsync();
        return org.Id;
    }

    [Fact]
    public async Task A_seed_runs_once_and_every_clone_starts_from_its_rows_alone()
    {
        const string key = "test-db-seed-tests";
        using var first = TestDb.FromSeed(key, SeedOneOrganizationAsync, out var firstId);
        using var second = TestDb.FromSeed(key, SeedOneOrganizationAsync, out var secondId);

        _seedRuns.Should().Be(1);
        secondId.Should().Be(firstId, "ids survive the clone");

        await using (var ctx = first.NewContext())
        {
            (await ctx.Organizations.AsNoTracking().AnyAsync(o => o.Id == firstId)).Should().BeTrue();
            ctx.Organizations.Add(new Organization { Name = "Only in first", Slug = "only-in-first", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = second.NewContext())
        {
            (await ctx.Organizations.AsNoTracking().AnyAsync(o => o.Id == secondId)).Should().BeTrue();
            (await ctx.Organizations.AsNoTracking().AnyAsync(o => o.Slug == "only-in-first")).Should().BeFalse(
                "each fixture is its own clone");
            // The sequence came across with the rows, so a new insert gets a free id.
            ctx.Organizations.Add(new Organization { Name = "New", Slug = "new-after-clone", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task A_plain_fixture_does_not_see_a_seeded_templates_rows()
    {
        using var seeded = TestDb.FromSeed("test-db-seed-tests", SeedOneOrganizationAsync, out _);
        using var plain = new TestDb();

        await using var ctx = plain.NewContext();
        (await ctx.Organizations.AsNoTracking().AnyAsync(o => o.Slug == "seeded-template-org")).Should().BeFalse();
    }

    [Fact]
    public void A_seed_that_fails_is_tried_again_by_the_next_caller()
    {
        const string key = "test-db-seed-tests-failing";
        var attempts = 0;
        Task<int> FailOnce(TestDb db)
        {
            if (++attempts == 1) throw new InvalidOperationException("first seed fails");
            return Task.FromResult(42);
        }

        var first = () => TestDb.FromSeed(key, FailOnce, out _).Dispose();
        first.Should().Throw<InvalidOperationException>();

        using var second = TestDb.FromSeed(key, FailOnce, out var value);
        value.Should().Be(42);
        attempts.Should().Be(2);
    }

    /// <summary>
    /// The app's own hashes stay at the production work factor; only the test
    /// seeding takes the shortcut.
    /// </summary>
    [Fact]
    public void The_app_hashes_passwords_at_the_production_work_factor()
    {
        using var db = new TestDb();
        using var ctx = db.NewContext();
        var hash = new ALDevToolbox.Services.Account.AuthService(
                ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<ALDevToolbox.Services.Account.AuthService>.Instance, TimeProvider.System)
            .HashPassword("correct-horse-battery");

        hash.Should().StartWith("$2a$12$");
    }

    [Fact]
    public void A_test_password_hash_is_cheap_and_verifies_only_its_password()
    {
        var hash = TestPasswords.Hash("correct-horse-battery");

        hash.Should().StartWith("$2a$04$");
        TestPasswords.Hash("correct-horse-battery").Should().BeSameAs(hash, "hashes are cached per password");
        BCrypt.Net.BCrypt.Verify("correct-horse-battery", hash).Should().BeTrue();
        BCrypt.Net.BCrypt.Verify("wrong-horse-battery", hash).Should().BeFalse();
    }
}
