namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Compares Business Central version strings (e.g. "28.1", "28.10",
/// "27.4.0.12345") numerically rather than lexicographically. Without this,
/// "28.10" sorts before "28.2" — which is the bug the Object Explorer
/// browser hit before this was wired in.
///
/// Missing parts are treated as zero so "28" and "28.0.0" compare equal -
/// which matters because the comparer also decides whether an app.json is
/// behind (<c>DependencyDriftService</c>), and "27.0" against "27.0.0.0" is
/// not drift. Callers that sort and need a fixed order between such twins
/// break the tie themselves. Non-numeric segments are pushed to the bottom
/// by treating their numeric value as -1, which keeps the sort total
/// without throwing on the occasional non-conforming label.
/// </summary>
internal sealed class BcVersionComparer : IComparer<string?>
{
    public static readonly BcVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        // null compares greater than any version, so "no version" rows sort
        // last in an ascending sort and first in a descending one.
        if (x is null && y is null) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var xs = x.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var ys = y.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var len = Math.Max(xs.Length, ys.Length);
        for (var i = 0; i < len; i++)
        {
            var xi = Segment(xs, i);
            var yi = Segment(ys, i);
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    private static int Segment(string[] parts, int index) =>
        index >= parts.Length ? 0 : int.TryParse(parts[index], out var value) ? value : -1;
}
