using ALDevToolbox.Services.ObjectExplorer;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// What <c>git ls-remote</c> answers when a repository's read access is checked
/// (<see cref="ALDevToolbox.Services.ObjectExplorer.Projects.RepositoryReadProbe"/>).
/// Readable by default, so a test that is not about access is not refused; a test
/// that is sets <see cref="Readable"/> to false. Every call is recorded, with its
/// environment, so a test can tell which credentials were tried.
/// </summary>
public sealed class FakeGitRemote : IProcessRunner
{
    /// <summary>Whether every ls-remote succeeds.</summary>
    public bool Readable { get; set; } = true;

    /// <summary>When set, decides per call instead of <see cref="Readable"/>: true for a success.</summary>
    public Func<ProcessRunRequest, bool>? Answer { get; set; }

    public List<ProcessRunRequest> Calls { get; } = new();

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add(request);
        var ok = Answer?.Invoke(request) ?? Readable;
        return Task.FromResult(ok
            ? new ProcessRunResult(0, "0123456789abcdef0123456789abcdef01234567\tHEAD\n", string.Empty)
            : new ProcessRunResult(128, string.Empty, "remote: Repository not found.\nfatal: repository not found"));
    }
}
