using ALDevToolbox.Domain.Entities;
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
    /// Adds an organisation no other test uses and returns its id, shaped like the
    /// default one <see cref="TestDb"/> seeds. For a test whose subject is state the
    /// whole organisation shares - a connected GitHub installation, the tools it
    /// switched off - so the tests of a class can each change it without seeing
    /// another's.
    /// </summary>
    public int SeedOrganization()
    {
        using var ctx = Db.NewContext();
        var slug = $"org-{Guid.NewGuid().ToString("N")[..12]}";
        var org = new Organization { Name = slug, Slug = slug, CreatedAt = DateTime.UtcNow };
        ctx.Organizations.Add(org);
        ctx.SaveChanges();
        return org.Id;
    }

    /// <summary>
    /// A bcrypt hash of <paramref name="password"/> the real sign-in accepts,
    /// made cheaply by <see cref="TestPasswords"/>.
    /// </summary>
    public string HashPassword(string password) => TestPasswords.Hash(password);

    public virtual void Dispose()
    {
        Factory.Dispose();
        Db.Dispose();
        GC.SuppressFinalize(this);
    }
}
