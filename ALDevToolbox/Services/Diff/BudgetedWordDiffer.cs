using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using DiffPlex.Model;

namespace ALDevToolbox.Services.Diff;

/// <summary>
/// An <see cref="IDiffer"/> for <see cref="SideBySideDiffBuilder"/> that puts
/// a ceiling on the word-level half of a diff, for the anonymous Diff tool
/// endpoint (<c>Endpoints/CompareEndpoints.cs</c>).
///
/// <para>The builder diffs the two texts line by line first, then word-diffs
/// every pair of changed lines it lined up. That second step costs roughly the
/// square of the pair's combined word count, so a few hundred long changed
/// lines (base64 image data, minified JSON, punctuation runs) can take far
/// longer than the whole line diff, and none of it can be cancelled. Its output
/// is also one highlighted range per changed word, so a large text of changed
/// words turns into a response many times its own size.</para>
///
/// <para>So each pair is charged against two budgets before it is word-diffed:
/// <c>(old words + new words)^2</c> against the time budget and
/// <c>old words + new words</c> against the size budget. A pair that does not
/// fit either is answered as one whole-line replacement: the line is still
/// reported and highlighted as changed, only without the finer within-line
/// highlighting. Identical lines are never charged, however long, because the
/// builder only word-diffs pairs that differ.</para>
///
/// <para>One instance per diff: the budgets are spent as it goes.</para>
/// </summary>
public sealed class BudgetedWordDiffer : IDiffer
{
    private readonly IDiffer _inner = Differ.Instance;
    private long _remainingCost;
    private long _remainingWords;

    /// <param name="costBudget">Sum of <c>(old words + new words)^2</c> over the pairs word-diffed.</param>
    /// <param name="wordBudget">Sum of <c>old words + new words</c> over the pairs word-diffed.</param>
    public BudgetedWordDiffer(long costBudget, long wordBudget)
    {
        _remainingCost = costBudget;
        _remainingWords = wordBudget;
    }

    /// <summary>How many changed line pairs were reported without a word diff because a budget ran out.</summary>
    public int SkippedPairs { get; private set; }

    /// <summary>Side-by-side diff of two texts under this differ's word-diff budgets.</summary>
    public SideBySideDiffModel Diff(string oldText, string newText, bool ignoreWhitespace) =>
        SideBySideDiffBuilder.Diff(this, oldText, newText, ignoreWhitespace, false,
            LineChunker.Instance, WordChunker.Instance);

    public DiffResult CreateDiffs(string oldText, string newText, bool ignoreWhiteSpace, bool ignoreCase, IChunker chunker)
    {
        // The line-level pass is not charged: its cost is bounded by the
        // endpoint's line cap, and the word pairs only exist once it has run.
        if (ReferenceEquals(chunker, LineChunker.Instance))
        {
            return _inner.CreateDiffs(oldText, newText, ignoreWhiteSpace, ignoreCase, chunker);
        }

        // Counting the words is linear, and the line cap and character cap
        // bound it across the whole request.
        long size = chunker.Chunk(oldText).Count + chunker.Chunk(newText).Count;
        if (size <= _remainingWords && size * size <= _remainingCost)
        {
            _remainingWords -= size;
            _remainingCost -= size * size;
            return _inner.CreateDiffs(oldText, newText, ignoreWhiteSpace, ignoreCase, chunker);
        }

        // Over budget: the whole line, as one piece, replaced by the other.
        // Returning no blocks instead would make the builder mark the line
        // unchanged.
        SkippedPairs++;
        return new DiffResult(new[] { oldText }, new[] { newText },
            new List<DiffBlock> { new(0, 1, 0, 1) });
    }

    // The rest of IDiffer is the older string-shaped API the builder never
    // calls; pass it through unchanged so the type is a complete differ.
#pragma warning disable CS0618 // obsolete members of the interface being implemented
    public DiffResult CreateLineDiffs(string oldText, string newText, bool ignoreWhitespace) =>
        _inner.CreateLineDiffs(oldText, newText, ignoreWhitespace);

    public DiffResult CreateLineDiffs(string oldText, string newText, bool ignoreWhitespace, bool ignoreCase) =>
        _inner.CreateLineDiffs(oldText, newText, ignoreWhitespace, ignoreCase);

    public DiffResult CreateCharacterDiffs(string oldText, string newText, bool ignoreWhitespace) =>
        _inner.CreateCharacterDiffs(oldText, newText, ignoreWhitespace);

    public DiffResult CreateCharacterDiffs(string oldText, string newText, bool ignoreWhitespace, bool ignoreCase) =>
        _inner.CreateCharacterDiffs(oldText, newText, ignoreWhitespace, ignoreCase);

    public DiffResult CreateWordDiffs(string oldText, string newText, bool ignoreWhitespace, char[] separators) =>
        _inner.CreateWordDiffs(oldText, newText, ignoreWhitespace, separators);

    public DiffResult CreateWordDiffs(string oldText, string newText, bool ignoreWhitespace, bool ignoreCase, char[] separators) =>
        _inner.CreateWordDiffs(oldText, newText, ignoreWhitespace, ignoreCase, separators);

    public DiffResult CreateCustomDiffs(string oldText, string newText, bool ignoreWhiteSpace, Func<string, string[]> chunker) =>
        _inner.CreateCustomDiffs(oldText, newText, ignoreWhiteSpace, chunker);

    public DiffResult CreateCustomDiffs(string oldText, string newText, bool ignoreWhiteSpace, bool ignoreCase, Func<string, string[]> chunker) =>
        _inner.CreateCustomDiffs(oldText, newText, ignoreWhiteSpace, ignoreCase, chunker);
#pragma warning restore CS0618
}
