using System.Text.Json;
using System.Text.Json.Serialization;
using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// One thing the gateway did around an on-premises deployment's apps (#1368): customer
/// IT's before script, one service tier's restart, or the after script. Stored on the
/// deployment (<c>oe_project_deliveries.gateway_steps</c>) in the order they ran, and
/// shown as a line each ("Restarted CRONUS-BC02 / BC140", or which one failed).
/// </summary>
public sealed record DeliveryGatewayStep
{
    /// <summary>One of <see cref="DeliveryGatewaySteps"/>' kinds.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The tier ("CRONUS-BC02 / BC140"), the tiers of a group that was not restarted, or the script's path.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>One of <see cref="DeliveryGatewaySteps"/>' outcomes.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The gateway's sentence: why it failed or was not done. Null when it went through.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Reads the restart group and scripts out of a finished <c>publish-install</c> result
/// into <see cref="DeliveryGatewayStep"/>s, stores and reads them back, and says what a
/// failure among them means for the deployment. See <c>.design/on-prem-gateway.md</c>,
/// "The restart group, as built".
/// </summary>
public static class DeliveryGatewaySteps
{
    public const string BeforeScript = "before-script";
    public const string Restart = "restart";
    public const string AfterScript = "after-script";

    public const string Done = "done";
    public const string Failed = "failed";
    public const string NotDone = "not-done";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// The steps in the order they ran: the before script, each tier of the restart group,
    /// the after script. A group the gateway did not restart at all (nothing was installed,
    /// the settings changed) is one line naming its tiers, not a line per tier saying the
    /// same thing. Empty for a result without any (online, or a gateway older than #1368).
    /// </summary>
    public static List<DeliveryGatewayStep> From(GatewayPublishInstallResult? result)
    {
        var steps = new List<DeliveryGatewayStep>();
        if (result is null) return steps;
        foreach (var script in result.Scripts.Where(s => s.Stage == GatewayScriptStages.Before))
        {
            steps.Add(ScriptStep(BeforeScript, script));
        }
        if (result.Restart is { } restart)
        {
            var untouched = restart.Tiers.All(t => t.Outcome == GatewayRestartTierOutcomes.NotRestarted);
            if (restart.Message is { Length: > 0 } why && untouched)
            {
                steps.Add(new DeliveryGatewayStep
                {
                    Kind = Restart,
                    Name = string.Join(", ", restart.Tiers.Select(t => $"{t.Machine} / {t.Instance}")),
                    Outcome = NotDone,
                    Message = why,
                });
            }
            else
            {
                steps.AddRange(restart.Tiers.Select(t => new DeliveryGatewayStep
                {
                    Kind = Restart,
                    Name = $"{t.Machine} / {t.Instance}",
                    Outcome = t.Outcome switch
                    {
                        GatewayRestartTierOutcomes.Restarted => Done,
                        GatewayRestartTierOutcomes.Failed => Failed,
                        _ => NotDone,
                    },
                    Message = string.IsNullOrWhiteSpace(t.Message) ? null : t.Message.Trim(),
                }));
            }
        }
        foreach (var script in result.Scripts.Where(s => s.Stage == GatewayScriptStages.After))
        {
            steps.Add(ScriptStep(AfterScript, script));
        }
        return steps;
    }

    private static DeliveryGatewayStep ScriptStep(string kind, GatewayScriptResult script) => new()
    {
        Kind = kind,
        Name = script.Path,
        Outcome = script.Outcome switch
        {
            GatewayScriptOutcomes.Succeeded => Done,
            GatewayScriptOutcomes.Failed => Failed,
            _ => NotDone,
        },
        Message = string.IsNullOrWhiteSpace(script.Message) ? null : script.Message.Trim(),
    };

    public static string? ToJson(IReadOnlyList<DeliveryGatewayStep> steps) =>
        steps.Count == 0 ? null : JsonSerializer.Serialize(steps, Json);

    /// <summary>The steps as stored; empty when there are none or they cannot be read.</summary>
    public static List<DeliveryGatewayStep> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DeliveryGatewayStep>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The script's file name, which is what a person recognises: <c>before-deploy.ps1</c>.</summary>
    public static string ScriptName(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    /// <summary>
    /// The deployment's failure line when a step around the apps failed: the before script
    /// (nothing was installed), a tier that did not come back, or the after script (the
    /// apps are installed). Null when none failed.
    /// </summary>
    public static string? FailureLine(IReadOnlyList<DeliveryGatewayStep> steps)
    {
        if (steps.FirstOrDefault(s => s.Kind == BeforeScript && s.Outcome == Failed) is { } before)
        {
            return $"The customer's before script ({ScriptName(before.Name)}) failed, so nothing was installed. {Sentence(before.Message)}".TrimEnd();
        }
        var failedTiers = steps.Where(s => s.Kind == Restart && s.Outcome == Failed).ToList();
        if (failedTiers.Count > 0)
        {
            var names = string.Join(" and ", failedTiers.Select(s => s.Name));
            return $"The apps were installed, but {names} did not restart. {Sentence(failedTiers[0].Message)}".TrimEnd();
        }
        if (steps.FirstOrDefault(s => s.Kind == AfterScript && s.Outcome == Failed) is { } after)
        {
            return $"The apps were installed and the service tiers restarted, but the customer's after script ({ScriptName(after.Name)}) failed. {Sentence(after.Message)}".TrimEnd();
        }
        return null;
    }

    /// <summary>
    /// True when the apps went in and what came after them did not all go through: a tier
    /// that did not restart (or was not restarted, because the settings changed), or an
    /// after script that failed. Deploying the same build again would then change nothing.
    /// Only meaningful for a deployment that failed.
    /// </summary>
    public static bool FailedAfterInstall(IReadOnlyList<DeliveryGatewayStep> steps) =>
        !steps.Any(s => s.Kind == BeforeScript && s.Outcome == Failed)
        && steps.Any(s => (s.Kind == Restart && s.Outcome != Done && s.Message is not null)
                          || (s.Kind == AfterScript && s.Outcome == Failed));

    /// <summary>"it ended with exit code 2." as "It ended with exit code 2."</summary>
    public static string Sentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = text.Trim();
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t.EndsWith('.') ? t : t + ".";
    }
}
