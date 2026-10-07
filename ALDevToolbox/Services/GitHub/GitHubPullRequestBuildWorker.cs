using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Turns a verified <c>pull_request</c> delivery into builds: works out which
/// organisation the installation belongs to, which of its solutions track the
/// repository, opens a check run per solution and queues a build behind each.
///
/// <para><strong>The organisation is found by looking, not by querying across
/// tenants.</strong> An installation id maps to exactly one organisation's
/// settings row, and a single cross-org read would answer it in one query - but
/// that read would be an <c>IgnoreQueryFilters()</c> call site inside code an
/// anonymous inbound request reaches, which is precisely the fence CLAUDE.md
/// asks about first. So this walks <c>organizations</c> (a table with no tenant
/// filter), enters an <see cref="AmbientOrganizationScope"/> per organisation and
/// asks each one, under its own filter, what installation it connected - the same
/// shape <c>EnvironmentRefreshScheduler</c> uses. It stops at the first match, and
/// deployments have tens of organisations rather than thousands.</para>
///
/// <para>The actual clone, compile and ingest is the ordinary project build,
/// entered through <see cref="ProjectBuildImporter.StartPullRequestBuildAsync"/>
/// and run by a <see cref="ObjectExplorer.Import.ProjectBuildWorker"/>. This worker's whole job is the
/// routing. See <c>.design/github-integration-phase2.md</c> (#627).</para>
///
/// <para>The same drain also records <c>push</c> deliveries and merged pull
/// requests (#963) through <see cref="GitHubBranchActivityService"/>, under the
/// same per-organisation resolution. They are what a pipeline's freshness is
/// compared against, and a push also builds the pipelines that build on push
/// (#1079, <see cref="PushBuildService"/>).</para>
/// </summary>
public sealed class GitHubPullRequestBuildWorker : QueueDrainWorker<GitHubWebhookJob>
{
    private readonly GitHubWebhookQueue _queue;
    private readonly IServiceProvider _services;
    private readonly MaintenanceModeState _maintenance;
    private readonly ILogger<GitHubPullRequestBuildWorker> _logger;

