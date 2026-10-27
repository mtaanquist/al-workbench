using System.Globalization;

namespace ALDevToolbox.Startup;

/// <summary>
/// The one culture the whole process formats and parses with.
///
/// The app's copy is English, and about eighty places build user-visible
/// numbers and dates in code with <c>:N0</c>, <c>ToString("N0")</c> and
/// friends without naming a culture. Left alone, those follow the host's
/// <c>LANG</c>: the published container sets none and so runs invariant
/// ("5,000"), but a container or a developer machine started with
/// <c>LANG=da_DK.UTF-8</c> renders "5.000" and logs <c>07.52.48</c> (#1424).
/// Pinning it once at boot is the fix rather than threading
/// <see cref="CultureInfo.InvariantCulture"/> through every format site, which
/// the next new site would forget. Invariant is chosen because it is what the
/// container already ran as, so nothing a production user sees changes.
///
/// The test assembly calls this from a module initializer, so the suite runs
/// under the same culture as the app whatever the machine's locale is.
/// </summary>
internal static class AppCulture
{
    internal static CultureInfo Culture => CultureInfo.InvariantCulture;

    /// <summary>
    /// Sets the default for every thread that has not chosen a culture of its
    /// own (request and circuit threads included, since nothing in the app sets
    /// one), and for the calling thread, which may already have read the
    /// host's.
    /// </summary>
    internal static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }
}
