namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Puts the import jobs that survived a restart back on <see cref="ReleaseImportQueue"/>
/// once the host has started (#1107). Builds go straight back on
/// <see cref="ProjectBuildQueue"/>, which never makes a writer wait (#1137).
///
/// <para>
/// The startup reconcile runs before <c>app.Run()</c>, when no hosted service is
/// running yet, so nothing drains the bounded queue. Writing to it there blocks on the
/// 17th job for good and the app never starts listening. Waiting for
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> means the worker is reading
/// by the time the writes can fill the queue, and a write past the bound simply waits
/// its turn. The rows stay <c>queued</c> until then, so a shutdown before every job is
/// written loses nothing: the next start resumes them again.
/// </para>
/// </summary>
internal static class ResumedImportJobs
{
    public static void QueueWhenStarted(
        IHostApplicationLifetime lifetime,
        ReleaseImportQueue queue,
        ProjectBuildQueue builds,
        IReadOnlyList<ReleaseImportJob> jobs,
        ILogger logger)
    {
        var imports = new List<ReleaseImportJob>();
        var resumedBuilds = 0;
        foreach (var job in jobs)
        {
            if (job.Source is ReleaseImportSource.ProjectBuild or ReleaseImportSource.PullRequestBuild)
            {
                builds.Enqueue(job);
                resumedBuilds++;
            }
            else
            {
                imports.Add(job);
            }
        }
        if (resumedBuilds > 0) logger.LogInformation("Resumed {Count} build(s) after restart.", resumedBuilds);

        if (imports.Count == 0) return;
        lifetime.ApplicationStarted.Register(() =>
            _ = EnqueueAllAsync(queue, imports, logger, "resumed after restart", lifetime.ApplicationStopping));
    }

    /// <summary>
    /// Writes <paramref name="jobs"/> in order, waiting whenever the queue is full. Never
    /// throws. Also the feeder behind the bulk system-reference backfill (#1542), which
    /// must not hold a request while the queue drains. <paramref name="purpose"/> only
    /// labels the log lines.
    /// </summary>
    internal static async Task EnqueueAllAsync(
        ReleaseImportQueue queue,
        IReadOnlyList<ReleaseImportJob> jobs,
        ILogger logger,
        string purpose,
        CancellationToken ct)
    {
        try
        {
            foreach (var job in jobs)
            {
                await queue.EnqueueAsync(job, ct).ConfigureAwait(false);
            }
            logger.LogInformation("Queued {Count} release import(s), {Purpose}.", jobs.Count, purpose);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the rows are still queued and the next start's reconcile
            // picks them up (resumes an import, marks a backfill failed to re-trigger).
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queuing {Count} release import(s), {Purpose}, failed.", jobs.Count, purpose);
        }
    }
}
