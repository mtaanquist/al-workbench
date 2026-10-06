namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Turns shipped Business Central version numbers into application-version
/// catalogue rows, so the daily sync can add a new release wave without anyone
/// typing it in. Everything here follows from the major version alone: Microsoft
/// ships two waves a year, starting with 15 (2019 release wave 2), and the AL
/// runtime of a wave's first release has been the major minus eleven since
/// version 17 (17.0 is runtime 6.0, 28.0 is runtime 17.0).
///
/// <para>
/// Only the wave's <c>.0</c> release is produced. Runtimes with a minor part
/// (15.1 for 26.1) are not published for every update and no feed carries the
/// highest runtime a version supports - the symbol packages report the runtime
/// Microsoft compiled with, which lags (26.1 reports 15.0) - so those rows stay
/// with the admin.
/// </para>
/// </summary>
public static class BusinessCentralWaves
{
    /// <summary>The oldest major the runtime rule holds for, and the oldest the Microsoft symbol feed publishes.</summary>
    public const int MinimumMajor = 17;

    /// <summary>
    /// The waves whose first release (<c>Major.0</c>) appears in
    /// <paramref name="versions"/>, newest first. Prerelease versions and
    /// anything that does not parse are ignored.
    /// </summary>
    public static IReadOnlyList<WaveEntry> FromVersions(IEnumerable<string> versions) =>
        versions
            .Where(v => !v.Contains('-', StringComparison.Ordinal))
            .Select(ParseMajorMinor)
            .Where(mm => mm is { Minor: 0, Major: >= MinimumMajor })
            .Select(mm => mm!.Value.Major)
            .Distinct()
            .OrderByDescending(m => m)
            .Select(ForMajor)
            .ToList();

    /// <summary>The catalogue row for one wave, e.g. 28 → <c>bc-2026-rw1</c>, "Business Central 2026 release wave 1", 28.0.0.0, runtime 17.0.</summary>
    public static WaveEntry ForMajor(int major)
    {
        // 15 is 2019 wave 2, 16 is 2020 wave 1: an even major is the spring wave.
        var year = 2000 + (major + 24) / 2;
        var wave = major % 2 == 0 ? 1 : 2;
        return new WaveEntry(
            Major: major,
            Key: $"bc-{year}-rw{wave}",
            Name: $"Business Central {year} release wave {wave}",
            Application: $"{major}.0.0.0",
            Runtime: $"{major - 11}.0");
    }

    /// <summary>The major of a catalogue row's application version, or null when it does not parse.</summary>
    public static int? MajorOf(string? application) => ParseMajorMinor(application)?.Major;

    private static (int Major, int Minor)? ParseMajorMinor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var parts = version.Trim().Split('.');
        if (parts.Length < 2) return null;
        return int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? (major, minor)
            : null;
    }
}

/// <summary>One release wave as the catalogue stores it.</summary>
public sealed record WaveEntry(int Major, string Key, string Name, string Application, string Runtime);
