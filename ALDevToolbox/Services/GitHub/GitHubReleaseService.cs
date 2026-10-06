using System.IO.Compression;
using System.Xml.Linq;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>What publishing one build to a repository's Releases page came to.</summary>
/// <param name="Published">True when the Release exists and carries this build's files.</param>
/// <param name="Tag">The tag the build was published at, when it was.</param>
/// <param name="Url">The Release's page on GitHub, when it was published.</param>
/// <param name="Error">Why it was not published, in words a consultant can act on.</param>
public sealed record GitHubReleasePublishResult(bool Published, string? Tag, string? Url, string? Error, string? Note = null)
{
    /// <summary>Nothing was asked of us: the pipeline does not publish anywhere.</summary>
    public static readonly GitHubReleasePublishResult NotRequested = new(false, null, null, null);
}

/// <summary>
/// One of a solution's GitHub repositories, offered as somewhere to publish builds or
/// to draw them from. <paramref name="FullName"/> is the <c>owner/name</c> GitHub
/// knows it by, shown so a person with two similarly-named repositories can tell them
/// apart.
/// </summary>
public sealed record GitHubReleaseRepositoryOption(int Id, string DisplayName, string FullName);

/// <summary>
/// What the release-repository picker should show, and why it has nothing to offer
/// when it hasn't: a deployment with no GitHub App at all hides the field, while an
/// organisation that simply has not connected GitHub yet is told so.
/// </summary>
/// <param name="Options">The repositories that can be picked, possibly empty.</param>
/// <param name="DeploymentConfigured">True when this deployment has a GitHub App at all.</param>
/// <param name="IsConnected">True once an admin has connected a GitHub organisation.</param>
/// <param name="HasGitHubRepositories">True when the solution names at least one GitHub repository.</param>
public sealed record GitHubReleaseRepositoryChoices(
    IReadOnlyList<GitHubReleaseRepositoryOption> Options,
    bool DeploymentConfigured,
    bool IsConnected,
    bool HasGitHubRepositories);

/// <summary>One Release offered as something to deploy, with the app files hanging off it.</summary>
public sealed record GitHubReleaseOption(
    string Tag,
    string? Name,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string> AppFileNames);

/// <summary>
/// GitHub Releases as the workbench uses them (issue #632), in both directions:
///
/// <list type="bullet">
/// <item><description><strong>Out.</strong> A build pipeline can name one of its
/// solution's GitHub repositories, and every successful build is then published
/// there as a Release tagged <c>v&lt;version&gt;</c> (or <c>build-&lt;number&gt;</c> when
/// its apps have different versions) with its <c>.app</c> files
/// attached.</description></item>
/// <item><description><strong>In.</strong> A release pipeline can draw from a
/// repository's Releases instead of from a build pipeline. Choosing a tag downloads
/// its <c>.app</c> files and stages them as an ordinary <see cref="OeProjectBuild"/>,
/// so delivery and every downstream reader work unchanged.</description></item>
/// </list>
///
/// <para><strong>The credential is the installation token throughout.</strong> A
/// Release is an act of the organisation, and the publish half runs inside the build
/// worker where there may be no user at all. The repository is therefore checked
/// against the connected GitHub organisation here: a repository outside it, or one
/// that is not on GitHub, is refused in words rather than attempted.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c> (#632).</para>
/// </summary>
public sealed class GitHubReleaseService
{
    private readonly AppDbContext _db;
    private readonly GitHubAppClient _github;
    private readonly GitHubConnectionService _connection;
    private readonly ProjectAccess _access;
    private readonly IOrganizationContext _orgContext;
    private readonly PublicOrigin _publicOrigin;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubReleaseService> _logger;
    private readonly ALDevToolbox.Services.Tools.ToolEnablement _tools;

