using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Components.Pages.Upgrades;

/// <summary>
/// The hover text on an upgrade's "Planned for" field, shared by the New upgrade and Edit
/// details dialogs so the two explain the slot the same way.
/// </summary>
public static class UpgradeSlotTip
{
    public static string For(DisplayTimeZone zone)
    {
        var name = zone.Zone.Id == TimeZoneInfo.Utc.Id ? "UTC" : zone.Zone.Id;
        return $"Optional. When the team plans to update the environments, in {name} time. "
            + "Nothing happens at this time on its own: it is filled in for you when you start the update.";
    }
}
