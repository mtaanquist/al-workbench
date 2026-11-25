using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="BcVersionComparer"/> exists because of a shipped bug: sorted as
/// text, "28.10" lands before "28.2" and the Object Explorer browser showed the
/// wrong newest version. Nothing was pinning the fix, so pin it — plus how a
/// short label compares against its zero-padded twin, the non-numeric fallback,
/// and both null positions.
/// </summary>
public sealed class BcVersionComparerTests
{
    private static int Compare(string? x, string? y) => Math.Sign(BcVersionComparer.Instance.Compare(x, y));

    [Theory]
    // The regression itself: numeric, not lexicographic.
    [InlineData("28.10", "28.2", 1)]
    [InlineData("28.2", "28.10", -1)]
    [InlineData("27.4.0.12345", "27.4.0.12345", 0)]
    // A missing part counts as zero, so a short label equals its zero-padded twin.
    // The drift check depends on it: "27.0" against "27.0.0.0" is not behind.
    [InlineData("28", "28.0.0", 0)]
    [InlineData("28.0.0", "28", 0)]
    [InlineData("27.0", "27.0.0.0", 0)]
    [InlineData("28", "28.0.1", -1)]
    // Ordinary ordering across major and build parts.
    [InlineData("29.0", "28.99", 1)]
    [InlineData("27.4.0.12345", "27.4.0.12344", 1)]
    // A non-numeric segment sorts below any number rather than throwing.
    [InlineData("28.preview", "28.0", -1)]
    [InlineData("28.preview", "28", -1)]
    // Null is "no version" and compares greater than any version: last ascending, first descending.
    [InlineData(null, "28.0", 1)]
    [InlineData("28.0", null, -1)]
    [InlineData(null, null, 0)]
    public void Compares_business_central_versions_numerically(string? x, string? y, int expected) =>
        Compare(x, y).Should().Be(expected);

    [Fact]
    public void Sorting_descending_puts_a_null_first_then_the_newest()
    {
        string?[] versions = ["28.2", null, "28.10", "27.9", "28"];

        var sorted = versions.OrderBy(v => v, BcVersionComparer.Instance).Reverse().ToArray();

        sorted.Should().Equal(null, "28.10", "28.2", "28", "27.9");
    }
}
