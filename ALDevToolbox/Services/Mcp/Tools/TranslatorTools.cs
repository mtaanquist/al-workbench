using System.ComponentModel;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Translation;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP surface for the Translator's cross-source translation memory — the same
/// curation an editor does in the web UI, so agents can keep the memory clean:
/// search it, vote good/bad pairs (which re-ranks suggestions), and remove bad
/// ones. All reads are org-scoped by the EF query filter; <c>remove</c> is
/// gated to the Editor / Admin role like the web page. See the Translator
/// curation feature and the "keep MCP parity" rule in <c>CLAUDE.md</c>.
/// </summary>
[McpServerToolType]
public sealed class TranslatorTools
{
    private readonly TranslationMemoryService _memory;
    private readonly MachineTranslationService _machineTranslation;
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    public TranslatorTools(
        TranslationMemoryService memory,
        MachineTranslationService machineTranslation,
        AppDbContext db,
        IOrganizationContext orgContext)
    {
        _memory = memory;
        _machineTranslation = machineTranslation;
        _db = db;
        _orgContext = orgContext;
    }

    [McpServerTool(Name = "search_translation_memory", ReadOnly = true)]
    [Description("Searches the organisation's translation memory — the accumulated source→target pairs that back the Translator's suggestions. Returns each entry's id (for vote_translation / remove_translation), languages, source + target text, kind, provenance (origin), net vote score and hit count, ranked by score then frequency. A pair learned from one of the organisation's own GitHub repositories also names that repository and the file inside it, so you can go and read the translation in context.")]
    public async Task<IReadOnlyList<TranslationMemoryHit>> SearchTranslationMemoryAsync(
        [Description("Free-text substring matched against source OR target text. Null/empty returns the top-ranked entries.")] string? query = null,
        [Description("Optional source-language filter (BCP-47, e.g. 'en-US').")] string? sourceLanguage = null,
        [Description("Optional target-language filter (BCP-47, e.g. 'da-DK').")] string? targetLanguage = null,
        [Description("Optional kind filter: caption, tooltip, label, option, instructional, other.")] string? kind = null,
        [Description("Optional substring match on the provenance/origin (e.g. 'Base Application').")] string? origin = null,
        [Description("Include soft-removed entries (default false).")] bool includeRemoved = false,
        [Description("Max results (1-50, default 25). Raise it only when the entry you need is not in the first 25.")] int maxResults = 25,
        CancellationToken ct = default)
    {
        var res = await _memory.SearchAsync(new MemorySearchQuery(
            query, sourceLanguage, targetLanguage,
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            origin, includeRemoved, 0, Math.Clamp(maxResults, 1, 50), CountTotal: false), ct);
        return res.Items.Select(e => new TranslationMemoryHit(
            e.Id, e.SourceLanguage, e.TargetLanguage, e.SourceText, e.TargetText,
            e.Kind, e.Origin, e.Score, e.HitCount, e.IsDeleted,
            e.SourceRepository, e.SourcePath)).ToList();
    }

    /// <summary>The longest <c>sourceText</c> <c>machine_translate</c> sends to the paid provider.</summary>
    internal const int MaxMachineTranslateSource = 5_000;

    /// <summary>The longest <c>context</c> hint <c>machine_translate</c> passes along.</summary>
    internal const int MaxMachineTranslateContext = 1_000;

