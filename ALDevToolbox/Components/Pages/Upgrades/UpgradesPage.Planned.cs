using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Components.Pages.Upgrades;

/// <summary>The three views of <c>/upgrades</c>, from <c>?view=</c>.</summary>
public enum UpgradesView { Open, Archive, Fleet }

/// <summary>
/// The Upgrades page's Open and Archive views and the page-wide parts around them: the
/// view switch and its counts, the subtitle, the planned upgrades' own moves (new, add
/// to, mark done, delete, reopen) and what they say afterwards. The Fleet view's code is
/// in the .razor file, where it was before the page grew the other two. See
/// <c>.design/environment-updates.md</c>, "The Upgrades page: Open, Archive and Fleet".
/// </summary>
public partial class UpgradesPage
{
    /// <summary>How many done upgrades the Archive shows at a time.</summary>
    internal const int ArchivePageSize = 20;

    private UpgradesView _view = UpgradesView.Open;

    /// <summary>The open upgrades in the Open view's order; null while loading.</summary>
    private List<EnvironmentUpgradeSummary>? _open;

    /// <summary>
    /// The done upgrades, most recently closed first, with their lines counted. Read only
    /// while the Archive view is shown - it is the one list that grows for good - and null
    /// otherwise, and while loading.
    /// </summary>
    private List<EnvironmentUpgradeSummary>? _archive;

    /// <summary>The Archive tab's count, from a count of headers rather than the list. Null until known.</summary>
    private int? _archiveCount;

    /// <summary>
    /// A tab followed while a Fleet run was working. The run owns the circuit's database
    /// context until it ends, so the switch waits for it; the address already says where
    /// to go, and <see cref="ApplyPendingViewAsync"/> goes there.
    /// </summary>
    private bool _viewChangePending;

    /// <summary>The Fleet tab's count: the fleet table's rows. Null until known.</summary>
    private int? _fleetCount;

    /// <summary>The versions Business Central offers the fleet, major.minor, newest first, for a new upgrade's target.</summary>
    private List<string> _offeredVersions = [];

    private string _openSearch = string.Empty;
    private string _openStatus = string.Empty;
    private string _archiveSearch = string.Empty;
    private int _archivePage;

    /// <summary>True while one of the row menus' moves is under way; the menus wait.</summary>
    private bool _planBusy;

    private PlanNotice? _planNotice;

    private NewUpgradeDialog? _newUpgrade;
    private AddToUpgradeDialog? _addToUpgrade;
    private ConfirmDialog? _doneConfirm;
    private ConfirmDialog? _deleteConfirm;

    /// <summary>What the Mark done confirm lists: the lines not checked yet, and how many lines there are.</summary>
    private List<EnvironmentUpgradeLineRow> _doneUnchecked = [];

    internal static UpgradesView ParseView(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "archive" => UpgradesView.Archive,
        "fleet" => UpgradesView.Fleet,
        _ => UpgradesView.Open,
    };

    // ── The frame's states, per view ────────────────────────────────────

    private bool ViewIsLoading => _view switch
    {
        UpgradesView.Open => _open is null,
        UpgradesView.Archive => _archive is null,
        _ => _rows is null,
    };

    private bool ViewIsEmpty => _view switch
    {
        UpgradesView.Open => _open is { Count: 0 },
        UpgradesView.Archive => _archive is { Count: 0 },
        _ => _rows is not null && _totalCount == 0,
    };

    private bool ViewHasNoMatches => _view switch
    {
        UpgradesView.Open => ShownOpen().Count == 0,
        UpgradesView.Archive => ShownArchive().Count == 0,
        _ => _rows is { Count: 0 },
    };

    /// <summary>
    /// "New upgrade" in the head - except on an Open view with nothing planned, whose empty
    /// state carries the same button, and while the Open view is still loading and cannot
    /// yet tell which of the two it will be.
    /// </summary>
    private bool ShowNewUpgrade => !_denied && !(_view == UpgradesView.Open && _open is not { Count: > 0 });

    private string HeadSubtitle => _denied || _open is null
        ? string.Empty
        : UpgradesListWording.Subtitle(_open, Zone.ToDisplay, DateTime.UtcNow);

    private IReadOnlyList<PillTabs.PillTabItem> ViewTabs() =>
    [
        // Held while a Fleet run works: following a tab then would read the database under it.
        new("Open", "/upgrades", null, _view == UpgradesView.Open, _open?.Count, Running),
        new("Archive", "/upgrades?view=archive", null, _view == UpgradesView.Archive, _archiveCount, Running),
        new("Fleet", "/upgrades?view=fleet", null, _view == UpgradesView.Fleet, _fleetCount, Running),
    ];

    // ── Loading ─────────────────────────────────────────────────────────