    public GitHubReleaseService(
        AppDbContext db,
        GitHubAppClient github,
        GitHubConnectionService connection,
        ProjectAccess access,
        IOrganizationContext orgContext,
        PublicOrigin publicOrigin,
        TimeProvider clock,
        ALDevToolbox.Services.Tools.ToolEnablement tools,
        ILogger<GitHubReleaseService> logger)
    {
        _tools = tools;
        _db = db;
        _github = github;
        _connection = connection;
        _access = access;
        _orgContext = orgContext;
        _publicOrigin = publicOrigin;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The solution's GitHub repositories that sit inside the connected GitHub
    /// organisation - the ones a pipeline can publish releases to, or draw them from.
    ///
    /// <para>Empty when the organisation has connected no GitHub organisation, which
    /// is what makes the option disappear from the editors rather than appear and
    /// refuse. Reads the database only, so it renders while GitHub is down.</para>
    /// </summary>
    public async Task<IReadOnlyList<GitHubReleaseRepositoryOption>> ListRepositoryOptionsAsync(
        int projectId, CancellationToken ct = default) =>
        (await DescribeRepositoryOptionsAsync(projectId, ct)).Options;

    /// <summary>
    /// The same repositories, plus what the editors need to explain an empty list. A
    /// solution that names GitHub repositories on a deployment that has a GitHub App
    /// but no connected organisation is somebody an admin can help, so the editors say
    /// so rather than silently dropping the field.
    /// </summary>
    public async Task<GitHubReleaseRepositoryChoices> DescribeRepositoryOptionsAsync(
        int projectId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);

        var connection = await _connection.GetStatusAsync(ct);

        var repositories = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.Provider == RepositoryProvider.GitHub)
            .OrderBy(r => r.DisplayName)
            .Select(r => new { r.Id, r.DisplayName, r.Url })
            .ToListAsync(ct);

        var options = new List<GitHubReleaseRepositoryOption>();
        if (connection.IsConnected && connection.OrgLogin is { Length: > 0 } orgLogin)
        {
            foreach (var repository in repositories)
            {
                if (!TryParseRepository(repository.Url, out var owner, out var name)) continue;
                if (!string.Equals(owner, orgLogin, StringComparison.OrdinalIgnoreCase)) continue;
                options.Add(new GitHubReleaseRepositoryOption(repository.Id, repository.DisplayName, $"{owner}/{name}"));
            }
        }