    [McpServerTool(Name = "machine_translate", ReadOnly = true)]
    [Description("Translates a string with the organisation's configured third-party machine-translation provider (e.g. DeepL), if one is set up. Use it to draft a target for a source string that has no good translation-memory match. Pass the developer note or field context to improve domain accuracy. Returns the translated text, the provider name, and the detected source language. Errors if the org hasn't configured machine translation, or if the provider refuses or fails the request.")]
    public async Task<MachineTranslationResult> MachineTranslateAsync(
        [Description("The source text to translate: one string, at most 5,000 characters.")] string sourceText,
        [Description("Target language (BCP-47, e.g. 'da-DK').")] string targetLanguage,
        [Description("Optional source language (BCP-47, e.g. 'en-US'). Omit to let the provider auto-detect.")] string? sourceLanguage = null,
        [Description("Optional context hint (developer note / field caption) to disambiguate domain terms, at most 1,000 characters.")] string? context = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new McpException("sourceText is required.");
        }
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new McpException("targetLanguage is required.");
        }
        // The provider bills the organisation per character, and the Translator page
        // only ever sends one string. Without a ceiling, an assistant told to "translate
        // this document" spends the quota by the megabyte (#1576).
        if (sourceText.Length > MaxMachineTranslateSource)
        {
            throw new McpException(
                $"sourceText is {sourceText.Length} characters; the limit is {MaxMachineTranslateSource}. "
                + "Send one string at a time, as the Translator does.");
        }
        if (context is { Length: > MaxMachineTranslateContext })
        {
            throw new McpException(
                $"context is {context.Length} characters; the limit is {MaxMachineTranslateContext}. "
                + "Pass a short developer note or field caption.");
        }

        // The service answers null both when nothing is set up and when the provider
        // call failed. Telling those apart here keeps a used-up quota or a refused
        // language from sending the agent (and the admin) to the settings page (#1344).
        if (await _machineTranslation.GetTriggerAsync(ct) == MtTrigger.Off)
        {
            throw new McpException("Machine translation is not configured for this organisation. An admin can enable it under Administration → Translation.");
        }
        var result = await _machineTranslation.TranslateAsync(sourceText, sourceLanguage, targetLanguage, context, ct);
        if (result is null)
        {
            throw new McpException("The translation provider refused or failed to translate this text. Try again later; if it keeps failing, check that the provider supports the language.");
        }
        return new MachineTranslationResult(result.TargetText, result.ProviderName, result.DetectedSourceLanguage);
    }

    [McpServerTool(Name = "vote_translation", ReadOnly = false, Idempotent = true)]
    [Description("Casts your up/down vote on a translation memory entry to steer suggestion ranking (a good pair floats above a more-frequent but worse one). One vote per user; voting the same direction again or 'clear' removes it. Returns the entry's new net score and your resulting vote.")]
    public async Task<TranslationVoteResult> VoteTranslationAsync(
        [Description("The memory entry id from search_translation_memory.")] long entryId,
        [Description("Vote direction: 'up', 'down', or 'clear' to remove your vote.")] string direction,
        CancellationToken ct = default)
    {
        var dir = (direction?.Trim().ToLowerInvariant()) switch
        {
            "up" => 1,
            "down" => -1,
            "clear" or "none" or "" or null => 0,
            _ => throw new McpException("direction must be 'up', 'down', or 'clear'."),
        };
        try
        {
            var r = await _memory.VoteAsync(entryId, dir, ct);
            return new TranslationVoteResult(r.EntryId, r.Score, r.MyVote);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "remove_translation", ReadOnly = false, Idempotent = true)]
    [Description("Soft-removes a bad translation memory entry so it stops being suggested. Requires the Editor or Admin role. Recoverable from the web admin page (Translation memory · 'Include removed').")]
    public async Task<TranslationRemoveResult> RemoveTranslationAsync(
        [Description("The memory entry id from search_translation_memory.")] long entryId,
        CancellationToken ct = default)
    {
        await RequireEditorAsync(ct);
        try
        {
            await _memory.DeleteAsync(entryId, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
        return new TranslationRemoveResult(entryId, true);
    }

    /// <summary>
    /// Throws unless the acting MCP user is Editor / Admin (or a SiteAdmin).
    /// Removing affects the whole org, so it mirrors the web page's role gate.
    /// </summary>
    private async Task RequireEditorAsync(CancellationToken ct)
    {
        var userId = _orgContext.CurrentUserId
            ?? throw new McpException("No user in scope; call this from an authenticated MCP session.");
        var role = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (UserRole?)u.Role)
            .FirstOrDefaultAsync(ct);
        if (!_orgContext.IsSiteAdmin && role is not (UserRole.Editor or UserRole.Admin))
        {
            throw new McpException("Removing translation memory entries requires the Editor or Admin role.");
        }
    }
}

/// <summary>
/// One translation memory entry as returned by <c>search_translation_memory</c>.
/// <c>SourceRepository</c> (<c>owner/name</c>) and <c>SourcePath</c> are set for
/// pairs learned from a file in one of the organisation's own repositories, and
/// null for everything else.
/// </summary>
public sealed record TranslationMemoryHit(
    long EntryId,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TargetText,
    string Kind,
    string? Origin,
    int Score,
    int HitCount,
    bool Removed,
    string? SourceRepository = null,
    string? SourcePath = null);

/// <summary>Result of <c>machine_translate</c>.</summary>
public sealed record MachineTranslationResult(string TargetText, string Provider, string? DetectedSourceLanguage);

/// <summary>Result of <c>vote_translation</c>.</summary>
public sealed record TranslationVoteResult(long EntryId, int Score, int MyVote);

/// <summary>Result of <c>remove_translation</c>.</summary>
public sealed record TranslationRemoveResult(long EntryId, bool Removed);
