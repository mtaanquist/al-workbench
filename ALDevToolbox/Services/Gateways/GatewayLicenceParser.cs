using System.Globalization;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Gateways;

/// <summary>One licensed object range as read from the licence text.</summary>
public sealed record LicenceRange(string ObjectType, int FromId, int ToId, string? Permissions);

/// <summary>What <see cref="GatewayLicenceParser"/> could read from a licence.</summary>
/// <param name="Problems">What could not be read, one sentence each; empty when everything was.</param>
public sealed record ParsedLicence(
    string? Licensee,
    string? SerialNumber,
    DateOnly? ExpiresOn,
    IReadOnlyList<LicenceRange> Ranges,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads the text <c>Export-NAVServerLicenseInformation</c> prints: who the licence is
/// for, its serial number, when it expires, and the licensed object ranges with their
/// object types (for the range check, #1370). AL Workbench never parses licence files
/// themselves; this is the only reading of a licence there is.
///
/// <para><b>Why here and not on the gateway.</b> The gateway sends the text and AL
/// Workbench reads it, so a correction to this reading reaches every customer with the
/// next AL Workbench release, without customer IT updating a gateway by hand on each
/// server, and the stored text can be read again. The gateway still bounds the text.</para>
///
/// <para><b>Tolerant on purpose.</b> The layout differs between versions (the
/// <c>.flf</c> licences before Business Central 22 and the <c>.bclicense</c> ones
/// after) and was written against synthetic samples (see
/// <c>ALDevToolbox.Tests/Gateways/InventorySamples/README.md</c>). It reads labelled
/// lines (<c>Licensed to : ...</c>) by a list of known labels, and any line that starts
/// with a known object type followed by two numbers as a range. Whatever it cannot read
/// becomes a problem in the result, never an exception, so a licence that reads badly
/// still leaves its text stored and the rest of the inventory intact.</para>
/// </summary>
public static partial class GatewayLicenceParser
{
    /// <summary>Ranges kept per licence; a real one has a few hundred at most.</summary>
    public const int MaxRanges = 5000;

    private static readonly string[] LicenseeLabels = ["Licensed to", "Licensee", "Customer name", "Customer", "Licensed To"];
    private static readonly string[] SerialLabels = ["Serial No.", "Serial No", "Serial number", "Serial Number", "Serial"];
    private static readonly string[] ExpiryLabels = ["Expiration Date", "Expiration date", "Expiry date", "Expiry Date", "Expires", "Expiration", "Valid until", "Valid to"];

    /// <summary>The object types a range line may start with, in the spelling stored.</summary>
    private static readonly Dictionary<string, string> ObjectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TableData"] = "TableData",
        ["Table"] = "Table",
        ["Page"] = "Page",
        ["Form"] = "Form",
        ["Report"] = "Report",
        ["Codeunit"] = "Codeunit",
        ["XMLport"] = "XMLport",
        ["Dataport"] = "Dataport",
        ["Query"] = "Query",
        ["MenuSuite"] = "MenuSuite",
        ["System"] = "System",
        ["Enum"] = "Enum",
        ["Interface"] = "Interface",
        ["PermissionSet"] = "PermissionSet",
        ["Permission Set"] = "PermissionSet",
    };

    [GeneratedRegex(@"^\s*(?<label>[A-Za-z][A-Za-z .()/]*?)\s*:\s*(?<value>.*?)\s*$")]
    private static partial Regex LabelledLine();

    [GeneratedRegex(@"^\s*(?<type>Permission Set|[A-Za-z]+)\s+(?<from>\d{1,10})\s*(?:\.\.|-|\s)\s*(?<to>\d{1,10})(?<rest>.*)$")]
    private static partial Regex RangeLine();

    public static ParsedLicence Parse(string? text)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedLicence(null, null, null, [], ["The licence information was empty."]);
        }

        string? licensee = null, serial = null, expiryText = null;
        var ranges = new List<LicenceRange>();
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var tooMany = false;
        foreach (var line in lines)
        {
            var range = RangeLine().Match(line);
            if (range.Success && ObjectTypes.TryGetValue(range.Groups["type"].Value, out var type))
            {
                if (int.TryParse(range.Groups["from"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                    && int.TryParse(range.Groups["to"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                    && from <= to)
                {
                    if (ranges.Count >= MaxRanges)
                    {
                        tooMany = true;
                        continue;
                    }
                    var rest = range.Groups["rest"].Value.Trim();
                    ranges.Add(new LicenceRange(type, from, to, rest.Length == 0 ? null : rest.Length > 200 ? rest[..200] : rest));
                }
                continue;
            }

            var labelled = LabelledLine().Match(line);
            if (!labelled.Success) continue;
            var label = labelled.Groups["label"].Value.Trim();
            var value = labelled.Groups["value"].Value.Trim();
            if (value.Length == 0) continue;
            if (licensee is null && LicenseeLabels.Contains(label, StringComparer.OrdinalIgnoreCase)) licensee = Cut(value);
            else if (serial is null && SerialLabels.Contains(label, StringComparer.OrdinalIgnoreCase)) serial = Cut(value);
            else if (expiryText is null && ExpiryLabels.Contains(label, StringComparer.OrdinalIgnoreCase)) expiryText = value;
        }

        if (licensee is null) problems.Add("The licensee could not be found in the licence information.");
        DateOnly? expires = null;
        if (expiryText is not null)
        {
            expires = ParseDate(expiryText, out var dateProblem);
            if (dateProblem is not null) problems.Add(dateProblem);
        }
        if (ranges.Count == 0) problems.Add("No licensed object ranges were found in the licence information.");
        if (tooMany) problems.Add($"The licence lists more than {MaxRanges} object ranges; only the first {MaxRanges} were kept.");
        return new ParsedLicence(licensee, serial, expires, ranges, problems);
    }

    /// <summary>
    /// A date as a licence prints it. ISO first; with dashes or dots, day first (how a
    /// Business Central server with a European culture prints it); with slashes, the
    /// order is decided by a part over 12, and a date that reads both ways is reported
    /// rather than guessed. A word such as "None" or "Never" means no expiry.
    /// </summary>
    internal static DateOnly? ParseDate(string text, out string? problem)
    {
        problem = null;
        var value = text.Trim();
        // "31-12-2026 (in 30 days)": the date is the first word.
        var first = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (first.Length == 0 || first.Equals("none", StringComparison.OrdinalIgnoreCase)
            || first.Equals("never", StringComparison.OrdinalIgnoreCase) || first.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (DateOnly.TryParseExact(first, ["yyyy-MM-dd", "yyyy-M-d", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }
        if (DateOnly.TryParseExact(first, ["dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayFirst))
        {
            return dayFirst;
        }
        var parts = first.Split('/');
        if (parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year >= 1900)
        {
            (int Month, int Day)? order = a > 12 && b <= 12 ? (b, a) : b > 12 && a <= 12 ? (a, b) : a == b ? (a, b) : null;
            if (order is { } md && md.Month is >= 1 and <= 12 && md.Day >= 1 && md.Day <= DateTime.DaysInMonth(year, md.Month))
            {
                return new DateOnly(year, md.Month, md.Day);
            }
            if (order is null)
            {
                problem = $"The expiry date '{first}' could be read as day/month or month/day, so it is not shown.";
                return null;
            }
        }
        problem = $"The expiry date '{Cut(value)}' could not be read.";
        return null;
    }

    private static string Cut(string value) => value.Length > 200 ? value[..200] : value;
}
