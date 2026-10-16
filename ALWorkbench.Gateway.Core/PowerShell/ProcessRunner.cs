using System.Diagnostics;
using System.Text;

namespace ALWorkbench.Gateway.Core.PowerShell;

/// <summary>One child process to run.</summary>
/// <param name="FileName">The program, e.g. <c>powershell.exe</c>.</param>
/// <param name="Arguments">Each argument as it should arrive; never joined into one command line by us.</param>
/// <param name="StandardInput">Written to the child's standard input, which is then closed. Null closes it at once.</param>
/// <param name="Timeout">How long the child may run before it and every process it started are killed.</param>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? StandardInput, TimeSpan Timeout)
{
    /// <summary>Called with each line of standard output as it arrives, for live progress.</summary>
    public Action<string>? OnOutputLine { get; init; }
}

/// <summary>How a child process ended.</summary>
/// <param name="ExitCode">Its exit code; -1 when it was killed.</param>
/// <param name="StandardOutput">Everything it wrote to standard output, up to <see cref="SystemProcessRunner.MaxOutputChars"/>.</param>
/// <param name="StandardError">Everything it wrote to standard error, up to the same limit.</param>
/// <param name="TimedOut">True when it ran past its timeout and was killed.</param>
public sealed record ProcessOutcome(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Starts a child process. The seam the PowerShell runner goes through, so what it
/// would start (program, arguments, standard input) is tested on Linux without
/// Business Central or Windows.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the process to its end or its timeout. Cancelling <paramref name="ct"/>
    /// kills the process and everything it started, then throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>
/// Ties a started child to the gateway so that whatever it starts in turn dies with
/// it. On Windows a process tree kill only reaches the children Windows still lists
/// under the process, and a grandchild whose parent already exited is no longer
/// listed; the Windows host puts each child in a job object that kills everything in
/// it when closed (<c>ALWorkbench.Gateway/JobObjectContainment</c>). Disposing the
/// returned object ends everything still running in it.
/// </summary>
public interface IProcessContainment
{
    IDisposable Contain(Process process);
}

/// <summary>
/// The real <see cref="IProcessRunner"/>. Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so the runtime quotes each one for the
/// platform; no shell is involved. A timeout or a cancellation kills the whole process
/// tree, because a PowerShell child may itself have started a service-control or
/// remoting process that must not outlive the command.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private readonly IProcessContainment? _containment;

    /// <param name="containment">On Windows, the job object each child goes into; null elsewhere, where the tree kill is used alone.</param>
    public SystemProcessRunner(IProcessContainment? containment = null)
    {
        _containment = containment;
    }

    /// <summary>Output beyond this many characters per stream is dropped, so a runaway child cannot exhaust the service's memory.</summary>
    public const int MaxOutputChars = 8 * 1024 * 1024;

    public async Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var start = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        // Disposed when this method returns, so anything the child left running when
        // it ended, or when it was killed, ends with it. A service the child started
        // through Windows' service manager is not its child and keeps running.
        IDisposable? containment;
        try
        {
            containment = _containment?.Contain(process);
        }
        catch
        {
            Kill(process);
            throw;
        }
        using var contained = containment;

        var stdout = ReadAsync(process.StandardOutput, spec.OnOutputLine);
        var stderr = ReadAsync(process.StandardError, null);
        var stdin = WriteInputAsync(process, spec.StandardInput);

        using var timeout = new CancellationTokenSource(spec.Timeout);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                await Settle(stdout, stderr, stdin).ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            }
            timedOut = true;
        }
        await Settle(stdout, stderr, stdin).ConfigureAwait(false);
        return new ProcessOutcome(
            timedOut ? -1 : process.ExitCode,
            stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
            stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty,
            timedOut);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>Waits briefly for the pipes to drain; a grandchild that kept a pipe open must not hang the gateway.</summary>
    private static async Task Settle(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // What arrived is kept; the rest is lost with the killed process.
        }
    }

    private static async Task WriteInputAsync(Process process, string? input)
    {
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child ended (or was killed) before reading all of it.
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? onLine)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine?.Invoke(line);
            if (text.Length + line.Length + 1 <= MaxOutputChars) text.Append(line).Append('\n');
        }
        return text.ToString();
    }
}
