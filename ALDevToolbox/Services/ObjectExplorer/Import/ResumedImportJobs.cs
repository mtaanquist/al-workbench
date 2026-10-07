namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Puts the import jobs that survived a restart back on <see cref="ReleaseImportQueue"/>
/// once the host has started (#1107).
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
        IReadOnlyList<ReleaseImportJob> jobs,
        ILogger logger)
    {
        if (jobs.Count == 0) return;
        lifetime.ApplicationStarted.Register(() =>
            _ = EnqueueAllAsync(queue, jobs, logger, lifetime.ApplicationStopping));
    }

    /// <summary>Writes <paramref name="jobs"/> in order, waiting whenever the queue is full. Never throws.</summary>
    internal static async Task EnqueueAllAsync(
        ReleaseImportQueue queue,
        IReadOnlyList<ReleaseImportJob> jobs,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            foreach (var job in jobs)
            {
                await queue.EnqueueAsync(job, ct).ConfigureAwait(false);
            }
            logger.LogInformation("Resumed {Count} release import(s) and build(s) after restart.", jobs.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the rows are still queued and the next start resumes them.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resuming {Count} release import(s) and build(s) after restart failed.", jobs.Count);
        }
    }
}
