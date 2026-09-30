using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The readable key a solution has in its web address: <c>/solutions/cronus</c>,
/// <c>/environments/cronus/Production</c>. Lowercase ASCII letters and digits in
/// words joined by single dashes, unique per organisation among active solutions.
///
/// <para>Two shapes are refused because the routes need them for something else:
/// a slug that is all digits would read as a solution id, and <c>new</c> is the
/// create page.</para>
/// </summary>
public static class SolutionSlug
{
    /// <summary>Longest slug accepted; the column is sized to match.</summary>
    public const int MaxLength = 60;

    /// <summary>
    /// The HTML <c>pattern=</c> that mirrors <see cref="IsValid"/>'s shape rule in the
    /// browser. The all-digits and reserved-word rules stay server-side.
    /// </summary>
    public const string HtmlPattern = "[a-z0-9]+(-[a-z0-9]+)*";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "new" };

    /// <summary>True when <paramref name="slug"/> can be used as a solution's slug as it stands.</summary>
    public static bool IsValid(string? slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > MaxLength) return false;
        if (slug[0] == '-' || slug[^1] == '-' || slug.Contains("--", StringComparison.Ordinal)) return false;
        if (!slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        if (slug.All(char.IsAsciiDigit)) return false;
        return !Reserved.Contains(slug);
    }

    /// <summary>
    /// The slug <paramref name="name"/> suggests, always valid: transliterated the way
    /// the generator names folders ("Jørgensen Møbler A/S" becomes
    /// <c>jorgensen-mobler-a-s</c>), cut to <see cref="MaxLength"/>, and prefixed with
    /// <c>solution-</c> when what is left is empty, all digits or reserved. Not unique on
    /// its own - the service appends a counter when another solution has it.
    /// </summary>
    public static string Derive(string? name)
    {
        var kebab = CustomerNaming.Apply(name, NamingStyle.KebabCase);

        // The generator keeps any letter; a web address keeps ASCII only.
        var sb = new StringBuilder(kebab.Length);
        foreach (var c in kebab)
        {
            var keep = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (keep) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = Trim(sb.ToString(), MaxLength);

        if (slug.Length == 0) return "solution";
        return IsValid(slug) ? slug : Trim("solution-" + slug, MaxLength);
    }

    /// <summary>
    /// <paramref name="slug"/> with <c>-2</c>, <c>-3</c>... appended for the
    /// <paramref name="attempt"/>th try, cut so the result still fits.
    /// </summary>
    public static string WithCounter(string slug, int attempt)
    {
        if (attempt <= 1) return slug;
        var suffix = "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Trim(slug, MaxLength - suffix.Length) + suffix;
    }

    private static string Trim(string slug, int max) =>
        (slug.Length <= max ? slug : slug[..max]).Trim('-');
}
