using System.Diagnostics;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// For "dismissing the question changes nothing". A page that ignored the answer would
/// carry on in the background after the dialog closed, and its write lands a moment
/// later, so a check made straight after the click passes either way. This keeps
/// checking for a short while instead: a correct page passes at once on every poll,
/// and a page that went ahead regardless has long finished by the last one.
/// </summary>
public static class StillTrue
{
    private const int PollMilliseconds = 50;

    public static void For(Func<bool> condition, string because, int milliseconds = 750)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            condition().Should().BeTrue(because);
            Thread.Sleep(PollMilliseconds);
        }
        while (clock.ElapsedMilliseconds < milliseconds);
        condition().Should().BeTrue(because);
    }

    /// <inheritdoc cref="For"/>
    public static async Task ForAsync(Func<Task<bool>> condition, string because, int milliseconds = 750)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            (await condition()).Should().BeTrue(because);
            await Task.Delay(PollMilliseconds, TestContext.Current.CancellationToken);
        }
        while (clock.ElapsedMilliseconds < milliseconds);
        (await condition()).Should().BeTrue(because);
    }
}