    /// <summary>
    /// What every view needs on first load: the open upgrades (the Open view's rows, its
    /// tab's count and the subtitle), and the done ones - listed when the Archive is shown,
    /// only counted otherwise.
    /// </summary>
    private async Task LoadPlannedAsync()
    {
        await LoadOpenAsync();
        if (_view == UpgradesView.Archive) await LoadArchiveAsync();
        else await CountArchiveAsync();
    }

    private async Task LoadOpenAsync()
    {
        var open = await Upgrades.ListOpenAsync();
        _open = UpgradesListWording.OpenOrder(open, Zone.ToDisplay, Zone.ToDisplay(DateTime.UtcNow));
    }

    private async Task LoadArchiveAsync()
    {
        _archive = await Upgrades.ListArchivedAsync();
        _archiveCount = _archive.Count;
        _archivePage = Math.Min(_archivePage, Math.Max(0, (ShownArchiveAll().Count - 1) / ArchivePageSize));
    }

    private async Task CountArchiveAsync() => _archiveCount = await Upgrades.CountArchivedAsync();

    /// <summary>
    /// The Fleet tab's count, and the versions a new upgrade can aim at, without drawing
    /// the fleet. Read on a DI scope of its own: the page's first load reads it, and New
    /// upgrade reads it again when that load has not got this far, so a click while the
    /// page is still loading would otherwise start a second query on the circuit's one
    /// context. Same as the history and the poll (#1069).
    /// </summary>
    private async Task CountFleetAsync()
    {
        List<UpgradeFleetRow> fleet;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            fleet = await scope.ServiceProvider.GetRequiredService<UpgradeFleetService>().ListFleetAsync(ct: _gone.Token);
        }
        catch (OperationCanceledException) when (_gone.IsCancellationRequested)
        {
            return; // the page went while the read was out
        }
        _fleetCount = fleet.Count;
        _offeredVersions = OfferedVersionNames(fleet);
    }

    private static List<string> OfferedVersionNames(IEnumerable<UpgradeFleetRow> rows) =>
        UpgradeFleetService.OfferedVersions(rows).Select(o => o.Version).ToList();

    /// <summary>
    /// A tab was followed. The Fleet view's timers belong to it: leaving it stops the
    /// refresh poll and the watch, and coming back reads the fleet afresh (the ticks are
    /// kept, less any row that has gone). The others were read with the page.
    /// </summary>
    private async Task OnViewChangedAsync(UpgradesView was)
    {
        _planNotice = null;
        if (was == UpgradesView.Fleet)
        {
            StopPolling();
            _watch.Clear();
        }
        if (was == UpgradesView.Archive) _archive = null;
        switch (_view)
        {
            case UpgradesView.Fleet:
                await LoadAsync();
                break;
            case UpgradesView.Archive:
                await LoadArchiveAsync();
                break;
            default:
                await LoadOpenAsync();
                break;
        }
    }

    /// <summary>Follows a tab that was taken while a run worked, now that the run is over.</summary>
    private async Task ApplyPendingViewAsync()
    {
        if (!_viewChangePending) return;
        _viewChangePending = false;
        var was = _view;
        _view = ParseView(ViewName);
        if (_view != was) await OnViewChangedAsync(was);
    }

    // ── Filtering the two lists ─────────────────────────────────────────

    private List<EnvironmentUpgradeSummary> ShownOpen()
    {
        if (_open is null) return [];
        var term = _openSearch.Trim();
        return _open
            .Where(u => term.Length == 0
                || u.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || u.TargetVersion.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Where(u => _openStatus.Length == 0 || u.Status.ToString() == _openStatus)
            .ToList();
    }

    /// <summary>The done upgrades the search matches, every page of them.</summary>
    private List<EnvironmentUpgradeSummary> ShownArchiveAll()
    {
        if (_archive is null) return [];
        var term = _archiveSearch.Trim();
        return term.Length == 0
            ? _archive
            : _archive.Where(u => u.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                  || u.TargetVersion.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The page of <see cref="ShownArchiveAll"/> on screen.</summary>
    private List<EnvironmentUpgradeSummary> ShownArchive() =>
        ShownArchiveAll().Skip(_archivePage * ArchivePageSize).Take(ArchivePageSize).ToList();

    private void OnArchiveSearch() => _archivePage = 0;

    /// <summary>
    /// The Archive's count line: "Showing 5 of 5 done upgrades" on one page, and the range
    /// on screen once there are several ("Showing 21-40 of 45 done upgrades").
    /// </summary>
    internal static string ArchivePagerText(int page, int shown, int total)
    {
        var noun = total == 1 ? "done upgrade" : "done upgrades";
        if (total <= ArchivePageSize) return $"Showing {shown} of {total} {noun}";
        var from = page * ArchivePageSize + 1;
        return $"Showing {from}-{from + shown - 1} of {total} {noun}";
    }

    private void ClearPlannedFilters()
    {
        _openSearch = string.Empty;
        _openStatus = string.Empty;
        _archiveSearch = string.Empty;
        _archivePage = 0;
    }

    // ── New upgrade, and Add to upgrade ─────────────────────────────────

    private async Task OpenNewUpgradeAsync()
    {
        if (_newUpgrade is null) return;
        // The fleet's offered versions, read now if this view never drew the fleet.
        if (_fleetCount is null) await CountFleetAsync();
        await _newUpgrade.OpenAsync(_offeredVersions);
    }

    /// <summary>The upgrade exists; its page is where environments go on it and the slot is refined.</summary>
    private void OnUpgradeCreated(int upgradeId) => Nav.NavigateTo($"/upgrades/{upgradeId}");

    private async Task OpenAddToUpgradeAsync()
    {
        var rows = SelectedRows();
        if (rows.Count == 0 || _addToUpgrade is null) return;
        _planNotice = null;
        // The open list read now, not when the page opened: a colleague may have made the
        // upgrade these are meant for in the meantime.
        await LoadOpenAsync();
        await _addToUpgrade.OpenAsync(rows, _open ?? []);
    }

    /// <summary>
    /// Says what went on and what did not, and stays on the Fleet view: the team builds
    /// the next wave from the same table, and the notice links to the upgrade for when
    /// they want to go there instead. The ticks are left alone, as a run leaves them.
    /// </summary>
    private async Task OnAddedToUpgradeAsync(AddToUpgradeOutcome outcome)
    {
        _planNotice = AddedNotice(outcome);
        await LoadOpenAsync();
    }

    internal static PlanNotice AddedNotice(AddToUpgradeOutcome outcome)
    {
        var r = outcome.Result;
        var added = r.Added.Count;
        var name = $"\"{outcome.UpgradeName}\"";
        var text = added == 0
            ? $"Nothing was added to {name}."
            : outcome.Created
                ? $"Created {name} and added {Environments(added)} to it."
                : $"Added {Environments(added)} to {name}.";
        if (r.AlreadyOnIt.Count > 0)
        {
            text += r.AlreadyOnIt.Count == 1 ? " 1 was on it already." : $" {r.AlreadyOnIt.Count} were on it already.";
        }
        if (outcome.LeftOut > 0)
        {
            text += $" {outcome.LeftOut} left out: already on another open upgrade.";
        }
        if (r.Refused.Count > 0)
        {
            var first = r.Refused.Values.First();
            text += r.Refused.Count == 1 ? $" {first}" : $" {r.Refused.Count} couldn't be added. {first}";
        }

        var tone = outcome.LeftOut > 0 || r.Refused.Count > 0 || added == 0 ? AlertTone.Warn : AlertTone.Success;
        return new PlanNotice(text, tone, $"/upgrades/{outcome.UpgradeId}", "Open the upgrade");
    }

    private static string Environments(int n) => n == 1 ? "1 environment" : $"{n} environments";

    // ── The row menus' moves ────────────────────────────────────────────

    /// <summary>
    /// Marks an upgrade done after saying what is still unchecked, named line by line, so
    /// it is never a blind click. The same confirm as the upgrade's own page, in its words.
    /// </summary>
    private async Task MarkDoneAsync(EnvironmentUpgradeSummary upgrade)
    {
        if (_doneConfirm is null || _planBusy) return;
        _planNotice = null;
        var detail = await Upgrades.GetAsync(upgrade.Id);
        if (detail is null)
        {
            _planNotice = new PlanNotice("That upgrade no longer exists. It may have been deleted.", AlertTone.Warn);
            await LoadOpenAsync();
            return;
        }

        var name = detail.Upgrade.Name;
        _doneUnchecked = detail.Lines.Where(l => !l.IsChecked).ToList();
        var (title, message) = MarkDoneWording(name, _doneUnchecked.Count, detail.Lines.Count);
        // The dialog's head icon follows _doneUnchecked through its parameters, so the page
        // redraws before it opens (the upgrade page does the same).
        StateHasChanged();
        if (!await _doneConfirm.OpenAsync(title, message, "Mark done", "btn--primary", "circle-check")) return;

        await RunPlanMoveAsync(async () =>
        {
            await Upgrades.CloseAsync(upgrade.Id);
            _planNotice = new PlanNotice($"\"{name}\" is marked done and moved to the Archive.", AlertTone.Success,
                "/upgrades?view=archive", "Go to the Archive");
        }, archiveChanged: true);
    }

    /// <summary>The Mark done confirm's title and sentence (PageUpgrade.dc.html, the "Mark done" confirm).</summary>
    /// <remarks>
    /// The sentences are the upgrade page's own (UpgradeDetail's Mark done), so the two doors
    /// to the same move say the same thing. Only the all-checked title names the upgrade:
    /// from a list, "this upgrade" does not say which.
    /// </remarks>
    internal static (string Title, string Message) MarkDoneWording(string name, int uncheckedCount, int total) =>
        uncheckedCount == 0
            ? ($"Mark \"{name}\" done?", $"Every environment is checked. \"{name}\" moves to the Archive and becomes read-only.")
            : ($"{uncheckedCount} of {total} {(uncheckedCount == 1 ? "is" : "are")} not checked yet. Mark done anyway?",
                $"\"{name}\" moves to the Archive and becomes read-only. The environments not ticked off stay that way in the record.");

    /// <summary>
    /// The line under the unchecked list that points at the second pass, counting only the
    /// lines it would carry (failed, or never started). Null when there are none.
    /// </summary>
    internal static string? LeftoversHint(IReadOnlyCollection<EnvironmentUpgradeLineRow> uncheckedLines)
    {
        var leftovers = uncheckedLines.Count(l => EnvironmentUpgradeLineState.IsLeftover(l.State));
        if (leftovers == 0) return null;
        // When only some of the listed lines would carry over, say which by name: "the one
        // that failed" beside a list of two read as a miscount in review.
        var which = leftovers == uncheckedLines.Count
            ? leftovers == 1 ? "this one" : $"these {NumberWord(leftovers)}"
            : leftovers == 1
                ? LineName(uncheckedLines.First(l => EnvironmentUpgradeLineState.IsLeftover(l.State)))
                : $"the {NumberWord(leftovers)} that failed or never started";
        return $"You can start a new upgrade with {which} from the upgrade's page afterwards: \"New upgrade from the leftovers\".";
    }

    /// <summary>"CRONUS UK Ltd. Production": a line named the way the team says it aloud.</summary>
    private static string LineName(EnvironmentUpgradeLineRow line) =>
        $"{line.Environment.ProjectName} {line.Environment.EnvironmentName}";

    /// <summary>"Production", or "Test (Sandbox)" when the name does not already say the type.</summary>
    internal static string EnvironmentLabel(UpgradeFleetRow row) =>
        string.Equals(row.EnvironmentName, row.EnvironmentType, StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(row.EnvironmentType)
            ? row.EnvironmentName
            : $"{row.EnvironmentName} ({row.EnvironmentType})";

    private static string NumberWord(int n) => n switch
    {
        2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six",
        7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private async Task DeleteUpgradeAsync(EnvironmentUpgradeSummary upgrade)
    {
        if (_deleteConfirm is null || _planBusy) return;
        _planNotice = null;
        var confirmed = await _deleteConfirm.OpenAsync(
            $"Delete \"{upgrade.Name}\"?",
            "Nothing has been done from it yet, so only the upgrade and its list of environments go. The environments themselves are not touched. This can't be undone.",
            "Delete upgrade", "btn--danger", "trash-2");
        if (!confirmed) return;

        await RunPlanMoveAsync(async () =>
        {
            await Upgrades.DeleteAsync(upgrade.Id);
            _planNotice = new PlanNotice($"\"{upgrade.Name}\" was deleted.", AlertTone.Success);
        }, archiveChanged: false);
    }

    /// <summary>
    /// Takes a done upgrade back out of the archive. Refused - naming them - when any of
    /// its environments has since gone on another open upgrade; the refusal is the notice.
    /// </summary>
    private async Task ReopenAsync(EnvironmentUpgradeSummary upgrade)
    {
        if (_planBusy) return;
        _planNotice = null;
        await RunPlanMoveAsync(async () =>
        {
            await Upgrades.ReopenAsync(upgrade.Id);
            _planNotice = new PlanNotice($"\"{upgrade.Name}\" is open again.", AlertTone.Success,
                $"/upgrades/{upgrade.Id}", "Open it");
        }, archiveChanged: true);
    }

    /// <summary>
    /// One of the moves above, with its refusal said as a notice rather than thrown at the
    /// person, and what it can have changed read again afterwards whichever way it went:
    /// the open list always (it drives the subtitle and a tab), and the archive only when
    /// the move touches it - listed if it is on screen, counted if not.
    /// </summary>
    private async Task RunPlanMoveAsync(Func<Task> move, bool archiveChanged)
    {
        _planBusy = true;
        try
        {
            await move();
        }
        catch (PlanValidationException ex)
        {
            _planNotice = new PlanNotice(FirstMessage(ex), AlertTone.Warn);
        }
        catch (ProjectAccessDeniedException ex)
        {
            _planNotice = new PlanNotice(ex.Message, AlertTone.Warn);
        }
        finally
        {
            _planBusy = false;
        }
        await LoadOpenAsync();
        if (!archiveChanged) return;
        if (_view == UpgradesView.Archive) await LoadArchiveAsync();
        else await CountArchiveAsync();
    }
}
