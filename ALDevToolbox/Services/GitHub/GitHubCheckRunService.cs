using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Reports a pull-request build back to GitHub as a check run (issue #627).
///
/// <para>Two calls, one lifecycle: the worker opens a run <c>in_progress</c>
/// before it queues the build, so the pull request shows something the moment
/// GitHub told us about it, and completes it once the build is done. Both act on
/// the installation token, because a check run is written by an <em>app</em> and
/// this build has no user behind it at all - see the credential split in
/// <c>.design/github-integration.md</c>.</para>
///
/// <para>Every method here is best-effort by design. A check run the workbench
/// could not write is a missing tick on a pull request; a build that fell over
/// because GitHub was unreachable would be a missing answer, which is worse. So
/// refusals are logged and swallowed, and <see cref="OpenAsync"/> answers null
/// rather than throwing - a build with no check run still builds, still ingests,
/// and is still visible in the workbench.</para>
/// </summary>
public sealed class GitHubCheckRunService
{
    /// <summary>
    /// How the run is named on the pull request. The prefix says who is speaking
    /// - a repository may be tracked by more than one solution, and each gets its
    /// own run, so the solution's name is what tells them apart.
    ///
    /// <para>This string is load-bearing outside the codebase: GitHub matches a
    /// required status check by name. An organisation that typed the old
    /// "AL Dev Toolbox / {solution}" into Repository standards -> Required status
    /// checks has to retype it, or its ruleset waits for a check that no longer
    /// reports. Renaming it again carries the same cost.</para>
    public static string CheckRunName(string solutionName) => $"AL Workbench / {solutionName}";

    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly PublicOrigin _origin;
    private readonly ILogger<GitHubCheckRunService> _logger;

    public GitHubCheckRunService(
        AppDbContext db,
        GitHubAppClient github,
        PublicOrigin origin,
        ILogger<GitHubCheckRunService> logger)
    {
        _db = db;
        _github = github;
        _origin = origin;
        _logger = logger;
    }

    /// <summary>
    /// Opens an <c>in_progress</c> check run for <paramref name="solutionName"/>
    /// on <paramref name="headSha"/>, returning its id or null when GitHub refused.
    /// The commonest refusal by far is the organisation not having granted the
    /// app permission to report build results, which is worth one warning per
    /// attempt and nothing more.
    /// </summary>
    public async Task<long?> OpenAsync(
        long installationId,
        string repositoryFullName,
        string solutionName,
        string headSha,
        int? projectId,
        CancellationToken ct = default)
    {
        var parts = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        try
        {
            var token = await _github.GetInstallationTokenAsync(installationId, ct);
            return await _github.CreateCheckRunAsync(
                token, parts[0], parts[1],
                name: CheckRunName(solutionName),
                headSha: headSha,
                status: "in_progress",
                detailsUrl: DetailsUrl(projectId),
                externalId: projectId?.ToString(),
                ct: ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Could not open a check run on {Repository} at {HeadSha}; the build runs anyway.",
                repositoryFullName, headSha);
            return null;
        }
    }

    /// <summary>
    /// Completes an open check run as <c>neutral</c> when the build behind it never
    /// started, or never finished because the workbench restarted under it (#1121),
    /// so the pull request is not left with a tick spinning forever.
    ///
    /// <para><c>neutral</c> rather than <c>failure</c> for the same reason as
    /// everywhere else here: nothing was learned about the code. Best-effort, like
    /// the rest of this service - if GitHub refuses this too, the log is where the
    /// reason lives.</para>
    /// </summary>
    public async Task AbandonAsync(
        long installationId,
        string repositoryFullName,
        long checkRunId,
        string reason,
        CancellationToken ct = default,
        string title = "The build could not be started")
    {
        var parts = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;

        try
        {
            var token = await _github.GetInstallationTokenAsync(installationId, ct);
            await _github.UpdateCheckRunAsync(
                token, parts[0], parts[1], checkRunId,
                status: "completed",
                conclusion: GitHubCheckConclusion.Neutral,
                title: title,
                summary: reason,
                ct: ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Could not close the abandoned check run {CheckRunId} on {Repository}.",
                checkRunId, repositoryFullName);
        }
    }

    /// <summary>
    /// Completes the check run the build carries, reading the build's own state and
    /// its compiler diagnostics to decide what to say.
    ///
    /// <para>The three conclusions mean three different things, and the
    /// distinction is deliberately simple:</para>
    /// <list type="bullet">
    /// <item><description><c>success</c> - the build reached <c>ready</c> and the
    /// compiler reported no errors.</description></item>
    /// <item><description><c>failure</c> - the compiler reported at least one
    /// error, so the pull request does not build.</description></item>
    /// <item><description><c>neutral</c> - the build could not run at all (no
    /// symbols for the declared application version, the compiler unavailable, the
    /// commit gone). Nothing was learned about the code, so a red X would be a
    /// claim we cannot support; the summary says what stopped it.</description></item>
    /// </list>
    /// </summary>
    /// <param name="forkAuthor">
    /// When the head being compiled lives in a member's own fork rather than in
    /// the repository itself, that member's GitHub login. The summary says so, so
    /// a reviewer reading the check run knows where the code came from without
    /// having to notice the branch label. Null for an ordinary pull request.
    /// </param>
    public async Task CompleteAsync(
        long installationId,
        string repositoryFullName,
        int releaseId,
        string? forkAuthor = null,
        CancellationToken ct = default)
    {
        var parts = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return;

        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => new { b.Id, b.ProjectId, b.Status, b.FailureMessage, b.CheckRunId, b.BcVersion })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (build?.CheckRunId is not long checkRunId) return;

        var allDiagnostics = await _db.OeProjectBuildDiagnostics.AsNoTracking()
            .Where(d => d.ProjectBuildId == build.Id)
            .OrderBy(d => d.Ordering)
            .ToListAsync(ct).ConfigureAwait(false);
        var results = await _db.OeProjectBuildResults.AsNoTracking()
            .Where(r => r.ReleaseId == releaseId)
            .Select(r => new { r.AppName, r.Status, r.Message })
            .ToListAsync(ct).ConfigureAwait(false);

        // A solution can list several repositories, and a pull request is about
        // exactly one of them. Annotating another repository's file would put a
        // marker on a line the reviewer's pull request does not contain (GitHub
        // drops it silently), and failing the run for an error the pull request
        // did not introduce would block a change that is fine. So the run is
        // decided on this repository's diagnostics; the rest are counted in the
        // summary so nothing disappears.
        var repositoryIds = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.ProjectId == build.ProjectId)
            .Select(r => new { r.Id, r.Url })
            .ToListAsync(ct).ConfigureAwait(false);
        var wanted = GitHubPullRequestBuildWorker.NormaliseRepositoryUrl($"https://github.com/{repositoryFullName}");
        var underReview = repositoryIds
            .Where(r => GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(r.Url) == wanted)
            .Select(r => r.Id)
            .ToHashSet();

        // A diagnostic with no repository is one the build could not attribute
        // (an inner-loop failure before any clone). It rides with the repository
        // under review rather than being hidden.
        var diagnostics = underReview.Count == 0
            ? allDiagnostics
            : allDiagnostics
                .Where(d => d.ProjectRepositoryId is null || underReview.Contains(d.ProjectRepositoryId.Value))
                .ToList();
        var elsewhereErrors = allDiagnostics.Count(d => d.Severity == ProjectBuildDiagnosticSeverity.Error)
            - diagnostics.Count(d => d.Severity == ProjectBuildDiagnosticSeverity.Error);

        var errors = diagnostics.Count(d => d.Severity == ProjectBuildDiagnosticSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == ProjectBuildDiagnosticSeverity.Warning);
        var compiled = results.Count(r => r.Status is ProjectBuildResultStatus.Compiled or ProjectBuildResultStatus.Ingested);

        string conclusion, title;
        if (errors > 0)
        {
            conclusion = GitHubCheckConclusion.Failure;
            title = errors == 1 ? "1 compile error" : $"{errors} compile errors";
        }
        else if (build.Status == ProjectBuildStatus.Ready)
        {
            conclusion = GitHubCheckConclusion.Success;
            title = compiled == 1 ? "1 extension compiled" : $"{compiled} extensions compiled";
        }
        else
        {
            conclusion = GitHubCheckConclusion.Neutral;
            title = "The build could not run";
        }

        var annotated = diagnostics
            .Where(d => d.Path.Length > 0 && d.Line > 0)
            .ToList();
        var omitted = Math.Max(0, annotated.Count - MaxAnnotations);

        var summary = BuildSummary(
            conclusion, build.FailureMessage, build.BcVersion, errors, warnings, elsewhereErrors, omitted,
            results.Select(r => (r.AppName, r.Status, r.Message)).ToList(), forkAuthor);

        var annotations = annotated
            .Take(MaxAnnotations)
            .Select(d => new GitHubCheckAnnotation(
                Path: d.Path,
                StartLine: d.Line,
                EndLine: d.Line,
                Level: d.Severity switch
                {
                    ProjectBuildDiagnosticSeverity.Error => GitHubCheckAnnotationLevel.Failure,
                    ProjectBuildDiagnosticSeverity.Warning => GitHubCheckAnnotationLevel.Warning,
                    _ => GitHubCheckAnnotationLevel.Notice,
                },
                Message: d.Message,
                Title: d.Code.Length > 0 ? d.Code : null))
            .ToList();

        try
        {
            var token = await _github.GetInstallationTokenAsync(installationId, ct);
            await _github.UpdateCheckRunAsync(
                token, parts[0], parts[1], checkRunId,
                status: "completed",
                conclusion: conclusion,
                title: title,
                summary: summary,
                annotations: annotations,
                ct: ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Could not complete check run {CheckRunId} on {Repository}; the build itself is recorded in the workbench.",
                checkRunId, repositoryFullName);
        }
    }

    /// <summary>
    /// The Markdown body of the check run: what happened, then per-extension
    /// outcomes. Deliberately short - the annotations carry the detail, and the
    /// summary is what a reviewer reads first.
    /// </summary>
    private static string BuildSummary(
        string conclusion,
        string? failureMessage,
        string? bcVersion,
        int errors,
        int warnings,
        int errorsElsewhere,
        int omittedAnnotations,
        IReadOnlyList<(string AppName, string Status, string? Message)> results,
        string? forkAuthor)
    {
        var lines = new List<string>();
        lines.Add(conclusion switch
        {
            GitHubCheckConclusion.Success => "Everything compiled.",
            GitHubCheckConclusion.Failure => "The compiler reported errors. Each one is marked on its own line in the Files tab.",
            _ => failureMessage ?? "The build could not run, so nothing was compiled.",
        });

        // Where the code came from, when that is not "a branch of this
        // repository". A member's fork is built, a stranger's is not, and the
        // reviewer is the person who should be told which of the two this was.
        if (forkAuthor is { Length: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"Built from {forkAuthor}'s fork.");
        }

        if (bcVersion is { Length: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"Compiled against Business Central {bcVersion}.");
        }

        if (errors > 0 || warnings > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{Count(errors, "error")} and {Count(warnings, "warning")}.");
        }

        if (errorsElsewhere > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{Count(errorsElsewhere, "error")} in other repositories of this solution. "
                + "They are not this pull request's, so they do not fail this check.");
        }

        if (omittedAnnotations > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Only the first {MaxAnnotations} are marked in the Files tab; "
                + $"{omittedAnnotations} more are in the build report in the workbench.");
        }

        if (results.Count > 0)
        {
            lines.Add(string.Empty);
            foreach (var r in results.OrderBy(r => r.AppName, StringComparer.OrdinalIgnoreCase))
            {
                var mark = r.Status is ProjectBuildResultStatus.Compiled or ProjectBuildResultStatus.Ingested ? "OK" : "Failed";
                lines.Add($"- {r.AppName}: {mark}{(r.Message is { Length: > 0 } m ? $" - {m}" : string.Empty)}");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// How many inline markers one check run carries at most: four of GitHub's
    /// batches of fifty. Past that the Files tab is unreadable anyway, and every
    /// batch is another call on the organisation's rate limit - the summary says
    /// how many were left out and where to read them.
    /// </summary>
    private const int MaxAnnotations = 200;

    /// <summary>"3 errors" / "1 warning". A person reads this on the pull request, so it reads like a sentence.</summary>
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>
    /// Where "Details" on the check run goes: the solution in the workbench, built
    /// from the configured public origin. Null when the deployment has not been
    /// told its own address - a link to <c>localhost</c> would be worse than no
    /// link, and GitHub simply renders the run without one.
    /// </summary>
    private string? DetailsUrl(int? projectId) =>
        _origin.IsConfigured && projectId is int id ? $"{_origin.Configured}/solutions/{id}" : null;
}
