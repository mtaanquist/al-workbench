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
    public const int RegisteredId = 2000;
    public const int WaitingForApprovalId = 2001;
    public const int ApprovedId = 2002;
    public const int UnreachableId = 2003;
    public const int ReconnectedId = 2004;
    public const int CredentialRefusedId = 2005;
    public const int NotRegisteredId = 2006;
    public const int FailedId = 2007;
    public const int FolderLoosenedId = 2008;

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
