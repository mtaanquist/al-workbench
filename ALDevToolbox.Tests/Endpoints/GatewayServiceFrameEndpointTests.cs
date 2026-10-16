using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Gateways;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The gateway's own code (<c>ALWorkbench.Gateway.Core</c>) against the real
/// <c>/gateway</c> routes: register with a code, wait for approval, take a command,
/// download and check its file, report progress and the result. Only the Windows key
/// store is replaced, by an in-memory key.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GatewayServiceFrameEndpointTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly GatewayCommandSignal _signal = new() { LongPollTimeout = TimeSpan.FromSeconds(1) };
    private readonly EndpointFactory _factory;
    private readonly ScratchFolder _scratch = new();

    public GatewayServiceFrameEndpointTests()
    {
        _factory = new EndpointFactory(_db, services => services.AddSingleton(_signal));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
        _scratch.Dispose();
    }

    [Fact]
    public async Task A_registered_and_approved_gateway_runs_a_command_end_to_end()
    {
        var admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "ida@cronus.example");
        _db.OrgContext.CurrentUserId = admin.Id;
        IssuedGatewayCode code;
        await using (var ctx = _db.NewContext())
        {
            code = await GatewayTestKit.NewService(ctx, _db.OrgContext, TimeProvider.System).CreateRegistrationCodeAsync();
        }

        using var http = _factory.CreateClient();
        var keys = new FakeKeyStore();
        var settings = new InMemorySettingsStore();
        var log = new ListLogger();
        var registered = await new GatewayRegistration(http, keys, settings, SystemGatewayClock.Instance, log)
            .RegisterAsync("https://localhost", code.Code, "CRONUS-BC01", "1.0.0", CancellationToken.None);

        await using (var ctx = _db.NewContext())
        {
            await GatewayTestKit.NewService(ctx, _db.OrgContext, TimeProvider.System).ApproveAsync(registered.GatewayId, registered.KeyFingerprint);
        }
        var app = RandomNumberGenerator.GetBytes(512);
        long commandId;
        await using (var ctx = _db.NewContext())
        {
            commandId = await new GatewayCommandService(ctx, _db.OrgContext, _signal, TimeProvider.System, NullLogger<GatewayCommandService>.Instance)
                .EnqueueAsync(new GatewayCommandRequest(registered.GatewayId, GatewayVerb.PublishInstall,
                    RequestedByUserId: admin.Id, Files: [new GatewayCommandFileInput("CRONUS_Coffee_1.4.0.0.app", app)]));
        }

        var executor = new RecordingExecutor
        {
            Run = (context, _) =>
            {
                context.Progress("Published CRONUS Coffee 1.4.0.0");
                return Task.FromResult(GatewayCommandOutcome.Success(new JsonObject { ["apps"] = 1 }));
            },
        };
        var folder = new GatewayDataFolder(_scratch.Path);
        var runner = new GatewayRunner(http, settings, keys, new FileRunOnceStore(folder.RunOnceFile, SystemGatewayClock.Instance),
            executor, SystemGatewayClock.Instance, log, new GatewayRunnerOptions(folder.WorkFolder, "CRONUS-BC01", "1.0.0"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = runner.RunAsync(stop.Token);

        GatewayCommand row;
        while (true)
        {
            await using (var ctx = _db.NewContext())
            {
                row = await ctx.GatewayCommands.AsNoTracking().SingleAsync(c => c.Id == commandId);
            }
            if (row.Status is GatewayCommandStatus.Succeeded or GatewayCommandStatus.Failed || run.IsCompleted) break;
            await Task.Delay(100);
        }
        await stop.CancelAsync();
        (await run).Should().Be(GatewayRunOutcome.Stopped);

        row.Status.Should().Be(GatewayCommandStatus.Succeeded, row.FailureMessage);
        row.Log.Should().Contain("Published CRONUS Coffee 1.4.0.0");
        executor.FileContents.Values.Single().Should().Equal(app);
        log.Messages(GatewayLog.CommandStartedId).Should().ContainSingle().Which.Should().Contain("ida@cronus.example");
    }
}
