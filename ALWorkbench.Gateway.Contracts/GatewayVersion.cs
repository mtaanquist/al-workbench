using System.Globalization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// A gateway or workbench version, compared the way semantic versioning orders them:
/// <c>12.0.0-rc.1</c> comes before <c>12.0.0</c>, numeric pre-release parts compare as
/// numbers (<c>rc.2</c> before <c>rc.10</c>), and build metadata after a <c>+</c> is
/// ignored. A gateway is released with every AL Workbench release and carries the same
/// version (the release tag), so the two can be compared directly. See
/// <c>.design/on-prem-gateway.md</c>, "Versions and updates".
///
/// <para>Major version 0 is not a release: an unstamped local build reports
/// <c>0.0.0-dev</c> and a staging preview <c>0.0.0-staging.&lt;commit&gt;</c>.
/// <see cref="IsPreview"/> says so, and neither side compares one against a
/// release: a preview is never refused for its version and never called out of date.</para>
/// </summary>
public sealed class GatewayVersion : IComparable<GatewayVersion>, IEquatable<GatewayVersion>
{
    private readonly string[] _preRelease;

    private GatewayVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The pre-release label without its dash (<c>rc.1</c>), or empty for a release.</summary>
    public string PreRelease => string.Join('.', _preRelease);

    /// <summary>A local or staging build rather than a release: major version 0.</summary>
    public bool IsPreview => Major == 0;

    /// <summary>
    /// Reads <c>major.minor.patch</c>, optionally prefixed with <c>v</c> and followed by
    /// <c>-pre.release</c> and <c>+build</c>. Anything else is not a version.
    /// </summary>
    public static bool TryParse(string? text, out GatewayVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) value = value[..plus];
        string[] preRelease = [];
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..].Split('.');
            value = value[..dash];
            if (preRelease.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return false;
        }
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }
        version = new GatewayVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>Like <see cref="TryParse"/>, for versions known to be well formed (the constants).</summary>
    public static GatewayVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version.");

    public int CompareTo(GatewayVersion? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        // A release sorts after any pre-release of the same version.
        if (_preRelease.Length == 0 && other._preRelease.Length == 0) return 0;
        if (_preRelease.Length == 0) return 1;
        if (other._preRelease.Length == 0) return -1;
        for (var i = 0; i < Math.Min(_preRelease.Length, other._preRelease.Length); i++)
        {
            var compared = ComparePart(_preRelease[i], other._preRelease[i]);
            if (compared != 0) return compared;
        }
        return _preRelease.Length.CompareTo(other._preRelease.Length);
    }

    /// <summary>Numbers compare as numbers and sort before words; words compare by character code.</summary>
    private static int ComparePart(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            // Compared as text by length first, so a number too long for an int still orders.
            var byLength = left.TrimStart('0').Length.CompareTo(right.TrimStart('0').Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left.TrimStart('0'), right.TrimStart('0'));
        }
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    public bool Equals(GatewayVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is GatewayVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator <(GatewayVersion left, GatewayVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(GatewayVersion left, GatewayVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(GatewayVersion left, GatewayVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(GatewayVersion left, GatewayVersion right) => left.CompareTo(right) >= 0;
    public static bool operator ==(GatewayVersion? left, GatewayVersion? right) => left?.Equals(right) ?? right is null;
    public static bool operator !=(GatewayVersion? left, GatewayVersion? right) => !(left == right);

    public override string ToString() =>
        _preRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
