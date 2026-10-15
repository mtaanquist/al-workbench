using System.Reflection;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The delivery seam (#1374): <see cref="DeliveryTargetFactory"/> picks the install
/// surface from the environment's kind, and <see cref="AdminCenterDeliveryTarget"/> keeps
/// the contract <see cref="IDeliveryTarget"/> promises - every call addressed to the
/// environment it was built for, with the token it connected with. The orchestration
/// itself is covered end to end by <c>DeliveryServiceTests</c>.
/// </summary>
public sealed class DeliveryTargetTests
{
    private const string Env = "Production";
    private const int ProjectId = 42;

    private readonly Recorder _calls = new();
    private readonly IDeliveryTokenSource _tokens;
    private readonly IBcAppManagementClient _apps;
    private readonly IBcAdminClient _admin;

    public DeliveryTargetTests()
    {
        _tokens = Recorder.Create<IDeliveryTokenSource>(_calls);
        _apps = Recorder.Create<IBcAppManagementClient>(_calls);
        _admin = Recorder.Create<IBcAdminClient>(_calls);
        _calls.Returns["AcquireDeliveryContextAsync"] = _ => new BcDeliveryContext("token-1", Guid.Empty);
    }

    private DeliveryTargetFactory Factory() => new(_tokens, _apps, _admin);

    private static DeliveryTargetEnvironment Online(string? family = "BusinessCentral") =>
        new(ProjectId, Env, OeEnvironmentKind.Online, 7, family);

    // ── Factory ─────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_builds_the_admin_center_target_for_an_online_environment()
    {
        var factory = Factory();

        factory.Unavailable(OeEnvironmentKind.Online, Env).Should().BeNull();
        factory.Create(Online()).Should().BeOfType<AdminCenterDeliveryTarget>();
    }

    [Fact]
    public void Factory_refuses_an_on_premises_service_tier_when_the_gateway_services_are_not_registered()
    {
        var factory = Factory();

        var why = factory.Unavailable(OeEnvironmentKind.OnPremises, "CRONUS-BC02");
        why.Should().Be("CRONUS-BC02 is an on-premises service tier, and deploying through its gateway isn't set up here, so nothing was sent.");

        var act = () => factory.Create(new DeliveryTargetEnvironment(ProjectId, "CRONUS-BC02", OeEnvironmentKind.OnPremises));
        act.Should().Throw<InvalidOperationException>().WithMessage(why);
        _calls.Names.Should().BeEmpty("refusing calls nothing");
    }

    [Fact]
    public void Factory_reports_deferred_schedules_for_online_only()
    {
        var factory = Factory();

        factory.CapabilitiesFor(OeEnvironmentKind.Online).DeferredSchedules.Should().BeTrue();
        factory.CapabilitiesFor(OeEnvironmentKind.OnPremises).DeferredSchedules.Should()
            .BeFalse("on-premises has only now and the delivery window");
        factory.Create(Online()).Capabilities.Should().Be(factory.CapabilitiesFor(OeEnvironmentKind.Online));
    }

    // ── Admin Center target ─────────────────────────────────────────────────

    [Fact]
    public async Task Admin_center_target_calls_nothing_until_it_connects()
    {
        var target = Factory().Create(Online());

        var act = () => target.ListInstalledAppsAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _calls.Names.Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_center_target_passes_a_sign_in_failure_through_as_the_connections_own_sentence()
    {
        _calls.Returns["AcquireDeliveryContextAsync"] = _ =>
            throw new BcApiException(null, "The client secret has expired.");
        var target = Factory().Create(Online());

        var act = () => target.ConnectAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<BcApiException>()).Which.Message.Should().Be("The client secret has expired.");
    }

    [Fact]
    public async Task Admin_center_target_addresses_every_call_to_its_environment_with_the_connected_token()
    {
        _calls.Returns["ListInstalledAppsAsync"] = _ => (IReadOnlyList<BcInstalledApp>)[];
        _calls.Returns["ListScheduledPteOperationsAsync"] = _ => (IReadOnlyList<BcScheduledPteOperation>)[];
        _calls.Returns["ListSessionsAsync"] = _ => (IReadOnlyList<BcSession>)[];
        _calls.Returns["InstallPteAsync"] = _ => Operation(appId: null);
        _calls.Returns["RemoveScheduledPteVersionAsync"] = _ => Operation(appId: null);
        var target = Factory().Create(Online("BusinessCentral"));

        await target.ConnectAsync(CancellationToken.None);
        await target.ListInstalledAppsAsync(CancellationToken.None);
        await target.ListHeldInstallsAsync(CancellationToken.None);
        await target.CheckOpenSessionsAsync(CancellationToken.None);
        var package = new DeliveryPackage(1, "CRONUS_Core_1.0.0.0.app", null, "CRONUS Core", "1.0.0.0", 3, _ => Task.FromResult<byte[]>([1, 2, 3]));
        await target.PrepareInstallsAsync(new DeliveryInstallBatch([package], "Add", true, null, false), CancellationToken.None);
        await target.StartInstallAsync(package, "Current version", "Add", CancellationToken.None);
        await target.CancelHeldInstallAsync(Guid.NewGuid(), "1.0.0.0", "Next minor update", CancellationToken.None);

        foreach (var call in _calls.Calls.Where(c => c.Name != "AcquireDeliveryContextAsync"))
        {
            call.Args[0].Should().Be("token-1", $"{call.Name} uses the connected token");
            call.Args[1].Should().Be("BusinessCentral", $"{call.Name} addresses the environment's family");
            call.Args[2].Should().Be(Env, $"{call.Name} addresses the environment");
        }
        var install = _calls.Calls.Single(c => c.Name == "InstallPteAsync");
        install.Args[7].Should().Be(string.Empty, "no install language is sent");
        install.Args[8].Should().Be(true, "dependencies are installed or updated as needed");
    }

    [Fact]
    public async Task Admin_center_target_defaults_the_family_for_app_calls_but_reads_the_environment_with_the_family_as_last_read()
    {
        _calls.Returns["ListInstalledAppsAsync"] = _ => (IReadOnlyList<BcInstalledApp>)[];
        _calls.Returns["GetEnvironmentAsync"] = _ => new BcEnvironment(Env, "Production") { Status = "Active" };
        var target = Factory().Create(Online(family: null));

        await target.ConnectAsync(CancellationToken.None);
        await target.ListInstalledAppsAsync(CancellationToken.None);
        await target.ReadStatusAsync(CancellationToken.None);

        _calls.Calls.Single(c => c.Name == "ListInstalledAppsAsync").Args[1].Should().Be(BcConstants.DefaultApplicationFamily);
        _calls.Calls.Single(c => c.Name == "GetEnvironmentAsync").Args[1].Should().BeNull();
    }

    [Fact]
    public async Task Admin_center_target_renews_its_token_through_the_token_source()
    {
        var issued = 0;
        _calls.Returns["AcquireDeliveryContextAsync"] = _ => new BcDeliveryContext($"token-{++issued}", Guid.Empty);
        _calls.Returns["ListInstalledAppsAsync"] = _ => (IReadOnlyList<BcInstalledApp>)[];
        var target = Factory().Create(Online());

        await target.ConnectAsync(CancellationToken.None);
        await target.RenewAsync(CancellationToken.None);
        await target.ListInstalledAppsAsync(CancellationToken.None);

        _calls.Calls.Single(c => c.Name == "ListInstalledAppsAsync").Args[0].Should().Be("token-2");
    }

    [Fact]
    public async Task Admin_center_target_reports_a_missing_environment_with_its_refusal()
    {
        _calls.Returns["GetEnvironmentAsync"] = _ => null;
        var target = Factory().Create(Online());
        await target.ConnectAsync(CancellationToken.None);

        var status = await target.ReadStatusAsync(CancellationToken.None);

        status.Exists.Should().BeFalse();
        status.Refusal.Should().Be("Business Central no longer has an environment called 'Production'. Refresh the environments on the solution's Business Central page.");
    }

    [Theory]
    [InlineData("Active", false)]
    [InlineData("Upgrading", true)]
    public async Task Admin_center_target_reports_the_live_status_and_whether_it_refuses(string liveStatus, bool refused)
    {
        _calls.Returns["GetEnvironmentAsync"] = _ => new BcEnvironment(Env, "Sandbox") { Status = liveStatus };
        var target = Factory().Create(Online());
        await target.ConnectAsync(CancellationToken.None);

        var status = await target.ReadStatusAsync(CancellationToken.None);

        status.Exists.Should().BeTrue();
        status.Type.Should().Be("Sandbox");
        status.Status.Should().Be(liveStatus);
        status.Refusal.Should().Be(BcEnvironmentStatus.RefusalMessage(Env, liveStatus));
        (status.Refusal is not null).Should().Be(refused);
    }

    [Fact]
    public async Task Admin_center_target_waits_for_an_install_to_succeed()
    {
        var started = Operation(Guid.NewGuid());
        _calls.Returns["GetAppOperationAsync"] = _ => started with { Status = BcAppOperationStatus.Succeeded };
        var target = Factory().Create(Online());
        await target.ConnectAsync(CancellationToken.None);

        var outcome = await target.WaitForInstallAsync(started, TimeSpan.Zero, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        outcome.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_center_target_counts_an_install_without_an_app_id_as_done_as_it_always_has()
    {
        var target = Factory().Create(Online());
        await target.ConnectAsync(CancellationToken.None);

        var outcome = await target.WaitForInstallAsync(Operation(appId: null), TimeSpan.Zero, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        outcome.Completed.Should().BeTrue();
        outcome.Message.Should().Contain("wasn't confirmed");
        _calls.Names.Should().NotContain("GetAppOperationAsync", "there is no app id to poll");
    }

    [Fact]
    public async Task Admin_center_target_reports_a_failed_install_as_the_outcome_not_an_exception()
    {
        var started = Operation(Guid.NewGuid());
        _calls.Returns["GetAppOperationAsync"] = _ => started with
        {
            Status = BcAppOperationStatus.Failed,
            ErrorMessage = "The install failed.",
        };
        var target = Factory().Create(Online());
        await target.ConnectAsync(CancellationToken.None);

        var outcome = await target.WaitForInstallAsync(started, TimeSpan.Zero, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        outcome.Completed.Should().BeFalse();
        outcome.Message.Should().NotBeNullOrEmpty();
    }

    private static BcAppOperation Operation(Guid? appId) => new(
        Guid.NewGuid(), appId, "install", BcAppOperationStatus.Running, "running",
        "", "1.0.0.0", null, "", "", "", false, "", null, null, null);

    // ── A recording stand-in for the three Business Central seams ───────────

    /// <summary>The calls made through any of the proxied interfaces, and what each method name answers.</summary>
    private sealed class Recorder
    {
        public List<(string Name, object?[] Args)> Calls { get; } = [];
        public Dictionary<string, Func<object?[], object?>> Returns { get; } = [];
        public IEnumerable<string> Names => Calls.Select(c => c.Name);

        public static T Create<T>(Recorder recorder) where T : class
        {
            var proxy = DispatchProxy.Create<T, RecordingProxy>();
            ((RecordingProxy)(object)proxy).Recorder = recorder;
            return proxy;
        }
    }

    public class RecordingProxy : DispatchProxy
    {
        internal object? Recorder { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var recorder = (Recorder)Recorder!;
            args ??= [];
            recorder.Calls.Add((method!.Name, args));
            var returnType = method.ReturnType;
            object? value = null;
            Exception? thrown = null;
            if (recorder.Returns.TryGetValue(method.Name, out var answer))
            {
                try { value = answer(args); }
                catch (Exception ex) { thrown = ex; }
            }
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                thrown = new NotSupportedException($"{method.Name} was not expected.");
            }

            if (returnType == typeof(Task))
            {
                return thrown is null ? Task.CompletedTask : Task.FromException(thrown);
            }
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var source = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType))!;
                if (thrown is null)
                {
                    source.GetType().GetMethod("SetResult")!.Invoke(source, [value]);
                }
                else
                {
                    source.GetType().GetMethod("SetException", [typeof(Exception)])!.Invoke(source, [thrown]);
                }
                return source.GetType().GetProperty("Task")!.GetValue(source);
            }
            if (thrown is not null) throw thrown;
            return value;
        }
    }
}
