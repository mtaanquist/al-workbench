namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Initials for the round avatar chips the design system uses to stand in for a
/// person — the audit row's author and the top bar's user button. Pulled out
/// when the shell port made it the second copy of the same eight lines.
/// </summary>
public static class Avatar
{
    /// <summary>
    /// Up to two initials for a display name or an email address. Callers pass
    /// whatever they have — <c>ChangedBy</c> is usually an address and
    /// <c>Identity.Name</c> usually is not — so the local part is taken first
    /// and then split on the separators either form uses.
    /// </summary>
    public static string Initials(string? who)
    {
        if (string.IsNullOrWhiteSpace(who)) return "?";
        // Audit rows store the actor as "display name <email>". Splitting that on
        // '@' leaves "display name <email-local", whose last word starts with '<' —
        // which is how every audit avatar in the app read "M<" instead of "MT".
        var angle = who.IndexOf('<');
        if (angle >= 0)
        {
            var displayName = who[..angle].Trim();
            // A bracketed address with no name in front of it ("<a@b>") has no
            // display name to take, so fall through to the address itself
            // rather than returning the bracket.
            who = displayName.Length > 0 ? displayName : who[(angle + 1)..].TrimEnd('>');
        }
        // A system actor reads "System (BackupScheduler)": the worker in brackets
        // names which part of the system, not a second word of a name.
        if (who.StartsWith(Services.AuditActor.System + " (", StringComparison.Ordinal)) who = Services.AuditActor.System;
        var name = who.Split('@')[0];
        var parts = name.Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]);
        return parts.Length > 1 ? $"{first}{char.ToUpperInvariant(parts[^1][0])}" : first.ToString();
    }
}
