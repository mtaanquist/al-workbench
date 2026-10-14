using System.Net;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// AL Workbench could not be reached, or answered in a way that says "try again
/// later": no connection, a timeout, a proxy error, 429 or a 5xx. The loop backs off
/// and tries again.
/// </summary>
public sealed class WorkbenchUnreachableException : Exception
{
    public WorkbenchUnreachableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 401: AL Workbench did not accept this gateway's credentials. The commonest reasons
/// are that the gateway was revoked, or that this computer's clock is far off. The
/// loop logs it and keeps trying on its back-off, because a clock can be corrected
/// without anyone restarting the service; <see cref="Exception.Message"/> carries
/// the workbench's own reason.
/// </summary>
public sealed class GatewayCredentialRefusedException : Exception
{
    public GatewayCredentialRefusedException(string message) : base(message) { }
}

/// <summary>403: the gateway is waiting for an Admin to approve it, and gets no commands or files until then.</summary>
public sealed class GatewayNotApprovedException : Exception
{
    public GatewayNotApprovedException(string message) : base(message) { }
}

/// <summary>409: the command is no longer this gateway's to report on (finished, expired or failed by the workbench).</summary>
public sealed class CommandClosedException : Exception
{
    public CommandClosedException(string message) : base(message) { }
}

/// <summary>
/// Any other refusal (400, 404, ...): AL Workbench understood the request and said no.
/// Retrying the same request will not help.
/// </summary>
public sealed class WorkbenchRefusedException : Exception
{
    public WorkbenchRefusedException(HttpStatusCode status, string message) : base(message)
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }
}
