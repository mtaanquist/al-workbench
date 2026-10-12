using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Diff;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The anonymous read-side endpoint the standalone Diff tool hits from
/// <c>source-viewer.js</c> to re-diff its two editable panes as the user types.
/// It reuses the same DiffPlex + <see cref="SideBySideDiffSerializer"/> pipeline
/// the read-only Object Explorer compare page renders, so both surfaces stay
/// visually identical — and since #581 the same
/// <see cref="UnifiedDiffSerializer"/> too, so the tool can offer the inline
/// layout. The Object Explorer bakes its unified document into the page once;
/// here it has to ride every response, because the document is a function of
/// text the reader is still typing.
///
/// <para>The route keeps the <c>compare</c> spelling although the tool is now
/// displayed as Diff (#578): it is shared with the Object Explorer's diff
/// views, where "compare" is still the right word.</para>
///
/// <para>Stateless and side-effect-free (no DB, no auth), matching the tool's
/// account-free design — the SSR page owns no Blazor circuit, so the live diff
/// rides a plain fetch instead. See <c>Components/Pages/Diff.razor</c>.</para>
///
/// <para>Anonymous is the design, so the limits below are the only things
/// standing between a script and the container every signed-in user's circuit
/// lives in: per-side caps on characters and lines plus a budget on the
/// within-line word diffs, which together bound how long one diff can run
/// (DiffPlex takes no cancellation token, so the work has to be bounded up
/// front), an app-wide cap on diffs running at once
/// (<see cref="CompareDiffGate"/>), and the per-IP rate limit. See issue #673
/// for the rate limit and the first cap.</para>
/// </summary>
internal static class CompareEndpoints
{
    /// <summary>
    /// Per-side input cap for this endpoint: a million characters, which is
    /// several times the largest AL file in the base application. Deliberately
    /// NOT Piper's cap (<see cref="PiperTransform.MaxInputLength"/>, 50 MB):
    /// that one bounds a signed-in, circuit-bound transform, while this route
    /// is anonymous and runs a full diff plus six serialisations and a unified
    /// rebuild per request, so the two numbers answer different questions.
    /// See issue #673.
    ///
    /// <para>The character cap alone does not bound the work, and DiffPlex
    /// cannot be cancelled once started. Its line diff grows with the line
    /// count times the number of changed lines, which <see cref="MaxLines"/>
    /// bounds; then every pair of changed lines gets a word diff of its own
    /// costing roughly the square of the pair's length, which
    /// <see cref="WordDiffCostBudget"/> and <see cref="WordDiffWordBudget"/>
    /// bound across the whole request. Measured for the whole request (diff
    /// plus every serialisation) on DiffPlex 1.9, Release build, one core of a
    /// 4-core cloud VM: two wholly different 15,000-line texts about 1.1 s; a
    /// million characters per side of 15,000 changed lines about 1.1 s; a
    /// million characters per side in a few hundred long changed lines, or in
    /// one line, about 0.3 s. The slowest shapes are the line diff, which only
    /// <see cref="MaxLines"/> bounds, so a request should not exceed roughly
    /// 1.1 s on that machine, or about 1.5 s on one a third slower.</para>
    /// </summary>
    internal const int MaxInputLength = 1_000_000;

    /// <summary>
    /// Per-side line cap. Fifteen thousand lines is past the longest object a
    /// Business Central developer is likely to compare, and keeps the line
    /// diff of two wholly different texts near a second.
    /// </summary>
    internal const int MaxLines = 15_000;

    /// <summary>
    /// Time budget for the within-line word diffs of one request, in
    /// <see cref="BudgetedWordDiffer"/>'s units (the squared word count of each
    /// changed pair): about 0.2 s of one core. Past it, or past
    /// <see cref="WordDiffWordBudget"/>, the remaining changed lines are still
    /// shown as changed, only without the finer within-line highlighting.
    /// Charged only for lines that differ, so long identical lines (embedded
    /// images, minified data) cost nothing here.
    /// </summary>
    internal const long WordDiffCostBudget = 200_000_000;

    /// <summary>
    /// Size budget for the same word diffs: how many words, both sides
    /// together, are word-diffed per request. Each changed word becomes one
    /// highlighted range in the response, so this is what keeps a large text
    /// of changed words from turning into a response many times its size. A
    /// thousand rewritten lines of AL spend well under it.
    /// </summary>
    internal const long WordDiffWordBudget = 100_000;

    /// <summary>
    /// Kestrel's body cap for this route. It exists so an oversized body is
    /// refused before it is buffered and bound into two strings — the checks
    /// below can only run once the whole payload has already been read. It
    /// covers two full-length sides of plain text with room for JSON escaping
    /// and some non-ASCII, but not two full sides made wholly of 3-byte UTF-8
    /// characters or escapes; such a body is refused here instead of by the
    /// character check, which is the same outcome with a less specific message.
    /// A body past this never reaches the handler, so the reply is the
    /// framework's own refusal rather than the friendly JSON below — which is
    /// why <c>source-viewer.js</c> checks both sides against the cap before it
    /// sends anything, and reports any refused response in words instead of
    /// trying to read a diff out of it.
    /// </summary>
    private const int MaxRequestBodyBytes = 6_000_000;

