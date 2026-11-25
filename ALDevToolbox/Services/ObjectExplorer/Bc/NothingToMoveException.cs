using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// A date move refused because there was nothing to move: no next update, no latest date
/// for it, or already on the latest date. Still a <see cref="PlanValidationException"/>,
/// so every caller that shows the reason keeps working; the worker tells it apart only for
/// the automatic move after an update, where finding nothing is an answer, not a failure
/// (.design/environment-updates.md, "Moving the next update out after an update").
/// </summary>
public sealed class NothingToMoveException(string message)
    : PlanValidationException(new Dictionary<string, string> { ["Update"] = message });
