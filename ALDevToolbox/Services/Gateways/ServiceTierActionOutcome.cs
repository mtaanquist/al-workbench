using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>Where an unfinished service tier action's gateway command stands.</summary>
public enum ServiceTierProgress
{
    /// <summary>Queued or handed out; the gateway has not started on it.</summary>
    Waiting,

    /// <summary>The gateway is stopping or starting the tiers.</summary>
    Running,
}

/// <summary>
/// How a service tier action from the environment page went (#1371), read from its gateway
/// command rather than stored on the history row, as a licence upload's import is: the row
/// is written once when the command is queued, and the command is what the gateway reports
/// on. Words for the consultant: the gateway's own refusals name its settings file, which is
/// customer IT's, so a refusal is said as "the customer's gateway settings did not allow
/// this", and the raw text stays in the command's log on the gateway's page. See
/// <c>.design/on-prem-gateway.md</c>, "Service tier actions, as built".
/// </summary>
public static class ServiceTierActionOutcome
{
    /// <summary>What the history says when the customer's gateway settings refused the action before it ran.</summary>
    public const string RefusedMessage =
        "The customer's gateway settings did not allow this, so nothing was changed. Ask the customer's IT to check them. An administrator can see the gateway's own reason on the gateway's page under Administration → Gateways.";

    /// <summary>How every refusal the gateway makes before running anything ends.</summary>
    private const string RefusalEnding = "Nothing was changed.";

    /// <summary>
    /// <paramref name="rows"/> with each service tier action's status and outcome read from
    /// its command; every other row as it was. One query for all of them. Under the caller's
    /// query filter.
    /// </summary>
    public static async Task<List<UpgradeActionRow>> ApplyAsync(AppDbContext db, List<UpgradeActionRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);
        var ids = rows.Where(r => r.IsServiceTierAction && r.GatewayCommandId is not null)
            .Select(r => r.GatewayCommandId!.Value)
            .Distinct()
            .ToList();
        if (!rows.Any(r => r.IsServiceTierAction)) return rows;
        var commands = ids.Count == 0
            ? new Dictionary<long, CommandFacts>()
            : await db.GatewayCommands.AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .Select(c => new CommandFacts(c.Id, c.Status, c.FailureMessage, c.ResultJson, c.ParametersJson, c.FinishedAt, c.GatewayId))
                .ToDictionaryAsync(c => c.Id, ct)
                .ConfigureAwait(false);

