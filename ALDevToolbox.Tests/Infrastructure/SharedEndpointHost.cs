using System.Collections.Concurrent;
using ALDevToolbox.Services.Account;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// One app host and one scratch database for every test in a class, as an
/// xUnit class fixture (<c>IClassFixture&lt;SharedEndpointHost&gt;</c>).
///
/// <para>Booting a host costs about a second and its first page render most of
/// another, against a few milliseconds for each request after that (#1536). A
/// class that built an <see cref="EndpointFactory"/> per test paid that for every
/// case: <c>LegacyRedirectTests</c> booted 38 hosts to check 38 redirects. The
/// tests of one class run one after another, so sharing the host costs no
/// isolation between classes - only between the tests of the class, which is
/// what the rules below are for.</para>
///
/// <para>A class that shares a host must not rely on an empty database or a
/// fresh host. Each test seeds data of its own (a user from
/// <see cref="UniqueEmail"/>, rows it created and looks up by id) and asserts
/// only on that. A class whose tests change state the whole host or database
/// shares - a <c>system_settings</c> value, maintenance mode, a singleton a test
/// configures, a rate limiter a test drains on purpose - keeps a host per test,
/// or gives just those tests one of their own. The README's "Endpoint tests"
/// section has the full rule.</para>
///
/// <para>A class that needs services swapped derives from this and passes them
/// to the protected constructor; xUnit builds the derived fixture with its
/// parameterless constructor.</para>
/// </summary>
public class SharedEndpointHost : IDisposable
{
    private readonly ConcurrentDictionary<string, string> _hashes = new();

    public SharedEndpointHost()
        : this(configureServices: null)
    {
    }

    protected SharedEndpointHost(
        Action<IServiceCollection>? configureServices,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        Db = new TestDb();
        Factory = new EndpointFactory(Db, configureServices, settings: settings);
    }

    /// <summary>The scratch database every test in the class shares.</summary>
    public TestDb Db { get; }

    /// <summary>The host every test in the class shares.</summary>
    public EndpointFactory Factory { get; }

    /// <summary>
    /// An address no other test in the run has used. Sign-in is rate limited per
    /// email address, so a class that signed the same seeded user in from every
    /// test would lock itself out part way through; a unique user per test also
    /// keeps one test's rows out of another's assertions.
    /// </summary>
    public static string UniqueEmail(string name) =>
        $"{name}-{Guid.NewGuid().ToString("N")[..12]}@cronus.example";

    /// <summary>
    /// The production bcrypt hash of <paramref name="password"/>, computed once per
    /// fixture. At the app's work factor each hash costs about a third of a
    /// second, and the tests only need a hash the real sign-in accepts.
    /// </summary>
    public string HashPassword(string password) =>
        _hashes.GetOrAdd(password, p => BCrypt.Net.BCrypt.HashPassword(p, AuthService.BcryptWorkFactor));

    public virtual void Dispose()
    {
        Factory.Dispose();
        Db.Dispose();
        GC.SuppressFinalize(this);
    }
}
