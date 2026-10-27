using System.Globalization;
using ALDevToolbox.Startup;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Startup;

/// <summary>
/// Numbers the app writes into its copy must not follow the server's locale:
/// a host started with <c>LANG=da_DK.UTF-8</c> rendered "5.000" where the page
/// means "5,000" (#1424). Each test starts from a Danish culture to stand in
/// for that host.
/// </summary>
public class AppCultureTests
{
    private static readonly CultureInfo Danish = CultureInfo.GetCultureInfo("da-DK");

    [Fact]
    public void A_danish_host_still_writes_thousands_with_a_comma()
    {
        CultureInfo.CurrentCulture = Danish;

        AppCulture.Apply();

        5000.ToString("N0").Should().Be("5,000");
        $"{15001:N0} lines".Should().Be("15,001 lines");
    }

    [Fact]
    public void A_thread_that_never_chose_a_culture_gets_the_app_culture()
    {
        CultureInfo.CurrentCulture = Danish;
        AppCulture.Apply();

        // A request or circuit thread starts with nothing set and no culture
        // flowing in from the code that queued it; suppressing the flow gives
        // this thread the same start.
        string? formatted = null;
        Thread thread;
        using (ExecutionContext.SuppressFlow())
        {
            thread = new Thread(() => formatted = 5000.ToString("N0"));
            thread.Start();
        }
        thread.Join();

        formatted.Should().Be("5,000");
    }
}