        // The database each command's tier serves, as its gateway last read it, so a restart
        // of every tier on it can say which database that was.
        var gatewayIds = commands.Values.Select(c => c.GatewayId).Distinct().ToList();
        var instances = gatewayIds.Count == 0
            ? []
            : await db.GatewayInstances.AsNoTracking()
                .Where(i => gatewayIds.Contains(i.GatewayId))
                .Select(i => new TierDatabaseFacts(i.GatewayId, i.Machine, i.Name, i.DatabaseServer, i.DatabaseInstance, i.DatabaseName))
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return rows.Select(r =>
            {
                if (!r.IsServiceTierAction) return r;
                var command = r.GatewayCommandId is { } id ? commands.GetValueOrDefault(id) : null;
                return Apply(r, command) with { TierDatabase = DatabaseOf(command, instances) };
            })
            .ToList();
    }

    private sealed record TierDatabaseFacts(int GatewayId, string Machine, string Name, string? Server, string? Instance, string? Database);

    private static string? DatabaseOf(CommandFacts? command, List<TierDatabaseFacts> instances)
    {
        if (command is null || TierOf(command.ParametersJson) is not { } tier) return null;
        var i = instances.FirstOrDefault(x => x.GatewayId == command.GatewayId && Same($"{x.Machine} / {x.Name}", tier));
        return i is null || (i.Database is null && i.Server is null)
            ? null
            : $"{i.Server}{(string.IsNullOrEmpty(i.Instance) ? string.Empty : "\\" + i.Instance)} / {i.Database}";
    }

    /// <summary>What one gateway command says about the action it carries.</summary>
    public sealed record CommandFacts(long Id, GatewayCommandStatus Status, string? FailureMessage, string? ResultJson, string ParametersJson, DateTime? FinishedAt, int GatewayId = 0);

    /// <summary>One row read against its command; null <paramref name="command"/> means it is gone.</summary>
    public static UpgradeActionRow Apply(UpgradeActionRow row, CommandFacts? command)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (command is null)
        {
            return row with
            {
                Status = UpgradeActionStatus.Failed,
                Outcome = "The record of this is gone (its gateway was removed), so how it went is not known. Check the service tier's state on this page.",
                TierProgress = null,
            };
        }
        var tier = TierOf(command.ParametersJson);
        var sent = command.FinishedAt ?? row.SentAt;
        return command.Status switch
        {
            GatewayCommandStatus.Queued or GatewayCommandStatus.Claimed =>
                row with { Status = UpgradeActionStatus.Pending, TierName = tier, TierProgress = ServiceTierProgress.Waiting, Outcome = null },
            GatewayCommandStatus.Running =>
                row with { Status = UpgradeActionStatus.Pending, TierName = tier, TierProgress = ServiceTierProgress.Running, Outcome = null },
            GatewayCommandStatus.Succeeded =>
                row with { Status = UpgradeActionStatus.Sent, TierName = tier, TierProgress = null, SentAt = sent, Outcome = Done(row.Kind, tier, command.ResultJson) },
            GatewayCommandStatus.Expired =>
                row with
                {
                    Status = UpgradeActionStatus.Failed, TierName = tier, TierProgress = null, SentAt = sent,
                    Outcome = "The gateway did not pick this up in time, so nothing was done. Check that the gateway is online, then try again.",
                },
            _ => row with { Status = UpgradeActionStatus.Failed, TierName = tier, TierProgress = null, SentAt = sent, Outcome = Failed(row.Kind, command) },
        };
    }

    private static string Done(UpgradeActionKind kind, string? tier, string? resultJson)
    {
        var name = tier ?? "The service tier";
        if (kind == UpgradeActionKind.RestartServiceTiers)
        {
            var group = GatewayRestartGroupResult.FromJson(Parse(resultJson));
            return group is { Tiers.Count: > 0 }
                ? $"Restarted {Count(group.Tiers.Count)}: {string.Join(", ", group.Tiers.Select(t => $"{t.Machine} / {t.Instance}"))}. They are all running."
                : "Restarted every service tier on the database. They are all running.";
        }
        var result = GatewayServiceTierResult.FromJson(Parse(resultJson));
        var before = result?.Before;
        return kind switch
        {
            UpgradeActionKind.StopServiceTier => Same(before, "Stopped")
                ? $"{name} was already stopped."
                : $"{name} is stopped. Nobody can sign in through it until someone starts it again.",
            UpgradeActionKind.StartServiceTier => Same(before, "Running")
                ? $"{name} was already running."
                : $"{name} is running.",
            _ => Same(before, "Stopped")
                ? $"{name} was stopped; the gateway started it, and it is running."
                : $"{name} restarted and is running.",
        };
    }

    private static string Failed(UpgradeActionKind kind, CommandFacts command)
    {
        var failure = command.FailureMessage?.Trim();
        // Refused before anything ran: the gateway's reason names its settings file.
        if (failure is not null && failure.EndsWith(RefusalEnding, StringComparison.Ordinal)
            && GatewayRefusalAdvice.IsSettingsRefusal(failure))
        {
            return RefusedMessage;
        }
        if (kind == UpgradeActionKind.RestartServiceTiers
            && GatewayRestartGroupResult.FromJson(Parse(command.ResultJson)) is { Tiers.Count: > 0 } group)
        {
            var lines = group.Tiers
                .Where(t => t.Outcome != GatewayRestartTierOutcomes.Restarted || t.Message is not null)
                .Select(t => $"{t.Machine} / {t.Instance}: {t.Message ?? "not restarted."}")
                .ToList();
            if (lines.Count > 0) return "Not every service tier restarted. " + string.Join(" ", lines);
        }
        return string.IsNullOrWhiteSpace(failure)
            ? "The gateway did not say what went wrong. Check the service tier's state on this page."
            : GatewayStepText.Cap(failure, 1_000)!;
    }

    private static string Count(int tiers) => tiers == 1 ? "1 service tier" : $"{tiers} service tiers";

    /// <summary>The tier a command names, <c>SERVER / INSTANCE</c>; null when it cannot be read.</summary>
    internal static string? TierOf(string? parametersJson)
    {
        if (Parse(parametersJson) is not { } parameters) return null;
        var machine = (parameters[GatewayCommandParameters.Machine] as JsonValue)?.TryGetValue<string>(out var m) == true ? m : null;
        var instance = (parameters[GatewayCommandParameters.Instance] as JsonValue)?.TryGetValue<string>(out var i) == true ? i : null;
        return string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(instance) ? null : $"{machine} / {instance}";
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Same(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
}
