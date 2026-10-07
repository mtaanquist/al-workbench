using System.ComponentModel;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools over SaaS delivery — the agent-facing parallel of the Deployments web tool.
/// A deployment pipeline (the <c>OeReleasePipeline</c> entity) is the reusable "where +
/// how" of a deploy (a build pipeline's artifacts → one Business Central environment); a
/// deployment (an <c>OeProjectDelivery</c> row) is one run of it. Agents can list
/// deployment pipelines, deploy a successful build now, and read a pipeline's deployment
/// history with per-app outcomes. Deploying runs asynchronously in the same in-process
/// worker the web "Deploy now" uses, so <c>deploy_build</c> returns a deployment id to
/// poll with <c>list_deployments</c> rather than blocking to completion.
/// Access-gating and validation come from <see cref="DeliveryService"/> itself (the
/// project owner / org Admin / an assigned team, via <c>ProjectAccess</c>); this class
/// only translates its exceptions into <see cref="McpException"/>. All reads are
/// org-scoped by the EF query filter and project-scoped by the same authority — a
/// Private project the caller has no grant on is absent from every list here and
/// unresolvable by id. Scheduling a future deployment stays a web-only surface for now,
/// and so does approving or dismissing a deployment the pipeline prepared (#934): agents
/// see it as "proposed" and nothing more. The tool names follow the product's words, not
/// the entity names; see <c>.design/saas-delivery.md</c> ("Vocabulary", "MCP parity").
/// </summary>
[McpServerToolType]
public sealed class DeliveryTools
{
    private readonly DeliveryService _deliveries;
    private readonly ReleasePipelineService _releasePipelines;
    private readonly GitHubReleaseService _githubReleases;
    private readonly ArtifactService _artifacts;

    public DeliveryTools(
        DeliveryService deliveries,
        ReleasePipelineService releasePipelines,
        GitHubReleaseService githubReleases,
        ArtifactService artifacts)
    {
        _deliveries = deliveries;
        _releasePipelines = releasePipelines;
        _githubReleases = githubReleases;
        _artifacts = artifacts;
    }

    [McpServerTool(Name = "list_deployment_pipelines", ReadOnly = true)]
    [Description("Lists the deployment pipelines you can see in the organisation — each is a named 'deploy this build pipeline's builds to this Business Central environment' target. Returns each pipeline's id, name, its owning solution (id and name), its source build pipeline (or, for a pipeline that installs a repository's GitHub releases, that repository), the target environment (name, Production/Sandbox type, and whether it is still present in Business Central), when installs run (its deployment schedule), schema sync mode, and whether a new successful build prepares a deployment for a person to approve (prepareDeploymentOnNewBuild; nothing installs until someone approves it in the web UI), and its branch rule: when restrictBranch is true it only deploys builds made from allowedBranch (null means the repositories' default branch). buildPipelineDeleted is true when its source build pipeline was deleted, so no new builds will reach it. disabled is true when someone disabled the deployment pipeline: deploy_build refuses it and nothing deploys through it until a person enables it again in the web UI. buildPipelineDisabled is true when its source build pipeline is disabled, so no new builds will reach it, though its earlier builds can still be deployed. Deploying refuses a build from another branch, and an app older than the version the environment already has. Pipelines under a private solution you are not on the team for are not listed. Use an id with deploy_build (to deploy a build) or list_deployments (to see its history).")]
    public async Task<IReadOnlyList<ReleasePipelineRow>> ListReleasePipelinesAsync(
        [Description("Optional solution id to list only that solution's deployment pipelines.")] int? solutionId = null,
        CancellationToken ct = default)
    {
        try
        {
            return await _releasePipelines.ListReleasePipelinesAsync(solutionId, ct);
        }
        catch (ProjectAccessDeniedException)
        {
            // Same answer as an id that isn't there — see
            // ReleasePipelineService.EnsureReleasePipelineExistsAsync.
            throw new McpException($"Solution {solutionId} does not exist in this organisation.");
        }
    }

    [McpServerTool(Name = "list_deployments", ReadOnly = true)]
    [Description("Lists a deployment pipeline's deployments, newest first, with per-app outcomes. Each deployment returns its id, its number in the pipeline's history (number; the web UI calls it 'Deployment 3'), status ('proposed'/'scheduled'/'claimed'/'uploading'/'installing'/'deployed'/'failed'/'cancelled'/'handed_off'/'dismissed': 'proposed' is a deployment the pipeline prepared from a new build that is waiting for a person to approve or dismiss it in the web UI - nothing has been sent, and there is no tool to approve it; 'dismissed' is such a prepared deployment that a person dismissed (dismissReason says why, when they gave a reason) or a newer build replaced (replacedByBuildId), so nothing was ever sent; 'handed_off' means Business Central accepted the apps and will install them on its own schedule), the build it installed, scheduled/started/finished times, who triggered it, whether it was scheduled outside the environment's delivery window, any failure message, and each app's install result. Use it to track a deploy_build call to completion. Returns the newest 20 unless you ask for more with limit; a number above 1 on the oldest row means there are older ones.")]
    public async Task<IReadOnlyList<DeliveryHistoryRow>> ListDeliveriesAsync(
        [Description("Deployment pipeline id (from list_deployment_pipelines).")] int deploymentPipelineId,
        [Description(McpListLimit.Description)] int limit = McpListLimit.Default,
        CancellationToken ct = default)
    {
        await _releasePipelines.EnsureReleasePipelineExistsAsync(deploymentPipelineId, ct);
        return await _deliveries.ListDeliveryHistoryAsync(deploymentPipelineId, McpListLimit.Clamp(limit), ct);
    }