        return new GitHubReleaseRepositoryChoices(
            options, connection.DeploymentConfigured, connection.IsConnected, repositories.Count > 0);
    }

    // ── Publishing a build ──────────────────────────────────────────────────

    /// <summary>
    /// Publishes <paramref name="projectBuildId"/>'s deliverables to the Releases page
    /// of the repository its pipeline names, and records the outcome on the build (tag
    /// and URL, or the reason it did not happen) plus a "GitHub Release" section in the
    /// build log.
    ///
    /// <para><strong>This never fails a build.</strong> The <c>.app</c> files exist and
    /// download whatever GitHub says, so every refusal - a tag rule, a missing grant, an
    /// unreachable GitHub - comes back as
    /// <see cref="GitHubReleasePublishResult.Error"/> on a build that stays
    /// <c>ready</c>.</para>
    ///
    /// <para>Publishing again under a tag that already exists replaces the Release's
    /// assets and rewrites its body. The tag is never moved: a version that has shipped points at
    /// the commit it shipped from.</para>
    /// </summary>
    public async Task<GitHubReleasePublishResult> PublishBuildAsync(int projectBuildId, CancellationToken ct = default)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == projectBuildId)
            .Select(b => new
            {
                b.Id,
                b.OrganizationId,
                b.ProjectId,
                RepositoryId = b.Pipeline!.GithubReleaseRepositoryId,
                RepositoryUrl = b.Pipeline.GithubReleaseRepository!.Url,
                RepositoryProvider = (RepositoryProvider?)b.Pipeline.GithubReleaseRepository.Provider,
                PipelineName = b.Pipeline.Name,
                b.BcTarget,
            })
            .FirstOrDefaultAsync(ct);

        // A preview build compiled against a Business Central version the customers
        // don't run yet, from manifests that still name the current one: a release
        // of it would install on today's environments while built for tomorrow's.
        // It is a check, so it is never tagged, whatever the pipeline names.
        if (build is null || build.RepositoryId is null || ProjectBuildTarget.IsPreview(build.BcTarget))
        {
            return GitHubReleasePublishResult.NotRequested;
        }

        GitHubReleasePublishResult result;
        try
        {
            result = await TryPublishAsync(projectBuildId, build.RepositoryId.Value, build.RepositoryProvider, build.RepositoryUrl, ct);
        }
        catch (GitHubApiException ex)
        {
            // GitHub's own words are the useful ones here - "tag creation is
            // restricted" is something a consultant can take to their admin,
            // where "the publish failed" is not.
            result = new GitHubReleasePublishResult(false, null, null, ex.Message);
        }
        catch (GitHubAppNotConfiguredException)
        {
            result = new GitHubReleasePublishResult(false, null, null,
                "This server has no GitHub App set up, so nothing could be published.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "GitHub could not be reached while publishing build {BuildId}.", projectBuildId);
            result = new GitHubReleasePublishResult(false, null, null,
                "GitHub could not be reached, so this build was not published. Build again to retry.");
        }

        await RecordOutcomeAsync(projectBuildId, result, ct);
        return result;
    }

    private async Task<GitHubReleasePublishResult> TryPublishAsync(
        int projectBuildId, int repositoryId, RepositoryProvider? provider, string repositoryUrl, CancellationToken ct)
    {
        if (provider != RepositoryProvider.GitHub || !TryParseRepository(repositoryUrl, out var owner, out var name))
        {
            return NotPublished("The repository chosen for releases is not a GitHub repository.");
        }

        var connection = await _connection.GetStatusAsync(ct);
        if (connection.InstallationId is not { } installationId)
        {
            return NotPublished("No GitHub organisation is connected, so this build was not published.");
        }
        if (!string.Equals(owner, connection.OrgLogin, StringComparison.OrdinalIgnoreCase))
        {
            return NotPublished(
                $"{owner}/{name} is outside the connected GitHub organisation ({connection.OrgLogin}), so nothing was published there.");
        }

        // An app carried over unchanged from an earlier build (#1094) was released by
        // that build already, so only what this build compiled itself goes up.
        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == projectBuildId && a.CarriedFromBuildId == null)
            .OrderBy(a => a.FileName)
            .Select(a => new { a.FileName, a.AppName, a.AppVersion, a.Content })
            .ToListAsync(ct);
        var anyCarried = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .AnyAsync(a => a.ProjectBuildId == projectBuildId && a.CarriedFromBuildId != null, ct);
        if (artifacts.Count == 0)
        {
            return anyCarried
                ? new GitHubReleasePublishResult(false, null, null, null,
                    "No extension changed since the last build, so no GitHub release was made.")
                : NotPublished("This build produced no app files to publish.");
        }

        var tag = ReleaseTag(artifacts.Select(a => a.AppVersion).ToList(), projectBuildId);
        var token = await _github.GetInstallationTokenAsync(installationId, ct);
        var body = ReleaseBody(artifacts.Select(a => (a.AppName, a.AppVersion)).ToList(), projectBuildId);

        var existing = await _github.GetReleaseByTagAsync(token, owner, name, tag, ct);
        if (existing is not null && anyCarried)
        {
            // Rewriting that release would drop the files of the apps this build carried
            // over (they are not uploaded again), so a build that releases only part of the
            // solution goes under its own number instead. Happens when numbering is off.
            tag = $"build-{projectBuildId}";
            existing = await _github.GetReleaseByTagAsync(token, owner, name, tag, ct);
        }
        GitHubRelease release;
        if (existing is null)
        {
            // The tag goes on the commit this build compiled from that repository.
            var builtCommit = await _db.OeProjectBuildRepoCommits.AsNoTracking()
                .Where(c => c.ProjectBuildId == projectBuildId && c.ProjectRepositoryId == repositoryId)
                .Select(c => c.CommitHash)
                .FirstOrDefaultAsync(ct);
            release = await _github.CreateReleaseAsync(token, owner, name, tag, tag, body, builtCommit, ct);
        }
        else
        {
            // Replace, don't append: GitHub refuses a second asset with the same
            // name, and a stale file beside a fresh one is worse than either.
            foreach (var asset in existing.Assets)
            {
                await _github.DeleteReleaseAssetAsync(token, owner, name, asset.Id, ct);
            }
            release = await _github.UpdateReleaseAsync(token, owner, name, existing.Id, existing.Name ?? tag, body, ct);
        }

        // Without an upload address there is nowhere to put the files, and a
        // Release with no .app on it is not something to report as published.
        if (string.IsNullOrWhiteSpace(release.UploadUrl))
        {
            _logger.LogWarning(
                "GitHub created the release {Tag} on {Owner}/{Repo} but said nothing about where to upload its files.",
                tag, owner, name);
            return new GitHubReleasePublishResult(
                false, tag, release.HtmlUrl,
                "GitHub did not say where to upload the app files, so the release has none. Try publishing again.");
        }

        foreach (var artifact in artifacts)
        {
            await _github.UploadReleaseAssetAsync(token, release.UploadUrl, artifact.FileName, artifact.Content, ct);
        }

        _logger.LogInformation(
            "Published build {BuildId} as the GitHub release {Tag} on {Owner}/{Repo} with {AssetCount} app file(s).",
            projectBuildId, tag, owner, name, artifacts.Count);
        return new GitHubReleasePublishResult(true, tag, release.HtmlUrl, null);
    }

    /// <summary>
    /// Stamps the outcome onto the build and appends the "GitHub Release" section to
    /// its log, so the build page can say what happened without a second call to
    /// GitHub. Both are best-effort: a build that is already <c>ready</c> must not be
    /// undone by a bookkeeping failure.
    /// </summary>
    private async Task RecordOutcomeAsync(int projectBuildId, GitHubReleasePublishResult result, CancellationToken ct)
    {
        if (result == GitHubReleasePublishResult.NotRequested) return;

        var build = await _db.OeProjectBuilds.FirstOrDefaultAsync(b => b.Id == projectBuildId, ct);
        if (build is null) return;

        build.GithubReleaseTag = result.Tag;
        build.GithubReleaseUrl = result.Url;
        build.GithubReleaseError = result.Error;

        var nextOrdering = await _db.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == projectBuildId)
            .Select(l => (int?)l.Ordering)
            .MaxAsync(ct) ?? -1;

        _db.OeProjectBuildLogs.Add(new OeProjectBuildLog
        {
            OrganizationId = build.OrganizationId,
            ProjectBuildId = projectBuildId,
            Section = "GitHub Release",
            Content = result.Published
                ? $"Published as {result.Tag} ({result.Url})."
                : result.Error ?? result.Note ?? "Not published.",
            Ordering = nextOrdering + 1,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The tag a build is published under: <c>v&lt;version&gt;</c> when every app has the
    /// same version, else <c>build-&lt;number&gt;</c>. Apps that each keep their own
    /// Major.Minor share only the build number, and naming the Release after one app's
    /// version would put a name on the page that the other files disagree with.
    /// </summary>
    internal static string ReleaseTag(IReadOnlyCollection<string> appVersions, int projectBuildId)
    {
        var versions = appVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return versions.Count == 1 ? $"v{versions[0]}" : $"build-{projectBuildId}";
    }

    /// <summary>The Release body: which apps this build produced, and where the build itself is.</summary>
    private string ReleaseBody(IReadOnlyList<(string Name, string Version)> apps, int projectBuildId)
    {
        var lines = new List<string> { "Published by AL Workbench.", string.Empty };
        foreach (var app in apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"- {app.Name} {app.Version}");
        }
        if (_publicOrigin.Configured is { Length: > 0 } origin)
        {
            lines.Add(string.Empty);
            lines.Add($"Build: {origin}/artifacts/build/{projectBuildId}");
        }
        return string.Join('\n', lines);
    }

    // ── Deploying from a Release ────────────────────────────────────────────

    /// <summary>
    /// The Releases a release pipeline can deploy from, newest first, each with the
    /// <c>.app</c> files attached to it. Only app files are listed: a Release carrying
    /// source archives and a changelog has nothing there this can install.
    /// </summary>
    /// <exception cref="PlanValidationException">The pipeline does not draw from GitHub Releases, or its repository is unusable.</exception>
    /// <exception cref="ProjectAccessDeniedException">The caller may not manage the solution.</exception>
    public async Task<IReadOnlyList<GitHubReleaseOption>> ListReleasesAsync(
        int releasePipelineId, CancellationToken ct = default)
    {
        var source = await ResolveSourceAsync(releasePipelineId, ct);
        var token = await _github.GetInstallationTokenAsync(source.InstallationId, ct);
        var releases = await _github.ListReleasesAsync(token, source.Owner, source.Name, ct);

        return releases
            .Select(r => new GitHubReleaseOption(
                r.TagName,
                r.Name,
                r.PublishedAt,
                r.Assets.Where(IsAppAsset).Select(a => a.Name).ToList()))
            .ToList();
    }

    /// <summary>
    /// Downloads the <c>.app</c> files attached to <paramref name="tag"/> and stages
    /// them as a <see cref="OeProjectBuild"/> — status <c>ready</c>, no pipeline, the tag
    /// recorded — so the ordinary delivery flow can publish them to Business Central.
    /// Returns the staged build's id.
    ///
    /// <para>Staging the same tag twice returns the build already staged rather than
    /// downloading it again: a second identical build in the history would make "which
    /// one did we deploy" a question with two answers.</para>
    /// </summary>
    /// <exception cref="PlanValidationException">No such tag, or the Release carries no app files.</exception>
    /// <exception cref="ProjectAccessDeniedException">The caller may not manage the solution.</exception>
    public async Task<int> StageReleaseAsync(int releasePipelineId, string tag, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; staging a GitHub release called outside an authenticated request.");
        // Fetches the files that will be deployed; same rule as deploying.
        await _tools.EnsureStepUpAsync(Domain.Tools.ToolKey.Releases, ct);
        var source = await ResolveSourceAsync(releasePipelineId, ct);
        tag = (tag ?? string.Empty).Trim();
        if (tag.Length == 0) throw Validation("Tag", "Choose a release to deploy.");

        var token = await _github.GetInstallationTokenAsync(source.InstallationId, ct);
        var release = await _github.GetReleaseByTagAsync(token, source.Owner, source.Name, tag, ct)
            ?? throw Validation("Tag", $"{source.Owner}/{source.Name} has no release tagged {tag} any more. Refresh the list and pick another.");

        var assets = release.Assets.Where(IsAppAsset).ToList();
        if (assets.Count == 0)
        {
            throw Validation("Tag", $"The release {tag} has no app files attached, so there is nothing to install.");
        }

        var alreadyStaged = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == source.ProjectId
                        && b.PipelineId == null
                        && b.GithubReleaseTag == tag
                        && b.GithubReleaseUrl == release.HtmlUrl)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync(ct);
        if (alreadyStaged is { } existingId)
        {
            _logger.LogInformation(
                "Release {Tag} on {Owner}/{Repo} is already staged as build {BuildId}.",
                tag, source.Owner, source.Name, existingId);
            return existingId;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var build = new OeProjectBuild
        {
            OrganizationId = orgId,
            ProjectId = source.ProjectId,
            PipelineId = null,
            StartedByUserId = _orgContext.CurrentUserId,
            Branch = null,
            Status = ProjectBuildStatus.Ready,
            GithubReleaseTag = tag,
            GithubReleaseUrl = release.HtmlUrl,
            StartedAt = now,
            FinishedAt = now,
        };

        foreach (var asset in assets)
        {
            var content = await _github.DownloadReleaseAssetAsync(token, source.Owner, source.Name, asset.Id, ct);
            var (appName, appVersion) = ReadAppIdentity(asset.Name, content);
            build.Artifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = orgId,
                FileName = asset.Name,
                AppId = BuildArtifactAppIdBackfill.ReadAppId(content),
                AppName = appName,
                AppVersion = appVersion,
                SizeBytes = content.LongLength,
                Content = content,
                CreatedAt = now,
            });
        }

        _db.OeProjectBuilds.Add(build);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Staged the GitHub release {Tag} on {Owner}/{Repo} as build {BuildId} with {AppCount} app file(s).",
            tag, source.Owner, source.Name, build.Id, assets.Count);
        return build.Id;
    }

    /// <summary>A release pipeline that draws from GitHub Releases, resolved to a repository we may act on.</summary>
    private sealed record ReleaseSource(int ProjectId, string Owner, string Name, long InstallationId);

    /// <summary>
    /// The access gate both Release-sourced calls share: the caller must be able to
    /// manage the solution, the pipeline must actually draw from Releases, and its
    /// repository must be one inside the connected GitHub organisation.
    /// </summary>
    private async Task<ReleaseSource> ResolveSourceAsync(int releasePipelineId, CancellationToken ct)
    {
        var rp = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId && r.DeletedAt == null)
            .Select(r => new
            {
                r.ProjectId,
                r.ArtifactSource,
                r.GithubReleaseRepositoryId,
                RepositoryUrl = r.GithubReleaseRepository!.Url,
                RepositoryProvider = (RepositoryProvider?)r.GithubReleaseRepository.Provider,
                OwnerId = r.Project!.CreatedByUserId,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("ReleasePipeline", "This deployment pipeline no longer exists.");

        await _access.EnsureCanManageAsync(rp.ProjectId, rp.OwnerId, ct);

        if (rp.ArtifactSource != ReleaseArtifactSource.GithubRelease)
        {
            throw Validation("ArtifactSource", "This deployment pipeline deploys builds from a build pipeline, not GitHub releases.");
        }
        // The repository row can go while the pipeline still draws from Releases:
        // removing a repository from the solution nulls it. Saying the pipeline is
        // fed from a build pipeline would send the user looking in the wrong place.
        if (rp.GithubReleaseRepositoryId is null)
        {
            throw Validation("GithubReleaseRepositoryId",
                "This deployment pipeline no longer names a repository; pick one on the deployment pipeline.");
        }
        if (rp.RepositoryProvider != RepositoryProvider.GitHub
            || !TryParseRepository(rp.RepositoryUrl, out var owner, out var name))
        {
            throw Validation("GithubReleaseRepositoryId", "The repository this deployment pipeline draws from is not a GitHub repository.");
        }

        var connection = await _connection.GetStatusAsync(ct);
        if (connection.InstallationId is not { } installationId)
        {
            throw Validation("GithubReleaseRepositoryId",
                "No GitHub organisation is connected. Connect one under Administration -> Repositories, then try again.");
        }
        if (!string.Equals(owner, connection.OrgLogin, StringComparison.OrdinalIgnoreCase))
        {
            throw Validation("GithubReleaseRepositoryId",
                $"{owner}/{name} is outside the connected GitHub organisation ({connection.OrgLogin}).");
        }

        return new ReleaseSource(rp.ProjectId, owner, name, installationId);
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Splits an https clone or browse URL into <c>owner</c> and repository name.
    /// False for anything that is not a github.com URL naming both.
    /// </summary>
    public static bool TryParseRepository(string? url, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("github.com" or "www.github.com")) return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return false;

        owner = segments[0];
        name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        return owner.Length > 0 && name.Length > 0;
    }

    /// <summary>A Release asset the workbench can install: a compiled extension, not a packaging by-product.</summary>
    private static bool IsAppAsset(GitHubReleaseAsset asset) =>
        asset.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        && !asset.Name.EndsWith(".dep.app", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The app's name and version, read from the <c>NavxManifest.xml</c> inside the
    /// <c>.app</c> when it can be, from the <c>Publisher_Name_Version.app</c> file-name
    /// convention when it cannot, and from the bare file name when that fails too.
    ///
    /// <para>The manifest is read directly rather than through the full package reader:
    /// staging wants two strings, and parsing every symbol in a base-app-sized package
    /// to get them would be minutes of work for nothing.</para>
    /// </summary>
    internal static (string AppName, string AppVersion) ReadAppIdentity(string fileName, byte[] content)
    {
        try
        {
            // A .app is a zip behind a 40-byte NAVX header.
            const int navxPrefix = 40;
            if (content.Length > navxPrefix)
            {
                using var stream = new MemoryStream(content, navxPrefix, content.Length - navxPrefix, writable: false);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var entry = archive.Entries.FirstOrDefault(
                    e => e.Name.Equals("NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    using var manifestStream = entry.Open();
                    var app = XDocument.Load(manifestStream).Root?.Element("App");
                    var name = app?.Attribute("Name")?.Value;
                    var version = app?.Attribute("Version")?.Value;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                    {
                        return (name!, version!);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NotSupportedException)
        {
            // Not a shape we can read; the file name still says something useful.
        }

        // Publisher_Name_Version.app — what the AL compiler emits.
        var bare = fileName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        var parts = bare.Split('_');
        return parts.Length >= 3
            ? (string.Join('_', parts[1..^1]), parts[^1])
            : (bare, string.Empty);
    }

    private static GitHubReleasePublishResult NotPublished(string reason) => new(false, null, null, reason);

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}
