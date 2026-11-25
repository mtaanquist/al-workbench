using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// How an app uploaded by hand to an on-premises environment went (#1786), read from its
/// gateway command, as a service tier action's is: the history row is written once when
/// the command is queued, and the command is what the gateway reports on. Each app is
/// found in the command's result by its file name, and said the way a pipeline
/// deployment's app is (<see cref="GatewayDeliveryTarget.OutcomeFor"/>).
/// </summary>
public static class OnPremisesUploadResult
{
    /// <summary>
    /// <paramref name="rows"/> with each sent on-premises upload's status and outcome read
    /// from its command; every other row as it was. One query for all of them. Under the
    /// caller's query filter.
    /// </summary>
    public static async Task<List<UpgradeActionRow>> ApplyAsync(AppDbContext db, List<UpgradeActionRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);
        var ids = rows.Where(IsSentUpload).Select(r => r.GatewayCommandId!.Value).Distinct().ToList();
        if (ids.Count == 0) return rows;
        var commands = await db.GatewayCommands.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new ServiceTierActionOutcome.CommandFacts(c.Id, c.Status, c.FailureMessage, c.ResultJson, c.ParametersJson, c.FinishedAt, c.GatewayId))
            .ToDictionaryAsync(c => c.Id, ct);
        return rows.Select(r => IsSentUpload(r) ? Apply(r, commands.GetValueOrDefault(r.GatewayCommandId!.Value)) : r).ToList();
    }

    /// <summary>An upload handed to a gateway whose outcome is still the command's to say: settled rows keep their own.</summary>
    private static bool IsSentUpload(UpgradeActionRow r) =>
        r.Kind == UpgradeActionKind.UploadApp && r.GatewayCommandId is not null && r.Status == UpgradeActionStatus.Sent && r.Outcome is null;

    /// <summary>One sent upload read against its command; null <paramref name="command"/> means it is gone.</summary>
    public static UpgradeActionRow Apply(UpgradeActionRow row, ServiceTierActionOutcome.CommandFacts? command)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (command is null)
        {
            return row with
            {
                Status = UpgradeActionStatus.Failed,
                Outcome = "The record of this is gone (its gateway was removed), so how it went is not known. Check the environment's apps.",
            };
        }
        switch (command.Status)
        {
            case GatewayCommandStatus.Queued or GatewayCommandStatus.Claimed or GatewayCommandStatus.Running:
                // Pending with a sent time reads as installing, as an online install does.
                return row with { Status = UpgradeActionStatus.Pending, Outcome = null };
            case GatewayCommandStatus.Expired:
                return row with
                {
                    Status = UpgradeActionStatus.Failed,
                    Outcome = "The gateway did not pick this up in time, so nothing was installed. Check that the gateway is online, then upload it again.",
                };
        }

        var result = Parse(command.ResultJson);
        var index = IndexOf(result, command.ParametersJson, row.PackageFileName);
        var outcome = GatewayDeliveryTarget.OutcomeFor(
            new GatewayCommandProgress(command.Status, command.FailureMessage, result, string.Empty, 0, LeftQueue: true),
            index, string.Empty);
        if (outcome.Completed)
        {
            return row with { Status = UpgradeActionStatus.Sent, Outcome = outcome.Message };
        }
        return outcome.Outcome switch
        {
            DeliveryAppOutcome.AlreadyInstalled => row with { Status = UpgradeActionStatus.Sent, Outcome = outcome.Message },
            DeliveryAppOutcome.Refused when outcome.Message is { } m && GatewayRefusalAdvice.IsSettingsRefusal(m) =>
                row with { Status = UpgradeActionStatus.Failed, Outcome = ServiceTierActionOutcome.RefusedMessage },
            _ => row with { Status = UpgradeActionStatus.Failed, Outcome = GatewayStepText.Cap(outcome.Message, 1_000) },
        };
    }

    /// <summary>Where the app sits in the command: by its file name in the result, else in the parameters; the end when not found.</summary>
    private static int IndexOf(JsonObject? result, string parametersJson, string? fileName)
    {
        if (fileName is null) return int.MaxValue;
        var parsed = GatewayPublishInstallResult.FromJson(result);
        if (parsed is not null)
        {
            for (var i = 0; i < parsed.Apps.Count; i++)
            {
                if (string.Equals(parsed.Apps[i].File, fileName, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        if (Parse(parametersJson)?[GatewayCommandParameters.Apps] is JsonArray apps)
        {
            for (var i = 0; i < apps.Count; i++)
            {
                var file = (apps[i]?[GatewayCommandParameters.AppFile] as JsonValue)?.TryGetValue<string>(out var f) == true ? f : null;
                if (string.Equals(file, fileName, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        return int.MaxValue;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