    [McpServerTool(Name = "deploy_build", ReadOnly = false, Idempotent = false)]
    [Description("Deploys a successful build to its deployment pipeline's Business Central environment NOW — uploads and installs the build's .app files. The build must be a 'ready' build of the deployment pipeline's source build pipeline, and not a preview build (isPreview true), which is check-only. A pipeline whose installs run in the environment's delivery window (deployment schedule 'OurDeliveryWindow') still deploys immediately through this tool; the deployment is recorded as outside the window when it is. Deploying runs in the background; this returns the new deployment's id immediately, which you poll with list_deployments for progress (uploading → installing → deployed/failed). The deployment uses the pipeline's own schema sync mode; this tool cannot turn on Force sync for a deployment, and there is no way to ask it to. A deployment pipeline whose environment is Production (or any other non-sandbox type) is refused unless the organisation allows agents to deploy to production; when it does, this tool deploys there immediately too, with no confirmation step. To schedule for later, to deploy to Production when this tool refuses it, or to deploy a failed build again with Force sync for that one deployment, use the web UI. Requires permission to manage the solution.")]
    public async Task<PublishBuildResult> PublishBuildAsync(
        [Description("Deployment pipeline id (from list_deployment_pipelines) — carries the target environment and modes.")] int deploymentPipelineId,
        [Description("Build id to deploy (from list_pipeline_builds / list_solution_builds) — must be a 'ready' build of this pipeline's source build pipeline.")] int buildId,
        CancellationToken ct = default)
    {
        try
        {
            var deliveryId = await _deliveries.DeployNowForAgentAsync(deploymentPipelineId, buildId, ct);
            return new PublishBuildResult(
                deliveryId,
                "Deployment queued. Poll list_deployments with this deployment pipeline id to watch it upload, install, and deploy (or fail).");
        }
        catch (ProjectAccessDeniedException)
        {
            throw new McpException("You don't have permission to deploy this solution's builds — you must be someone who manages the solution.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't deploy that build: " + string.Join("; ", ex.Errors.Values));
        }
    }

    [McpServerTool(Name = "list_github_releases", ReadOnly = true)]
    [Description("Lists the GitHub releases a deployment pipeline can install, newest first, with each release's tag, title, publication date and the app files attached to it. Only works for a deployment pipeline whose apps come from a repository's GitHub releases - one that deploys a build pipeline's builds is refused, and you should use list_pipeline_builds for that. Releases with no app files attached cannot be installed. Requires permission to manage the solution.")]
    public async Task<IReadOnlyList<GitHubReleaseOption>> ListGitHubReleasesAsync(
        [Description("Deployment pipeline id (from list_deployment_pipelines).")] int deploymentPipelineId,
        CancellationToken ct = default)
    {
        await _releasePipelines.EnsureReleasePipelineExistsAsync(deploymentPipelineId, ct);
        try
        {
            return await _githubReleases.ListReleasesAsync(deploymentPipelineId, ct);
        }
        catch (ProjectAccessDeniedException)
        {
            throw new McpException("You don't have permission to read this solution's releases — you must be someone who manages the solution.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't list the releases: " + string.Join("; ", ex.Errors.Values));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused to list the releases: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            // No GitHub App on this deployment. An agent gets the same sentence a
            // person sees, rather than a stack trace it can do nothing with.
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "stage_github_release", ReadOnly = false, Idempotent = true)]
    [Description("Downloads the app files attached to one GitHub release and records them as a build, so deploy_build can install them into the deployment pipeline's Business Central environment. Nothing is installed yet — this only fetches the files. Staging the same release twice returns the build already recorded rather than fetching it again. Refused when the deployment pipeline does not draw from GitHub releases, when the tag no longer exists, or when the release has no app files attached. Requires permission to manage the solution.")]
    public async Task<BuildRow> StageGitHubReleaseAsync(
        [Description("Deployment pipeline id (from list_deployment_pipelines) — says which repository the release is read from.")] int deploymentPipelineId,
        [Description("The release's tag, exactly as list_github_releases reports it (for example 'v1.2.3.0').")] string tag,
        CancellationToken ct = default)
    {
        await _releasePipelines.EnsureReleasePipelineExistsAsync(deploymentPipelineId, ct);
        try
        {
            var buildId = await _githubReleases.StageReleaseAsync(deploymentPipelineId, tag, ct);
            return await _artifacts.GetBuildRowAsync(buildId, ct)
                ?? throw new McpException($"Release {tag} was staged as build {buildId}, but the build could not be read back.");
        }
        catch (ProjectAccessDeniedException)
        {
            throw new McpException("You don't have permission to deploy this solution's builds — you must be someone who manages the solution.");
        }
        catch (PlanValidationException ex)
        {
            throw new McpException("Couldn't stage that release: " + string.Join("; ", ex.Errors.Values));
        }
        catch (GitHubApiException ex)
        {
            throw new McpException("GitHub refused: " + ex.Message);
        }
        catch (GitHubAppNotConfiguredException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

/// <summary>
/// The outcome of a <c>deploy_build</c> call — the new deployment's id and how to track it.
/// MCP-only, so its member names are the agent-facing ones.
/// </summary>
public sealed record PublishBuildResult(int DeploymentId, string Message);
