using ALDevToolbox.Services.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// xUnit collection for the few test classes that boot the app with a
/// process-wide environment variable set (<see cref="EndpointFactory"/>'s
/// <c>environment</c> argument). Startup reads some knobs, such as
/// <c>SINGLE_TENANT_MODE</c>, straight off the environment, so a host booting
/// in parallel would pick the value up too. With parallelisation disabled,
/// xUnit runs this collection on its own, after the parallel ones.
///
/// Every other app-booting class runs in parallel: the factory hands the
/// connection string to its host through configuration, not the environment
/// (#1535). Configuration keys belong in the <c>settings</c> argument, which
/// needs no collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}

/// <summary>
/// Boots <c>Program.cs</c> end-to-end against a per-fixture Postgres database
/// supplied by <see cref="TestDb"/>. Used by endpoint behaviour tests that
/// need the real auth + antiforgery + routing stack — the service-layer
/// fixtures don't.
///
/// The lifecycle is tied to <see cref="TestDb"/>: each fixture owns one
/// scratch database, one host, and disposes both together. CI's service
/// container is the same one used by <c>TestDb</c>; we don't fork a second
/// host (Issue #69 §"shared fixture").
///
/// The factory touches no process-wide state unless a test passes
/// <c>environment</c>, so factories in different test classes can boot at
/// the same time.
/// </summary>
public sealed class EndpointFactory : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Dictionary<string, string?> _previousExtraEnvironment = [];

    /// <param name="configureServices">
    /// Applied via <c>ConfigureTestServices</c>, so a registration here wins
    /// over the one <c>Program.cs</c> made — the hook for swapping a real
    /// dependency (SMTP, say) for a fake.
    /// </param>
    /// <param name="environment">
    /// Extra environment variables to set for the lifetime of the factory, for
    /// the knobs startup reads straight off the environment rather than through
    /// configuration. The previous values are restored on dispose. The variable
    /// is process-wide, so a class that passes this must be in
    /// <see cref="ProcessEnvironmentCollection"/>.
    /// </param>
    /// <param name="settings">
    /// Extra configuration values (<c>Auth:WebAuthn:RpId</c>, say), handed to
    /// this host alone. Prefer this to <paramref name="environment"/> for
    /// anything the app reads through <c>IConfiguration</c>.
    /// </param>
    /// <param name="keepSchedulers">
    /// The polled schedulers (backups, the BCQuality mirror, the Microsoft feed
    /// sync and the rest) are taken out of the host by default: they would clone
    /// repositories and call Microsoft and GitHub from any host that outlives
    /// their startup delay, and no endpoint test is about them. Scheduler tests
    /// drive the scheduler directly. Pass true to keep the registrations.
    /// </param>
    public EndpointFactory(
        TestDb db,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool keepSchedulers = false)
    {
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            _previousExtraEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(LocateProjectFolder());
                builder.UseEnvironment("Test");
                // UseSetting values reach WebApplication.CreateBuilder's
                // configuration before Program.cs reads it, so AddAppDatabase
                // binds to this fixture's database. Setting the
                // ConnectionStrings__DefaultConnection env var instead (as this
                // factory once did) forced every app-booting class into one
                // serial collection.
                builder.UseSetting("ConnectionStrings:DefaultConnection", db.ConnectionString);
                foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
                {
                    builder.UseSetting(key, value);
                }
                if (!keepSchedulers)
                {
                    builder.ConfigureTestServices(RemoveSchedulers);
                }
                if (configureServices is not null)
                {
                    builder.ConfigureTestServices(configureServices);
                }
            });
    }

    /// <summary>
    /// Returns a fresh <see cref="HttpClient"/>. Redirects are not followed so
    /// tests can assert the redirect target. The auth cookie defaults to
    /// "Secure" so requests must use HTTPS — base address is set to https.
    /// Pass <paramref name="handleCookies"/> false to send the Cookie header
    /// by hand, for a test that needs the cookie value itself.
    /// </summary>
    public HttpClient CreateClient(bool handleCookies = true) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost/"),
            HandleCookies = handleCookies,
        });

    public IServiceProvider Services => _factory.Services;

    public void Dispose()
    {
        _factory.Dispose();
        foreach (var (key, value) in _previousExtraEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static void RemoveSchedulers(IServiceCollection services)
    {
        var schedulers = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType is { } type
                && typeof(PolledScheduler).IsAssignableFrom(type))
            .ToList();
        foreach (var descriptor in schedulers)
        {
            services.Remove(descriptor);
        }
    }

    private static string LocateProjectFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ALDevToolbox", "ALDevToolbox.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "ALDevToolbox");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the ALDevToolbox project folder from " + AppContext.BaseDirectory);
    }
}
