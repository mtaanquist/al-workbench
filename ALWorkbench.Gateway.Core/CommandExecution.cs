using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// What a verb gets to work with: the command, its files already downloaded and their
/// hashes checked, and a way to add lines to the command's log in AL Workbench.
/// </summary>
/// <param name="Command">The command as AL Workbench sent it.</param>
/// <param name="Files">Local path of each of the command's files, by file id.</param>
/// <param name="Progress">Adds a line to the command's log. Cheap: lines are batched before they are sent.</param>
public sealed record GatewayCommandContext(
    GatewayCommandMessage Command,
    IReadOnlyDictionary<long, string> Files,
    Action<string> Progress);

/// <summary>How a verb ended, as reported to AL Workbench.</summary>
public sealed record GatewayCommandOutcome(bool Succeeded, JsonObject? Result = null, string? FailureMessage = null)
{
    public static GatewayCommandOutcome Success(JsonObject? result = null) => new(true, result);

    public static GatewayCommandOutcome Failure(string message, JsonObject? result = null) => new(false, result, message);
}

/// <summary>
/// Runs one command's verb. The seam the policy file and the PowerShell runner (#1373)
/// and the verbs themselves (#1365 onward) plug into; the frame only knows that a
/// command goes in and an outcome comes out. Cancellation means the service is
/// stopping or AL Workbench closed the command.
/// </summary>
public interface ICommandExecutor
{
    Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct);
}

/// <summary>
/// Hands each command to the executor of its verb; a verb with none fails with
/// <see cref="NotSupportedCommandExecutor"/>'s message. What the Windows service gives
/// <c>GatewayRunner</c>: a new verb is one more entry here, not a change to the loop.
/// (<c>refresh-inventory</c> is the loop's own, because the loop also runs it on a schedule.)
/// </summary>
public sealed class VerbExecutors : ICommandExecutor
{
    private static readonly NotSupportedCommandExecutor NotSupported = new();
    private readonly IReadOnlyDictionary<GatewayVerb, ICommandExecutor> _verbs;

    public VerbExecutors(IReadOnlyDictionary<GatewayVerb, ICommandExecutor> verbs)
    {
        _verbs = verbs;
    }

    public Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (_verbs.GetValueOrDefault(context.Command.Verb) ?? NotSupported).ExecuteAsync(context, ct);
    }
}

/// <summary>
/// The executor for a verb this version cannot run: every command fails with a message saying so.
/// It keeps the loop whole end to end - claim, download, report - before any verb can
/// touch a service tier.
/// </summary>
public sealed class NotSupportedCommandExecutor : ICommandExecutor
{
    public Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var verb = GatewayVerbs.ToWire(context.Command.Verb);
        return Task.FromResult(GatewayCommandOutcome.Failure(
            $"This version of the gateway cannot run '{verb}' commands yet. Nothing was changed. Update the gateway to a version that can."));
    }
}