    /// <summary>
    /// How long a delivery waits before being offered again while a restore is in
    /// flight. Settable for tests, which cannot afford to wait half a minute.
    /// </summary>
    internal TimeSpan MaintenanceRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public GitHubPullRequestBuildWorker(
        GitHubWebhookQueue queue,
        IServiceProvider services,
        MaintenanceModeState maintenance,
        ILogger<GitHubPullRequestBuildWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // Routing only - the build itself is the release-import worker's active
        // duration, not this one's. Five minutes is generous for "ask a few orgs
        // what they connected, then open a check run".
        : base(queue.Reader, logger, heartbeats, nameof(GitHubPullRequestBuildWorker), TimeSpan.FromMinutes(5))
    {
        _queue = queue;
        _services = services;
        _maintenance = maintenance;
        _logger = logger;
    }

    /// <summary>Runs one job as the drain loop would. Test seam.</summary>
    internal Task RunOneAsync(GitHubWebhookJob job, CancellationToken ct) => RunJobAsync(job, ct);

    protected override string Describe(GitHubWebhookJob job) => job switch
    {
        GitHubPullRequestJob pr => $"{pr.RepositoryFullName}#{pr.PullRequestNumber}@{pr.HeadSha}",
        GitHubPushJob push => $"{push.RepositoryFullName}:{push.Branch}@{push.HeadSha}",
        GitHubMergedPullRequestJob merged => $"{merged.RepositoryFullName}#{merged.Number} merged",
        _ => job.RepositoryFullName,
    };

    protected override async Task RunJobAsync(GitHubWebhookJob job, CancellationToken ct)
    {
        // A restore is rewriting the database underneath us. The delivery was
        // accepted (the webhook route stays open through maintenance on purpose,
        // because GitHub disables a hook whose deliveries keep failing), but the
        // build behind it reads and writes tables that are being replaced. So the
        // job goes back on the queue rather than into the database.
        if (_maintenance.IsActive)
        {
            _logger.LogInformation(
                "Holding a webhook delivery for {Job}: maintenance mode is active ({Reason}).",
                Describe(job), _maintenance.Reason);
            await Task.Delay(MaintenanceRetryDelay, ct).ConfigureAwait(false);
            if (!_queue.TryEnqueue(job))
            {
                _logger.LogWarning(
                    "Dropped a webhook delivery for {Job}: the queue was full while maintenance mode was active.",
                    Describe(job));
            }
            return;
        }

        if (job is not GitHubPullRequestJob pullRequest)
        {
            await RecordBranchActivityAsync(job, ct).ConfigureAwait(false);
            return;
        }

        await BuildPullRequestAsync(pullRequest, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a push or a merged pull request for the organisation that connected
    /// the installation (#963), then builds the pipelines that build on push
    /// (#1079). A repository no solution in that
    /// organisation tracks is dropped at Debug - pushes are frequent, and most
    /// repositories in a GitHub organisation are not a solution's.
    /// </summary>
    private async Task RecordBranchActivityAsync(GitHubWebhookJob job, CancellationToken ct)
    {
        var resolved = await ResolveOrganizationAsync(job.InstallationId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            _logger.LogDebug(
                "Dropped a delivery for {Repository}: no organisation on this deployment has connected installation {InstallationId}.",
                job.RepositoryFullName, job.InstallationId);
            return;
        }

        var identity = resolved.Value.Identity;
        using var orgScope = AmbientOrganizationScope.Enter(identity);
        await using var scope = _services.CreateAsyncScope();
        var activity = scope.ServiceProvider.GetRequiredService<GitHubBranchActivityService>();

        var matched = job switch
        {
            GitHubPushJob push => await activity.RecordPushAsync(push, ct).ConfigureAwait(false),
            GitHubMergedPullRequestJob merged => await activity.RecordMergedPullRequestAsync(merged, ct).ConfigureAwait(false),
            _ => 0,
        };
        if (matched == 0)
        {
            _logger.LogDebug(
                "Dropped a delivery for {Repository}: no solution in organisation {OrganizationId} tracks it.",
                job.RepositoryFullName, identity.OrganizationId);
            return;
        }

        if (job is GitHubPushJob buildable && PushBuildService.IsBuildable(buildable))
        {
            await StartPushBuildsAsync(buildable, identity, scope, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queues a build of every pipeline that builds on push and watches the pushed
    /// branch (#1079), each as the person who turned that on, the way
    /// <c>PreviewCheckScheduler</c> starts a nightly check as its person. A refusal
    /// pauses building on push for that pipeline with the reason on the page; a
    /// started build lifts an earlier pause. One pipeline's trouble is not the others'.
    /// </summary>
    private async Task StartPushBuildsAsync(
        GitHubPushJob push, AmbientOrganizationScope.OrganizationIdentity identity, AsyncServiceScope orgScope, CancellationToken ct)
    {
        var pushBuilds = orgScope.ServiceProvider.GetRequiredService<PushBuildService>();
        foreach (var due in await pushBuilds.ListDueAsync(push, ct).ConfigureAwait(false))
        {
            var (settled, blocked) = (true, due.Blocked);
            if (due.UserId is { } userId)
            {
                (settled, blocked) = await StartPushBuildAsync(push, identity, due, userId, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "Did not build pipeline {PipelineId} for {Job}: {Reason}", due.PipelineId, Describe(push), due.Blocked);
            }
            if (settled)
            {
                await pushBuilds.SetBlockedAsync(due.PipelineId, blocked, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Starts one build on push as <paramref name="userId"/>. Returns whether the
    /// attempt settles the pipeline's pause, and the pause to show: null when it
    /// queued, the reason when it was refused. An unexpected failure settles nothing.
    /// </summary>
    private async Task<(bool Settled, string? Blocked)> StartPushBuildAsync(
        GitHubPushJob push, AmbientOrganizationScope.OrganizationIdentity identity, PushBuildDue due, int userId, CancellationToken ct)
    {
        var outcome = await AutomatedBuilds.StartAsAsync(
            _services, identity.OrganizationId, identity.IsSystemOrganization, userId,
            importer => importer.StartPushBuildAsync(due.PipelineId, due.RepositoryId, push.HeadSha, ct),
            "the build could not start.").ConfigureAwait(false);
        if (outcome.Error is { } error)
        {
            // Not something the pipeline can be told to fix: log it and leave any
            // pause as it is. The next push tries again.
            _logger.LogError(error, "Could not start a build on push of pipeline {PipelineId} for {Job}.", due.PipelineId, Describe(push));
            return (false, null);
        }
        if (outcome.Refusal is { } reason)
        {
            _logger.LogInformation(
                "Paused building on push for pipeline {PipelineId} ({Job}) as user {UserId}: {Reason}", due.PipelineId, Describe(push), userId, reason);
        }
        return (true, outcome.Refusal);
    }

    private async Task BuildPullRequestAsync(GitHubPullRequestJob job, CancellationToken ct)
    {
        // Superseded before we even reached it: a newer push to the same pull
        // request arrived while this one waited. Building it would spend a
        // compile on a commit no reviewer is looking at, and would then complete
        // the check run for the wrong head.
        if (!_queue.IsLatest(job.Key, job.HeadSha))
        {
            _logger.LogInformation(
                "Skipping a pull-request build for {Job}: a newer commit was pushed before it started.", Describe(job));
            return;
        }

        var resolved = await ResolveOrganizationAsync(job.InstallationId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            // Ordinary rather than alarming: a GitHub organisation can have the
            // app installed without any workbench organisation having connected it,
            // and a disconnected one keeps its webhook until somebody removes the
            // installation on GitHub.
            _logger.LogInformation(
                "Dropped a pull-request delivery for {Repository}: no organisation on this deployment has connected installation {InstallationId}.",
                job.RepositoryFullName, job.InstallationId);
            return;
        }

        var (identity, orgLogin) = resolved.Value;
        using var orgScope = AmbientOrganizationScope.Enter(identity);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A pull request from a member's own fork is built, and this is where
        // "member" stops being GitHub's word from the delivery and becomes an
        // answer we asked for ourselves. The delivery's author_association is
        // stamped when the pull request is opened and re-used by every later
        // push, so somebody who has left the organisation in the meantime still
        // arrives labelled MEMBER. Nothing is cloned until GitHub confirms the
        // membership now, on the installation token.
        if (job.IsMemberFork && !await AuthorIsStillAMemberAsync(job, orgLogin, scope, ct).ConfigureAwait(false))
        {
            // Dropped before any check run is opened: there is nothing to leave
            // spinning, and a pull request whose author we cannot vouch for is
            // one the workbench says nothing about at all.
            return;
        }

        // Which solutions track this repository, under the organisation's own
        // query filter. Matching is on the normalised clone URL, because the same
        // repository is entered by hand with and without the .git suffix and with
        // either case.
        var matches = (await GitHubRepositoryMatch.TrackingRepositoriesAsync(db, job.CloneUrl, ct).ConfigureAwait(false))
            // One solution, one build, even when it lists the repository twice.
            .GroupBy(r => r.ProjectId)
            .Select(g => g.First())
            .ToList();

        if (matches.Count == 0)
        {
            _logger.LogInformation(
                "Dropped a pull-request delivery for {Repository}: no solution in organisation {OrganizationId} tracks it.",
                job.RepositoryFullName, identity.OrganizationId);
            return;
        }

        var checks = scope.ServiceProvider.GetRequiredService<GitHubCheckRunService>();
        var importer = scope.ServiceProvider.GetRequiredService<ProjectBuildImporter>();

        foreach (var match in matches)
        {
            long? checkRunId = null;
            try
            {
                checkRunId = await checks.OpenAsync(
                    job.InstallationId, job.RepositoryFullName, match.ProjectName, job.HeadSha, match.ProjectId, ct)
                    .ConfigureAwait(false);

                await importer.StartPullRequestBuildAsync(
                    projectId: match.ProjectId,
                    repositoryId: match.RepositoryId,
                    repositoryFullName: job.RepositoryFullName,
                    installationId: job.InstallationId,
                    headSha: job.HeadSha,
                    headRef: job.HeadRef,
                    pullRequestNumber: job.PullRequestNumber,
                    checkRunId: checkRunId,
                    forkAuthor: job.IsMemberFork ? job.AuthorLogin : null,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One solution's failure is not the others'. The pull request
                // simply gets one fewer answer, and the reason is here.
                _logger.LogError(ex,
                    "Could not start a pull-request build of solution {ProjectId} for {Job}.", match.ProjectId, Describe(job));

                // The run was opened before the build was queued, so a failure
                // between the two would leave it spinning on the pull request
                // until somebody pushed again. Close it instead, saying why.
                if (checkRunId is long openRun)
                {
                    await checks.AbandonAsync(
                        job.InstallationId, job.RepositoryFullName, openRun,
                        "The workbench could not start this build. Push again, or look at the solution in the workbench.",
                        ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Whether GitHub still calls <paramref name="job"/>'s author a member of
    /// <paramref name="orgLogin"/>, asked on the installation token.
    ///
    /// <para><strong>An answer we could not get is a refusal.</strong> GitHub
    /// being unreachable, the app being unconfigured, the organisation login not
    /// recorded - none of those are a yes, and the cost of guessing wrong is a
    /// stranger's code compiled on the customer's own installation. So every
    /// unhappy path here returns false, and the reason is in the log.</para>
    /// </summary>
    private async Task<bool> AuthorIsStillAMemberAsync(
        GitHubPullRequestJob job, string orgLogin, AsyncServiceScope scope, CancellationToken ct)
    {
        if (orgLogin.Length == 0 || job.AuthorLogin.Length == 0)
        {
            _logger.LogInformation(
                "Dropped a fork pull-request delivery for {Job}: there is no organisation login or author to check membership for.",
                Describe(job));
            return false;
        }

        try
        {
            var github = scope.ServiceProvider.GetRequiredService<GitHubAppClient>();
            var token = await github.GetInstallationTokenAsync(job.InstallationId, ct).ConfigureAwait(false);
            if (await github.InstallationSeesOrgMemberAsync(token, orgLogin, job.AuthorLogin, ct).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Building {Job} from the author's own fork: GitHub confirms {Author} is a member of {Org}.",
                    Describe(job), job.AuthorLogin, orgLogin);
                return true;
            }

            _logger.LogInformation(
                "Dropped a fork pull-request delivery for {Job}: GitHub does not report {Author} as a member of {Org}.",
                Describe(job), job.AuthorLogin, orgLogin);
            return false;
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
        {
            _logger.LogWarning(ex,
                "Dropped a fork pull-request delivery for {Job}: could not ask GitHub whether {Author} is a member of {Org}.",
                Describe(job), job.AuthorLogin, orgLogin);
            return false;
        }
    }

    /// <summary>
    /// The organisation that connected <paramref name="installationId"/>, or null
    /// when none did. Internal so a test can drive the routing without the hosted
    /// loop.
    ///
    /// <para>Each organisation is asked under its own ambient scope and its own DI
    /// scope, so <see cref="GitHubConnectionService"/> reads that organisation's
    /// settings through the ordinary tenant filter - there is no
    /// <c>IgnoreQueryFilters()</c> anywhere in this path.</para>
    /// </summary>
    internal async Task<(AmbientOrganizationScope.OrganizationIdentity Identity, string OrgLogin)?>
        ResolveOrganizationAsync(long installationId, CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one cross-org read the schedulers already make: which
            // organisations exist. organizations carries no tenant filter, so this
            // needs no bypass.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        foreach (var (orgId, isSystem) in orgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem);
                using var ambient = AmbientOrganizationScope.Enter(identity);
                await using var scope = _services.CreateAsyncScope();
                var connection = scope.ServiceProvider.GetRequiredService<GitHubConnectionService>();
                var status = await connection.GetStatusAsync(ct).ConfigureAwait(false);
                if (status.InstallationId == installationId)
                {
                    return (identity, status.OrgLogin ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read organisation {OrgId}'s GitHub connection while routing a webhook delivery.", orgId);
            }
        }
        return null;
    }

    /// <summary>
    /// A repository URL reduced to what identifies it: lower-cased host and path,
    /// no scheme, no credentials, no <c>.git</c> suffix, no trailing slash. Two
    /// spellings of the same repository - what an admin typed into a solution and
    /// what GitHub sent us - have to compare equal, or a tracked repository looks
    /// untracked.
    /// </summary>
    internal static string NormaliseRepositoryUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            trimmed = uri.Host + uri.AbsolutePath;
        }
        else
        {
            // A scp-style remote (git@github.com:cronus/app.git) is not a URI.
            var at = trimmed.IndexOf('@');
            if (at >= 0) trimmed = trimmed[(at + 1)..];
            trimmed = trimmed.Replace(':', '/');
        }

        trimmed = trimmed.TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed.ToLowerInvariant();
    }
}
