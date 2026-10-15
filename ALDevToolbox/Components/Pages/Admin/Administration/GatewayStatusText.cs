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
    /// Why a service tier could not be read, for an Admin who does not know how the
    /// gateway works: what happened and what to do next. The gateway's own words stay
    /// on the page under "What the gateway said" for whoever has to dig further. The
    /// patterns follow the messages the gateway writes (<c>PowerShellRunner</c>,
    /// <c>PowerShellInventoryReader</c>, <c>InventoryNormaliser</c>).
    /// </summary>
    public static string PlainReadError(string raw)
    {
        const string KeepsHappening = "If it keeps happening, ask the customer's IT to check the server.";
        bool Has(string text) => raw.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("did not finish in time") || Has("did not answer"))
        {
            return "Could not be read: the server took too long to answer. Try Refresh now. " + KeepsHappening;
        }
        if (Has("is another computer"))
        {
            return "Could not be read: it is on another computer, and the gateway reads only the service tiers on its own.";
        }
        if (Has("no Business Central service for"))
        {
            return "Could not be read: there is no Business Central service by this name on the server. Ask the customer's IT to check the name in the gateway's settings.";
        }
        if (Has("Someone other than administrators"))
        {
            return "Not read, for safety: people other than administrators can change the Business Central program files on the server. Ask the customer's IT to restrict them.";
        }
        if (Has("No Business Central administration module was found"))
        {
            return "Could not be read: the Business Central administration tools were not found on the server. " + KeepsHappening;
        }
        return "Could not be read. Try Refresh now. " + KeepsHappening;
    }

    /// <summary>
    /// A licence's expiry date as the licence states it. A calendar date, not an
    /// instant: unlike the times on the page it does not go through
    /// <c>&lt;Timestamp&gt;</c>, whose time-zone conversion would move it a day in
    /// some zones.
    /// </summary>
    public static string LicenceDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
