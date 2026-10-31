using System.Text.RegularExpressions;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Routing;

/// <summary>
/// Every form on the auth pages posts to a minimal-API endpoint, and PR 11
/// rewrote all eight of those pages wholesale onto the design system's auth
/// card. A mistyped or dropped <c>action</c> survives every other check in the
/// suite: the page still renders, the markup still looks right, and the only
/// symptom is that signing in silently does nothing.
///
/// So this walks the real endpoint map and asserts each action an auth page
/// posts to is actually mapped for POST. Source-level rather than bUnit,
/// because the point is the pairing of eight files against the route table
/// rather than any one page's render.
/// </summary>
public class AuthFormActionTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;

    public AuthFormActionTests(TestDb db) => _db = db;

    /// <summary>The auth family, as of PR 11. Add a page here when one joins it.</summary>
    private static readonly string[] AuthPages =
    [
        "AcceptInvite.razor", "ForgotPassword.razor", "Login.razor", "LoginChallenge.razor",
        "MagicLogin.razor", "MagicLoginConfirm.razor", "ResetPassword.razor", "Signup.razor", "SignupDetails.razor",
    ];

    [Fact]
    public void Every_auth_form_posts_to_an_endpoint_that_exists()
    {
        var actions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var page in AuthPages)
        {
            var source = File.ReadAllText(Path.Combine(LocatePagesFolder(), page));
            // Two shapes: the card takes FormAction="...", loose <form>s inside
            // it (LoginChallenge posts three different ones) carry action="...".
            foreach (Match m in Regex.Matches(source, "(?:FormAction|action)=\"(/auth/[^\"]+)\""))
            {
                actions.Add(m.Groups[1].Value);
            }
        }

        actions.Should().NotBeEmpty(
            "the auth pages post to /auth/* endpoints; finding none means this test stopped looking in the right place");

        var mapped = PostRoutes();
        foreach (var action in actions)
        {
            mapped.Should().Contain(action,
                $"{action} is posted to by an auth page and must be a mapped POST endpoint");
        }
    }

    [Fact]
    public void Every_auth_page_renders_on_the_shell_less_layout()
    {
        foreach (var page in AuthPages)
        {
            var source = File.ReadAllText(Path.Combine(LocatePagesFolder(), page));
            // Losing this line puts the sidebar and top bar back on a page whose
            // whole archetype is "no shell" — and it would look deliberate
            // enough in a screenshot that nobody would query it.
            source.Should().Contain("@layout ALDevToolbox.Components.Layout.AuthLayout",
                $"{page} is part of the auth family and must not render inside the app shell");
        }
    }

    private HashSet<string> PostRoutes()
    {
        using var factory = new EndpointFactory(_db);
        using var _ = factory.CreateClient();

        return factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Contains("POST", StringComparer.OrdinalIgnoreCase) == true)
            .Select(e => e.RoutePattern.RawText!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string LocateProjectFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ALDevToolbox")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "ALDevToolbox");
    }

    private static string LocatePagesFolder() =>
        Path.Combine(LocateProjectFolder(), "Components", "Pages");
}
