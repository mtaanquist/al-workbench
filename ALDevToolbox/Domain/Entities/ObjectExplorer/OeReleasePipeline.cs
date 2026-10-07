using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A reusable "where + how" of a deploy: a named release configuration that draws a
/// <see cref="OePipeline"/> (build) pipeline's artifacts and targets one
/// <see cref="OeProjectEnvironment"/>. The deliberate counterpart to the build half of
/// the split — a build pipeline can feed several release pipelines, so the same build
/// is deployed to several environments (test in Sandbox, promote the identical
/// artifact to Production). Reads as <em>"Release Contoso App on Production."</em>
/// Org-scoped via the standard query filter; soft-deleted; management rights come
/// from the parent project's owner via <c>ProjectAccess</c>. A release of a chosen
/// build is an <see cref="OeProjectDelivery"/>; this entity is the config each one
/// snapshots when it is created. See <c>.design/saas-delivery.md</c>.
/// </summary>
public class OeReleasePipeline
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project (customer) this release pipeline belongs to.</summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>
    /// The user who created it — its owner of record. Nullable
    /// (<c>ON DELETE SET NULL</c>) so the release pipeline outlives the account;
    /// management rights come from the parent project's owner via <c>ProjectAccess</c>.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>
    /// Display name, unique per project among active rows. Generated from the
    /// pipeline's setup by <c>PipelineNames</c> (e.g. <c>main to Production</c>) unless
    /// <see cref="NameIsCustom"/>.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// True when a person typed the name because the generated one was already taken
    /// in the solution. Such a name is kept on later saves until they clear it.
    /// </summary>
    public bool NameIsCustom { get; set; }

    /// <summary>
    /// Where the artifacts come from: <see cref="ReleaseArtifactSource.Build"/> (this
    /// build pipeline's builds) or <see cref="ReleaseArtifactSource.GithubRelease"/>
    /// (the Releases published on a GitHub repository). Exactly one of
    /// <see cref="BuildPipelineId"/> / <see cref="GithubReleaseRepositoryId"/> is set
    /// accordingly. See <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    public string ArtifactSource { get; set; } = ReleaseArtifactSource.Build;

    /// <summary>
    /// The artifact source — releases publish this build pipeline's builds. Null when
    /// <see cref="ArtifactSource"/> is <see cref="ReleaseArtifactSource.GithubRelease"/>,
    /// which draws its apps from a repository's Releases instead.
    /// </summary>
    public int? BuildPipelineId { get; set; }
    public OePipeline? BuildPipeline { get; set; }

    /// <summary>
    /// The solution repository whose GitHub Releases this pipeline publishes from.
    /// Set only when <see cref="ArtifactSource"/> is
    /// <see cref="ReleaseArtifactSource.GithubRelease"/>; nullable FK with
    /// <c>ON DELETE SET NULL</c> so removing the repository from the solution leaves
    /// the pipeline's history intact.
    /// </summary>
    public int? GithubReleaseRepositoryId { get; set; }
    public OeProjectRepository? GithubReleaseRepository { get; set; }

    /// <summary>The target environment (and with it the Production/Sandbox type and the delivery window).</summary>
    public int ProjectEnvironmentId { get; set; }
    public OeProjectEnvironment? ProjectEnvironment { get; set; }

    /// <summary>
    /// When Business Central installs the uploaded package, sent verbatim as the App
    /// Management API's <c>deploymentSchedule</c>. One of
    /// <see cref="BcDeploymentSchedule"/>.
    /// </summary>
    public string DeploymentSchedule { get; set; } = BcDeploymentSchedule.Immediate;

    /// <summary>
    /// How the install reconciles table schema, sent verbatim as the App Management
    /// API's <c>syncMode</c>. One of <see cref="BcSyncMode"/>;
    /// <see cref="BcSyncMode.ForceSync"/> can drop columns and is gated behind a confirm.
    /// </summary>
    public string SchemaSyncMode { get; set; } = BcSyncMode.Add;

    /// <summary>
    /// When true, a new successful build of <see cref="BuildPipeline"/> prepares a
    /// release through this pipeline in the <see cref="ProjectDeliveryStatus.Proposed"/>
    /// state: the build and its apps chosen, the time set by the pipeline's rule, nothing
    /// sent. A person approves or dismisses it on the pipeline's page; nothing is ever
    /// approved on its own. Off by default, and only honoured for a pipeline that draws
    /// from a build pipeline. See <c>.design/saas-delivery.md</c> (#934).
    /// </summary>
    public bool PrepareReleaseOnNewBuild { get; set; }

    /// <summary>
    /// When true, a new successful build is deployed straight away instead of being
    /// prepared for approval: only with <see cref="PrepareReleaseOnNewBuild"/> on, and only
    /// while the target environment is a sandbox. The type is checked again when the build
    /// lands and, live, before anything is uploaded, so this can never reach a Production
    /// environment. Off by default. See <c>.design/saas-delivery.md</c>, "Deploying to a
    /// sandbox without approval" (#1096).
    /// </summary>
    public bool DeployWithoutApproval { get; set; }

    /// <summary>
    /// Who turned <see cref="DeployWithoutApproval"/> on. Those deployments run as this
    /// person, the way building on push runs as <see cref="OePipeline.BuildOnPushByUserId"/>:
    /// their access is checked and the deployment is theirs in the history. Null once that
    /// user is deleted, which leaves new builds waiting for approval again.
    /// </summary>
    public int? DeployWithoutApprovalByUserId { get; set; }
    public User? DeployWithoutApprovalByUser { get; set; }

    /// <summary>
    /// When true, this pipeline only deploys builds made from <see cref="AllowedBranch"/>,
    /// so a build of a test branch cannot reach the environment by way of a build pipeline
    /// whose branch was changed. On by default for a Production environment. Only builds
    /// of a build pipeline carry a branch; a pipeline that installs GitHub releases ignores
    /// it. See <c>.design/saas-delivery.md</c>, "Which branch may reach an environment".
    /// </summary>
    public bool RestrictBranch { get; set; }

    /// <summary>
    /// The branch <see cref="RestrictBranch"/> allows, compared with
    /// <see cref="OeProjectBuild.Branch"/> (or the default branch the build recorded, see
    /// <c>DeploymentBranchRule</c>). Null means the repositories' default branch, which is
    /// what a build pipeline with no branch builds.
    /// </summary>
    public string? AllowedBranch { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker. Hidden from lists unless restored.</summary>
    public DateTime? DeletedAt { get; set; }
}
