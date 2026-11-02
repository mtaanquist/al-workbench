using ALDevToolbox.Endpoints;
using ALDevToolbox.Startup;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// PUBLIC_BASE_URL reaches the app only through <see cref="PublicOrigin.FromEnvironment"/>,
/// which startup registers as a singleton. The endpoint tests for the #670 reset
/// link register that singleton directly, so this pins the half they skip: the
/// variable itself is read, parsed, and registered.
/// </summary>
// Sets PUBLIC_BASE_URL, a process-wide variable.
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublicOriginEnvironmentTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(PublicOrigin.EnvVarName);

    public void Dispose() => Environment.SetEnvironmentVariable(PublicOrigin.EnvVarName, _previous);

    [Fact]
    public void A_valid_value_is_read_and_normalised()
    {
        Environment.SetEnvironmentVariable(PublicOrigin.EnvVarName, " https://workbench.cronus.example/ ");

        var origin = PublicOrigin.FromEnvironment();

        origin.Configured.Should().Be("https://workbench.cronus.example");
        origin.InvalidValue.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_leaves_it_unconfigured_without_a_warning(string? raw)
    {
        Environment.SetEnvironmentVariable(PublicOrigin.EnvVarName, raw);

        var origin = PublicOrigin.FromEnvironment();

        origin.IsConfigured.Should().BeFalse();
        origin.InvalidValue.Should().BeNull();
    }

    [Fact]
    public void An_invalid_value_leaves_it_unconfigured_and_keeps_the_value_for_the_warning()
    {
        Environment.SetEnvironmentVariable(PublicOrigin.EnvVarName, " ftp://workbench.cronus.example ");

        var origin = PublicOrigin.FromEnvironment();

        origin.IsConfigured.Should().BeFalse();
        origin.InvalidValue.Should().Be("ftp://workbench.cronus.example");
    }

    [Fact]
    public void Startup_registers_the_value_it_read()
    {
        Environment.SetEnvironmentVariable(PublicOrigin.EnvVarName, "https://workbench.cronus.example");
        var services = new ServiceCollection();

        var returned = services.AddPublicOrigin();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<PublicOrigin>().Should().BeSameAs(returned);
        returned.Configured.Should().Be("https://workbench.cronus.example");
    }
}
