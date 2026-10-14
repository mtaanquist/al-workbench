using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Startup;

/// <summary>
/// The bootstrap admin runs on a fresh database only (#1279). If it ran on a
/// database with users, a <c>BOOTSTRAP_ADMIN_*</c> variable left set would add
/// or reset an admin on every restart.
/// </summary>
public sealed class BootstrapAdminTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();

    public BootstrapAdminTests()
    {
        // Startup has no request organisation; the probe reads across it.
        _db.OrgContext.CurrentOrganizationId = null;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_fresh_database_gets_exactly_one_site_admin()
    {
        bool hadUsers;
        await using (var ctx = _db.NewContext())
            hadUsers = await RunAsync(ctx, "  Operator@CRONUS.example ", Password);

        hadUsers.Should().BeFalse();
        await using var read = _db.NewContext();
        var user = (await read.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync()).Should().ContainSingle().Subject;
        user.Email.Should().Be("operator@cronus.example");
        user.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        user.IsSiteAdmin.Should().BeTrue();
        user.Role.Should().Be(UserRole.Admin);
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task A_database_with_any_user_is_left_exactly_as_it_was()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.OtherOrgId, Email = "someone@fabrikam.example", DisplayName = "Someone",
                PasswordHash = "existing-hash", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        bool hadUsers;
        await using (var ctx = _db.NewContext())
            hadUsers = await RunAsync(ctx, "operator@cronus.example", Password);

        hadUsers.Should().BeTrue();
        await using var read = _db.NewContext();
        var users = await read.Users.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        users.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Email = "someone@fabrikam.example",
            PasswordHash = "existing-hash",
            IsSiteAdmin = false,
        });
    }

    [Fact]
    public async Task A_fresh_database_without_both_values_gets_no_admin()
    {
        await using (var ctx = _db.NewContext())
            await RunAsync(ctx, "operator@cronus.example", "  ");

        await using var read = _db.NewContext();
        (await read.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    private static Task<bool> RunAsync(AppDbContext ctx, string? email, string? password) =>
        StartupTasks.EnsureBootstrapAdminAsync(
            ctx, new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System), TestDb.DefaultOrgId,
            email, password, NullLogger.Instance, CancellationToken.None);
}
