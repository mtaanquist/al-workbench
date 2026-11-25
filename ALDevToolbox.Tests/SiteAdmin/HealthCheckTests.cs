using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Tests for the M21 health checks. The Data Protection check is the
/// interesting one — a deliberately-broken provider must surface as
/// <see cref="HealthStatus.Unhealthy"/> so <c>/healthz</c> drops the node
/// out of rotation.
/// </summary>
public sealed class HealthCheckTests
{
    [Fact]
    public async Task DataProtectionHealthCheck_returns_healthy_with_a_working_provider()
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("ALDevToolbox.Tests");
        var sp = services.BuildServiceProvider();
        var check = new DataProtectionHealthCheck(
            sp.GetRequiredService<IDataProtectionProvider>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task DataProtectionHealthCheck_returns_unhealthy_when_round_trip_throws()
    {
        var check = new DataProtectionHealthCheck(new ThrowingDataProtectionProvider());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task StartupReadinessHealthCheck_is_unhealthy_until_MarkReady()
    {
        var state = new StartupReadinessState();
        var check = new StartupReadinessHealthCheck(state);

        var before = await check.CheckHealthAsync(new HealthCheckContext());
        before.Status.Should().Be(HealthStatus.Unhealthy);

        state.MarkReady();

        var after = await check.CheckHealthAsync(new HealthCheckContext());
        after.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task DatabaseHealthCheck_is_healthy_when_the_database_answers()
    {
        using var db = new ALDevToolbox.Tests.Infrastructure.TestDb();
        await using var ctx = db.NewContext();

        var result = await new DatabaseHealthCheck(ctx).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    /// <summary>The probe must answer 503, not fail with an exception of its own.</summary>
    [Fact]
    public async Task DatabaseHealthCheck_is_unhealthy_rather_than_throwing_when_the_database_is_unreachable()
    {
        await using var ctx = ALDevToolbox.Tests.Endpoints.HealthEndpointTests.UnreachableContext();

        var result = await new DatabaseHealthCheck(ctx).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    private sealed class ThrowingDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new ThrowingProtector();
    }

    private sealed class ThrowingProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext)
            => throw new CryptographicProtectionException("simulated key-ring failure");
        public byte[] Unprotect(byte[] protectedData)
            => throw new CryptographicProtectionException("simulated key-ring failure");
    }

    private sealed class CryptographicProtectionException : Exception
    {
        public CryptographicProtectionException(string message) : base(message) { }
    }
}
