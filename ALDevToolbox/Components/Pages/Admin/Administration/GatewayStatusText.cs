using ALDevToolbox.Services.Gateways;

namespace ALDevToolbox.Components.Pages.Admin.Administration;

/// <summary>How a gateway's connection status reads on the Gateways tab and on a gateway's own page.</summary>
public static class GatewayStatusText
{
    public static string ToneOf(GatewayStatus status) => status switch
    {
        GatewayStatus.Online => "success",
        GatewayStatus.Offline => "danger",
        GatewayStatus.WaitingForApproval => "warn",
        _ => "muted",
    };

    public static string LabelOf(GatewayStatus status) => status switch
    {
        GatewayStatus.Online => "Online",
        GatewayStatus.Offline => "Offline",
        GatewayStatus.WaitingForApproval => "Waiting for approval",
        GatewayStatus.NotConnectedYet => "Not connected yet",
        _ => "Revoked",
    };

    /// <summary>
    /// A licence's expiry date as the licence states it. A calendar date, not an
    /// instant: unlike the times on the page it does not go through
    /// <c>&lt;Timestamp&gt;</c>, whose time-zone conversion would move it a day in
    /// some zones.
    /// </summary>
    public static string LicenceDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
