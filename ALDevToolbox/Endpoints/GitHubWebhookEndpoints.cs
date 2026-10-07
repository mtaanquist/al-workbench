using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The workbench's one inbound route: GitHub telling us a pull request opened,
/// reopened or gained a new commit, so the compile gate (#627) can answer "does
/// this still build?" on the pull request itself.
///
/// <para>Phase 1 had no such route on purpose - "nothing needs GitHub to call us".
/// This one exists because a check run is by definition something GitHub asks for,
/// and it is written to be the smallest inbound surface that can be: anonymous,
/// antiforgery-disabled (there is no browser and no cookie), rate-limited per
/// source address, capped at a megabyte (25 MB for a push), and doing nothing at all until an
/// HMAC-SHA256 over the raw body matches the deployment's stored webhook secret.
/// A delivery that verifies is parsed and enqueued; nothing here reads or writes
/// the database, and nothing here decides which organisation a delivery belongs
/// to. See <c>.design/github-integration-phase2.md</c> (#627).</para>
///
/// <para>The same route also takes <c>push</c> deliveries and merged pull
/// requests (#963), so a build pipeline can tell when the branch it watches has
/// moved past what it last built. The worker records those, and a push also starts
/// a build on each pipeline that builds on push, unless it was forced or deleted
/// the branch (#1079); they pass the same secret,
/// size and signature checks first.</para>
/// </summary>
public static class GitHubWebhookEndpoints
{
    /// <summary>Where GitHub posts. Copied into the App's Webhook URL box by the SiteAdmin.</summary>
    public const string WebhookPath = "/github/webhook";

    /// <summary>Rate-limit policy name for the webhook, registered in <c>OperationsRegistration</c>.</summary>
    public const string WebhookRateLimitPolicy = "github-webhook";

    /// <summary>
    /// A megabyte: the cap for every event except <c>push</c>. A
    /// <c>pull_request</c> event is a few kilobytes of metadata and we read the
    /// whole body into memory to hash it, so the cap is set to what the event
    /// actually is, not to what the largest event could be.
    /// </summary>
    public const int MaxRequestBodyBytes = 1_000_000;

    /// <summary>
    /// GitHub's own documented ceiling for a delivery, 25&#160;MB, and the cap for a
    /// <c>push</c> (#1126). A push lists up to twenty commits with every file each
    /// one added, removed or changed, so a large import or a merge touching
    /// thousands of files goes well past a megabyte, and a refused push is a build
    /// that never happens. This is also the route's socket-level limit; the
    /// smaller cap for every other event is applied while the body is read.
    /// </summary>
    public const int MaxPushBodyBytes = 25 * 1024 * 1024;

    /// <summary>How many push bodies past a megabyte may be read at once (#1126).</summary>
    internal const int MaxConcurrentLargeBodies = 4;

    private static readonly SemaphoreSlim LargeBodyGate = new(MaxConcurrentLargeBodies);

    /// <summary>The pull-request actions worth a build. Everything else is a no-op we answer 204 to.</summary>
    private static readonly HashSet<string> BuildableActions =
        new(StringComparer.OrdinalIgnoreCase) { "opened", "synchronize", "reopened" };

    /// <summary>
    /// The two values of GitHub's <c>author_association</c> that mean the author
    /// is inside the organisation the repository belongs to. Everything else -
    /// <c>COLLABORATOR</c>, <c>CONTRIBUTOR</c>, <c>NONE</c>, or the field being
    /// absent - is somebody whose fork is not built.
    /// </summary>
    private static readonly HashSet<string> MemberAssociations =
        new(StringComparer.OrdinalIgnoreCase) { "MEMBER", "OWNER" };

