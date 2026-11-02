using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Which credentials a manual build clones with, and in what order: the
/// connected GitHub account before the stored build token, the token alone
/// when the account is not connected, Azure DevOps on its token only, and a
/// plain "nothing" when the person has neither. See
/// <c>.design/github-integration.md</c>, "#624 Solution repositories".
/// </summary>
public sealed class CloneCredentialResolverTests : IDisposable
{
    private const int UserId = 624;
    private const string LinkedToken = "ghu_linked";
    private const string GitHubPat = "ghp_pasted";
    private const string AzurePat = "azdo_pasted";

    private readonly TestDb _db = new();

    public CloneCredentialResolverTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "Dev Eloper",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_connected_github_account_comes_first_and_the_build_token_second()
    {
        await LinkGitHubAsync();
        await StoreTokenAsync(RepositoryProvider.GitHub, GitHubPat);
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx).ResolveAsync(RepositoryProvider.GitHub);

        credentials.Select(c => c.Secret).Should().Equal(LinkedToken, GitHubPat);
        credentials[0].Source.Should().Be(CloneCredentialResolver.ConnectedAccountSource);
        credentials[1].Source.Should().Be(CloneCredentialResolver.BuildTokenSource);
    }

    [Fact]
    public async Task With_no_connected_account_the_build_token_is_the_only_credential()
    {
        await StoreTokenAsync(RepositoryProvider.GitHub, GitHubPat);
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx).ResolveAsync(RepositoryProvider.GitHub);

        credentials.Should().ContainSingle().Which.Secret.Should().Be(GitHubPat);
    }

    [Fact]
    public async Task With_no_build_token_the_connected_account_is_enough()
    {
        // The point of the change: a person who connected their GitHub account
        // has nothing to paste.
        await LinkGitHubAsync();
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx).ResolveAsync(RepositoryProvider.GitHub);

        credentials.Should().ContainSingle().Which.Secret.Should().Be(LinkedToken);
    }

    [Fact]
    public async Task Azure_devops_never_uses_the_github_account()
    {
        await LinkGitHubAsync();
        await StoreTokenAsync(RepositoryProvider.AzureDevOps, AzurePat);
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx).ResolveAsync(RepositoryProvider.AzureDevOps);

        credentials.Should().ContainSingle().Which.Secret.Should().Be(AzurePat);
    }

    [Fact]
    public async Task With_neither_the_answer_is_empty_and_the_message_names_both_routes()
    {
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx).ResolveAsync(RepositoryProvider.GitHub);

        credentials.Should().BeEmpty();
        // Two ways out for GitHub, one for Azure DevOps, and where both are.
        CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.GitHub)
            .Should().Contain("Connect").And.Contain("token").And.Contain("Repository access");
        CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.AzureDevOps)
            .Should().Contain("Azure DevOps").And.Contain("Repository access").And.NotContain("Connect");
    }

    [Fact]
    public async Task A_connected_account_that_cannot_be_renewed_falls_through_to_the_build_token()
    {
        // The access token has expired and GitHub is not answering the refresh:
        // the account is unwell, the build token is not, and the clone must not
        // lose the credential that used to be its only one.
        await LinkGitHubAsync(expiresInSeconds: 1);
        await StoreTokenAsync(RepositoryProvider.GitHub, GitHubPat);
        var later = new ALDevToolbox.Tests.Auth.FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.ServiceUnavailable);
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx, api, later).ResolveAsync(RepositoryProvider.GitHub);

        credentials.Should().ContainSingle().Which.Secret.Should().Be(GitHubPat);
    }

    [Fact]
    public async Task Outside_a_user_scope_there_is_nothing_to_clone_with()
    {
        await LinkGitHubAsync();
        await StoreTokenAsync(RepositoryProvider.GitHub, GitHubPat);
        _db.OrgContext.CurrentUserId = null;
        await using var ctx = _db.NewContext();

        var credentials = await NewResolver(ctx).ResolveAsync(RepositoryProvider.GitHub);

        credentials.Should().BeEmpty("a clone with no user has no personal credential, whatever is stored");
    }

    // --- helpers ------------------------------------------------------------

    private CloneCredentialResolver NewResolver(AppDbContext ctx, FakeGitHubApi? api = null, TimeProvider? clock = null)
    {
        var client = _db.NewGitHubAppClient(ctx, api ?? new FakeGitHubApi(), clock);
        var access = _db.NewGitHubAccessService(ctx, client, clock);
        var tokens = new UserRepositoryTokenService(
            ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider);
        return new CloneCredentialResolver(tokens, access, _db.OrgContext, NullLogger<CloneCredentialResolver>.Instance);
    }

    private async Task StoreTokenAsync(RepositoryProvider provider, string token)
    {
        await using var ctx = _db.NewContext();
        var tokens = new UserRepositoryTokenService(
            ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider);
        await tokens.SaveTokenAsync(provider, token, clear: false);
    }

    /// <summary>Connects the acting user's GitHub account the way the Account page does.</summary>
    private async Task LinkGitHubAsync(int expiresInSeconds = 28800)
    {
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: TestKeys.GitHubAppPem, ClearPrivateKey: false));

        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK,
                FakeGitHubApi.TokenJson(LinkedToken, expiresIn: expiresInSeconds))
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson(4711, "cronus-dev"));
        await using var ctx = _db.NewContext();
        await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
    }
}
