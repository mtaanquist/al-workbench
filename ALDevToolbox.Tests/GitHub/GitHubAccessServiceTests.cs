using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The per-user GitHub account link (issue #621): what linking stores, how the
/// token pair is refreshed under the caller, and the three access questions
/// every later feature asks.
///
/// <para>GitHub itself is stood in for by <see cref="FakeGitHubApi"/> - see its
/// note on why, and on which of these behaviours are GitHub's documented shapes
/// rather than observed ones.</para>
/// </summary>
public sealed class GitHubAccessServiceTests : IDisposable
{
    private const int UserId = 501;
    private const int ColleagueId = 502;

    private readonly TestDb _db = new();

    public GitHubAccessServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.AddRange(
            NewUser(UserId, "dev@cronus.example"),
            NewUser(ColleagueId, "colleague@cronus.example"));
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        DisplayName = email,
        PasswordHash = "x",
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// A deployment with an app registration complete enough for the OAuth half
    /// (client id and secret), which is what the link flow needs on top of #620's
    /// app id and private key.
    /// </summary>
    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    /// <summary>Connects the organisation to a GitHub organisation without going through the guarded ConnectAsync.</summary>
    private async Task ConnectOrganisationAsync(string login = "cronus-dk")
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = 42,
            GitHubOrgLogin = login,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private (GitHubAccessService Service, AppDbContext Context) NewService(
        FakeGitHubApi api, TimeProvider? clock = null)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api, clock);
        return (_db.NewGitHubAccessService(ctx, client, clock), ctx);
    }

    /// <summary>The happy-path link: a code exchange, then GitHub naming the account.</summary>
    private static FakeGitHubApi LinkableApi(long githubUserId = 4711, string login = "cronus-dev") =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson(githubUserId, login));

    // --- Linking ------------------------------------------------------------

    [Fact]
    public async Task Nobody_is_linked_to_start_with()
    {
        var (service, ctx) = NewService(new FakeGitHubApi());
        await using var _ = ctx;

        var status = await service.GetLinkStatusAsync();

        status.IsLinked.Should().BeFalse();
        status.Login.Should().BeNull();
        status.NeedsRelink.Should().BeFalse();
    }

    [Fact]
    public async Task Linking_stores_the_github_user_id_and_login()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        await service.LinkAsync("the-code");

        var status = await service.GetLinkStatusAsync();
        status.IsLinked.Should().BeTrue();
        status.GitHubUserId.Should().Be(4711, "the numeric id is stable and the login is not");
        status.Login.Should().Be("cronus-dev");
        status.NeedsRelink.Should().BeFalse();
    }

    [Fact]
    public async Task Linking_stamps_the_row_as_a_github_link_with_the_constant_issuer()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        await service.LinkAsync("the-code");

        await using var read = _db.NewContext();
        var row = await read.UserExternalLogins.AsNoTracking().SingleAsync();
        row.Provider.Should().Be("github");
        row.Issuer.Should().Be("github.com", "the unique index keeps its (provider, issuer, subject) shape");
        row.Subject.Should().Be("4711");
    }

    [Fact]
    public async Task Linking_encrypts_both_halves_of_the_token_pair()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        await service.LinkAsync("the-code");

        await using var read = _db.NewContext();
        var row = await read.UserExternalLogins.AsNoTracking().SingleAsync();
        row.AccessTokenEncrypted.Should().NotBeNullOrEmpty().And.NotContain("ghu_access");
        row.RefreshTokenEncrypted.Should().NotBeNullOrEmpty().And.NotContain("ghr_refresh");
        row.AccessTokenExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Linking_records_membership_of_the_connected_organisation()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "orgs/cronus-dk/members/cronus-dev", HttpStatusCode.NoContent);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.LinkAsync("the-code");

        (await service.GetLinkStatusAsync()).IsOrgMember.Should().BeTrue();
    }

    [Fact]
    public async Task Linking_records_a_non_member_as_a_definite_no()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "orgs/cronus-dk/members/", HttpStatusCode.Found);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.LinkAsync("the-code");

        (await service.GetLinkStatusAsync()).IsOrgMember
            .Should().BeFalse("GitHub answers 302 when the asker is not in the organisation at all");
    }

    [Fact]
    public async Task Linking_leaves_membership_unknown_when_no_organisation_is_connected()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        await service.LinkAsync("the-code");

        (await service.GetLinkStatusAsync()).IsOrgMember
            .Should().BeNull("'we never asked' is not the same as 'no', and the row says so");
    }

    [Fact]
    public async Task Linking_again_replaces_the_link_rather_than_adding_a_second()
    {
        await ConfigureDeploymentAsync();
        var (first, ctx1) = NewService(LinkableApi());
        await using (ctx1) await first.LinkAsync("code-one");

        var (second, ctx2) = NewService(LinkableApi(4711, "renamed-dev"));
        await using (ctx2) await second.LinkAsync("code-two");

        await using var read = _db.NewContext();
        (await read.UserExternalLogins.AsNoTracking().CountAsync()).Should().Be(1);
        (await read.UserExternalLogins.AsNoTracking().SingleAsync()).DisplayIdentity.Should().Be("renamed-dev");
    }

    [Fact]
    public async Task Linking_a_github_account_a_colleague_already_uses_is_refused()
    {
        await ConfigureDeploymentAsync();
        await using (var seed = _db.NewContext())
        {
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = ColleagueId,
                Provider = "github",
                Issuer = "github.com",
                Subject = "4711",
                DisplayIdentity = "cronus-dev",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        Func<Task> act = () => service.LinkAsync("the-code");

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubLink");
    }

    [Fact]
    public async Task Linking_a_github_account_someone_in_another_organisation_uses_is_refused_not_a_500()
    {
        await ConfigureDeploymentAsync();
        SeedStrangerInAnotherOrganisation();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        Func<Task> act = () => service.LinkAsync("the-code");

        // The unique index is deployment-wide and the pre-check runs inside the
        // tenant filter, so this clash can only be met at the save - and the
        // callback has already spent its one-shot state by then, which is what
        // turns a 500 here into "did not finish in time" on the retry too.
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubLink");
        // Nothing about the other organisation reaches the person reading it.
        ex.Which.Errors["GitHubLink"].Should().NotContain("stranger@example.com").And.NotContain("organisation");

        await using var read = _db.NewContext();
        (await read.UserExternalLogins.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.UserId == UserId)).Should().Be(0, "nothing was written for the refused link");
    }

    [Fact]
    public async Task Relinking_onto_a_github_account_another_organisation_holds_is_refused_not_a_500()
    {
        await ConfigureDeploymentAsync();
        SeedStrangerInAnotherOrganisation();
        // This user already has a link to a different GitHub account, so the
        // save is an update onto the taken subject rather than an insert.
        var (first, ctx1) = NewService(LinkableApi(1234, "someone-else"));
        await using (ctx1) await first.LinkAsync("code-one");

        var (second, ctx2) = NewService(LinkableApi());
        await using var _ = ctx2;
        Func<Task> act = () => second.LinkAsync("code-two");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GitHubLink");

        await using var read = _db.NewContext();
        var row = await read.UserExternalLogins.AsNoTracking().SingleAsync(l => l.UserId == UserId);
        row.Subject.Should().Be("1234", "the link they had is still the link they have");
        row.DisplayIdentity.Should().Be("someone-else");
    }

    /// <summary>
    /// Gives GitHub account 4711 to a user in another organisation, which the
    /// tenant filter hides from every read the linking flow makes.
    /// </summary>
    private void SeedStrangerInAnotherOrganisation()
    {
        using var ctx = _db.NewContext();
        var stranger = NewUser(9001, "stranger@example.com");
        stranger.OrganizationId = TestDb.OtherOrgId;
        ctx.Users.Add(stranger);
        ctx.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = stranger.Id,
            Provider = GitHubAccessService.ProviderName,
            Issuer = GitHubAccessService.IssuerValue,
            Subject = "4711",
            DisplayIdentity = "cronus-dev",
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Linking_without_an_oauth_client_on_the_deployment_says_so()
    {
        // App id and private key only - the #620 half. The link flow needs the
        // OAuth client id and secret on top, and the failure names the setup
        // rather than GitHub.
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: null,
            ClientSecret: null, ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;

        Func<Task> act = () => service.LinkAsync("the-code");

        await act.Should().ThrowAsync<GitHubAppNotConfiguredException>();
    }

    [Fact]
    public async Task A_stale_authorisation_code_surfaces_as_githubs_own_words()
    {
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi().On(HttpMethod.Post, "login/oauth/access_token",
            HttpStatusCode.OK, "{\"error\":\"bad_verification_code\",\"error_description\":\"The code passed is incorrect or expired.\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        Func<Task> act = () => service.LinkAsync("stale");

        // GitHub answers 200 with an error field rather than an error status,
        // which would otherwise read as a success with no token.
        (await act.Should().ThrowAsync<GitHubApiException>())
            .Which.Message.Should().Contain("incorrect or expired");
    }

    [Fact]
    public async Task Unlinking_removes_the_row()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        await service.UnlinkAsync();

        (await service.GetLinkStatusAsync()).IsLinked.Should().BeFalse();
        await using var read = _db.NewContext();
        (await read.UserExternalLogins.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unlinking_when_nothing_is_linked_is_a_no_op()
    {
        var (service, ctx) = NewService(new FakeGitHubApi());
        await using var _ = ctx;

        Func<Task> act = () => service.UnlinkAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Unlinking_leaves_a_colleagues_link_alone()
    {
        await ConfigureDeploymentAsync();
        await using (var seed = _db.NewContext())
        {
            seed.UserExternalLogins.Add(new UserExternalLogin
            {
                UserId = ColleagueId,
                Provider = "github",
                Issuer = "github.com",
                Subject = "9999",
                DisplayIdentity = "someone-else",
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        await service.UnlinkAsync();

        await using var read = _db.NewContext();
        (await read.UserExternalLogins.AsNoTracking().SingleAsync()).UserId.Should().Be(ColleagueId);
    }

    // --- Token refresh ------------------------------------------------------

    [Fact]
    public async Task An_expiring_token_is_refreshed_and_the_new_pair_written_back()
    {
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = new FakeGitHubApi()
            .OnSequence(HttpMethod.Post, "login/oauth/access_token",
                (HttpStatusCode.OK, FakeGitHubApi.TokenJson("ghu_first", refreshToken: "ghr_first")),
                (HttpStatusCode.OK, FakeGitHubApi.TokenJson("ghu_second", refreshToken: "ghr_second")))
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson())
            .On(HttpMethod.Get, "repos/cronus-dk/app", HttpStatusCode.OK, "{}");
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        // Eight hours on, the stored access token is past its 5-minute margin.
        clock.Advance(TimeSpan.FromHours(8));
        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeTrue();

        api.OAuthBodies.Should().HaveCount(2);
        api.OAuthBodies[1].Should().Contain("grant_type=refresh_token").And.Contain("ghr_first");
        await using var read = _db.NewContext();
        var row = await read.UserExternalLogins.AsNoTracking().SingleAsync();
        row.AccessTokenExpiresAt.Should().BeAfter(clock.GetUtcNow().UtcDateTime,
            "the rotated pair has to be persisted or the next call refreshes a spent token");
    }

    [Fact]
    public async Task Two_callers_refreshing_one_link_at_once_spend_the_refresh_token_once()
    {
        // The nightly next minor and next major checks start side by side as the same
        // person, long after the access token lapsed. GitHub rotates the refresh token
        // on use, so whichever refreshes second with the same token is refused, and that
        // build had nothing to clone with.
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var spent = 0;
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!body.Contains("grant_type=refresh_token"))
                {
                    return (HttpStatusCode.OK, FakeGitHubApi.TokenJson("ghu_first", refreshToken: "ghr_first"));
                }
                return body.Contains("ghr_first") && Interlocked.Increment(ref spent) == 1
                    ? (HttpStatusCode.OK, FakeGitHubApi.TokenJson("ghu_second", refreshToken: "ghr_second"))
                    : (HttpStatusCode.BadRequest, "{\"error\":\"bad_refresh_token\"}");
            })
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        var (first, firstCtx) = NewService(api, clock);
        await using var _ = firstCtx;
        await first.LinkAsync("the-code");
        var (second, secondCtx) = NewService(api, clock);
        await using var __ = secondCtx;

        clock.Advance(TimeSpan.FromHours(8));
        // Hold the first refresh at GitHub, and let it go only once the second caller has
        // read the same expired row and is queued behind the first. Waiting for those two
        // facts rather than for a while (#1439): a second caller that came late would read
        // the refreshed row and pass without any lock at all.
        var gate = api.PauseUntilReleased(HttpMethod.Post, "login/oauth/access_token");
        var firstToken = first.ResolveUserTokenAsync(UserId, TestContext.Current.CancellationToken);
        await api.HeldAt(HttpMethod.Post, "login/oauth/access_token")
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var secondToken = second.ResolveUserTokenAsync(UserId, TestContext.Current.CancellationToken);
        await UntilSomeoneWaitsForTheRefreshLockAsync();
        gate.SetResult();

        (await firstToken).Should().Be("ghu_second");
        (await secondToken).Should().Be("ghu_second", "the second caller waits and uses the pair the first stored");
        api.OAuthBodies.Count(b => b.Contains("grant_type=refresh_token")).Should().Be(1);
    }

    /// <summary>
    /// Polls Postgres until a session in this test's database is queued for the per-user
    /// refresh lock: the advisory lock the service takes is two keys, the namespace "GHRT"
    /// and the user id.
    /// </summary>
    private async Task UntilSomeoneWaitsForTheRefreshLockAsync()
    {
        const long refreshLockClass = 0x47_48_52_54;
        const long userId = UserId;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var ctx = _db.NewContext();
            var waiting = await ctx.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted AND objsubid = 2
                  AND classid::bigint = {refreshLockClass} AND objid::bigint = {userId}
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                """).SingleAsync(TestContext.Current.CancellationToken);
            if (waiting > 0) return;
            DateTime.UtcNow.Should().BeBefore(deadline, "the second caller has to queue for the lock the first one holds");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_token_still_inside_its_lifetime_is_not_refreshed()
    {
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = LinkableApi().On(HttpMethod.Get, "repos/cronus-dk/app", HttpStatusCode.OK, "{}");
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        clock.Advance(TimeSpan.FromHours(1));
        await service.CanAccessRepoAsync(UserId, "cronus-dk/app");

        api.OAuthBodies.Should().HaveCount(1, "only the original code exchange");
    }

    [Fact]
    public async Task An_app_that_does_not_expire_user_tokens_stores_no_expiry_and_never_refreshes()
    {
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK,
                FakeGitHubApi.TokenJson(expiresIn: null, refreshToken: null, refreshExpiresIn: null))
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson())
            .On(HttpMethod.Get, "repos/cronus-dk/app", HttpStatusCode.OK, "{}");
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        clock.Advance(TimeSpan.FromDays(30));

        (await service.GetLinkStatusAsync()).NeedsRelink.Should().BeFalse();
        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeTrue();
        api.OAuthBodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_expired_link_that_cannot_be_refreshed_asks_the_user_to_link_again()
    {
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = new FakeGitHubApi()
            .OnSequence(HttpMethod.Post, "login/oauth/access_token",
                (HttpStatusCode.OK, FakeGitHubApi.TokenJson()),
                (HttpStatusCode.BadRequest, "{\"message\":\"refresh token expired\"}"))
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        clock.Advance(TimeSpan.FromDays(200));

        (await service.ResolveUserTokenAsync(UserId)).Should().BeNull();
        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app"))
            .Should().BeFalse("a link we cannot use is not permission to see anything");
    }

    // --- Access questions ---------------------------------------------------

    [Fact]
    public async Task A_repository_the_user_cannot_see_answers_404_and_that_means_no_access()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "repos/cronus-dk/secret", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        // GitHub answers 404 rather than 403 for a repository you cannot see;
        // treating it as "gone" would be the wrong conclusion entirely.
        (await service.CanAccessRepoAsync(UserId, "cronus-dk/secret")).Should().BeFalse();
    }

    [Fact]
    public async Task A_renamed_repository_still_counts_as_visible()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "repos/cronus-dk/old-name", HttpStatusCode.MovedPermanently);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAccessRepoAsync(UserId, "cronus-dk/old-name")).Should().BeTrue();
    }

    [Fact]
    public async Task An_unlinked_user_can_see_nothing()
    {
        var (service, ctx) = NewService(new FakeGitHubApi());
        await using var _ = ctx;

        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeFalse();
        (await service.IsOrgMemberAsync(UserId)).Should().BeFalse();
        (await service.FilterAccessibleAsync(UserId, ["cronus-dk/app"])).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    [InlineData("too/many/parts")]
    public async Task A_name_that_is_not_owner_slash_repo_is_refused_without_asking_github(string name)
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");
        var callsBefore = api.Calls.Count;

        (await service.CanAccessRepoAsync(UserId, name)).Should().BeFalse();
        api.Calls.Count.Should().Be(callsBefore);
    }

    [Fact]
    public async Task Filtering_keeps_only_what_the_user_can_see_and_keeps_the_order()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "repos/cronus-dk/alpha", HttpStatusCode.OK, "{}")
            .On(HttpMethod.Get, "repos/cronus-dk/beta", HttpStatusCode.NotFound, "{}")
            .On(HttpMethod.Get, "repos/cronus-dk/gamma", HttpStatusCode.OK, "{}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        var visible = await service.FilterAccessibleAsync(
            UserId, ["cronus-dk/alpha", "cronus-dk/beta", "cronus-dk/gamma"]);

        visible.Should().Equal("cronus-dk/alpha", "cronus-dk/gamma");
    }

    /// <summary>
    /// #1340: the per-repository burst is what trips GitHub's secondary rate limit,
    /// so once one answer comes back rate limited the rest are not asked.
    /// </summary>
    [Fact]
    public async Task Filtering_stops_asking_after_github_rate_limits_an_answer()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "repos/cronus-dk/alpha", HttpStatusCode.OK, "{}")
            .OnWithHeaders(HttpMethod.Get, "repos/cronus-dk/beta", _ => (
                HttpStatusCode.Forbidden,
                "{\"message\":\"You have exceeded a secondary rate limit.\"}",
                [("retry-after", "60"), ("x-ratelimit-remaining", "4987")]))
            .On(HttpMethod.Get, "repos/cronus-dk/gamma", HttpStatusCode.OK, "{}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        var visible = await service.FilterAccessibleAsync(
            UserId, ["cronus-dk/alpha", "cronus-dk/beta", "cronus-dk/gamma"]);

        visible.Should().Equal("cronus-dk/alpha");
        api.Calls.Should().NotContain(c => c.Contains("repos/cronus-dk/gamma"));
    }

    [Fact]
    public async Task Asking_the_same_question_twice_in_one_burst_only_asks_github_once()
    {
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = LinkableApi().On(HttpMethod.Get, "repos/cronus-dk/app", HttpStatusCode.OK, "{}");
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        await service.CanAccessRepoAsync(UserId, "cronus-dk/app");
        await service.CanAccessRepoAsync(UserId, "cronus-dk/app");

        api.Calls.Count(c => c.Contains("repos/cronus-dk/app")).Should().Be(1);
    }

    [Fact]
    public async Task A_permission_revoked_on_github_takes_effect_without_a_restart()
    {
        await ConfigureDeploymentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = LinkableApi()
            .OnSequence(HttpMethod.Get, "repos/cronus-dk/app",
                (HttpStatusCode.OK, "{}"),
                (HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");
        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeTrue();

        // A Blazor circuit outlives a page load, so the remembered answer has to
        // lapse on its own rather than on the next scope.
        clock.Advance(TimeSpan.FromMinutes(1));

        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeFalse();
    }

    [Fact]
    public async Task Membership_is_false_when_the_organisation_has_connected_nothing()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(LinkableApi());
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.IsOrgMemberAsync(UserId)).Should().BeFalse();
    }

    [Fact]
    public async Task Membership_refreshes_the_stored_answer_when_it_changes()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = LinkableApi()
            .OnSequence(HttpMethod.Get, "orgs/cronus-dk/members/",
                (HttpStatusCode.NotFound, "{}"),
                (HttpStatusCode.NoContent, null));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");
        (await service.GetLinkStatusAsync()).IsOrgMember.Should().BeFalse();

        (await service.IsOrgMemberAsync(UserId)).Should().BeTrue();

        await using var read = _db.NewContext();
        (await read.UserExternalLogins.AsNoTracking().SingleAsync()).IsOrgMember
            .Should().BeTrue("the Account row must not keep saying 'not a member' after they join");
    }

    [Fact]
    public async Task A_membership_github_would_not_answer_is_not_remembered_as_a_no()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-04T09:00:00Z"));
        var api = LinkableApi()
            .OnSequence(HttpMethod.Get, "orgs/cronus-dk/members/",
                // One at link time, one for each question below.
                (FakeGitHubApi.Unreachable, null),
                (FakeGitHubApi.Unreachable, null),
                (HttpStatusCode.NoContent, null));
        var (service, ctx) = NewService(api, clock);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.IsOrgMemberAsync(UserId))
            .Should().BeFalse("an answer we could not get is never promoted to permission");

        // No clock advance: a scope here is a whole Blazor circuit, so a single
        // timeout remembered as a definite "not a member" would keep telling
        // this person to ask a GitHub owner to add them for the rest of the day.
        (await service.IsOrgMemberAsync(UserId)).Should().BeTrue();
        api.Calls.Count(c => c.Contains("orgs/cronus-dk/members/")).Should().Be(3);
    }

    [Fact]
    public async Task A_membership_github_would_not_answer_is_unknown_rather_than_no()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "orgs/cronus-dk/members/", FakeGitHubApi.Unreachable);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        // What the Account page needs to say "we could not reach GitHub just
        // now" instead of "GitHub still does not list you".
        (await service.TryGetOrgMembershipAsync(UserId)).Should().BeNull();
    }

    [Fact]
    public async Task A_definite_membership_answer_is_reused_within_the_window()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = LinkableApi().On(HttpMethod.Get, "orgs/cronus-dk/members/", HttpStatusCode.NoContent);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.IsOrgMemberAsync(UserId)).Should().BeTrue();
        (await service.IsOrgMemberAsync(UserId)).Should().BeTrue();

        // One at link time and one for the first question: the second question
        // is what the window is for.
        api.Calls.Count(c => c.Contains("orgs/cronus-dk/members/")).Should().Be(2);
    }

    [Fact]
    public async Task A_repository_check_github_would_not_answer_is_not_remembered_as_a_no()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .OnSequence(HttpMethod.Get, "repos/cronus-dk/app",
                (FakeGitHubApi.Unreachable, null),
                (HttpStatusCode.OK, "{}"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeFalse();
        (await service.CanAccessRepoAsync(UserId, "cronus-dk/app")).Should().BeTrue();
    }

    // --- The installation-claim gate ---------------------------------------

    [Fact]
    public async Task An_unlinked_user_cannot_claim_any_installation()
    {
        var (service, ctx) = NewService(new FakeGitHubApi());
        await using var _ = ctx;

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.NotLinked);
    }

    [Fact]
    public async Task An_installation_on_an_organisation_the_user_owns_is_confirmed()
    {
        await ConfigureDeploymentAsync();
        var api = ClaimableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.Confirmed);
        // Reaching the installation is not the answer on its own, so the role
        // has to have been asked for.
        api.Calls.Should().Contain(c => c.Contains("user/memberships/orgs/cronus-dk"));
    }

    [Fact]
    public async Task An_outside_collaborator_on_one_repository_cannot_claim_the_installation()
    {
        await ConfigureDeploymentAsync();
        // GET /user/installations lists every installation covering a repository
        // this person can reach - including one they are only an outside
        // collaborator on. Being in the list is reach, not authority.
        var api = ClaimableApi(role: "member");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.NotTheirs,
                "otherwise a collaborator could connect somebody else's GitHub organisation and mint its installation token");
    }

    [Fact]
    public async Task An_invitation_nobody_has_accepted_is_not_ownership()
    {
        await ConfigureDeploymentAsync();
        var api = ClaimableApi(role: "admin", state: "pending");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.NotTheirs);
    }

    [Fact]
    public async Task An_installation_on_a_personal_account_is_refused_without_asking_about_an_organisation()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK,
                FakeGitHubApi.InstallationsJson("cronus-dev", "User", 42));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.NotTheirs);
        api.Calls.Should().NotContain(c => c.Contains("user/memberships/orgs"),
            "a personal account has no owners to be one of");
    }

    [Fact]
    public async Task A_membership_github_will_not_report_is_not_a_pass()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK, FakeGitHubApi.InstallationsJson(42))
            .On(HttpMethod.Get, "user/memberships/orgs/", FakeGitHubApi.Unreachable);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.Unknown);
    }

    [Fact]
    public async Task Someone_who_is_in_no_such_organisation_at_all_is_refused()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK, FakeGitHubApi.InstallationsJson(42))
            // GitHub answers 404 for a membership that is not there, as it does
            // for anything else you cannot see.
            .On(HttpMethod.Get, "user/memberships/orgs/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.NotTheirs);
    }

    [Fact]
    public async Task An_installation_github_does_not_list_is_refused()
    {
        await ConfigureDeploymentAsync();
        var api = ClaimableApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK, FakeGitHubApi.InstallationsJson(7));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        // The whole point: installation ids are small sequential integers, so an
        // id nobody proved is theirs is somebody else's customer.
        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.NotTheirs);
    }

    [Fact]
    public async Task An_answer_github_would_not_give_is_not_a_pass()
    {
        await ConfigureDeploymentAsync();
        var api = LinkableApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.ServiceUnavailable, "{\"message\":\"unavailable\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.LinkAsync("the-code");

        (await service.CanAdministerInstallationAsync(UserId, 42))
            .Should().Be(GitHubInstallationClaim.Unknown);
    }

    /// <summary>
    /// A GitHub that answers both halves of the install gate: the installations
    /// this person can reach, and what they are in the organisation the one they
    /// are claiming sits on.
    /// </summary>
    private static FakeGitHubApi ClaimableApi(string role = "admin", string state = "active") =>
        LinkableApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK, FakeGitHubApi.InstallationsJson(7, 42))
            .On(HttpMethod.Get, "user/memberships/orgs/cronus-dk", HttpStatusCode.OK,
                FakeGitHubApi.OrgMembershipJson(role, state));
}
