using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Which checks answer which probe, through the real host. The split is load-bearing
/// (#377): the container's health check polls <c>/healthz</c> and restarts on a 503, so a
/// background worker that is merely stuck must show on <c>/healthz/workers</c> only,
/// while a database the app cannot reach must fail <c>/healthz</c>.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class HealthEndpointTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_stalled_worker_fails_the_workers_probe_and_not_the_liveness_probe()
    {
        using var factory = new EndpointFactory(_db);
        using var client = factory.CreateClient();
        (await StatusAsync(client, "/healthz/workers")).Should().Be(HttpStatusCode.OK, "nothing is stalled yet");

        // A job that has been running for longer than its worker allows.
        var heartbeat = factory.Services.GetRequiredService<WorkerHeartbeatRegistry>()
            .Register("StalledForTest", maxActiveDuration: TimeSpan.FromTicks(-1), maxIdleSilence: null);
        heartbeat.BeginActive();

        (await StatusAsync(client, "/healthz/workers")).Should().Be(HttpStatusCode.ServiceUnavailable);
        (await StatusAsync(client, "/healthz")).Should().Be(HttpStatusCode.OK,
            "a stuck background job must not get the container restarted");
    }

    [Fact]
    public async Task An_unreachable_database_fails_the_liveness_probe()
    {
        // One context for the test's few sequential requests, disposed with the test.
        await using var unreachable = UnreachableContext();
        using var factory = new EndpointFactory(_db, services =>
            services.AddScoped(_ => new DatabaseHealthCheck(unreachable)));
        using var client = factory.CreateClient();

        (await StatusAsync(client, "/healthz")).Should().Be(HttpStatusCode.ServiceUnavailable);
        (await StatusAsync(client, "/healthz/workers")).Should().Be(HttpStatusCode.OK,
            "the workers probe reports on workers only");
    }

    /// <summary>A context pointed at a port nothing listens on.</summary>
    internal static AppDbContext UnreachableContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=nobody;Password=x;Timeout=2")
        .Options);

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
