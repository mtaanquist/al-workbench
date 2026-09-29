using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Components.Pages.Upgrades;

/// <summary>
/// The environments one of <see cref="UpgradeActionRunner"/>'s dialogs acts on, taken
/// when the dialog opens. The page keeps its own selection; the runner only ever sees
/// this copy of it.
/// </summary>
/// <param name="Rows">
/// Every row the dialog lists and the run then works through, in order. Only rows the
/// person may act on (<see cref="UpgradeFleetRow.CanAct"/>): the runner does not filter,
/// so a host passes its own selection already narrowed, as the fleet page's
/// <c>SelectedRows</c> does.
/// </param>
/// <param name="IsOneRow">
/// True when the dialog was opened from one row's own menu. The titles go singular, so
/// the rows ticked for something else and still tinted behind the dialog are plainly
/// not part of it.
/// </param>
public sealed record UpgradeActionTarget(IReadOnlyList<UpgradeFleetRow> Rows, bool IsOneRow)
{
    /// <summary>The ticked rows, as the bar's commands act on them.</summary>
    public static UpgradeActionTarget ForSelection(IEnumerable<UpgradeFleetRow> rows) => new(rows.ToList(), false);

    /// <summary>One row, as its own menu acts on it.</summary>
    public static UpgradeActionTarget ForRow(UpgradeFleetRow row) => new([row], true);
}

/// <summary>
/// One row's live result during and after a run. The class is built here rather
/// than interpolated into the markup, so every class name the page renders is a
/// whole name a stylesheet can be checked against. A result that needs the reader
/// to go somewhere else carries the link to get there.
/// </summary>
public sealed record UpgradeRowResult(
    string Text, string Tone, int? ProjectId = null, string? LinkLabel = null, int? ActionId = null)
{
    public string CssClass => $"upg-note upg-note--{Tone}";

    /// <summary>
    /// Short enough for the Next update cell without widening it: a few words, and
    /// nothing to click. Refusals carry Business Central's sentence and stay below.
    /// </summary>
    public bool IsBrief => ProjectId is null && LinkLabel is null && ActionId is null && Text.Length <= 28;
}

/// <summary>
/// The line above the table after an action. Its tone is part of the message: a
/// run that skipped or failed something must not read like a success.
/// </summary>
public sealed record UpgradeNotice(string Text, string Tone, bool ShowReloadNow)
{
    public string CssClass => $"upg-notice upg-notice--{Tone}";
}

/// <summary>
/// What a finished run hands back to the page that hosts the runner. The page re-reads
/// its rows afterwards in every case, so the table shows what Business Central now says
/// rather than what was asked of it.
/// </summary>
/// <param name="Notice">The one-line summary for above the table.</param>
/// <param name="Started">
/// The updates this run started, by environment, with when each was sent - for the page
/// to watch through to their end (#982). Empty for a date move, a version change or a
/// booking.
/// </param>
public sealed record UpgradeRunOutcome(UpgradeNotice Notice, IReadOnlyDictionary<int, DateTime> Started);
