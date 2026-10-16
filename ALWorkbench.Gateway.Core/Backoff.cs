namespace ALWorkbench.Gateway.Core;

/// <summary>
/// Exponential back-off with jitter for when AL Workbench cannot be reached: 2, 4, 8
/// ... seconds up to five minutes, each wait drawn from its upper half so many
/// gateways that lost the workbench at once (a restart of the app) do not all come
/// back in the same second. <see cref="Reset"/> after any success.
/// </summary>
public sealed class Backoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Maximum = TimeSpan.FromMinutes(5);

    private readonly Random _random;
    private int _failures;

    public Backoff(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    /// <summary>How many failures in a row since the last success.</summary>
    public int Failures => _failures;

    /// <summary>Counts a failure and returns how long to wait before trying again.</summary>
    public TimeSpan Next()
    {
        _failures++;
        var ceiling = Ceiling(_failures);
        var half = ceiling.TotalMilliseconds / 2;
        return TimeSpan.FromMilliseconds(half + (_random.NextDouble() * half));
    }

    public void Reset() => _failures = 0;

    /// <summary>The longest wait after <paramref name="failures"/> failures in a row.</summary>
    public static TimeSpan Ceiling(int failures)
    {
        // 2^(n-1) * Initial, capped; the exponent is capped first so it cannot overflow.
        var exponent = Math.Min(Math.Max(failures - 1, 0), 20);
        var ms = Initial.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(ms, Maximum.TotalMilliseconds));
    }
}
