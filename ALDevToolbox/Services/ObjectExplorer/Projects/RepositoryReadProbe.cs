using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Whether the acting person can read a repository with their own access, asked
/// of git itself with a <c>git ls-remote</c>: their connected GitHub account and
/// their stored build token, in the order <see cref="CloneCredentialResolver"/>
/// gives them, and last with no credential at all, so a public repository passes
/// for someone with nothing set up. The cheapest read a host serves, and the same
/// one a clone starts with, so a yes here is a yes from the clone.
///
/// <para>Used where the GitHub App cannot answer: a repository outside the
/// connected GitHub organisation, any Azure DevOps repository, and every
/// repository when no GitHub organisation is connected. See
/// <c>ProjectService.ReadAccessErrorAsync</c> and
/// <c>.design/object-explorer-project-builds.md</c>, "Confirmed repositories".</para>
/// </summary>
public sealed class RepositoryReadProbe
{
    /// <summary>A probe that takes longer than this is a no: a host that slow would fail the build too.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CloneCredentialResolver _credentials;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<RepositoryReadProbe> _logger;

    public RepositoryReadProbe(
        CloneCredentialResolver credentials,
        IProcessRunner processRunner,
        ILogger<RepositoryReadProbe> logger)
    {
        _credentials = credentials;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>What <see cref="CanReadAsync"/> found.</summary>
    /// <param name="CanRead">True when one of the attempts listed the repository.</param>
    /// <param name="HadCredential">Whether the person had anything of their own to try, which decides what to tell them on a no.</param>
    public sealed record Outcome(bool CanRead, bool HadCredential);

    /// <summary>
    /// Lists <paramref name="url"/> with each of the acting person's credentials for
    /// <paramref name="provider"/>, then anonymously. Never throws for a refusal or a
    /// timeout; those are a no. The URL is one the caller has already validated as an
    /// https address on the provider's host.
    /// </summary>
    public async Task<Outcome> CanReadAsync(RepositoryProvider provider, string url, CancellationToken ct = default)
    {
        var gitPath = Environment.GetEnvironmentVariable("GIT_PATH") is { Length: > 0 } configured ? configured : "git";
        var credentials = await _credentials.ResolveAsync(provider, ct).ConfigureAwait(false);
        var attempts = credentials
            .Select(c => (Source: c.Source, Env: (IReadOnlyDictionary<string, string>)ProjectBuildService.GitAuthEnv(provider, c.Secret)))
            .Append(("no credential", AnonymousEnv()));
        foreach (var (source, env) in attempts)
        {
            ct.ThrowIfCancellationRequested();
            ProcessRunResult result;
            try
            {
                result = await _processRunner.RunAsync(new ProcessRunRequest(
                    gitPath, ["ls-remote", url, "HEAD"], null, env, Timeout), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not run git to check read access to {Url}.", url);
                return new Outcome(false, credentials.Count > 0);
            }
            if (result.Succeeded)
            {
                _logger.LogInformation("Read access to {Url} confirmed using {Source}.", url, source);
                return new Outcome(true, credentials.Count > 0);
            }
        }
        _logger.LogInformation("Read access to {Url} could not be confirmed with {CredentialCount} credential(s).", url, credentials.Count);
        return new Outcome(false, credentials.Count > 0);
    }

    /// <summary>The same no-prompt, no-helper environment a build's git runs in, with nothing to authenticate with.</summary>
    private static Dictionary<string, string> AnonymousEnv() => new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_CONFIG_COUNT"] = "1",
        ["GIT_CONFIG_KEY_0"] = "credential.helper",
        ["GIT_CONFIG_VALUE_0"] = "",
    };
}
