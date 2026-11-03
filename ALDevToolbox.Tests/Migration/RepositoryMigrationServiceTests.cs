using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Migration;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// The Migrate repositories service (#1699): it lists a project with the
/// Admin's own Azure DevOps token, queues a preview only of repositories Azure
/// DevOps itself lists for that project, keeps one preview per person, and shows
/// a preview only to the person who started it.
/// </summary>
public sealed class RepositoryMigrationServiceTests : IDisposable
{
    private const string Project = "https://dev.azure.com/cronus/Sales";

    private const string Listing = """
        { "value": [
            { "id": "r1", "name": "cronus-base", "defaultBranch": "refs/heads/main", "size": 10 },
            { "id": "r2", "name": "cronus-payroll", "defaultBranch": "refs/heads/main", "size": 20 }
        ] }
        """;

    private readonly TestDb _db = new();
    private readonly RepositoryMigrationQueue _queue = new(TimeProvider.System);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Without_a_token_the_listing_says_where_to_add_one()
    {
        await SeedUserAsync();
        var handler = new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing);

        var act = () => NewService(_db.NewContext(), handler).ListRepositoriesAsync(Project);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors[RepositoryMigrationService.ProjectField].Should().Contain("Account → Repository access");
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://github.com/cronus/sales")]
    public async Task An_address_that_is_not_a_project_is_refused_under_the_field(string address)
    {
        await SeedUserWithTokenAsync();

        var act = () => NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing))
            .ListRepositoriesAsync(address);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey(RepositoryMigrationService.ProjectField);
    }

    [Fact]
    public async Task Lists_the_project_with_the_persons_own_token()
    {
        await SeedUserWithTokenAsync("my-ado-pat");
        var handler = new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing);

        var listing = await NewService(_db.NewContext(), handler).ListRepositoriesAsync(Project + "/_git/cronus-base");

        listing.Project.Should().Be(new AzureDevOpsProject("cronus", "Sales"));
        listing.Repositories.Select(r => r.Name).Should().Equal("cronus-base", "cronus-payroll");
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(handler.Request!.Headers.Authorization!.Parameter!))
            .Should().Be(":my-ado-pat");
    }

    [Fact]
    public async Task Azure_DevOps_refusing_is_shown_under_the_project_field()
    {
        await SeedUserWithTokenAsync();

        var act = () => NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.Unauthorized, ""))
            .ListRepositoriesAsync(Project);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors[RepositoryMigrationService.ProjectField].Should().Contain("refused your token");
    }

    [Fact]
    public async Task Starting_a_preview_queues_the_listed_repositories_for_the_person()
    {
        var userId = await SeedUserWithTokenAsync();

        var preview = await NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing))
            .StartPreviewAsync(Project, ["r2"]);

        preview.Status.Should().Be(MigrationPreviewStatus.Waiting);
        preview.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        preview.UserId.Should().Be(userId);
        preview.ProjectName.Should().Be("cronus/Sales");
        preview.Repositories.Select(r => (r.Name, r.Status)).Should().Equal(("cronus-payroll", MigrationRepositoryStatus.Waiting));

        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.Id.Should().Be(preview.Id);
        job.Identity.UserId.Should().Be(userId);
        job.Repositories.Single().CloneUrl.Should().Be("https://dev.azure.com/cronus/Sales/_git/cronus-payroll");
    }

    [Fact]
    public async Task No_repository_chosen_is_refused()
    {
        await SeedUserWithTokenAsync();

        var act = () => NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing))
            .StartPreviewAsync(Project, []);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors[RepositoryMigrationService.RepositoriesField].Should().Contain("at least one");
    }

    [Fact]
    public async Task A_repository_that_is_not_in_the_project_is_refused()
    {
        await SeedUserWithTokenAsync();

        var act = () => NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing))
            .StartPreviewAsync(Project, ["r1", "somewhere-else"]);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors[RepositoryMigrationService.RepositoriesField].Should().Contain("no longer in this project");
        _queue.Reader.TryRead(out _).Should().BeFalse("nothing is queued");
    }

    [Fact]
    public async Task One_preview_per_person_at_a_time()
    {
        await SeedUserWithTokenAsync();
        var svc = NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing));
        var first = await svc.StartPreviewAsync(Project, ["r1"]);

        var act = () => svc.StartPreviewAsync(Project, ["r2"]);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors[RepositoryMigrationService.RepositoriesField].Should().Contain("still running");

        _queue.MarkDone(first.Id, new MigrationSourceAnalysis([], []));
        (await svc.StartPreviewAsync(Project, ["r2"])).Id.Should().NotBe(first.Id, "a finished preview no longer holds the slot");
    }

    [Fact]
    public async Task A_preview_is_only_shown_to_the_person_who_started_it()
    {
        var owner = await SeedUserWithTokenAsync();
        var svc = NewService(_db.NewContext(), new AzureDevOpsClientTests.StubHandler(HttpStatusCode.OK, Listing));
        var preview = await svc.StartPreviewAsync(Project, ["r1"]);

        svc.GetPreview(preview.Id).Should().NotBeNull();

        _db.OrgContext.CurrentUserId = await SeedUserAsync();
        svc.GetPreview(preview.Id).Should().BeNull("a colleague in the same organisation does not see it");

        _db.OrgContext.CurrentUserId = owner;
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        svc.GetPreview(preview.Id).Should().BeNull("nor does the same id in another organisation");
    }

    private RepositoryMigrationService NewService(AppDbContext ctx, HttpMessageHandler handler) => new(
        AzureDevOpsClientTests.NewClient(handler),
        NewTokens(ctx),
        _queue,
        _db.OrgContext,
        NullLogger<RepositoryMigrationService>.Instance);

    private UserRepositoryTokenService NewTokens(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider);

    private async Task<int> SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            DisplayName = "Admin",
            Role = UserRole.Admin,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = user.Id;
        return user.Id;
    }

    private async Task<int> SeedUserWithTokenAsync(string token = "ado-pat")
    {
        var id = await SeedUserAsync();
        await using var ctx = _db.NewContext();
        await NewTokens(ctx).SaveTokenAsync(RepositoryProvider.AzureDevOps, token, clear: false);
        return id;
    }
}
