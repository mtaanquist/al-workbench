namespace ALDevToolbox.Services.Notifications;

/// <summary>Text shaping shared by the notifiers.</summary>
internal static class NotificationText
{
    /// <summary>The longest a <see cref="FirstLine"/> is before it is cut and ends in "...".</summary>
    internal const int MaxLineLength = 200;

    /// <summary>
    /// The first non-blank line of <paramref name="message"/>, for a
    /// notification's one-line detail: leading blank lines are skipped, and a
    /// line longer than <see cref="MaxLineLength"/> is cut and ends in "...".
    /// </summary>
    internal static string FirstLine(string message)
    {
        var line = message.Trim().Split('\n')[0].Trim();
        return line.Length > MaxLineLength ? line[..MaxLineLength].TrimEnd() + "..." : line;
    }
}
