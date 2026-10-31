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
        // The jobs were built from the database as it is now; a restore before they are
        // all fed replaces it, and the feed then drops what is left (#1538).
        var epoch = queue.Epoch;
        var imports = new List<ReleaseImportJob>();
        var backfills = new List<ReleaseImportJob>();
        var resumedBuilds = 0;
        foreach (var job in jobs)
        {
            if (job.Source is ReleaseImportSource.ProjectBuild or ReleaseImportSource.PullRequestBuild)
            {
                builds.Enqueue(job, epoch);
                resumedBuilds++;
            }
            else if (job.Source is ReleaseImportSource.Backfill)
            {
                // A bulk backfill cut off by the restart can be hundreds of
                // jobs; paced, so it does not crowd out the imports (#1542).
                backfills.Add(job);
            }
            else
            {
                imports.Add(job);
            }
        }
        if (resumedBuilds > 0) logger.LogInformation("Resumed {Count} build(s) after restart.", resumedBuilds);

        if (imports.Count > 0)
        {
            lifetime.ApplicationStarted.Register(() =>
                _ = EnqueueAllAsync(queue, imports, logger, "resumed after restart", lifetime.ApplicationStopping, epoch: epoch));
        }
        if (backfills.Count > 0)
        {
            lifetime.ApplicationStarted.Register(() =>
                _ = EnqueueAllAsync(queue, backfills, logger, "backfills resumed after restart", lifetime.ApplicationStopping, paced: true, epoch: epoch));
        }
    }

    /// <summary>
    /// Writes <paramref name="jobs"/> in order, waiting whenever the queue is full. Never
    /// throws. Also the feeder behind the bulk system-reference backfill (#1542), which
    /// must not hold a request while the queue drains. <paramref name="purpose"/> only
    /// labels the log lines. <paramref name="paced"/> writes through
    /// <see cref="ReleaseImportQueue.EnqueuePacedAsync"/>, so a long run never holds more
    /// than a slot or so of the queue and other imports get in promptly.
    /// <paramref name="epoch"/> is the restore epoch the jobs were built under
    /// (defaults to now): every job is stamped with it, and the feed stops once a
    /// restore has moved it on, since the rest belong to the database that was
    /// replaced (#1538).
    /// </summary>
    internal static async Task EnqueueAllAsync(
        ReleaseImportQueue queue,
        IReadOnlyList<ReleaseImportJob> jobs,
        ILogger logger,
        string purpose,
        CancellationToken ct,
        bool paced = false,
        long? epoch = null)
    {
        var builtUnder = epoch ?? queue.Epoch;
        try
        {
            foreach (var job in jobs)
            {
                if (queue.Epoch != builtUnder)
                {
                    logger.LogWarning(
                        "Stopped queuing release imports, {Purpose}: a restore replaced the database they were read from.",
                        purpose);
                    return;
                }
                if (paced) await queue.EnqueuePacedAsync(job, ct, builtUnder).ConfigureAwait(false);
                else await queue.EnqueueAsync(job, ct, builtUnder).ConfigureAwait(false);
            }
            logger.LogInformation("Queued {Count} release import(s), {Purpose}.", jobs.Count, purpose);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the rows are still queued and the next start's reconcile
            // resumes them.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queuing {Count} release import(s), {Purpose}, failed.", jobs.Count, purpose);
        }
    }
}
