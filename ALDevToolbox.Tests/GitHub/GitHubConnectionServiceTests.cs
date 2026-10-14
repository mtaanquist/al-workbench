using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The per-organisation half of the GitHub App connection (issues #620 and
/// #621): what the install callback writes, what the Repositories tab reads
/// back, and the rules that keep a connection which cannot work - or which is
/// not the acting user's to make - from being stored in the first place.
/// </summary>
public sealed class GitHubConnectionServiceTests : IDisposable
{
    private const int AdminUserId = 601;

    private readonly TestDb _db = new();

    /// <summary>
    /// What GitHub says the acting admin administers. Tests that care replace
    /// the routes; the default is "the installations these tests connect".
    /// </summary>
    private FakeGitHubApi _api = InstallationsOwnedBy(42, 43, 77, 99);

    public GitHubConnectionServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = AdminUserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "admin@cronus.example",
            DisplayName = "CRONUS admin",
            PasswordHash = "x",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = AdminUserId;
        LinkAdminGitHubAccount();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A GitHub that lists <paramref name="ids"/> for the acting admin and
    /// calls them an owner of the organisation those installations sit on -
    /// which is both halves of the install gate.
    /// </summary>
    private static FakeGitHubApi InstallationsOwnedBy(params long[] ids) =>
        new FakeGitHubApi()
            .On(HttpMethod.Get, "user/installations", HttpStatusCode.OK, FakeGitHubApi.InstallationsJson(ids))
            .On(HttpMethod.Get, "user/memberships/orgs/cronus-dk", HttpStatusCode.OK,
                FakeGitHubApi.OrgMembershipJson());

    /// <summary>
    /// Gives the acting admin a GitHub link with a non-expiring token, written
    /// straight to the row so these tests do not have to replay the OAuth
    /// handshake that <see cref="GitHubAccessServiceTests"/> already covers.
    /// </summary>
    private void LinkAdminGitHubAccount()
    {
        using var ctx = _db.NewContext();
        ctx.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = AdminUserId,
            Provider = GitHubAccessService.ProviderName,
            Issuer = GitHubAccessService.IssuerValue,
            Subject = "4711",
            DisplayIdentity = "cronus-admin",
            CreatedAt = DateTime.UtcNow,
            AccessTokenEncrypted = _db.DataProtectionProvider
                .CreateProtector(GitHubAccessService.AccessTokenProtectionPurpose)
                .Protect("ghu_admin"),
        });
        ctx.SaveChanges();
    }

    /// <summary>Removes the acting admin's GitHub link, leaving nothing to prove the claim with.</summary>
    private void UnlinkAdminGitHubAccount()
    {
        using var ctx = _db.NewContext();
        ctx.UserExternalLogins.RemoveRange(ctx.UserExternalLogins.Where(l => l.UserId == AdminUserId));
        ctx.SaveChanges();
    }

    private GitHubConnectionService NewService()
    {
        var ctx = _db.NewContext();
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, _api));
        return _db.NewGitHubConnectionService(ctx, access);
    }

    private static GitHubInstallation Installation(
        long id = 42,
        string login = "cronus-dk",
        string accountType = "Organization",
        params (string Name, string Level)[] permissions) =>
        new(id, login, accountType,
            permissions.Length == 0
                ? new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "write", ["administration"] = "write" }
                : permissions.ToDictionary(p => p.Name, p => p.Level));

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: null,
            ClientSecret: null, ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    // --- Status -------------------------------------------------------------

    [Fact]
    public async Task Status_starts_out_not_connected_and_not_configured()
    {
        var status = await NewService().GetStatusAsync();

        status.DeploymentConfigured.Should().BeFalse();
        status.IsConnected.Should().BeFalse();
        status.OrgLogin.Should().BeNull();
        status.Permissions.Should().BeEmpty();
        status.CanCreateRepositories.Should().BeFalse();
    }

    [Fact]
    public async Task Status_reports_the_deployment_registration_separately_from_the_connection()
    {
        await ConfigureDeploymentAsync();

        var status = await NewService().GetStatusAsync();

        status.DeploymentConfigured.Should().BeTrue();
        status.AppSlug.Should().Be("al-workbench");
        status.IsConnected.Should().BeFalse("the deployment having an app is not the org having connected one");
    }

    // --- Connect ------------------------------------------------------------

    [Fact]
    public async Task Connect_stores_the_login_permissions_and_timestamp()
    {
        await NewService().ConnectAsync(Installation());

        var status = await NewService().GetStatusAsync();
        status.IsConnected.Should().BeTrue();
        status.InstallationId.Should().Be(42);
        status.OrgLogin.Should().Be("cronus-dk");
        status.ConnectedAt.Should().NotBeNull();
        status.Permissions.Should().ContainKey("contents").WhoseValue.Should().Be("write");
        status.CanCreateRepositories.Should().BeTrue();
    }

    [Fact]
    public async Task Connecting_does_not_confirm_a_repository_nobody_has_confirmed()
    {
        // A repository added before the organisation connected GitHub was never asked
        // about; connecting must not make it one a pull-request build clones.
        int repositoryId;
        await using (var ctx = _db.NewContext())
        {
            var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "CRONUS",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var repository = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId,
                Provider = RepositoryProvider.GitHub,
                Url = "https://github.com/cronus-dk/payroll",
                DisplayName = "payroll",
            };
            project.Repositories.Add(repository);
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
            repositoryId = repository.Id;
        }

        await NewService().ConnectAsync(Installation());

        await using var read = _db.NewContext();
        (await read.OeProjectRepositories.AsNoTracking().SingleAsync(r => r.Id == repositoryId))
            .AccessConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Connect_without_repository_creation_rights_is_stored_but_flagged()
    {
        await NewService().ConnectAsync(Installation(permissions: [("metadata", "read"), ("contents", "write")]));

        var status = await NewService().GetStatusAsync();
        status.IsConnected.Should().BeTrue("a read-only installation is still useful for the other features");
        status.CanCreateRepositories.Should().BeFalse("the tab says so before someone hits it from New Workspace");
    }

    [Fact]
    public async Task Connect_treats_a_read_level_administration_grant_as_not_enough()
    {
        await NewService().ConnectAsync(Installation(permissions: [("administration", "read")]));

        (await NewService().GetStatusAsync()).CanCreateRepositories.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_refuses_a_personal_github_account()
    {
        Func<Task> act = () => NewService().ConnectAsync(Installation(login: "some-person", accountType: "User"));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubOrgLogin");
    }

    [Fact]
    public async Task Connect_refuses_an_installation_without_an_account_login()
    {
        Func<Task> act = () => NewService().ConnectAsync(Installation(login: "   "));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubOrgLogin");
    }

    [Fact]
    public async Task Connect_refuses_an_installation_id_that_is_not_positive()
    {
        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 0));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubInstallationId");
    }

    [Fact]
    public async Task Connect_twice_replaces_the_previous_connection()
    {
        await NewService().ConnectAsync(Installation(id: 42, login: "cronus-dk"));
        await NewService().ConnectAsync(Installation(id: 77, login: "cronus-uk"));

        var status = await NewService().GetStatusAsync();
        status.InstallationId.Should().Be(77);
        status.OrgLogin.Should().Be("cronus-uk");

        await using var read = _db.NewContext();
        (await read.OrganizationSettings.AsNoTracking().CountAsync(s => s.OrganizationId == TestDb.DefaultOrgId))
            .Should().Be(1, "the connection lives on the org's single settings row");
    }

    // --- The cross-organisation claim guard ---------------------------------

    /// <summary>
    /// Seeds another workbench organisation that already holds
    /// <paramref name="installationId"/>. Writes through the tracked context
    /// directly: the query filter scopes reads, not inserts, so this needs no
    /// bypass of its own.
    /// </summary>
    private async Task SeedOtherOrgConnectionAsync(long installationId)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
        {
            OrganizationId = TestDb.OtherOrgId,
            GitHubInstallationId = installationId,
            GitHubOrgLogin = "cronus-uk",
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Connect_refuses_an_installation_another_organisation_already_holds()
    {
        // The setup callback trusts the installation_id GitHub hands back, and
        // the App JWT can read every installation of the app - so this guard is
        // what stops one tenant claiming another tenant's GitHub organisation.
        // See "Binding the installation to the acting user" in the design doc.
        await SeedOtherOrgConnectionAsync(42);

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubOrgLogin");
        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse("nothing was written");
    }

    [Fact]
    public async Task Connect_allows_the_same_organisation_to_reconnect_its_own_installation()
    {
        await NewService().ConnectAsync(Installation(id: 42));

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42, permissions: [("contents", "write")]));

        await act.Should().NotThrowAsync("the guard excludes the acting org, so re-connecting must still work");
        var status = await NewService().GetStatusAsync();
        status.InstallationId.Should().Be(42);
        status.Permissions.Should().ContainSingle();
    }

    [Fact]
    public async Task Connect_allows_a_different_installation_while_another_org_holds_one()
    {
        await SeedOtherOrgConnectionAsync(42);

        await NewService().ConnectAsync(Installation(id: 43));

        (await NewService().GetStatusAsync()).InstallationId.Should().Be(43);
    }

    // --- The installation-claim gate (issue #621) ---------------------------

    [Fact]
    public async Task Connect_refuses_an_installation_github_does_not_say_the_admin_manages()
    {
        // The App JWT is authorised for every installation of the app, and
        // installation ids are small sequential integers - so without asking the
        // admin's own credential, a hand-edited callback connects this org to
        // somebody else's GitHub organisation. See the design doc's
        // "Binding the installation to the acting user".
        _api = InstallationsOwnedBy(7);

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("GitHubInstallationId");
        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse("nothing was written");
    }

    [Fact]
    public async Task Connect_tells_an_admin_who_has_not_linked_github_what_to_do()
    {
        UnlinkAdminGitHubAccount();

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42));

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["GitHubInstallationId"].Should().Contain("Repository access",
            "the requirement has to name where to go, not just refuse");
    }

    [Fact]
    public async Task Connect_refuses_when_github_could_not_be_asked()
    {
        _api = new FakeGitHubApi().On(
            HttpMethod.Get, "user/installations", HttpStatusCode.ServiceUnavailable, "{\"message\":\"unavailable\"}");

        Func<Task> act = () => NewService().ConnectAsync(Installation(id: 42));

        await act.Should().ThrowAsync<PlanValidationException>();
        (await NewService().GetStatusAsync()).IsConnected
            .Should().BeFalse("an answer we could not get is not a pass");
    }

    // --- Refresh ------------------------------------------------------------

    [Fact]
    public async Task Refresh_updates_the_permissions_and_login_without_moving_the_connected_date()
    {
        await NewService().ConnectAsync(Installation(permissions: [("metadata", "read")]));
        var connectedAt = (await NewService().GetStatusAsync()).ConnectedAt;

        await NewService().RefreshAsync(Installation(
            login: "cronus-dk-renamed", permissions: [("metadata", "read"), ("administration", "write")]));

        var status = await NewService().GetStatusAsync();
        status.OrgLogin.Should().Be("cronus-dk-renamed");
        status.CanCreateRepositories.Should().BeTrue("widening the app's access on GitHub shows up here");
        status.ConnectedAt.Should().Be(connectedAt, "it is the same connection, so its date must not move");
    }

    [Fact]
    public async Task Refresh_does_nothing_when_the_organisation_is_not_connected()
    {
        await NewService().RefreshAsync(Installation());

        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse("refresh updates a connection, it never makes one");
    }

    [Fact]
    public async Task Refresh_ignores_an_installation_that_is_not_the_connected_one()
    {
        await NewService().ConnectAsync(Installation(id: 42, permissions: [("metadata", "read")]));

        await NewService().RefreshAsync(Installation(id: 99, login: "someone-else", permissions: [("administration", "write")]));

        var status = await NewService().GetStatusAsync();
        status.InstallationId.Should().Be(42);
        status.OrgLogin.Should().Be("cronus-dk");
        status.CanCreateRepositories.Should().BeFalse();
    }

    // --- Disconnect ---------------------------------------------------------

    [Fact]
    public async Task Disconnect_clears_every_github_column()
    {
        await NewService().ConnectAsync(Installation());

        await NewService().DisconnectAsync();

        var status = await NewService().GetStatusAsync();
        status.IsConnected.Should().BeFalse();
        status.OrgLogin.Should().BeNull();
        status.ConnectedAt.Should().BeNull();
        status.Permissions.Should().BeEmpty();

        await using var read = _db.NewContext();
        var row = await read.OrganizationSettings.AsNoTracking()
            .FirstAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        row.GitHubInstallationPermissions.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_on_an_unconnected_org_is_a_no_op()
    {
        Func<Task> act = () => NewService().DisconnectAsync();

        await act.Should().NotThrowAsync();
        (await NewService().GetStatusAsync()).IsConnected.Should().BeFalse();
    }

    // --- Tenant scope -------------------------------------------------------

    [Fact]
    public async Task Connect_writes_only_to_the_acting_organisation()
    {
        await NewService().ConnectAsync(Installation());

        await using var read = _db.NewContext();
        var others = await read.OrganizationSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OrganizationId != TestDb.DefaultOrgId && s.GitHubInstallationId != null)
            .CountAsync();
        others.Should().Be(0);
    }
}
