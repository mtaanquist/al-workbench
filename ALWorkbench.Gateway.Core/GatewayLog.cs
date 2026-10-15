using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The log lines customer IT reads, in the Windows event log and the gateway's log
/// files: every command, who asked for it, and how it ended. Fixed event ids so they
/// can filter on them (Event Viewer, "Filter Current Log"). Written for a Windows
/// administrator who knows nothing about AL Workbench's code.
/// </summary>
public static partial class GatewayLog
{
    public const int CommandStartedId = 1000;
    public const int CommandSucceededId = 1001;
    public const int CommandFailedId = 1002;
    public const int CommandRefusedId = 1003;
    public const int CommandInterruptedId = 1004;
    public const int ResultReportedLateId = 1005;
    public const int InventorySentId = 1006;
    public const int InventoryNotSentId = 1007;
    public const int RegisteredId = 2000;
    public const int WaitingForApprovalId = 2001;
    public const int ApprovedId = 2002;
    public const int UnreachableId = 2003;
    public const int ReconnectedId = 2004;
    public const int CredentialRefusedId = 2005;
    public const int NotRegisteredId = 2006;
    public const int PolicyLoadedId = 3000;
    public const int PolicyInvalidId = 3001;
    public const int PolicyMissingId = 3002;
    public const int PolicyUnsafeId = 3003;
    public const int FailedId = 2007;
    public const int FolderLoosenedId = 2008;
    public const int OutdatedId = 2009;

    [LoggerMessage(EventId = CommandStartedId, Level = LogLevel.Information,
        Message = "Started command {CommandId} ({Verb}), requested by {RequestedBy}.")]
    public static partial void CommandStarted(this ILogger logger, long commandId, string verb, string requestedBy);

    [LoggerMessage(EventId = CommandSucceededId, Level = LogLevel.Information,
        Message = "Command {CommandId} ({Verb}), requested by {RequestedBy}, succeeded.")]
    public static partial void CommandSucceeded(this ILogger logger, long commandId, string verb, string requestedBy);

    [LoggerMessage(EventId = CommandFailedId, Level = LogLevel.Warning,
        Message = "Command {CommandId} ({Verb}), requested by {RequestedBy}, failed: {Reason}")]
    public static partial void CommandFailed(this ILogger logger, long commandId, string verb, string requestedBy, string reason);

    [LoggerMessage(EventId = CommandRefusedId, Level = LogLevel.Warning,
        Message = "Refused command {CommandId} ({Verb}), requested by {RequestedBy}: {Reason}")]
    public static partial void CommandRefused(this ILogger logger, long commandId, string verb, string requestedBy, string reason);

    [LoggerMessage(EventId = CommandInterruptedId, Level = LogLevel.Warning,
        Message = "Command {CommandId} was cut short when the gateway last stopped. Reported it to AL Workbench as interrupted; it will not be run again.")]
    public static partial void CommandInterrupted(this ILogger logger, long commandId);

    [LoggerMessage(EventId = ResultReportedLateId, Level = LogLevel.Information,
        Message = "Reported the result of command {CommandId} to AL Workbench. It finished before the gateway last stopped, and the result had not arrived yet.")]
    public static partial void ResultReportedLate(this ILogger logger, long commandId);

    [LoggerMessage(EventId = InventorySentId, Level = LogLevel.Information,
        Message = "Read {Read} service tier(s) and sent what they report to AL Workbench; {Failed} could not be read. The gateway only reads, and reads only the tiers gateway.toml allows 'inventory' on.")]
    public static partial void InventorySent(this ILogger logger, int read, int failed);

    [LoggerMessage(EventId = InventoryNotSentId, Level = LogLevel.Warning,
        Message = "Could not send the service tiers' inventory to AL Workbench ({Reason}). Trying again in {Minutes} minutes.")]
    public static partial void InventoryNotSent(this ILogger logger, string reason, int minutes);

    [LoggerMessage(EventId = RegisteredId, Level = LogLevel.Information,
        Message = "Registered with {WorkbenchUrl} as gateway {GatewayId} ({Name}), key fingerprint {Fingerprint}.")]
    public static partial void Registered(this ILogger logger, string workbenchUrl, int gatewayId, string name, string fingerprint);

    [LoggerMessage(EventId = WaitingForApprovalId, Level = LogLevel.Warning,
        Message = "Waiting for an Admin to approve this gateway in AL Workbench (Administration → Gateways, key fingerprint {Fingerprint}). No commands are taken until then.")]
    public static partial void WaitingForApproval(this ILogger logger, string fingerprint);

