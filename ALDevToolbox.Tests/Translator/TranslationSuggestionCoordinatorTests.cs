using ALDevToolbox.Data;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Translator;

/// <summary>
/// The coordinator's job is to serialise every <see cref="TranslationMemoryService"/>
/// call behind one gate so a background prefetch can never touch the shared
/// circuit <c>DbContext</c> at the same time as an on-demand lookup, vote, or
/// remove. Regression for the "second operation started on this context" failure
/// when a "remove suggestion" click raced an in-flight prefetch.
/// </summary>
public sealed class TranslationSuggestionCoordinatorTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private TranslationMemoryService NewMemory(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, NullLogger<TranslationMemoryService>.Instance);

    private static TranslationMemoryUpsert Pair(string source, string target) =>
        new("en-US", "da-DK", source, target, "caption", "Base Application");

    private async Task<long> EntryIdAsync(string sourceText)
    {
        await using var read = _db.NewContext();
        return await read.TranslationMemory
            .Where(e => e.SourceText == sourceText).Select(e => e.Id).SingleAsync();
    }

    /// <summary>
    /// #1268 (L45): a lookup that finishes after the user switched language is
    /// cached under the language it was made in, so the new language never
    /// reads the old one's matches back.
    /// </summary>
    [Fact]
    public async Task The_cache_is_kept_per_language_pair()
    {
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[] { Pair("Posting Date", "Bogføringsdato") });

        await using var shared = _db.NewContext();
        using var coordinator = new TranslationSuggestionCoordinator(NewMemory(shared));

        // The Danish lookup lands after the switch to German cleared the caches.
        coordinator.ClearCaches();
        var danish = await coordinator.LookupAndCacheAsync("Posting Date", "en-US", "da-DK");
        danish.Should().ContainSingle();

        coordinator.TryGetCached("Posting Date", "en-US", "de-DE", out _).Should().BeFalse();
        coordinator.IsCached("Posting Date", "en-US", "de-DE").Should().BeFalse();
        coordinator.TryGetCached("Posting Date", "en-US", "da-DK", out var hits).Should().BeTrue();
        hits.Should().ContainSingle();

        var mt = new TranslationSuggestion(0, "Buchungsdatum", "DeepL", "caption", 0, 0, 0, IsMachineTranslation: true);
        coordinator.CacheMt("Posting Date", "en-US", "de-DE", mt);
        coordinator.TryGetCachedMt("Posting Date", "en-US", "da-DK", out _).Should().BeFalse();
        coordinator.TryGetCachedMt("Posting Date", "en-US", "de-DE", out _).Should().BeTrue();
    }

    /// <summary>
    /// #1268 (L43): learning after a repository save goes through the same gate
    /// as the lookups, so the two never overlap on the circuit's context.
    /// </summary>
    [Fact]
    public async Task Gate_serialises_learning_with_lookups_on_one_shared_context()
    {
        await using var shared = _db.NewContext();
        using var coordinator = new TranslationSuggestionCoordinator(NewMemory(shared));
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
              <file datatype="xml" source-language="en-US" target-language="da-DK" original="App">
                <body><group id="body">
                  <trans-unit id="u1"><source>Amount</source><target state="translated">Beløb</target></trans-unit>
                </group></body>
              </file>
            </xliff>
            """;

        var work = new List<Task>();
        for (var i = 0; i < 6; i++)
            work.Add(coordinator.LookupGatedAsync("Amount", "en-US", "da-DK"));
        work.Add(coordinator.LearnGatedAsync(xml, "App.da-DK.xlf", ["u1"]));

        var act = async () => await Task.WhenAll(work);
        await act.Should().NotThrowAsync();

        await using var read = _db.NewContext();
        (await NewMemory(read).SuggestAsync("Amount", "en-US", "da-DK")).Should().ContainSingle();
    }

    /// <summary>
    /// #1324: the Translator's repository save names where it wrote, and that
    /// is what keeps a Private solution's strings with its own team.
    /// </summary>
    [Fact]
    public async Task Learning_after_a_repository_save_records_the_repository_and_path()
    {
        await using var shared = _db.NewContext();
        using var coordinator = new TranslationSuggestionCoordinator(NewMemory(shared));
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
              <file datatype="xml" source-language="en-US" target-language="da-DK" original="App">
                <body><group id="body">
                  <trans-unit id="u1"><source>Amount</source><target state="translated">Beløb</target></trans-unit>
                </group></body>
              </file>
            </xliff>
            """;

        await coordinator.LearnGatedAsync(xml, "App.da-DK.xlf", ["u1"],
            "cronus-dk/customer-app", "App/Translations/App.da-DK.xlf");

        await using var read = _db.NewContext();
        var row = await read.TranslationMemory.SingleAsync(e => e.SourceText == "Amount");
        row.SourceRepository.Should().Be("cronus-dk/customer-app");
        row.SourcePath.Should().Be("App/Translations/App.da-DK.xlf");
    }

    [Fact]
    public async Task Gate_serialises_lookup_and_delete_on_one_shared_context()
    {
        // Seed a handful of entries to delete while lookups run.
        await using (var ctx = _db.NewContext())
            await NewMemory(ctx).UpsertAsync(new[]
            {
                Pair("Posting Date", "Bogføringsdato"),
                Pair("Quantity", "Antal"),
                Pair("Customer", "Kunde"),
                Pair("Vendor", "Leverandør"),
            });

        var toDelete = new[]
        {
            await EntryIdAsync("Quantity"),
            await EntryIdAsync("Customer"),
            await EntryIdAsync("Vendor"),
        };

        // One context shared across the coordinator, exactly like a Blazor Server
        // circuit. Without the gate, the concurrent lookups and deletes below
        // reliably throw "A second operation was started on this context".
        await using var shared = _db.NewContext();
        var coordinator = new TranslationSuggestionCoordinator(NewMemory(shared));

        var work = new List<Task>();
        for (var i = 0; i < 6; i++)
            work.Add(coordinator.LookupGatedAsync("Posting Date", "en-US", "da-DK"));
        foreach (var id in toDelete)
            work.Add(coordinator.DeleteGatedAsync(id));

        // The whole point: no InvalidOperationException from overlapping ops.
        var act = async () => await Task.WhenAll(work);
        await act.Should().NotThrowAsync();

        coordinator.Dispose();

        await using (var ctx = _db.NewContext())
            (await NewMemory(ctx).SuggestAsync("Quantity", "en-US", "da-DK")).Should().BeEmpty();
    }
}
