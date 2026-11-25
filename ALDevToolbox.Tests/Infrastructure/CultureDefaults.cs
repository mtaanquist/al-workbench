using System.Runtime.CompilerServices;
using ALDevToolbox.Startup;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Runs the suite under the app's own culture. Most tests never pass through
/// <c>Program.cs</c> (bUnit renders components directly, services are built
/// by hand), so without this they would format with the machine's locale and
/// fail on a Danish one: "first 5.000" where the page says "first 5,000"
/// (#1424). Calling the production pin, not a test-only culture, keeps the
/// two from drifting apart.
/// </summary>
internal static class CultureDefaults
{
    [ModuleInitializer]
    internal static void Apply() => AppCulture.Apply();
}