    public static IEndpointRouteBuilder MapGitHubWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(WebhookPath, async (
            HttpContext ctx,
            SystemSettingsService settings,
            GitHubWebhookQueue queue,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("ALDevToolbox.GitHubWebhook");

            // Every response writes a body. UseStatusCodePagesWithReExecute
            // re-runs the pipeline at GET /not-found for a bare 4xx, and for a
            // POST that re-execute comes back to the client as 400 - which would
            // turn "your signature is wrong" into "your request is malformed" in
            // GitHub's delivery log. Writing a body ourselves makes the
            // status-pages middleware skip the rewrite. Same reasoning as
            // McpEndpoints.
            var secret = await settings.ResolveGitHubWebhookSecretAsync(ct);
            if (secret is null)
            {
                log.LogWarning("Refused a GitHub webhook delivery: no webhook secret is configured for this deployment.");
                return Results.Text("This deployment has no GitHub webhook secret configured.", "text/plain", statusCode: 401);
            }

            // Only a push is allowed past a megabyte, and only when it carries a
            // well-formed signature: anything else is refused below whatever its
            // size, so there is no reason to read 25 MB of it first. The body has
            // to be read before the signature can be checked, so large reads also
            // take a turn at LargeBodyGate: a stranger sending forged pushes in
            // parallel cannot hold more than a few of them in memory at once.
            var signature = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
            var eventName = ctx.Request.Headers["X-GitHub-Event"].ToString();
            var large = string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase)
                && IsWellFormedSignature(signature);
            byte[]? body;
            if (large)
            {
                await LargeBodyGate.WaitAsync(ct);
                try
                {
                    body = await ReadBodyAsync(ctx.Request, MaxPushBodyBytes, ct);
                }
                finally
                {
                    LargeBodyGate.Release();
                }
            }
            else
            {
                body = await ReadBodyAsync(ctx.Request, MaxRequestBodyBytes, ct);
            }
            if (body is null)
            {
                return Results.Text("The delivery body is too large.", "text/plain", statusCode: 413);
            }

            if (!SignatureMatches(secret, body, signature))
            {
                log.LogWarning("Refused a GitHub webhook delivery: the X-Hub-Signature-256 header did not match.");
                return Results.Text("The delivery signature did not match.", "text/plain", statusCode: 401);
            }

            var deliveryId = ctx.Request.Headers["X-GitHub-Delivery"].ToString();

            // GitHub sends a ping the moment the hook is saved, and shows the
            // answer to the operator. Answering it is how "did I paste the right
            // address and secret" gets a yes.
            if (string.Equals(eventName, "ping", StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation("GitHub webhook ping accepted (delivery {DeliveryId}).", deliveryId);
                return Results.Text("pong", "text/plain");
            }

            // A branch moved. Recorded, and built only by pipelines that build on
            // push: see "Branch watching" (#963) and "Building on push" (#1079) in
            // the design doc. A tag push, or a payload we cannot read, is 204.
            if (string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase))
            {
                var push = TryReadPush(body, deliveryId, log);
                if (push is null) return Results.NoContent();
                if (!queue.TryEnqueue(push))
                {
                    log.LogWarning(
                        "Refused a push delivery for {Repository} ({Branch}): the webhook queue is full.",
                        push.RepositoryFullName, push.Branch);
                    return Results.Text("Busy; the delivery will be requested again.", "text/plain", statusCode: 503);
                }
                log.LogInformation(
                    "Queued a push to {Repository} ({Branch} at {HeadSha}, installation {InstallationId}, delivery {DeliveryId}).",
                    push.RepositoryFullName, push.Branch, push.HeadSha, push.InstallationId, deliveryId);
                return Results.Text("Queued.", "text/plain", statusCode: 202);
            }

            if (!string.Equals(eventName, "pull_request", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NoContent();
            }

            // A merged pull request is recorded for the branch it landed on (#963).
            // It is a closed pull request, so it never reaches the build path below.
            var merged = TryReadMergedPullRequest(body, deliveryId, log);
            if (merged is not null)
            {
                if (!queue.TryEnqueue(merged))
                {
                    log.LogWarning(
                        "Refused a merged pull-request delivery for {Repository}#{Number}: the webhook queue is full.",
                        merged.RepositoryFullName, merged.Number);
                    return Results.Text("Busy; the delivery will be requested again.", "text/plain", statusCode: 503);
                }
                log.LogInformation(
                    "Queued a merged pull request {Repository}#{Number} into {Branch} (delivery {DeliveryId}).",
                    merged.RepositoryFullName, merged.Number, merged.BaseBranch, deliveryId);
                return Results.Text("Queued.", "text/plain", statusCode: 202);
            }

            var job = TryReadPullRequest(body, deliveryId, log);
            if (job is null) return Results.NoContent();

            // A full channel means the workbench is already behind on builds.
            // Waiting here would hold GitHub's request open behind that backlog.
            // GitHub does not resend a 5xx by itself: the refusal stays in the
            // App's delivery log, and GitHubWebhookRecoveryScheduler asks GitHub
            // to send it again once the queue has room (#1121).
            if (!queue.TryEnqueue(job))
            {
                log.LogWarning(
                    "Refused a pull-request delivery for {Repository}#{Number}: the build queue is full.",
                    job.RepositoryFullName, job.PullRequestNumber);
                return Results.Text("Busy; the delivery will be requested again.", "text/plain", statusCode: 503);
            }

            // Announced only once the job is really queued. Announcing first would
            // cancel the build running for the previous head on the strength of a
            // job that then never arrived, leaving the pull request with no answer
            // at all.
            queue.Announce(job.Key, job.HeadSha, job.UpdatedAt);

            log.LogInformation(
                "Queued a pull-request build for {Repository}#{Number} at {HeadSha} (installation {InstallationId}, delivery {DeliveryId}).",
                job.RepositoryFullName, job.PullRequestNumber, job.HeadSha, job.InstallationId, deliveryId);
            return Results.Text("Queued.", "text/plain", statusCode: 202);
        })
        .AllowAnonymous()
        // There is no browser, no cookie and no antiforgery token in a webhook
        // delivery; the HMAC over the raw body is what authenticates it.
        .DisableAntiforgery()
        .RequireRateLimiting(WebhookRateLimitPolicy)
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxPushBodyBytes));

        return app;
    }

    /// <summary>
    /// Reads the whole body, or <see langword="null"/> when it exceeds
    /// <paramref name="cap"/>. The signature is over the raw bytes, so the body has
    /// to be read once and hashed before it is parsed - there is no streaming
    /// shortcut here. With a declared length (GitHub always sends one) the bytes
    /// land in one array of exactly that size, so a large push is held once.
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int cap, CancellationToken ct)
    {
        if (request.ContentLength > cap) return null;
        try
        {
            if (request.ContentLength is { } length)
            {
                var exact = new byte[length];
                var filled = 0;
                int got;
                while (filled < exact.Length
                       && (got = await request.Body.ReadAsync(exact.AsMemory(filled), ct)) > 0)
                {
                    filled += got;
                }
                return filled == exact.Length ? exact : exact[..filled];
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > cap) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The server's own limit tripped first; answer it the same way.
            return null;
        }
    }

    /// <summary>Whether <paramref name="signature"/> has the shape of GitHub's header, <c>sha256=</c> and 64 hex digits.</summary>
    internal static bool IsWellFormedSignature(string? signature) =>
        signature is { Length: 71 }
        && signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
        && !signature.AsSpan(7).ContainsAnyExcept("0123456789abcdefABCDEF");

    /// <summary>
    /// Whether <paramref name="signature"/> is GitHub's <c>sha256=&lt;hex&gt;</c>
    /// HMAC of <paramref name="body"/> under <paramref name="secret"/>, compared in
    /// constant time.
    ///
    /// <para>Internal so the endpoint's own tests can mint a valid header rather
    /// than reimplementing the rule they are meant to be checking.</para>
    /// </summary>
    internal static bool SignatureMatches(string secret, byte[] body, string? signature)
    {
        const string Prefix = "sha256=";
        if (string.IsNullOrEmpty(signature)) return false;
        if (!signature.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var hex = signature[Prefix.Length..];
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>The header value a delivery signed with <paramref name="secret"/> carries. Test seam.</summary>
    internal static string SignatureHeader(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>
    /// Pulls the fields a build needs out of a <c>pull_request</c> payload, or
    /// <see langword="null"/> when the action is not one we build or the payload
    /// is missing something. A payload we cannot read is not an error worth a 4xx:
    /// GitHub would retry it forever, and there is nothing on our side to fix.
    /// </summary>
    private static GitHubPullRequestJob? TryReadPullRequest(byte[] body, string deliveryId, ILogger log)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var action = Text(root, "action");
            if (action is null || !BuildableActions.Contains(action)) return null;

            if (!root.TryGetProperty("installation", out var installation)
                || !installation.TryGetProperty("id", out var installationId)
                || !installationId.TryGetInt64(out var installationIdValue))
            {
                log.LogWarning("A pull_request delivery ({DeliveryId}) named no installation; nothing to act for.", deliveryId);
                return null;
            }

            if (!root.TryGetProperty("repository", out var repository)
                || !root.TryGetProperty("pull_request", out var pullRequest))
            {
                return null;
            }

            var fullName = Text(repository, "full_name");
            var cloneUrl = Text(repository, "clone_url");
            var number = pullRequest.TryGetProperty("number", out var n) && n.TryGetInt32(out var numberValue)
                ? numberValue : 0;
            var head = pullRequest.TryGetProperty("head", out var h) ? h : default;
            var baseRef = pullRequest.TryGetProperty("base", out var b) ? Text(b, "ref") : null;
            var headSha = head.ValueKind == JsonValueKind.Object ? Text(head, "sha") : null;
            var headRef = head.ValueKind == JsonValueKind.Object ? Text(head, "ref") : null;

            if (fullName is null || cloneUrl is null || headSha is null || headRef is null || number <= 0)
            {
                log.LogWarning("A pull_request delivery ({DeliveryId}) was missing fields the build needs.", deliveryId);
                return null;
            }

            // A pull request whose head lives in another repository is a fork
            // pull request. Building one blindly would clone and compile a
            // stranger's code on the customer's own installation token, on a
            // machine holding that organisation's symbols - anybody on GitHub can
            // open such a pull request. So a fork is built only when GitHub says
            // its author is a member or owner of the organisation *and* the fork
            // is that person's own; the worker then asks GitHub the membership
            // question again with the installation token before anything is
            // cloned. See .design/github-integration-phase2.md (#627).
            var headRepository = head.ValueKind == JsonValueKind.Object
                && head.TryGetProperty("repo", out var repoElement)
                    ? repoElement : default;
            var headFullName = Text(headRepository, "full_name");
            var headOwner = headRepository.ValueKind == JsonValueKind.Object
                && headRepository.TryGetProperty("owner", out var ownerElement)
                    ? Text(ownerElement, "login") : null;
            var authorLogin = pullRequest.TryGetProperty("user", out var userElement)
                ? Text(userElement, "login") : null;
            var association = Text(pullRequest, "author_association");

            var isMemberFork = false;
            if (headFullName is null)
            {
                log.LogWarning(
                    "A pull_request delivery ({DeliveryId}) named no head repository; not built.", deliveryId);
                return null;
            }

            if (!string.Equals(headFullName, fullName, StringComparison.OrdinalIgnoreCase))
            {
                // GitHub's own verdict on who the author is to the repository.
                // MEMBER and OWNER are the two that mean "inside the
                // organisation"; CONTRIBUTOR, COLLABORATOR, FIRST_TIME_CONTRIBUTOR
                // and NONE are not, and a missing field is read as NONE.
                if (!MemberAssociations.Contains(association ?? string.Empty))
                {
                    log.LogInformation(
                        "A pull request on {Repository} ({DeliveryId}) comes from a fork ({HeadRepository}) opened by somebody who is not a member of the organisation; not built.",
                        fullName, deliveryId, headFullName);
                    return null;
                }

                // The fork has to be the author's own. A fork's owner can hand
                // push rights to anyone, so a member opening a pull request from
                // a third party's fork is still somebody else's code arriving
                // under a member's name.
                if (authorLogin is null
                    || headOwner is null
                    || !string.Equals(headOwner, authorLogin, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogInformation(
                        "A pull request on {Repository} ({DeliveryId}) comes from a fork owned by {ForkOwner}, not by its author {Author}; not built.",
                        fullName, deliveryId, headOwner ?? "unknown", authorLogin ?? "unknown");
                    return null;
                }

                isMemberFork = true;
                log.LogInformation(
                    "A pull request on {Repository} ({DeliveryId}) comes from the author's own fork and GitHub reports {Author} as a member of the organisation; the build worker confirms that with GitHub before cloning.",
                    fullName, deliveryId, authorLogin);
            }

            // The SHA and the branch name go on a git command line, so they are
            // checked against what git can name before they get there rather than
            // trusted because GitHub sent them.
            if (!HeadShaRegex.IsMatch(headSha) || !IsSafeRef(headRef))
            {
                log.LogWarning(
                    "A pull_request delivery ({DeliveryId}) named a commit or branch git could not be asked for.",
                    deliveryId);
                return null;
            }

            return new GitHubPullRequestJob(
                InstallationId: installationIdValue,
                RepositoryFullName: fullName,
                CloneUrl: cloneUrl,
                PullRequestNumber: number,
                HeadSha: headSha,
                HeadRef: headRef,
                BaseRef: baseRef ?? string.Empty,
                DeliveryId: deliveryId,
                AuthorLogin: authorLogin ?? string.Empty,
                IsMemberFork: isMemberFork,
                // Moves forward with every push to the pull request, so the queue can
                // tell which of two deliveries is newer whichever arrived first (#1120).
                UpdatedAt: Text(pullRequest, "updated_at") is { } updated
                           && DateTimeOffset.TryParse(updated, System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out var updatedAt)
                    ? updatedAt
                    : null);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "A GitHub webhook delivery ({DeliveryId}) carried a body that is not JSON.", deliveryId);
            return null;
        }
    }

    /// <summary>The prefix of a branch ref in a push payload. Anything else (tags, notes) is ignored.</summary>
    private const string BranchRefPrefix = "refs/heads/";

    /// <summary>How many of a push's commits are kept; GitHub itself lists at most twenty.</summary>
    internal const int KeptPushCommits = 10;

    /// <summary>A commit message or title is stored for a one-line summary, not as an archive.</summary>
    private const int MaxMessageLength = 500;

    /// <summary>
    /// Reads a <c>push</c> payload into a job, or <see langword="null"/> when it is
    /// not a branch push or is missing something. A tag push is not an error: it is
    /// simply not a branch moving, so it is answered 204 like any event we do not
    /// act on.
    /// </summary>
    internal static GitHubPushJob? TryReadPush(byte[] body, string deliveryId, ILogger log)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var reference = Text(root, "ref");
            if (reference is null || !reference.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
            {
                // refs/tags/..., or no ref at all.
                return null;
            }
            var branch = reference[BranchRefPrefix.Length..];

            if (!TryInstallationId(root, out var installationId))
            {
                log.LogWarning("A push delivery ({DeliveryId}) named no installation; nothing to act for.", deliveryId);
                return null;
            }

            if (!root.TryGetProperty("repository", out var repository)) return null;
            var fullName = Text(repository, "full_name");
            var cloneUrl = Text(repository, "clone_url");
            var defaultBranch = Text(repository, "default_branch") ?? Text(repository, "master_branch") ?? string.Empty;

            var deleted = Bool(root, "deleted");
            var forced = Bool(root, "forced");
            var before = Text(root, "before") ?? string.Empty;
            // A delete names the all-zero object as "after"; the commit the branch
            // last pointed at is "before", and that is the one worth keeping.
            var headSha = deleted ? before : Text(root, "after");

            if (fullName is null || cloneUrl is null || headSha is null)
            {
                log.LogWarning("A push delivery ({DeliveryId}) was missing fields a branch head needs.", deliveryId);
                return null;
            }

            // Stored and compared, and handed to git by a build on push (#1079),
            // so held to the same shape as everything else from a webhook: a full
            // object id, and a branch name the pipeline editor would also accept.
            if (!FullShaRegex.IsMatch(headSha) || !GitBranchName.IsValid(branch))
            {
                log.LogWarning(
                    "A push delivery ({DeliveryId}) named a commit or branch the workbench does not store.", deliveryId);
                return null;
            }

            var pusher = root.TryGetProperty("pusher", out var pusherElement) ? Text(pusherElement, "name") : null;
            if (pusher is null && root.TryGetProperty("sender", out var sender)) pusher = Text(sender, "login");

            var commits = new List<GitHubPushCommit>();
            if (root.TryGetProperty("commits", out var commitArray) && commitArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var commit in commitArray.EnumerateArray())
                {
                    var id = Text(commit, "id");
                    if (id is null || !FullShaRegex.IsMatch(id)) continue;
                    commits.Add(new GitHubPushCommit(id.ToLowerInvariant(), Clip(Text(commit, "message") ?? string.Empty)));
                }
            }

            // repository.pushed_at is a Unix timestamp on push deliveries (and an
            // ISO string on every other event); a payload without it keeps the
            // arrival time, which is seconds away.
            var pushedAt = repository.TryGetProperty("pushed_at", out var pushedElement)
                           && pushedElement.ValueKind == JsonValueKind.Number
                           && pushedElement.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : DateTime.UtcNow;

            return new GitHubPushJob(
                InstallationId: installationId,
                RepositoryFullName: fullName,
                CloneUrl: cloneUrl,
                DeliveryId: deliveryId,
                Branch: branch,
                HeadSha: headSha.ToLowerInvariant(),
                BeforeSha: before.ToLowerInvariant(),
                DefaultBranch: defaultBranch,
                PusherLogin: pusher ?? string.Empty,
                Forced: forced,
                Deleted: deleted,
                PushedAt: pushedAt,
                CommitCount: commits.Count,
                Commits: commits.TakeLast(KeptPushCommits).ToList());
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "A GitHub webhook delivery ({DeliveryId}) carried a body that is not JSON.", deliveryId);
            return null;
        }
    }

    /// <summary>
    /// Reads a <c>pull_request</c> payload whose action is <c>closed</c> with
    /// <c>merged: true</c>, or <see langword="null"/> for anything else. A pull
    /// request closed without merging is not recorded: nothing landed.
    ///
    /// <para>There is no fork check here, unlike the build path: nothing about a
    /// merged pull request is cloned or run, and whatever merged is in the
    /// repository now regardless of where it was written.</para>
    /// </summary>
    internal static GitHubMergedPullRequestJob? TryReadMergedPullRequest(byte[] body, string deliveryId, ILogger log)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!string.Equals(Text(root, "action"), "closed", StringComparison.OrdinalIgnoreCase)) return null;
            if (!root.TryGetProperty("pull_request", out var pullRequest) || !Bool(pullRequest, "merged")) return null;

            if (!TryInstallationId(root, out var installationId))
            {
                log.LogWarning("A merged pull_request delivery ({DeliveryId}) named no installation; nothing to act for.", deliveryId);
                return null;
            }
            if (!root.TryGetProperty("repository", out var repository)) return null;

            var fullName = Text(repository, "full_name");
            var cloneUrl = Text(repository, "clone_url");
            var number = pullRequest.TryGetProperty("number", out var n) && n.TryGetInt32(out var numberValue)
                ? numberValue : 0;
            var baseRef = pullRequest.TryGetProperty("base", out var b) ? Text(b, "ref") : null;
            var mergeSha = Text(pullRequest, "merge_commit_sha") ?? string.Empty;
            var author = pullRequest.TryGetProperty("user", out var user) ? Text(user, "login") : null;
            var mergedAt = Text(pullRequest, "merged_at") is { } stamp
                           && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed.UtcDateTime
                : DateTime.UtcNow;

            if (fullName is null || cloneUrl is null || baseRef is null || number <= 0 || !GitBranchName.IsValid(baseRef))
            {
                log.LogWarning("A merged pull_request delivery ({DeliveryId}) was missing fields the record needs.", deliveryId);
                return null;
            }
            if (!FullShaRegex.IsMatch(mergeSha)) mergeSha = string.Empty;

            return new GitHubMergedPullRequestJob(
                InstallationId: installationId,
                RepositoryFullName: fullName,
                CloneUrl: cloneUrl,
                DeliveryId: deliveryId,
                Number: number,
                Title: Clip(Text(pullRequest, "title") ?? string.Empty),
                BaseBranch: baseRef,
                MergeSha: mergeSha.ToLowerInvariant(),
                MergedAt: mergedAt,
                AuthorLogin: author ?? string.Empty);
        }
        catch (JsonException)
        {
            // Not logged here: the build path reads the same body next and says so once.
            return null;
        }
    }

    private static string Clip(string text) => text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;

    private static bool TryInstallationId(JsonElement root, out long installationId)
    {
        installationId = 0;
        return root.TryGetProperty("installation", out var installation)
               && installation.ValueKind == JsonValueKind.Object
               && installation.TryGetProperty("id", out var id)
               && id.TryGetInt64(out installationId);
    }

    private static bool Bool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>A full SHA-1 object id. A push names whole commits, never abbreviations.</summary>
    private static readonly System.Text.RegularExpressions.Regex FullShaRegex =
        new("^[0-9a-fA-F]{40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A git object name: hex, and between an abbreviated and a full SHA-1.</summary>
    private static readonly System.Text.RegularExpressions.Regex HeadShaRegex =
        new("^[0-9a-fA-F]{7,40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="headRef"/> is a branch name safe to hand to git.
    /// Deliberately narrower than git's own rules: anything outside
    /// <c>A-Z a-z 0-9 . _ / -</c>, and any name starting with a dash (which git
    /// would read as an option), is refused rather than escaped.
    /// </summary>
    private static bool IsSafeRef(string headRef) =>
        headRef.Length > 0
        && headRef[0] != '-'
        && headRef.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-');

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
