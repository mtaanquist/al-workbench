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
    public static void For(Func<bool> condition, string because, int milliseconds = 750)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            condition().Should().BeTrue(because);
            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < until);
        condition().Should().BeTrue(because);
    }
}