    [LoggerMessage(EventId = ApprovedId, Level = LogLevel.Information,
        Message = "Connected to {WorkbenchUrl} as gateway {GatewayId}; approved, waiting for commands.")]
    public static partial void Approved(this ILogger logger, string workbenchUrl, int gatewayId);

    [LoggerMessage(EventId = UnreachableId, Level = LogLevel.Warning,
        Message = "Cannot reach AL Workbench ({Reason}). Trying again in {Seconds} seconds.")]
    public static partial void Unreachable(this ILogger logger, string reason, int seconds);

    [LoggerMessage(EventId = ReconnectedId, Level = LogLevel.Information,
        Message = "Reached AL Workbench again after {Failures} failed attempts.")]
    public static partial void Reconnected(this ILogger logger, int failures);

    [LoggerMessage(EventId = CredentialRefusedId, Level = LogLevel.Error,
        Message = "AL Workbench refused this gateway's credentials: {Reason} Trying again in {Seconds} seconds. If the reason mentions the time, correct this computer's clock; the gateway carries on by itself once it is right. If the gateway was revoked, ask an Admin for a new registration code and run 'register' again.")]
    public static partial void CredentialRefused(this ILogger logger, string reason, int seconds);

    [LoggerMessage(EventId = OutdatedId, Level = LogLevel.Error,
        Message = "AL Workbench refuses this version of the gateway ({Version}): {Reason} Trying again in {Seconds} seconds. To update: download the gateway from Administration → Gateways in AL Workbench, stop the AL Workbench Gateway service, replace ALWorkbench.Gateway.exe with the new file, and start the service. The registration and settings are kept.")]
    public static partial void Outdated(this ILogger logger, string version, string reason, int seconds);

    [LoggerMessage(EventId = PolicyLoadedId, Level = LogLevel.Information,
        Message = "Read the gateway settings in {PolicyFile}: {Count} service tiers listed. Anything not listed there is refused.")]
    public static partial void PolicyLoaded(this ILogger logger, string policyFile, int count);

    [LoggerMessage(EventId = PolicyInvalidId, Level = LogLevel.Error,
        Message = "The gateway settings in {PolicyFile} have mistakes, so until they are corrected the gateway only reads (inventory) from the tiers the last correct version listed and refuses every change. {Problems}")]
    public static partial void PolicyInvalid(this ILogger logger, string policyFile, string problems);

    [LoggerMessage(EventId = PolicyMissingId, Level = LogLevel.Warning,
        Message = "There are no gateway settings at {PolicyFile}, so the gateway refuses every command. Copy gateway.example.toml to gateway.toml beside the program and list the service tiers the gateway may manage.")]
    public static partial void PolicyMissing(this ILogger logger, string policyFile);

    [LoggerMessage(EventId = PolicyUnsafeId, Level = LogLevel.Error,
        Message = "The gateway's program folder, its settings file or a script the settings name can be changed by someone other than administrators, and whoever can change them can run code as a Business Central administrator. {Problems} Only Administrators and SYSTEM may have write access to them. Until this is fixed the gateway runs no commands, and a gateway that is starting stops.")]
    public static partial void PolicyUnsafe(this ILogger logger, string problems);

    [LoggerMessage(EventId = NotRegisteredId, Level = LogLevel.Error,
        Message = "This gateway is not registered yet. Run 'ALWorkbench.Gateway register --url <address> --code <code>' as an administrator, then start the service.")]
    public static partial void NotRegistered(this ILogger logger);

    [LoggerMessage(EventId = FailedId, Level = LogLevel.Critical,
        Message = "The gateway stopped because of an unexpected error: {Reason} Windows restarts the service as set in its recovery options. If this repeats, send the gateway's log files to whoever looks after AL Workbench.")]
    public static partial void Failed(this ILogger logger, Exception exception, string reason);

    [LoggerMessage(EventId = FolderLoosenedId, Level = LogLevel.Critical,
        Message = "The gateway will not run: the access rights on its data folder {Folder} are not the ones it set. {Problems} Someone other than an administrator may have changed its settings or files. Check the folder, then run 'ALWorkbench.Gateway install' again as an administrator to put the rights back.")]
    public static partial void FolderLoosened(this ILogger logger, string folder, string problems);
}
