using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>The two kinds of build a pipeline starts on its own, as the person who last saved it.</summary>
public enum PipelineAutomation
{
    /// <summary>The nightly preview check (<see cref="PreviewCheckScheduler"/>).</summary>
    PreviewCheck,

    /// <summary>Building when new commits are pushed (#1079).</summary>
    BuildOnPush,
}

/// <summary>
/// What the nightly preview check and building on push share: starting a build as the
/// person a pipeline's automatic builds run as, and the pause the pipeline shows when one
/// could not start. The two callers decide what is due and what to log; how a start is
/// made, what a refusal means and where the pause is stored live here once. See
/// <c>.design/object-explorer-project-builds.md</c> and
/// <c>.design/github-integration-phase2.md</c>.
/// </summary>
public static class AutomatedBuilds
{
    /// <summary>The pause when the person the builds run as no longer has an active account.</summary>
    public const string NoOwnerMessage =
        "the person its builds run as no longer has an active account.";

    /// <summary>The pause when that person can no longer manage the solution.</summary>
    public const string NoAccessMessage =
        "the person its builds run as can no longer manage this solution.";

    /// <summary>
    /// Records why <paramref name="pipelineId"/>'s <paramref name="automation"/> could not
    /// start, or clears it with null. Writes only when the value changes.
    /// </summary>
    public static async Task SetBlockedAsync(
        AppDbContext db, int pipelineId, PipelineAutomation automation, string? reason, CancellationToken ct)
    {
        if (reason is { Length: > 500 }) reason = reason[..500];
        var pipelines = db.OePipelines.Where(p => p.Id == pipelineId);
        var update = automation == PipelineAutomation.PreviewCheck
            ? pipelines.Where(p => p.PreviewCheckBlocked != reason)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PreviewCheckBlocked, reason), ct)
            : pipelines.Where(p => p.BuildOnPushBlocked != reason)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.BuildOnPushBlocked, reason), ct);
        await update.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="start"/> in a scope of its own, signed in as
    /// <paramref name="userId"/> in <paramref name="organizationId"/>. A refusal the
    /// pipeline should show comes back as <see cref="AutomatedStart.Refusal"/>: the person
    /// can no longer manage the solution, or the build refused with a reason
    /// (<paramref name="fallbackReason"/> when it gave none). Anything else comes back as
    /// <see cref="AutomatedStart.Error"/> for the caller to log; it is not something the
    /// pipeline can be told to fix. Cancellation is not caught.
    /// </summary>
    public static async Task<AutomatedStart> StartAsAsync(
        IServiceProvider services, int organizationId, bool isSystemOrganization, int? userId,
        Func<ProjectBuildImporter, Task> start, string fallbackReason)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(organizationId, isSystemOrganization, userId));
        await using var scope = services.CreateAsyncScope();
        try
        {
            await start(scope.ServiceProvider.GetRequiredService<ProjectBuildImporter>()).ConfigureAwait(false);
            return new AutomatedStart(true, null, null);
        }
        catch (ProjectAccessDeniedException)
        {
            return new AutomatedStart(false, NoAccessMessage, null);
        }
        catch (PlanValidationException ex)
        {
            return new AutomatedStart(false, ex.Errors.Values.FirstOrDefault() ?? fallbackReason, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutomatedStart(false, null, ex);
        }
    }
}

/// <summary>How one automatic build start went: started, refused with a reason to show, or failed unexpectedly.</summary>
public sealed record AutomatedStart(bool Started, string? Refusal, Exception? Error);
