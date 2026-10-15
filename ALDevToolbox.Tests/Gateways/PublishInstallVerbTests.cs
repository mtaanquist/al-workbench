using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.PublishInstall;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The <c>publish-install</c> verb (#1375) against a fake Business Central behind the
/// fake process runner: what it decides for each app, what it refuses before anything
/// runs, and how it stops. See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed class PublishInstallVerbTests : IDisposable
{
    private const string TestTier = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC250"
        environment = "test"
        allow = ["inventory", "publish", "unpublish", "restart"]
        tenants = ["default", "cronus-dk", "cronus-se"]
        allow_unsigned = true
        """;

    private readonly ScratchFolder _scratch = new();
    private readonly FakeBusinessCentral _server = new();
    private readonly List<string> _progress = [];
    private TextPolicy _policy = new(TestTier);

    public void Dispose() => _scratch.Dispose();

    private PublishInstallVerb Verb(string machine = "CRONUS-BC01") => new(
        _policy,
        new PowerShellRunner(_server, new PowerShellExecutables("powershell.exe", "pwsh.exe"), new FakeFileAccess()),
        new FixedTierLocator(@"C:\Program Files\Microsoft Dynamics 365 Business Central\250\Service"),
        machine);

    /// <summary>An app for the command: its file on disk, and how the command declares it.</summary>
    private sealed record App(GatewayCommandFileMessage File, GatewayPublishInstallApp Declared, string Path);

    private int _nextFile = 1;

    /// <summary>The package id of the CRONUS Coffee 1.4.0.0 build the tests deploy, when one is asked for.</summary>
    private static readonly Guid ThisBuild = Guid.Parse("0f9e8d7c-6b5a-4938-8271-605f4e3d2c1b");

    private App MakeApp(string name = "CRONUS Coffee", string version = "1.4.0.0", string publisher = "CRONUS Partner",
        Guid? id = null, bool signed = false, Func<GatewayPublishInstallApp, GatewayPublishInstallApp>? declare = null, Guid? packageId = null)
    {
        var appId = id ?? (name == "CRONUS Coffee" ? AppFiles.CoffeeId : Guid.NewGuid());
        _server.AppIds[name] = appId;
        var bytes = AppFiles.Build(appId, name, publisher, version, signed, packageId: packageId);
        var fileName = $"{publisher}_{name}_{version}.app";
        var fileId = _nextFile++;
        var path = Path.Combine(_scratch.Path, $"{fileId}-{fileName}");
        File.WriteAllBytes(path, bytes);
        var declared = new GatewayPublishInstallApp { File = fileName, Id = appId, Name = name, Publisher = publisher, Version = version, Signed = signed };
        return new App(
            new GatewayCommandFileMessage { Id = fileId, Name = fileName, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length },
            declare?.Invoke(declared) ?? declared,
            path);
    }

    private async Task<GatewayCommandOutcome> RunAsync(IReadOnlyList<App> apps, string? tenant = null, bool unpublish = true,
        string syncMode = GatewaySyncModes.Add, Action<JsonObject>? more = null, string machine = "CRONUS-BC01")
    {
        var parameters = new GatewayPublishInstallParameters
        {
            Machine = "CRONUS-BC01",
            Instance = "BC250",
            Tenant = tenant,
            SyncMode = syncMode,
            UnpublishPrevious = unpublish,
            Apps = apps.Select(a => a.Declared).ToList(),
        }.ToParameters();
        more?.Invoke(parameters);
        var command = new GatewayCommandMessage
        {
            Id = 1375,
            Verb = GatewayVerb.PublishInstall,
            Parameters = parameters,
            SecondsLeft = 900,
            RequestedBy = "Ida Consultant",
            Files = apps.Select(a => a.File).ToList(),
        };
        var files = apps.ToDictionary(a => a.File.Id, a => a.Path);
        return await Verb(machine).ExecuteAsync(new GatewayCommandContext(command, files, _progress.Add), CancellationToken.None);
    }

    private static GatewayPublishInstallResult Result(GatewayCommandOutcome outcome) =>
        GatewayPublishInstallResult.FromJson(outcome.Result) ?? throw new InvalidOperationException("The command reported no publish-install result.");

    private static IEnumerable<string> StepWords(GatewayPublishInstallAppResult app) => app.Steps.Select(s => $"{s.Step}:{s.Outcome}");

    // ---------------------------------------------------------------- what is decided

    [Fact]
    public async Task An_app_never_installed_is_published_to_the_tenant_synced_and_installed()
    {
        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Equal(
            "publish CRONUS Coffee 1.4.0.0 Tenant default skip=True",
            "sync CRONUS Coffee 1.4.0.0 default Add",
            "install CRONUS Coffee 1.4.0.0 default");
        var app = Result(outcome).Apps.Should().ContainSingle().Subject;
        app.Outcome.Should().Be(GatewayPublishInstallOutcomes.Installed);
        app.Id.Should().Be(AppFiles.CoffeeId.ToString("D"));
        StepWords(app).Should().Equal("verify:done", "publish:done", "sync:done", "install:done");
        Result(outcome).Restarted.Should().BeFalse();
        _progress.Should().Contain("CRONUS Coffee 1.4.0.0: published to tenant 'default'.")
            .And.Contain("CRONUS Coffee 1.4.0.0: installed.")
            .And.Contain(l => l.Contains("were not restarted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_cmdlet_that_acts_on_one_app_is_given_its_name_publisher_and_version_from_the_file()
    {
        await RunAsync([MakeApp()]);

        var apply = _server.Runs.Single(r => r.Operation == "apply").Parameters;
        apply["name"]!.GetValue<string>().Should().Be("CRONUS Coffee");
        apply["publisher"]!.GetValue<string>().Should().Be("CRONUS Partner");
        apply["version"]!.GetValue<string>().Should().Be("1.4.0.0");
        apply["path"]!.GetValue<string>().Should().EndWith("CRONUS Partner_CRONUS Coffee_1.4.0.0.app");
    }

    [Fact]
    public async Task An_older_installed_version_is_upgraded_and_then_unpublished_from_the_tenant()
    {
        _server.Publish("CRONUS Coffee", "1.3.0.0", tenant: "default");
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 3, 0, 0);
        _server.Data[("default", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Equal(
            "publish CRONUS Coffee 1.4.0.0 Tenant default skip=True",
            "sync CRONUS Coffee 1.4.0.0 default Add",
            "upgrade CRONUS Coffee 1.4.0.0 default",
            "unpublish CRONUS Coffee 1.3.0.0 default");
        var app = Result(outcome).Apps.Single();
        app.Outcome.Should().Be(GatewayPublishInstallOutcomes.Upgraded);
        app.FromVersion.Should().Be("1.3.0.0");
        StepWords(app).Should().Equal("verify:done", "publish:done", "sync:done", "upgrade:done", "unpublish:done");
        _progress.Should().Contain("CRONUS Coffee 1.4.0.0: upgraded from 1.3.0.0.");
    }

    [Fact]
    public async Task An_app_uninstalled_with_its_data_kept_is_upgraded_from_its_data_version()
    {
        _server.Publish("CRONUS Coffee", "1.3.0.0", tenant: "default");
        _server.Data[("default", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()], unpublish: false);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Contain("upgrade CRONUS Coffee 1.4.0.0 default").And.NotContain(s => s.StartsWith("install", StringComparison.Ordinal));
        Result(outcome).Apps.Single().FromVersion.Should().Be("1.3.0.0");
    }

    [Fact]
    public async Task The_same_version_already_installed_is_left_alone()
    {
        _server.Publish("CRONUS Coffee", "1.4.0.0", tenant: "default", packageId: ThisBuild);
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 4, 0, 0);

        var outcome = await RunAsync([MakeApp(packageId: ThisBuild)]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Runs.Select(r => r.Operation).Should().Equal("examine", "inspect");
        _server.Steps.Should().BeEmpty();
        var app = Result(outcome).Apps.Single();
        app.Outcome.Should().Be(GatewayPublishInstallOutcomes.AlreadyInstalled);
        app.Message.Should().Contain("already installed on tenant 'default'");
    }

    [Fact]
    public async Task A_version_already_published_is_not_published_again()
    {
        // A deployment that failed after publishing, sent again.
        _server.Publish("CRONUS Coffee", "1.4.0.0", tenant: "default", packageId: ThisBuild);

        var outcome = await RunAsync([MakeApp(packageId: ThisBuild)]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Equal("sync CRONUS Coffee 1.4.0.0 default Add", "install CRONUS Coffee 1.4.0.0 default");
        StepWords(Result(outcome).Apps.Single()).Should().Equal("verify:done", "publish:skipped", "sync:done", "install:done");
    }

    [Fact]
    public async Task A_different_build_already_published_with_the_same_version_is_refused_rather_than_installed()
    {
        _server.Publish("CRONUS Coffee", "1.4.0.0", tenant: "default", packageId: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"));

        var outcome = await RunAsync([MakeApp(packageId: ThisBuild)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("a different build with the same version (1.4.0.0) is already published");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine", "inspect");
        _server.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_different_build_of_the_installed_version_is_refused_rather_than_called_already_installed()
    {
        _server.Publish("CRONUS Coffee", "1.4.0.0", tenant: "default", packageId: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"));
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 4, 0, 0);

        var outcome = await RunAsync([MakeApp(packageId: ThisBuild)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("a different build with the same version");
        Result(outcome).Apps.Single().Outcome.Should().Be(GatewayPublishInstallOutcomes.Failed);
    }

    [Fact]
    public async Task A_published_version_whose_build_cannot_be_read_is_not_installed_in_the_files_place()
    {
        _server.ReportsPackageId = false;
        _server.Publish("CRONUS Coffee", "1.4.0.0", tenant: "default", packageId: ThisBuild);

        var outcome = await RunAsync([MakeApp(packageId: ThisBuild)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("could not read which build that is");
        _server.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_newer_installed_version_is_never_downgraded()
    {
        _server.Publish("CRONUS Coffee", "1.5.0.0", tenant: "default");
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 5, 0, 0);

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("version 1.5.0.0 is installed").And.Contain("does not install an older version");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine", "inspect");
    }

    [Fact]
    public async Task A_tenant_whose_installed_versions_cannot_be_read_is_not_touched()
    {
        // Neither -TenantSpecificProperties nor Get-NAVAppTenant: a newer version may be
        // installed, so neither install nor upgrade is safe to guess.
        _server.InstalledStateUnknown = true;
        _server.Publish("CRONUS Coffee", "1.5.0.0", tenant: "default");
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 5, 0, 0);

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("could not tell whether version 1.5.0.0 is installed on tenant 'default'");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine", "inspect");
        _server.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task The_sync_mode_the_command_asks_for_is_used()
    {
        _policy = new TextPolicy(TestTier + "\nallow_force_sync = true\n");

        var outcome = await RunAsync([MakeApp()], syncMode: GatewaySyncModes.ForceSync);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Contain("sync CRONUS Coffee 1.4.0.0 default ForceSync");
    }

    [Fact]
    public async Task ForceSync_on_a_tier_that_does_not_allow_it_is_refused_before_anything_runs()
    {
        var outcome = await RunAsync([MakeApp()], syncMode: GatewaySyncModes.ForceSync);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("does not allow ForceSync (allow_force_sync)").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sync_mode_the_gateway_does_not_know_is_refused_before_anything_runs()
    {
        var outcome = await RunAsync([MakeApp()], more: p => p[GatewayCommandParameters.SyncMode] = "Clean");

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("schema sync mode").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task On_business_central_14_without_data_versions_an_installed_older_version_is_still_upgraded()
    {
        // The inspect script reads the installed version from Get-NAVAppTenant there.
        _server.Current = false;
        _server.Publish("CRONUS Coffee", "1.3.0.0");
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Equal(
            "publish CRONUS Coffee 1.4.0.0 Tenant default skip=True",
            "sync CRONUS Coffee 1.4.0.0 default Add",
            "upgrade CRONUS Coffee 1.4.0.0 default",
            "unpublish CRONUS Coffee 1.3.0.0");
    }

    // ---------------------------------------------------------------- multitenant servers

    [Fact]
    public async Task On_a_multitenant_server_the_app_is_published_to_the_one_tenant()
    {
        _server.Tenants.AddRange(["cronus-dk", "cronus-se"]);

        var outcome = await RunAsync([MakeApp()], tenant: "cronus-dk");

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps[0].Should().Be("publish CRONUS Coffee 1.4.0.0 Tenant cronus-dk skip=True");
        _server.PublishedApps.Should().ContainSingle().Which.Tenant.Should().Be("cronus-dk");
        _server.Installed.Keys.Should().Equal(("cronus-dk", "CRONUS Coffee"));
    }

    [Fact]
    public async Task A_global_previous_version_another_tenant_still_uses_is_left_published_and_the_result_says_why()
    {
        _server.Tenants.AddRange(["cronus-dk", "cronus-se"]);
        _server.Publish("CRONUS Coffee", "1.3.0.0");
        _server.Installed[("cronus-dk", "CRONUS Coffee")] = new Version(1, 3, 0, 0);
        _server.Installed[("cronus-se", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()], tenant: "cronus-dk");

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().NotContain(s => s.StartsWith("unpublish", StringComparison.Ordinal));
        _server.PublishedApps.Should().Contain(p => p.Version == new Version(1, 3, 0, 0));
        var unpublish = Result(outcome).Apps.Single().Steps.Single(s => s.Step == GatewayPublishInstallSteps.Unpublish);
        unpublish.Outcome.Should().Be(GatewayPublishInstallStepOutcomes.Skipped);
        unpublish.Version.Should().Be("1.3.0.0");
        unpublish.Message.Should().Contain("tenant 'cronus-se' still has it installed");
        _progress.Should().Contain(l => l.Contains("left published", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_global_previous_version_is_left_published_when_the_other_tenants_cannot_be_read()
    {
        _server.Tenants.AddRange(["cronus-dk", "cronus-se"]);
        _server.OtherTenantsUnknown = "Get-NAVAppInfo did not say what tenant cronus-se has installed.";
        _server.Publish("CRONUS Coffee", "1.3.0.0");
        _server.Installed[("cronus-dk", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()], tenant: "cronus-dk");

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().NotContain(s => s.StartsWith("unpublish", StringComparison.Ordinal));
        var unpublish = Result(outcome).Apps.Single().Steps.Single(s => s.Step == GatewayPublishInstallSteps.Unpublish);
        unpublish.Outcome.Should().Be(GatewayPublishInstallStepOutcomes.Skipped);
        unpublish.Message.Should().Contain("could not tell whether another tenant still uses it").And.Contain("cronus-se has installed");
    }

    [Fact]
    public async Task A_global_previous_version_no_other_tenant_uses_is_unpublished()
    {
        _server.Tenants.AddRange(["cronus-dk", "cronus-se"]);
        _server.Publish("CRONUS Coffee", "1.3.0.0");
        _server.Installed[("cronus-dk", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()], tenant: "cronus-dk");

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().Contain("unpublish CRONUS Coffee 1.3.0.0");
        _server.Runs.Single(r => r.Operation == "apply").Parameters["steps"]!.AsArray()
            .Last()!["tenantScoped"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_unpublished_unless_the_command_asks()
    {
        _server.Publish("CRONUS Coffee", "1.3.0.0", tenant: "default");
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 3, 0, 0);

        var outcome = await RunAsync([MakeApp()], unpublish: false);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Should().NotContain(s => s.StartsWith("unpublish", StringComparison.Ordinal));
        _server.Runs.Single(r => r.Operation == "inspect").Parameters["otherTenants"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_tier_set_to_global_scope_publishes_for_every_tenant()
    {
        _policy = new TextPolicy(TestTier + "\nscope = \"global\"\n");

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps[0].Should().Be("publish CRONUS Coffee 1.4.0.0 Global default skip=True");
    }

    // ---------------------------------------------------------------- signatures and publishers

    private const string SignedOnlyTier = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC250"
        environment = "test"
        allow = ["inventory", "publish", "unpublish", "restart"]
        allow_unsigned = false
        """;

    [Fact]
    public async Task An_unsigned_app_is_refused_where_unsigned_apps_are_not_allowed_whatever_the_command_claims()
    {
        _policy = new TextPolicy(SignedOnlyTier);

        var outcome = await RunAsync([MakeApp(signed: false, declare: d => d with { Signed = true })]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("is not signed").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
        Result(outcome).Apps.Single().Outcome.Should().Be(GatewayPublishInstallOutcomes.Refused);
    }

    [Fact]
    public async Task An_app_declared_unsigned_is_refused_by_the_policy_check_where_unsigned_apps_are_not_allowed()
    {
        _policy = new TextPolicy(SignedOnlyTier);

        var outcome = await RunAsync([MakeApp(signed: false)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("does not take unsigned apps").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_app_is_checked_by_windows_first_and_published_without_skipping_verification()
    {
        _policy = new TextPolicy(SignedOnlyTier);

        var outcome = await RunAsync([MakeApp(signed: true)]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Runs.Select(r => r.Operation).Should().Equal("examine", "inspect", "apply");
        _server.Runs[0].Parameters["files"]![0]!["signature"]!.GetValue<bool>().Should().BeTrue();
        _server.Steps.Should().StartWith(["publish CRONUS Coffee 1.4.0.0 Tenant default skip=False"]);
        StepWords(Result(outcome).Apps.Single()).Should().StartWith(["verify:done", "signature:done", "publish:done"]);
    }

    [Fact]
    public async Task The_signer_is_recorded_on_the_signature_step_and_in_the_result()
    {
        _policy = new TextPolicy(SignedOnlyTier);

        var outcome = await RunAsync([MakeApp(signed: true)]);

        var app = Result(outcome).Apps.Single();
        app.SignerSubject.Should().Be("CN=CRONUS Partner, O=CRONUS Partner, C=DK");
        app.SignerThumbprint.Should().Be("3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E");
        var step = app.Steps.Single(s => s.Step == GatewayPublishInstallSteps.Signature);
        step.SignerSubject.Should().Be(app.SignerSubject);
        step.SignerThumbprint.Should().Be(app.SignerThumbprint);
        _progress.Should().Contain(l => l.Contains("signed by CN=CRONUS Partner, O=CRONUS Partner, C=DK (thumbprint 3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_signature_windows_does_not_accept_stops_before_anything_changes()
    {
        _policy = new TextPolicy(SignedOnlyTier);
        _server.SignatureValid = false;

        var outcome = await RunAsync([MakeApp(signed: true)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("does not verify on this computer (HashMismatch)").And.Contain("Nothing was changed");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine");
        var app = Result(outcome).Apps.Single();
        app.Outcome.Should().Be(GatewayPublishInstallOutcomes.Refused);
        StepWords(app).Should().Equal("signature:failed");
        app.SignerThumbprint.Should().Be("3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E", "who signed it is recorded even when it is refused");
    }

    [Fact]
    public async Task A_signature_that_cannot_be_read_is_refused_where_signatures_are_required()
    {
        _policy = new TextPolicy(SignedOnlyTier);
        _server.SignatureError = "Windows signature checking (Get-AuthenticodeSignature) is not available here";

        var outcome = await RunAsync([MakeApp(signed: true)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("could not be checked on this computer").And.Contain("Get-AuthenticodeSignature");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine");
    }

    [Theory]
    [InlineData("3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E")]
    [InlineData("3f 2a 9c 41 d0 7e 5b 86 a1 c2 e4 f6 07 18 29 3a 4b 5c 6d 7e")]
    [InlineData("cn=cronus partner, o=cronus partner, c=dk")]
    public async Task A_signer_on_the_tiers_list_is_accepted_by_thumbprint_or_subject(string signer)
    {
        _policy = new TextPolicy(SignedOnlyTier + $"\nsigners = [\"{signer}\"]\n");

        var outcome = await RunAsync([MakeApp(signed: true)]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
    }

    [Fact]
    public async Task A_valid_signature_from_a_signer_not_on_the_tiers_list_is_refused()
    {
        _policy = new TextPolicy(SignedOnlyTier + "\nsigners = [\"0000000000000000000000000000000000000000\"]\n");
        _server.SignerSubject = "CN=Fabrikam Code Signing";
        _server.SignerThumbprint = "9999999999999999999999999999999999999999";

        var outcome = await RunAsync([MakeApp(signed: true)]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("is signed by CN=Fabrikam Code Signing (thumbprint 9999999999999999999999999999999999999999)")
            .And.Contain("not on CRONUS-BC01 / BC250's list of signers").And.Contain("Nothing was changed");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine");
        Result(outcome).Apps.Single().SignerSubject.Should().Be("CN=Fabrikam Code Signing");
    }

    [Fact]
    public async Task A_signers_list_also_holds_where_unsigned_apps_are_allowed()
    {
        _policy = new TextPolicy(TestTier + "\nsigners = [\"0000000000000000000000000000000000000000\"]\n");

        var signedByAnother = await RunAsync([MakeApp(signed: true)]);
        signedByAnother.FailureMessage.Should().Contain("not on CRONUS-BC01 / BC250's list of signers");

        var unsigned = await RunAsync([MakeApp(name: "CRONUS Tea")]);
        unsigned.Succeeded.Should().BeTrue(unsigned.FailureMessage);
    }

    [Fact]
    public async Task Where_unsigned_apps_are_allowed_publishing_skips_verification_and_the_signer_is_still_recorded()
    {
        var outcome = await RunAsync([MakeApp(signed: true)]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps[0].Should().EndWith("skip=True");
        Result(outcome).Apps.Single().SignerThumbprint.Should().Be("3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E");
    }

    [Fact]
    public async Task An_unsigned_app_where_unsigned_apps_are_allowed_is_not_sent_to_windows_for_a_signature()
    {
        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Runs[0].Parameters["files"]![0]!["signature"]!.GetValue<bool>().Should().BeFalse();
        Result(outcome).Apps.Single().SignerSubject.Should().BeNull();
    }

    // ---------------------------------------------------------------- the file as Business Central reads it

    [Fact]
    public async Task A_file_business_central_reads_as_another_publisher_is_refused_before_anything_changes()
    {
        // The gateway's reading passes the publisher list; Business Central's is what gets installed.
        _policy = new TextPolicy(TestTier + "\npublishers = [\"CRONUS Partner\"]\n");
        _server.BusinessCentralReads = app => app with { Publisher = "Fabrikam" };

        var outcome = await RunAsync([MakeApp(), MakeApp(name: "CRONUS Tea")]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("Business Central reads 'CRONUS Partner_CRONUS Coffee_1.4.0.0.app' as a different app than its manifest says")
            .And.Contain("publisher 'Fabrikam', not 'CRONUS Partner'").And.Contain("Nothing was changed");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine");
        Result(outcome).Apps.Select(a => a.Outcome).Should().Equal(GatewayPublishInstallOutcomes.Refused, GatewayPublishInstallOutcomes.Skipped);
    }

    public static TheoryData<string, string> BusinessCentralDifferences => new()
    {
        { "id", "app id 11111111-2222-3333-4444-555555555555" },
        { "name", "name 'CRONUS Tea', not 'CRONUS Coffee'" },
        { "version", "version 1.5.0.0, not 1.4.0.0" },
        { "package", "package id 11111111-2222-3333-4444-555555555555" },
    };

    [Theory]
    [MemberData(nameof(BusinessCentralDifferences))]
    public async Task Any_difference_in_how_business_central_reads_the_file_is_refused(string field, string expected)
    {
        var other = Guid.Parse("11111111-2222-3333-4444-555555555555");
        _server.BusinessCentralReads = app => field switch
        {
            "id" => app with { Id = other },
            "name" => app with { Name = "CRONUS Tea" },
            "version" => app with { Version = new Version(1, 5, 0, 0) },
            _ => app with { PackageId = other },
        };

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain(expected);
        _server.Runs.Select(r => r.Operation).Should().Equal("examine");
    }

    [Fact]
    public async Task A_version_that_cannot_read_an_app_file_is_refused_rather_than_trusted()
    {
        _server.ReadsFiles = false;

        var outcome = await RunAsync([MakeApp()]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("Business Central could not read").And.Contain("cannot read an .app file");
        _server.Runs.Select(r => r.Operation).Should().Equal("examine");
    }

    [Fact]
    public async Task Every_file_is_read_by_business_central_in_one_run_before_the_first_app_is_inspected()
    {
        var outcome = await RunAsync([MakeApp(name: "CRONUS Base"), MakeApp(name: "CRONUS Coffee"), MakeApp(name: "CRONUS Tea")]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Runs[0].Operation.Should().Be("examine");
        _server.Runs[0].Parameters["files"]!.AsArray().Should().HaveCount(3);
        _server.Runs.Count(r => r.Operation == "examine").Should().Be(1);
    }

    [Fact]
    public async Task A_publisher_the_tier_does_not_take_is_refused()
    {
        _policy = new TextPolicy(TestTier + "\npublishers = [\"CRONUS Partner\"]\n");

        var outcome = await RunAsync([MakeApp(publisher: "Fabrikam")]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("does not take apps from the publisher 'Fabrikam'").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_from_another_publisher_than_declared_is_refused_even_when_the_declared_one_is_allowed()
    {
        _policy = new TextPolicy(TestTier + "\npublishers = [\"CRONUS Partner\"]\n");

        var outcome = await RunAsync([MakeApp(publisher: "Fabrikam", declare: d => d with { Publisher = "CRONUS Partner" })]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("its publisher is 'Fabrikam', not 'CRONUS Partner'").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the file against the declaration

    public static TheoryData<string, string> Mismatches => new()
    {
        { "id", "its app id is" },
        { "version", "its version is 1.4.0.0, not 1.5.0.0" },
        { "name", "its name is 'CRONUS Coffee', not 'CRONUS Tea'" },
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task A_file_that_is_not_the_app_declared_is_refused_before_anything_runs(string field, string expected)
    {
        var app = MakeApp(declare: d => field switch
        {
            "id" => d with { Id = Guid.Parse("11111111-2222-3333-4444-555555555555") },
            "version" => d with { Version = "1.5.0.0" },
            _ => d with { Name = "CRONUS Tea" },
        });

        var outcome = await RunAsync([app]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("is not the app the command declares").And.Contain(expected).And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
        var result = Result(outcome).Apps.Single();
        result.Outcome.Should().Be(GatewayPublishInstallOutcomes.Refused);
        StepWords(result).Should().Equal("verify:failed");
    }

    [Fact]
    public async Task A_command_without_the_app_id_or_version_is_refused()
    {
        var app = MakeApp();
        var outcome = await RunAsync([app], more: p => p[GatewayCommandParameters.Apps]![0]!.AsObject().Remove(GatewayCommandParameters.AppId));

        outcome.FailureMessage.Should().Contain("does not give the app id");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_whose_bytes_no_longer_match_its_hash_when_the_verb_reads_it_is_refused()
    {
        var app = MakeApp();
        await File.WriteAllBytesAsync(app.Path, AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0", signed: true));

        var outcome = await RunAsync([app]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("SHA-256 does not match").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task One_refused_app_refuses_the_whole_command_even_when_it_is_not_the_first()
    {
        var outcome = await RunAsync([MakeApp(), MakeApp(name: "CRONUS Tea", declare: d => d with { Version = "9.9.9.9" })]);

        outcome.Succeeded.Should().BeFalse();
        _server.Runs.Should().BeEmpty();
        Result(outcome).Apps.Select(a => a.Outcome).Should().Equal(GatewayPublishInstallOutcomes.Skipped, GatewayPublishInstallOutcomes.Refused);
    }

    [Fact]
    public async Task A_tier_on_another_computer_is_refused()
    {
        var outcome = await RunAsync([MakeApp()], machine: "CRONUS-BC02");

        outcome.FailureMessage.Should().Contain("is on another computer").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_policy_changed_to_refuse_while_the_files_downloaded_is_obeyed()
    {
        _policy = new TextPolicy(TestTier.Replace("\"publish\", ", string.Empty, StringComparison.Ordinal));

        var outcome = await RunAsync([MakeApp()]);

        outcome.FailureMessage.Should().Contain("does not allow 'publish'").And.Contain("Nothing was changed");
        _server.Runs.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- stopping at the first failure

    [Fact]
    public async Task The_first_failure_stops_the_run_and_the_apps_after_it_are_skipped()
    {
        _server.Failures[("sync", "CRONUS Tea")] = "The schema has a destructive change. Use ForceSync to apply it.";
        var apps = new[] { MakeApp(), MakeApp(name: "CRONUS Tea"), MakeApp(name: "CRONUS Cocoa") };

        var outcome = await RunAsync(apps);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be(
            "CRONUS Tea 1.4.0.0: synchronising failed. The schema has a destructive change. Use ForceSync to apply it. The apps after it were not installed.");
        _server.Installed.Keys.Should().Equal(("default", "CRONUS Coffee"));
        _server.Runs.Select(r => r.Operation).Should().Equal("examine", "inspect", "apply", "inspect", "apply");

        var result = Result(outcome).Apps;
        result.Select(a => a.Outcome).Should().Equal(
            GatewayPublishInstallOutcomes.Installed, GatewayPublishInstallOutcomes.Failed, GatewayPublishInstallOutcomes.Skipped);
        StepWords(result[1]).Should().Equal("verify:done", "publish:done", "sync:failed");
        result[2].Message.Should().Be("Not started, because CRONUS Tea 1.4.0.0 failed.");
        Result(outcome).Restarted.Should().BeFalse();
        _progress.Should().Contain("CRONUS Cocoa 1.4.0.0: skipped, because CRONUS Tea 1.4.0.0 failed.")
            .And.NotContain(l => l.Contains("were not restarted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apps_run_in_the_order_the_command_lists_them()
    {
        var outcome = await RunAsync([MakeApp(name: "CRONUS Base"), MakeApp(name: "CRONUS Coffee"), MakeApp(name: "CRONUS Tea")]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Steps.Where(s => s.StartsWith("install", StringComparison.Ordinal)).Should().Equal(
            "install CRONUS Base 1.4.0.0 default", "install CRONUS Coffee 1.4.0.0 default", "install CRONUS Tea 1.4.0.0 default");
        _progress.Should().Contain("App 2 of 3: CRONUS Coffee 1.4.0.0 on tenant 'default'.");
    }

    [Fact]
    public async Task Steps_cut_short_by_the_time_limit_blame_no_step_and_say_to_check_the_tenant()
    {
        _server.ApplyTimesOut = true;

        var outcome = await RunAsync([MakeApp(), MakeApp(name: "CRONUS Tea")]);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("PowerShell stopped before it reported how the steps went")
            .And.Contain("did not finish in time")
            .And.Contain("check tenant 'default'")
            .And.EndWith("The apps after it were not installed.");
        var apps = Result(outcome).Apps;
        StepWords(apps[0]).Should().Equal("verify:done");
        apps.Select(a => a.Outcome).Should().Equal(GatewayPublishInstallOutcomes.Failed, GatewayPublishInstallOutcomes.Skipped);
    }

    [Fact]
    public async Task PowerShell_that_fails_outright_fails_the_app_with_its_error()
    {
        var verb = new PublishInstallVerb(_policy,
            new PowerShellRunner(new FailingProcesses(), new PowerShellExecutables("powershell.exe", "pwsh.exe"), new FakeFileAccess()),
            new FixedTierLocator(@"C:\BC\Service"), "CRONUS-BC01");
        var app = MakeApp();
        var command = new GatewayCommandMessage
        {
            Id = 1,
            Verb = GatewayVerb.PublishInstall,
            Parameters = new GatewayPublishInstallParameters { Machine = "CRONUS-BC01", Instance = "BC250", Apps = [app.Declared] }.ToParameters(),
            Files = [app.File],
        };

        var outcome = await verb.ExecuteAsync(new GatewayCommandContext(command, new Dictionary<long, string> { [app.File.Id] = app.Path }, _progress.Add), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("could not check the files the way Business Central reads them").And.Contain("The term 'Get-NAVAppInfo' is not recognized")
            .And.Contain("Nothing was changed");
        Result(outcome).Apps.Single().Outcome.Should().Be(GatewayPublishInstallOutcomes.Refused);
    }

    private sealed class FailingProcesses : IProcessRunner
    {
        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct) =>
            Task.FromResult(new ProcessOutcome(1, string.Empty, "The term 'Get-NAVAppInfo' is not recognized as a name of a cmdlet.", false));
    }

    // ---------------------------------------------------------------- the contract and the dispatch

    [Fact]
    public void The_typed_parameters_carry_everything_the_verb_reads()
    {
        var parameters = new GatewayPublishInstallParameters
        {
            Machine = "CRONUS-BC01",
            Instance = "BC250",
            Tenant = "cronus-dk",
            SyncMode = GatewaySyncModes.ForceSync,
            UnpublishPrevious = false,
            Apps = [new GatewayPublishInstallApp { File = "a.app", Id = AppFiles.CoffeeId, Name = "CRONUS Coffee", Publisher = "CRONUS Partner", Version = "1.4.0.0", Signed = true }],
        }.ToParameters();
        var command = new GatewayCommandMessage
        {
            Verb = GatewayVerb.PublishInstall,
            Parameters = parameters,
            Files = [new GatewayCommandFileMessage { Id = 1, Name = "a.app" }],
        };

        var request = PolicyRequest.FromCommand(command, out var problem);

        problem.Should().BeNull();
        request!.Target.Should().Be(new TierRef("CRONUS-BC01", "BC250"));
        request.Tenant.Should().Be("cronus-dk");
        request.UnpublishPrevious.Should().BeFalse();
        request.Apps.Should().Equal(new AppToPublish("a.app", "CRONUS Partner", true, AppFiles.CoffeeId.ToString("D"), "CRONUS Coffee", "1.4.0.0"));
        parameters[GatewayCommandParameters.SyncMode]!.GetValue<string>().Should().Be("ForceSync");
        request.SyncMode.Should().Be(GatewaySyncModes.ForceSync);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_app_without_a_name_leaves_the_name_out_and_the_command_still_runs(string? name)
    {
        var app = MakeApp(declare: d => d with { Name = name });

        var parameters = new GatewayPublishInstallParameters { Machine = "CRONUS-BC01", Instance = "BC250", Apps = [app.Declared] }.ToParameters();
        parameters[GatewayCommandParameters.Apps]![0]!.AsObject().ContainsKey(GatewayCommandParameters.AppName).Should().BeFalse();

        var outcome = await RunAsync([app]);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        Result(outcome).Apps.Single().Name.Should().Be("CRONUS Coffee", "the name is read from the file");
    }

    [Fact]
    public void A_blank_name_written_by_hand_still_refuses_the_command()
    {
        var parameters = new GatewayPublishInstallParameters
        {
            Machine = "CRONUS-BC01",
            Instance = "BC250",
            Apps = [new GatewayPublishInstallApp { File = "a.app", Id = AppFiles.CoffeeId, Publisher = "CRONUS Partner", Version = "1.4.0.0" }],
        }.ToParameters();
        parameters[GatewayCommandParameters.Apps]![0]![GatewayCommandParameters.AppName] = "";

        var request = PolicyRequest.FromCommand(
            new GatewayCommandMessage { Verb = GatewayVerb.PublishInstall, Parameters = parameters, Files = [new GatewayCommandFileMessage { Id = 1, Name = "a.app" }] },
            out var problem);

        request.Should().BeNull();
        problem.Should().Contain("'name' is not text the gateway can read");
    }

    [Fact]
    public void The_result_reads_back_the_way_AL_Workbench_will_read_it()
    {
        var result = new GatewayPublishInstallResult
        {
            Apps = [new GatewayPublishInstallAppResult
            {
                File = "a.app", Id = AppFiles.CoffeeId.ToString("D"), Name = "CRONUS Coffee", Publisher = "CRONUS Partner", Version = "1.4.0.0",
                Outcome = GatewayPublishInstallOutcomes.Upgraded, FromVersion = "1.3.0.0",
                Steps = [new GatewayPublishInstallStep { Step = GatewayPublishInstallSteps.Upgrade, Outcome = GatewayPublishInstallStepOutcomes.Done }],
            }],
        };

        var json = result.ToJson();

        json["apps"]![0]!["fromVersion"]!.GetValue<string>().Should().Be("1.3.0.0");
        json["apps"]![0]!.AsObject().ContainsKey("message").Should().BeFalse();
        GatewayPublishInstallResult.FromJson(json).Should().BeEquivalentTo(result);
        GatewayPublishInstallResult.FromJson(new JsonObject { ["apps"] = "nope" }).Should().BeNull();
    }

    [Fact]
    public async Task Each_verb_goes_to_its_own_executor_and_one_without_fails_saying_so()
    {
        var publish = new RecordingExecutor();
        var verbs = new VerbExecutors(new Dictionary<GatewayVerb, ICommandExecutor> { [GatewayVerb.PublishInstall] = publish });
        GatewayCommandContext Context(GatewayVerb verb) => new(new GatewayCommandMessage { Verb = verb }, new Dictionary<long, string>(), _ => { });

        (await verbs.ExecuteAsync(Context(GatewayVerb.PublishInstall), CancellationToken.None)).Succeeded.Should().BeTrue();
        var other = await verbs.ExecuteAsync(Context(GatewayVerb.ImportLicence), CancellationToken.None);

        publish.Calls.Should().ContainSingle();
        other.Succeeded.Should().BeFalse();
        other.FailureMessage.Should().Contain("cannot run 'import-licence' commands yet");
    }
}
