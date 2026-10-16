using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// What a person does next about an app the gateway refused (#1367). A refusal by the
/// customer's gateway settings is not fixed by deploying the same build again, so the
/// deployment page offers this sentence in place of "Deploy again". The gateway words its
/// refusals itself (<c>ALWorkbench.Gateway.Core</c>, the policy check and the
/// publish-install verb), so the kinds are recognised from those sentences: the publisher,
/// an unsigned app, the signer, and Force sync, with a general sentence for any other rule.
/// </summary>
public static partial class GatewayRefusalAdvice
{
    /// <summary>
    /// Whether <paramref name="message"/> is a refusal by the customer's gateway settings,
    /// rather than by the file or the state of the tier.
    /// </summary>
    public static bool IsSettingsRefusal(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("Not allowed by", StringComparison.Ordinal)
            || message.Contains("customer's gateway settings", StringComparison.Ordinal)
            || message.Contains("gateway.toml", StringComparison.Ordinal));

    /// <summary>
    /// The next step for a settings refusal on <paramref name="tierName"/>
    /// (<c>SERVER / INSTANCE</c>), or null when <paramref name="message"/> is not one.
    /// </summary>
    public static string? NextStep(string? message, string tierName)
    {
        if (!IsSettingsRefusal(message)) return null;
        var text = message!;
        if (PublisherPattern().Match(text) is { Success: true } publisher)
        {
            return $"Ask the customer's IT to allow apps published by {publisher.Groups[1].Value} on {tierName}, then deploy again.";
        }
        if (SignerPattern().Match(text) is { Success: true } signer)
        {
            return $"Ask the customer's IT to add {signer.Groups[1].Value.Trim()} to the signers allowed on {tierName}, then deploy again.";
        }
        if (text.Contains("unsigned apps", StringComparison.OrdinalIgnoreCase))
        {
            return $"Sign the app, or ask the customer's IT to allow unsigned apps on {tierName}, then deploy again.";
        }
        if (text.Contains("Force sync", StringComparison.OrdinalIgnoreCase) || text.Contains("ForceSync", StringComparison.Ordinal))
        {
            return $"Set this deployment pipeline's schema sync to Add, or ask the customer's IT to allow Force sync on {tierName}, then deploy again.";
        }
        return $"Ask the customer's IT to allow this in the gateway settings for {tierName}, then deploy again.";
    }

    [GeneratedRegex(@"does not take apps from the publisher '([^']+)'")]
    private static partial Regex PublisherPattern();

    [GeneratedRegex(@"is signed by (.+?) \(thumbprint [^)]*\), who is not on")]
    private static partial Regex SignerPattern();
}
