using System.Text.Json;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// <c>push</c> and <c>pull_request</c> bodies in the shape GitHub sends them,
/// trimmed to the fields a delivery carries that matter here plus a few that do
/// not (so a parser that trips over an unexpected field shows up). Used by the
/// endpoint tests and the branch-watching tests, which replay them (#963).
/// </summary>
internal static class GitHubWebhookPayloads
{
    public const string Before = "1111111111111111111111111111111111111111";
    public const string After = "2222222222222222222222222222222222222222";
    public const string MergeSha = "3333333333333333333333333333333333333333";
    public const string Zero = "0000000000000000000000000000000000000000";

    /// <summary>A full SHA derived from <paramref name="n"/>, for commit lists.</summary>
    public static string Sha(int n) => n.ToString("x").PadLeft(40, 'a');

    /// <param name="commitCount">How many commits the push lists; the last one's id is <paramref name="after"/>.</param>
    public static string Push(
        string branch = "main",
        string? reference = null,
        string before = Before,
        string after = After,
        bool forced = false,
        bool deleted = false,
        bool created = false,
        int commitCount = 1,
        long installationId = 42,
        string cloneUrl = "https://github.com/cronus-dk/customer-app.git",
        string defaultBranch = "main",
        long pushedAt = 1_790_000_000,
        int filesPerCommit = 1,
        string[]? paths = null)
    {
        var commits = Enumerable.Range(1, deleted ? 0 : commitCount)
            .Select(i => new
            {
                id = i == commitCount ? after : Sha(i),
                message = $"Commit {i} on {branch}\n\nWith a body that is not the summary.",
                timestamp = "2026-09-24T09:00:00+02:00",
                author = new { name = "Erik", email = "erik@cronus.example", username = "erik" },
                added = Array.Empty<string>(),
                removed = Array.Empty<string>(),
                modified = paths ?? (filesPerCommit == 1
                    ? new[] { "app/src/Vat.Codeunit.al" }
                    : Enumerable.Range(1, filesPerCommit).Select(f => $"app/src/Generated/Object{f:D5}.Table.al").ToArray()),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            @ref = reference ?? "refs/heads/" + branch,
            before = before,
            after = deleted ? Zero : after,
            created,
            deleted,
            forced,
            base_ref = (string?)null,
            compare = "https://github.com/cronus-dk/customer-app/compare/1111...2222",
            commits,
            head_commit = deleted ? null : commits.LastOrDefault(),
            repository = new
            {
                id = 123,
                name = "customer-app",
                full_name = "cronus-dk/customer-app",
                clone_url = cloneUrl,
                default_branch = defaultBranch,
                master_branch = defaultBranch,
                pushed_at = pushedAt,
                created_at = 1_700_000_000,
            },
            pusher = new { name = "erik", email = "erik@cronus.example" },
            sender = new { login = "erik", id = 7 },
            installation = new { id = installationId, node_id = "MDIz" },
        });
    }

    public static string MergedPullRequest(
        int number = 12,
        bool merged = true,
        string baseBranch = "main",
        string mergedAt = "2026-09-24T08:30:00Z",
        string mergeSha = MergeSha,
        long installationId = 42,
        string cloneUrl = "https://github.com/cronus-dk/customer-app.git") =>
        JsonSerializer.Serialize(new
        {
            action = "closed",
            number,
            pull_request = new
            {
                number,
                state = "closed",
                title = "Post VAT to the right account",
                user = new { login = "erik" },
                author_association = "MEMBER",
                merged,
                merged_at = merged ? mergedAt : null,
                merge_commit_sha = mergeSha,
                head = new { @ref = "feature/vat", sha = After, repo = new { full_name = "cronus-dk/customer-app" } },
                @base = new { @ref = baseBranch, sha = Before },
            },
            repository = new { full_name = "cronus-dk/customer-app", clone_url = cloneUrl, default_branch = "main" },
            installation = new { id = installationId },
        });
}