    /// <summary>
    /// Per-IP rate-limit policy, registered in <c>OperationsRegistration</c>.
    /// The route is anonymous by design, so the caller's address is the only
    /// thing to partition on.
    /// </summary>
    public const string DiffRateLimitPolicy = "compare-diff";

    public static IEndpointRouteBuilder MapCompareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/compare/diff", async (CompareDiffRequest request, CompareDiffGate gate, CancellationToken cancellationToken) =>
        {
            var left = request.Left ?? string.Empty;
            var right = request.Right ?? string.Empty;

            var refusal = CheckSize(left) ?? CheckSize(right);
            if (refusal is not null)
            {
                return Results.Json(new { error = refusal }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!await gate.Semaphore.WaitAsync(CompareDiffGate.SlotWait, cancellationToken))
            {
                return Results.Json(
                    new { error = "The server is busy comparing other texts. Edit either side to retry in a moment." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            byte[] body;
            try
            {
                // The caller may have gone (a newer keystroke superseded this
                // request) while it waited for a slot; the diff itself cannot be
                // stopped once started, so this is the last place to skip it.
                cancellationToken.ThrowIfCancellationRequested();
                // Serialised inside the slot so the whole cost of the request,
                // not just the diff, is counted against the concurrency cap.
                body = JsonSerializer.SerializeToUtf8Bytes(
                    BuildResponse(left, right, request.IgnoreWhitespace ?? true, cancellationToken),
                    JsonSerializerOptions.Web);
            }
            finally
            {
                gate.Semaphore.Release();
            }
            return Results.Bytes(body, "application/json; charset=utf-8");
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .RequireRateLimiting(DiffRateLimitPolicy)
        // See MaxRequestBodyBytes: bigger than the framework default, small
        // enough that an oversized body is dropped at the socket instead of
        // being materialised as two strings first.
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBodyBytes));

        return app;
    }

    /// <summary>
    /// The reader-facing reason a side is too large to diff, or null when it
    /// is within every cap. Counts lines the way DiffPlex's line chunker
    /// splits them (<c>\r\n</c>, <c>\r</c> or <c>\n</c>), so the cap measures
    /// the lines the diff will actually see.
    /// </summary>
    internal static string? CheckSize(string text)
    {
        if (text.Length > MaxInputLength)
        {
            return $"One of the texts is too large ({text.Length:N0} characters). Trim it under {MaxInputLength:N0} and try again.";
        }

        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;

            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines++;
        }

        return lines > MaxLines
            ? $"One of the texts has too many lines ({lines:N0}). Trim it under {MaxLines:N0} lines and try again."
            : null;
    }

    private static object BuildResponse(string left, string right, bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        var model = new BudgetedWordDiffer(WordDiffCostBudget, WordDiffWordBudget).Diff(left, right, ignoreWhitespace);
        cancellationToken.ThrowIfCancellationRequested();

        var summary = SideBySideDiffSerializer.Summarize(model);
        // The inline layout's document, rebuilt on every request rather
        // than once at page load: on this tool the two panes ARE the
        // input, so the unified text is a function of what the user has
        // typed a keystroke ago. No declarations — the tool takes arbitrary
        // text, so there is no outline to name a hunk after.
        var unified = UnifiedDiffSerializer.Build(model);

        // Each Serialize* returns a JSON string; embed it as raw JSON (not a
        // re-encoded string) so the client receives real arrays.
        static JsonElement Raw(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        return new
        {
            left = new
            {
                diff = Raw(SideBySideDiffSerializer.SerializeSide(model.OldText)),
                fillers = Raw(SideBySideDiffSerializer.SerializeFillers(model.OldText)),
                wordDiff = Raw(SideBySideDiffSerializer.SerializeWordDiff(model.OldText)),
            },
            right = new
            {
                diff = Raw(SideBySideDiffSerializer.SerializeSide(model.NewText)),
                fillers = Raw(SideBySideDiffSerializer.SerializeFillers(model.NewText)),
                wordDiff = Raw(SideBySideDiffSerializer.SerializeWordDiff(model.NewText)),
            },
            inline = new
            {
                content = unified.Content,
                rows = Raw(unified.Rows),
                gutters = Raw(unified.Gutters),
                collapse = Raw(unified.Collapse),
                wordDiff = Raw(unified.WordDiff),
            },
            summary = new
            {
                added = summary.Added,
                removed = summary.Removed,
                modified = summary.Modified,
                identical = summary.Identical,
            },
        };
    }

    /// <summary>
    /// The two texts to diff. Both optional; empty means "no content yet".
    ///
    /// <para><see cref="IgnoreWhitespace"/> is nullable and treated as true
    /// when absent, which is what DiffPlex has always done for this endpoint —
    /// so a client that predates the toggle keeps the behaviour it was written
    /// against instead of silently starting to report every reindent.</para>
    /// </summary>
    public sealed record CompareDiffRequest(string? Left, string? Right, bool? IgnoreWhitespace = null);
}
