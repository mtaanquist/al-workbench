using System.Reflection;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Keeps code that reads the signed-in user on the caller's synchronization
/// context (#1724, #1729).
///
/// <para>When a page renders on the server (a static page, or the prerender of
/// an interactive one) the renderer reads <c>HttpContext.User</c> on its own
/// synchronization context. A service that awaits with
/// <c>ConfigureAwait(false)</c> and then reads the user, directly through
/// <see cref="IOrganizationContext"/> or through the tenant filter of its next
/// query, makes that read on a pool thread at the same time. <c>HttpContext</c>
/// is not thread-safe: two first reads race in its feature cache, one sees
/// null, and the page becomes a 500. ASP.NET Core has no synchronization
/// context of its own, so <c>ConfigureAwait(false)</c> buys nothing in a
/// request; the rule is simply not to use it in code that can read the
/// user.</para>
///
/// <para>"Can read the user" is decided from the code, not from a list: a type
/// whose source names <see cref="AppDbContext"/>, <see cref="IOrganizationContext"/>
/// or the HTTP context, or whose constructor takes (directly, or through
/// another such type or an interface one of them implements) something that
/// does. Hosted services are exempt: they run with no request, and keep
/// <c>ConfigureAwait(false)</c> because their continuations have no context to
/// return to anyway.</para>
///
/// <para>What the scan cannot see: a static helper that runs a caller's
/// delegate after its own <c>ConfigureAwait(false)</c> (<c>BcAppOperationPoller</c>
/// was one), and EF Core's own internals, which continue off the caller's
/// context before some interceptor callbacks run. Keep helpers that take
/// callbacks free of it too.</para>
/// </summary>
public sealed class ConfigureAwaitCallerContextTests
{
    private static readonly Regex Declaration =
        new(@"\b(?:record\s+(?:class|struct)|class|record|struct|interface)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled);

    private static readonly Regex UserReadingName =
        new(@"\b(?:AppDbContext|IOrganizationContext|IHttpContextAccessor|HttpContext|AuthenticationStateProvider)\b",
            RegexOptions.Compiled);

    [Fact]
    public void Code_that_can_read_the_signed_in_user_does_not_leave_the_callers_context()
    {
        var sources = CSharpSources();
        var readers = UserReadingTypes(sources);
        var hosted = AppTypes()
            .Where(t => typeof(IHostedService).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var file in sources)
        {
            var lines = file.Lines.Select((l, i) => (l.Code, Line: i + 1))
                .Where(l => l.Code.Contains("ConfigureAwait(false)", StringComparison.Ordinal))
                .ToList();
            if (lines.Count == 0) continue;

            var declared = DeclaredKeys(file);
            if (declared.Any(hosted.Contains)) continue;
            var readsUser = UserReadingName.IsMatch(Code(file)) || declared.Any(readers.Contains);
            if (!readsUser) continue;

            offenders.AddRange(lines.Select(l => $"{file.Relative}:{l.Line}"));
        }

        string.Join(Environment.NewLine, offenders).Should().BeEmpty(
            "code that can read the signed-in user must stay on the caller's synchronization context; " +
            "drop ConfigureAwait(false) there (see #1729)");
    }

    [Fact]
    public void The_scan_recognises_code_that_reads_the_user()
    {
        // A guard that finds nothing looks the same as one that works, so pin a few
        // types the rule must see: one through its own context field, one only through
        // an injected service, one through a list of an interface, and one it must not.
        var readers = UserReadingTypes(CSharpSources());

        readers.Should().Contain(
        [
            Key(typeof(ALDevToolbox.Services.Organizations.TeamService)),
            Key(typeof(ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerService)),
            Key(typeof(ALDevToolbox.Services.ObjectExplorer.Projects.RepositoryReadProbe)),
            Key(typeof(ALDevToolbox.Services.Palette.PaletteSearchService)),
        ]);
        readers.Should().NotContain(Key(typeof(ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient)));
    }

    private static readonly Regex Namespace =
        new(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static List<FenceSourceScan.SourceFile> CSharpSources() =>
        FenceSourceScan.AppSources.Where(f => f.Relative.EndsWith(".cs", StringComparison.Ordinal)).ToList();

    private static List<Type> AppTypes() => typeof(AppDbContext).Assembly.GetTypes()
        .Where(t => t.Namespace is not null && !t.Name.Contains('<'))
        .ToList();

    /// <summary>Namespace plus simple name: what a source file can tell about a type it declares.</summary>
    private static string Key(Type t) => $"{t.Namespace}.{t.Name.Split('`')[0]}";

    private static string Code(FenceSourceScan.SourceFile file) =>
        string.Join('\n', file.Lines.Select(l => l.Code));

    private static HashSet<string> DeclaredKeys(FenceSourceScan.SourceFile file)
    {
        var code = Code(file);
        var ns = Namespace.Match(code) is { Success: true } m ? m.Groups["ns"].Value : "";
        return Declaration.Matches(code).Select(d => $"{ns}.{d.Groups["name"].Value}").ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The app's types that can read the user: those declared in a file that names
    /// one of the user-reading types, then, to a fixed point, those whose
    /// constructor takes one of them, and the interfaces any of them implements
    /// (so a service taking <c>IEnumerable&lt;IPaletteSource&gt;</c> counts once
    /// one palette source reads the user).
    /// </summary>
    private static HashSet<string> UserReadingTypes(IReadOnlyList<FenceSourceScan.SourceFile> sources)
    {
        var readers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in sources.Where(f => UserReadingName.IsMatch(Code(f))))
        {
            readers.UnionWith(DeclaredKeys(file));
        }

        var types = AppTypes();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var t in types)
            {
                if (readers.Contains(Key(t))) continue;
                var takesReader = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SelectMany(c => c.GetParameters())
                    .SelectMany(p => Flatten(p.ParameterType))
                    .Where(IsService)
                    .Any(p => p == typeof(AppDbContext) || (p.Namespace is not null && readers.Contains(Key(p))));
                if (takesReader && readers.Add(Key(t))) changed = true;
            }
            foreach (var t in types.Where(t => t.IsClass && readers.Contains(Key(t))))
            {
                foreach (var i in t.GetInterfaces().Where(i => i.Assembly == t.Assembly))
                {
                    if (readers.Add(Key(i))) changed = true;
                }
            }
        }
        return readers;
    }

    /// <summary>
    /// Data handed to a constructor (a record, a struct, an enum) carries no way to
    /// read the user, even when it is declared beside a type that does.
    /// </summary>
    private static bool IsService(Type t) =>
        (t.IsClass || t.IsInterface) && t.GetMethod("<Clone>$") is null;

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.IsArray) foreach (var inner in Flatten(type.GetElementType()!)) yield return inner;
        if (!type.IsGenericType) yield break;
        foreach (var arg in type.GetGenericArguments())
        {
            foreach (var inner in Flatten(arg)) yield return inner;
        }
    }
}
