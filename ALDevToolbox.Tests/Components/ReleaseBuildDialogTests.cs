using ALDevToolbox.Components.Shared;
using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The publish confirmation is the point of no return on the most destructive
/// action in the product, so it has to say <em>whose</em> tenant is about to be
/// written to. Environments are routinely named literally "Production", which is
/// exactly the case where the environment name alone identifies nothing (#714).
///
/// The dialog is opened imperatively and never reaches the database here - the
/// <see cref="DeliveryService"/> it injects is constructed but not called, so
/// the context is pointed at a connection string that is never opened.
/// </summary>
public sealed class ReleaseBuildDialogTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ReleaseBuildDialogTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddUtcDisplayTimeZone();
        _ctx.Services.AddSingleton(NewUnusedDeliveryService());
        _ctx.Services.AddSingleton(NewUnusedGitHubReleaseService());
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// The dialog injects the Releases service so a Release-sourced pipeline can stage
    /// its apps when the person releases. Nothing here releases, so this one is
    /// constructed and never called - like the delivery service beside it.
    /// </summary>
    private static ALDevToolbox.Services.GitHub.GitHubReleaseService NewUnusedGitHubReleaseService()
    {
        var db = NewUnopenedContext();
        var org = new SignedOutOrganizationContext();
        var settings = new SystemSettingsService(db, NewProtection(), NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        var client = new ALDevToolbox.Services.GitHub.GitHubAppClient(
            new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri(ALDevToolbox.Services.GitHub.GitHubAppClient.ApiBaseUrl) },
            settings, new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System, NullLogger<ALDevToolbox.Services.GitHub.GitHubAppClient>.Instance);
        return new ALDevToolbox.Services.GitHub.GitHubReleaseService(
            db, client,
            // Constructed so the service resolves; nothing here asks it anything.
            new ALDevToolbox.Services.GitHub.GitHubConnectionService(
                db, org, null!, settings, null!,
                NullLogger<ALDevToolbox.Services.GitHub.GitHubConnectionService>.Instance, TimeProvider.System),
            new ProjectAccess(db, org), org,
            new ALDevToolbox.Endpoints.PublicOrigin(null), TimeProvider.System,
            new ALDevToolbox.Services.Tools.ToolEnablement(TestDb.EverythingEnabled(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor(), db, org, TimeProvider.System),
            NullLogger<ALDevToolbox.Services.GitHub.GitHubReleaseService>.Instance);
    }

    private static Microsoft.AspNetCore.DataProtection.IDataProtectionProvider NewProtection() =>
        Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aldt-release-dialog-tests")));

    private static AppDbContext NewUnopenedContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened").Options);

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("GitHub is not reachable from this test.");
    }

    private static DeliveryService NewUnusedDeliveryService()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened")
            .Options;
        var db = new AppDbContext(options);
        var org = new SignedOutOrganizationContext();
        return new DeliveryService(db, org,
            new ProjectAccess(db, org),
            new UnusedTokenSource(), new UnusedAppManagementClient(), new UnusedAdminClient(),
            new DeliveryQueue(),
            new ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache(TimeProvider.System),
            new ALDevToolbox.Services.Tools.ToolEnablement(TestDb.EverythingEnabled(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor(), db, org, TimeProvider.System),
            NullLogger<DeliveryService>.Instance);
    }

    private IRenderedComponent<ReleaseBuildDialog> OpenedDialog(string envName, string envType = "Production")
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: envName,
            envType: envType,
            deploymentSchedule: "Immediate",
            schemaSyncMode: "Add",
            builds:
            [
                new ReleaseBuildDialog.ReleasableBuildOption(412, "#412 · main @a3f9c21 · 2 apps · built 29 Aug 14:21",
                [
                    new ReleaseBuildDialog.ReleasableBuildApp("CRONUS Sales Extension", "1.4.2.0"),
                    new ReleaseBuildDialog.ReleasableBuildApp("CRONUS Shared Library", "3.0.1.0"),
                ]),
            ],
            timeZone: "Europe/Copenhagen",
            windowStart: null,
            windowEnd: null)).GetAwaiter().GetResult();
        return cut;
    }

    [Fact]
    public void An_environment_literally_named_Production_still_names_the_customer_in_the_title_and_the_acknowledgement()
    {
        var cut = OpenedDialog("Production");

        cut.Find("#rb-title").TextContent.Should().Be("Deploy to CRONUS A/S — Production");

        var ack = cut.Find(".check--ack").TextContent;
        ack.Should().Contain("CRONUS A/S");
        ack.Should().Contain("Production");

        cut.Find(".confirm-dialog__body p").TextContent
            .Should().Contain("to Production in CRONUS A/S's Business Central");
    }

    [Fact]
    public void The_dialog_lists_the_selected_builds_apps_and_versions()
    {
        var cut = OpenedDialog("Production");

        var apps = cut.FindAll(".rb-apps__list li").Select(li => li.TextContent.Trim()).ToList();
        apps.Should().HaveCount(2);
        apps[0].Should().Contain("CRONUS Sales Extension").And.Contain("1.4.2.0");
        apps[1].Should().Contain("CRONUS Shared Library").And.Contain("3.0.1.0");
    }

    [Fact]
    public void A_delivery_window_pipeline_defaults_to_the_next_opening_and_says_so()
    {
        var now = DateTime.UtcNow;
        var opens = new TimeOnly(now.AddHours(3).Hour, now.AddHours(3).Minute);
        var expected = ALDevToolbox.Domain.ValueObjects.ObjectExplorer.UpdateWindow.NextOpeningUtc(
            opens, opens.AddHours(2), TimeZoneInfo.Utc, now);

        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: "Production",
            envType: "Sandbox",
            deploymentSchedule: "OurDeliveryWindow",
            schemaSyncMode: "Add",
            builds:
            [
                new ReleaseBuildDialog.ReleasableBuildOption(412, "#412", [new ReleaseBuildDialog.ReleasableBuildApp("CRONUS Sales Extension", "1.4.2.0")]),
            ],
            timeZone: "UTC",
            windowStart: opens,
            windowEnd: opens.AddHours(2))).GetAwaiter().GetResult();

        cut.Find("#rb-when").GetAttribute("value").Should().StartWith(expected.ToString("yyyy-MM-ddTHH:mm"));
        cut.Find(".rb-when-row + .field__hint").TextContent.Should()
            .StartWith("Scheduled for the next delivery window, ")
            .And.Contain($"{opens:HH:mm}, in the solution's time zone");
        cut.Find(".confirm-dialog__body p b:last-of-type").TextContent.Should().Be("right away",
            "Business Central is told to install on arrival; the window only decided when we send");
        cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Schedule deployment");

        // "Now" is the explicit way out of the window.
        cut.WaitForAssertion(() => cut.Find(".rb-when-row .btn").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find(".rb-when-row + .field__hint").TextContent.Should().NotContain("Scheduled for the next delivery window");
            cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Deploy now");
            cut.Markup.Should().Contain("delivery window (" + $"{opens:HH:mm}" + "-");
            cut.Markup.Should().Contain("isn't open now");
        });
        // ...and "Next window" puts the window's opening back.
        cut.WaitForAssertion(() => cut.FindAll(".rb-when-row .btn").Single(b => b.TextContent == "Next window").Click());
        cut.WaitForAssertion(() =>
            cut.Find(".rb-when-row + .field__hint").TextContent.Should().StartWith("Scheduled for the next delivery window, "));
    }

    [Fact]
    public void A_non_production_target_names_the_customer_and_asks_for_no_acknowledgement()
    {
        var cut = OpenedDialog("UAT", envType: "Sandbox");

        cut.Find("#rb-title").TextContent.Should().Be("Deploy to CRONUS A/S — UAT");
        cut.FindAll(".check--ack").Should().BeEmpty();
    }

    [Fact]
    public void A_release_sourced_pipeline_picks_a_github_release_and_shows_the_files_it_installs()
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: "Production",
            envType: "Production",
            deploymentSchedule: "Immediate",
            schemaSyncMode: "Add",
            builds: [],
            timeZone: "Europe/Copenhagen",
            windowStart: null,
            windowEnd: null,
            secretExpiresAt: null,
            releases:
            [
                new ALDevToolbox.Services.GitHub.GitHubReleaseOption(
                    "v1.4.2.0", "v1.4.2.0", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
                    ["CRONUS Sales Extension_1.4.2.0.app"]),
                // Nothing installable on it, so it is not offered at all.
                new ALDevToolbox.Services.GitHub.GitHubReleaseOption("v1.4.1.0", null, null, []),
            ],
            repositoryName: "cronus-customer")).GetAwaiter().GetResult();

        // The build picker is gone: this pipeline has no builds of its own.
        cut.FindAll("#rb-build").Should().BeEmpty();
        var tags = cut.FindAll("#rb-release option").Select(o => o.TextContent.Trim()).ToList();
        tags.Should().ContainSingle().Which.Should().StartWith("v1.4.2.0");
        cut.Markup.Should().Contain("This release installs");
        cut.Markup.Should().Contain("CRONUS Sales Extension_1.4.2.0.app");
        cut.Markup.Should().Contain("cronus-customer");
    }

    [Fact]
    public async Task A_repository_with_no_installable_release_says_what_to_publish_instead_of_an_empty_picker()
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1,
            customerName: "CRONUS A/S",
            envName: "Production",
            envType: "Sandbox",
            deploymentSchedule: "Immediate",
            schemaSyncMode: "Add",
            builds: [],
            timeZone: "Europe/Copenhagen",
            windowStart: null,
            windowEnd: null,
            secretExpiresAt: null,
            // One release, nothing installable attached to it.
            releases: [new ALDevToolbox.Services.GitHub.GitHubReleaseOption("v1.4.1.0", null, null, [])],
            repositoryName: "cronus-customer"));

        cut.FindAll("#rb-release").Should().BeEmpty();
        cut.Markup.Should().Contain("No GitHub release on cronus-customer has an .app file attached yet.");
        cut.Markup.Should().Contain("Publish a GitHub release with the compiled apps attached");
        cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();
    }

    // ── Release again (#931): DeliveryRowPanel.dc.html, section 4, its three states ──

    private IRenderedComponent<ReleaseAgainDialog> OpenedReleaseAgain(string envName, string envType, bool forceSync,
        bool failedOnSchemaChange = false)
    {
        var cut = _ctx.Render<ReleaseAgainDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            new ReleaseAgainDialog.Request(
                DeliveryId: 49, ReleasePipelineId: 7, BuildId: 118, BuildSource: "from build pipeline \"Test\"", CustomerName: "CRONUS A/S",
                EnvironmentName: envName, EnvironmentType: envType, WireSchedule: "Immediate", PipelineSyncMode: "Add",
                Apps: ["CRONUS Base", "CRONUS Core", "CRONUS Reports"], AlreadyOn: ["CRONUS Base"],
                FailedOnSchemaChange: failedOnSchemaChange),
            forceSync)).GetAwaiter().GetResult();
        return cut;
    }

    [Fact]
    public void Release_again_on_a_sandbox_left_on_Add_can_release_straight_away()
    {
        var cut = OpenedReleaseAgain("Test", "Sandbox", forceSync: false);

        cut.Find("#ra-title").TextContent.Should().Be("Deploy build #118 again?");
        cut.Find(".ra-lead").TextContent.Should().Be(
            "This installs build #118 from build pipeline \"Test\" into the Sandbox environment \"Test\" for CRONUS A/S, right away. "
            + "CRONUS Base is already on this version and is left alone.");
        cut.Find(".ra-facts").TextContent.Should().Contain("CRONUS Base, CRONUS Core, CRONUS Reports").And.Contain("Right away").And.Contain("Test Sandbox");
        cut.Find(".ra-check input").HasAttribute("checked").Should().BeFalse("Force sync is off unless asked for");
        cut.Find(".ra-check").TextContent.Should().Contain("The pipeline stays on Add. The next deployment goes back to Add.");
        cut.FindAll(".check--ack").Should().BeEmpty();
        cut.FindAll(".confirm-dialog--danger").Should().BeEmpty();
        var release = cut.Find(".confirm-dialog__actions .btn--primary");
        release.TextContent.Trim().Should().Be("Deploy");
        release.HasAttribute("disabled").Should().BeFalse();
    }

    // ── Who is online, asked before an install that runs right away ──
    // The delivery service here has no organisation behind it, so the check fails the way
    // a fault at our end does: the dialog says it couldn't tell and still lets the person go on.

    [Fact]
    public void Deploy_now_asks_who_is_online_and_turns_into_Deploy_anyway()
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1, customerName: "CRONUS A/S", envName: "Test", envType: "Sandbox",
            deploymentSchedule: "Immediate", schemaSyncMode: "Add",
            builds: [new ReleaseBuildDialog.ReleasableBuildOption(412, "#412", [])],
            timeZone: "UTC", windowStart: null, windowEnd: null)).GetAwaiter().GetResult();
        cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Deploy now");

        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__body [role=status]").TextContent.Trim().Should().Be(
                "Couldn't check who is signed in to Test. Something went wrong at our end. Are you sure you want to deploy the build?");
            var deploy = cut.Find(".confirm-dialog__actions .btn--primary");
            deploy.TextContent.Should().Contain("Deploy anyway");
            deploy.HasAttribute("disabled").Should().BeFalse("not knowing who is online never blocks a deployment");
        });
    }

    [Fact]
    public void A_deployment_handed_to_the_next_update_does_not_ask_who_is_online()
    {
        var cut = _ctx.Render<ReleaseBuildDialog>();
        cut.InvokeAsync(() => cut.Instance.OpenAsync(
            releasePipelineId: 1, customerName: "CRONUS A/S", envName: "Test", envType: "Sandbox",
            deploymentSchedule: "NextMinorUpdate", schemaSyncMode: "Add",
            builds: [new ReleaseBuildDialog.ReleasableBuildOption(412, "#412", [])],
            timeZone: "UTC", windowStart: null, windowEnd: null)).GetAwaiter().GetResult();

        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__body .alert").TextContent.Should().Contain("Couldn't start the deployment",
                "it went straight to deploying, which this unconnected service refuses");
            cut.Markup.Should().NotContain("Are you sure");
        });
    }

    [Fact]
    public void Deploy_again_asks_who_is_online_and_turns_into_Deploy_anyway()
    {
        var cut = OpenedReleaseAgain("Test", "Sandbox", forceSync: false);

        cut.Find(".confirm-dialog__actions .btn--primary").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__body [role=status]").TextContent.Trim().Should().Be(
                "Couldn't check who is signed in to Test. Something went wrong at our end. Are you sure you want to deploy the build?");
            cut.Find(".confirm-dialog__actions .btn--primary").TextContent.Should().Contain("Deploy anyway");
        });
    }

    [Fact]
    public void Release_again_with_force_sync_ticked_waits_for_its_acknowledgement()
    {
        var cut = OpenedReleaseAgain("Test", "Sandbox", forceSync: true);

        cut.Find(".ra-check input").HasAttribute("checked").Should().BeTrue("the failure's Force sync button pre-ticks it");
        cut.Find(".check--ack").TextContent.Should().Contain("I understand Force sync can drop columns and permanently lose data in this environment.");
        cut.Find(".field-warn").TextContent.Should().Contain("Tick the acknowledgement to deploy.");
        cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();

        cut.WaitForAssertion(() => cut.Find(".check--ack input").Change(true));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeFalse();
            cut.FindAll(".field-warn").Should().BeEmpty();
        });
    }

    [Fact]
    public void Release_again_into_production_with_force_sync_needs_both_acknowledgements()
    {
        var cut = OpenedReleaseAgain("Production", "Production", forceSync: true);

        cut.FindAll(".confirm-dialog--danger").Should().ContainSingle();
        cut.Find(".ra-lead").TextContent.Should().StartWith(
            "This installs build #118 from build pipeline \"Test\" into CRONUS A/S's Production environment \"Production\", right away.");
        var acks = cut.FindAll(".check--ack");
        acks.Should().HaveCount(2);
        acks[1].TextContent.Should().Contain("CRONUS A/S").And.Contain("live").And.Contain("Production");
        cut.Find(".field-warn").TextContent.Should().Contain("Tick both acknowledgements to deploy.");

        cut.WaitForAssertion(() => cut.FindAll(".check--ack input")[0].Change(true));
        cut.WaitForAssertion(() =>
        {
            cut.Find(".field-warn").TextContent.Should().Contain("Tick the acknowledgement to deploy.");
            cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeTrue();
        });
        cut.WaitForAssertion(() => cut.FindAll(".check--ack input")[1].Change(true));
        cut.WaitForAssertion(() =>
            cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Release_again_without_force_sync_after_a_refused_schema_change_says_it_will_likely_fail_again()
    {
        var cut = OpenedReleaseAgain("Production", "Production", forceSync: false, failedOnSchemaChange: true);

        cut.Find(".ra-facts").TextContent.Should().NotContain("Production Production", "the type alone names an environment called after it");
        cut.FindAll(".field-warn").Select(w => w.TextContent).Should().Contain(t => t.Contains("will most likely fail the same way"));

        cut.WaitForAssertion(() => cut.Find(".ra-check input").Change(true));
        cut.WaitForAssertion(() =>
            cut.FindAll(".field-warn").Select(w => w.TextContent).Should().NotContain(t => t.Contains("fail the same way")));
    }

    [Fact]
    public void Release_again_unticking_force_sync_drops_its_acknowledgement()
    {
        var cut = OpenedReleaseAgain("Test", "Sandbox", forceSync: true);

        cut.WaitForAssertion(() => cut.Find(".ra-check input").Change(false));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".check--ack").Should().BeEmpty();
            cut.Find(".confirm-dialog__actions .btn--primary").HasAttribute("disabled").Should().BeFalse();
        });
    }

    // ── Unused seams: the dialog never releases in these tests ────────────────

    /// <summary>No signed-in user: nothing here queries, so the filter sentinel is enough.</summary>
    private sealed class SignedOutOrganizationContext : IOrganizationContext
    {
        public int? CurrentOrganizationId => null;
        public int? CurrentUserId => null;
        public bool IsSiteAdmin => false;
        public bool IsSystemOrganization => false;
        public int OrganizationIdForFilter => 0;
    }

    private sealed class UnusedTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedAdminClient : IBcAdminClient
    {
        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcSession>> ListSessionsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelSessionAsync(string accessToken, string? applicationFamily, string environmentName, int sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironmentCopy> CopyEnvironmentAsync(string accessToken, string? applicationFamily, string sourceEnvironmentName, string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAppManagementClient : IBcAppManagementClient
    {
        public Task<BcAppOperation> InstallPteAsync(string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName, string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation?> GetAppOperationAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, string scheduleKind, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
