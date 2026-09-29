using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Components.Pages.Upgrades;

/// <summary>What "Add to upgrade..." did, for the Fleet view to report.</summary>
/// <param name="UpgradeId">The upgrade the environments went on.</param>
/// <param name="UpgradeName">Its name, for the notice.</param>
/// <param name="Result">The service's per-environment answer.</param>
/// <param name="LeftOut">Ticked environments the dialog showed as on another open upgrade and did not send.</param>
/// <param name="Created">True when the dialog made the upgrade.</param>
public sealed record AddToUpgradeOutcome(
    int UpgradeId, string UpgradeName, AddUpgradeLinesResult Result, int LeftOut, bool Created);

/// <summary>
/// A notice about the planned upgrades themselves - marked done, reopened, deleted,
/// environments added - above the view, in every state. A refusal is one of these too,
/// in the warning tone, never a raw exception.
/// </summary>
public sealed record PlanNotice(string Text, AlertTone Tone, string? LinkHref = null, string? LinkLabel = null);
