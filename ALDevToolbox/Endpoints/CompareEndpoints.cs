using System.Text.Json;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Diff;
using DiffPlex.DiffBuilder;

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
/// lives in: per-side caps on characters, lines and line length that bound how
/// long one diff can run (DiffPlex takes no cancellation token, so the input
/// is the only budget there is), an app-wide cap on diffs running at once, and
/// the per-IP rate limit. See issue #673 for the rate limit and the first
/// cap.</para>
/// </summary>
internal static class CompareEndpoints
{
    /// <summary>
    /// Per-side input cap for this endpoint: a million characters, which is
    /// several times the largest AL file in the base application and far
    /// below what the diff below can take its time over. Deliberately NOT
    /// Piper's cap (<see cref="PiperTransform.MaxInputLength"/>, 50 MB): that
    /// one bounds a signed-in, circuit-bound transform, while this route is
    /// anonymous and runs a full diff plus six serialisations and a unified
    /// rebuild per request, so the two numbers answer different questions.
    /// See issue #673.
    ///
    /// <para>The character cap alone does not bound the work. DiffPlex's line
    /// diff is O(N*D) in lines, and every changed pair of lines gets a word
    /// diff of its own that is O(N*D) in words, and none of it can be
    /// cancelled once started. So <see cref="MaxLines"/> bounds the first and
    /// <see cref="MaxLineLength"/> the second; with all three at their limits
    /// and every line different, one request measures under two seconds of a
    /// single core on DiffPlex 1.9.</para>
    /// </summary>
    internal const int MaxInputLength = 1_000_000;

    /// <summary>
    /// Per-side line cap. Twenty thousand lines is past the longest object a
    /// Business Central developer is likely to compare, and keeps the line
    /// diff of two wholly different texts near a second and a half.
    /// </summary>
    internal const int MaxLines = 20_000;

    /// <summary>
    /// Longest single line either side may carry. AL code does not get near
    /// it; the cap is there because a changed line is word-diffed against its
    /// counterpart, and two very long changed lines cost the square of their
    /// length.
    /// </summary>
    internal const int MaxLineLength = 4_000;

    /// <summary>
    /// Kestrel's body cap for this route, sized at two full-length sides of
    /// multi-byte text plus JSON overhead. It exists so an oversized body is
    /// refused before it is buffered and bound into two strings — the checks
    /// below can only run once the whole payload has already been read. A body
    /// past this never reaches the handler, so the reply is the framework's own
    /// refusal rather than the friendly JSON below — which is why
    /// <c>source-viewer.js</c> checks both sides against the cap before it
    /// sends anything, and reports any refused response in words instead of
    /// trying to read a diff out of it.
    /// </summary>
    private const int MaxRequestBodyBytes = 6_000_000;

    /// <summary>
    /// How many diffs the whole app runs at once, whoever asks. The per-IP
    /// rate limit meters each caller, but a diff cannot be cancelled once it
    /// starts, so this is what keeps enough cores free for everyone's pages
    /// and the background workers however many callers there are.
    /// </summary>
    internal static readonly int MaxConcurrentDiffs = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// How long a request waits for one of the <see cref="MaxConcurrentDiffs"/>
    /// slots before it is answered 503. Short, because the Diff tool sends a
    /// fresh request on the next edit and a late diff has usually been
    /// superseded by then.
    /// </summary>
    internal static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);

    /// <summary>The slots behind <see cref="MaxConcurrentDiffs"/>; internal so a test can hold them.</summary>
    internal static readonly SemaphoreSlim DiffSlots = new(MaxConcurrentDiffs, MaxConcurrentDiffs);

    /// <summary>
    /// Per-IP rate-limit policy, registered in <c>OperationsRegistration</c>.
    /// The route is anonymous by design, so the caller's address is the only
    /// thing to partition on.
    /// </summary>
    public const string DiffRateLimitPolicy = "compare-diff";

    public static IEndpointRouteBuilder MapCompareEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/compare/diff", async (CompareDiffRequest request, CancellationToken cancellationToken) =>
        {
            var left = request.Left ?? string.Empty;
            var right = request.Right ?? string.Empty;

            var refusal = CheckSize(left) ?? CheckSize(right);
            if (refusal is not null)
            {
                return Results.Json(new { error = refusal }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!await DiffSlots.WaitAsync(SlotWait, cancellationToken))
            {
                return Results.Json(
                    new { error = "The server is busy comparing other texts. Edit either side to retry in a moment." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                // The caller may have gone (a newer keystroke superseded this
                // request) while it waited for a slot; the diff itself cannot be
                // stopped once started, so this is the last place to skip it.
                cancellationToken.ThrowIfCancellationRequested();
                return Results.Json(BuildResponse(left, right, request.IgnoreWhitespace ?? true, cancellationToken));
            }
            finally
            {
                DiffSlots.Release();
            }
        })
        .AllowAnonymous()
        .DisableAntiforgery()
        .RequireRateLimiting(DiffRateLimitPolicy)
        // Two full-length sides plus JSON overhead. Bigger than the framework
        // default, small enough that a hostile body is dropped at the socket
        // instead of being materialised as two strings first.
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
        var lineStart = 0;
        var longest = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;

            longest = Math.Max(longest, i - lineStart);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lineStart = i + 1;
            lines++;
        }
        longest = Math.Max(longest, text.Length - lineStart);

        if (lines > MaxLines)
        {
            return $"One of the texts has too many lines ({lines:N0}). Trim it under {MaxLines:N0} lines and try again.";
        }
        if (longest > MaxLineLength)
        {
            return $"One of the texts has a line that is too long ({longest:N0} characters). Break it into lines under {MaxLineLength:N0} characters and try again.";
        }
        return null;
    }

    private static object BuildResponse(string left, string right, bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        var model = SideBySideDiffBuilder.Diff(left, right, ignoreWhitespace);
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
