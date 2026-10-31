using TranslatorPage = ALDevToolbox.Components.Pages.Translator;
using ALDevToolbox.Services.Translation;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// "Save to repository" leaves the editor live while GitHub answers, so a
/// translator can type into more units during the save. Those units used to be
/// marked saved along with the ones that were actually committed, so the next
/// save left them out and closing the tab gave no warning (#1249).
/// </summary>
public sealed class TranslatorRepositorySaveTests
{
    [Fact]
    public void Only_units_unchanged_since_the_commit_was_built_count_as_saved()
    {
        var committedAsIs = Unit("a", "Beloeb", "translated");
        var editedAgain = Unit("b", "Bogfoert", "translated");
        var typedDuringSave = Unit("c", "Dato", "translated");
        var committed = new Dictionary<string, TargetEdit>(StringComparer.Ordinal)
        {
            ["a"] = new("Beloeb", "translated"),
            ["b"] = new("Bogf", "translated"),
        };

        TranslatorPage.MarkCommitted([committedAsIs, editedAgain, typedDuringSave], committed);

        committedAsIs.Dirty.Should().BeFalse("its text is in the commit");
        editedAgain.Dirty.Should().BeTrue("the commit carries the text it had before the last keystrokes");
        typedDuringSave.Dirty.Should().BeTrue("it was not in the commit at all");
    }

    [Fact]
    public void A_state_change_during_the_save_keeps_the_unit_unsaved()
    {
        var unit = Unit("a", "Beloeb", "final");

        TranslatorPage.MarkCommitted([unit], new Dictionary<string, TargetEdit>
        {
            ["a"] = new("Beloeb", "translated"),
        });

        unit.Dirty.Should().BeTrue();
    }

    /// <summary>
    /// Saving in place and exporting keep every edit in what they send, so the
    /// memory is told only about the ones whose text changed since it last
    /// learned them - otherwise each save added another hit to every unit touched
    /// since the file was opened (#1343).
    /// </summary>
    [Fact]
    public void Only_edits_with_new_text_since_the_last_save_are_learned()
    {
        var edits = new Dictionary<string, TargetEdit>(StringComparer.Ordinal)
        {
            ["a"] = new("Beloeb", "translated"),
            ["b"] = new("Bogfoeringsdato", "translated"),
            ["c"] = new("Dato", "translated"),
            ["d"] = new("", "needs-translation"),
        };
        var learned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a"] = "Beloeb",
            ["b"] = "Bogfoert",
        };

        var learn = TranslatorPage.UnitsToLearn(edits, learned);

        learn.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["b"] = "Bogfoeringsdato",
            ["c"] = "Dato",
        }, "a is unchanged since it was learned, and d has no text to learn");
    }

    /// <summary>
    /// #1540: "Export a copy" of a file opened from a repository used to teach the
    /// memory its strings with no repository attached, so a Private solution's
    /// translations were suggested to everyone in the organisation. A file from a
    /// repository is learned only by Save to repository, which names it.
    /// </summary>
    [Fact]
    public void An_export_of_a_file_opened_from_a_repository_teaches_the_memory_nothing()
    {
        var edits = new Dictionary<string, TargetEdit>(StringComparer.Ordinal)
        {
            ["a"] = new("Beloeb", "translated"),
            ["b"] = new("Bogfoeringsdato", "translated"),
        };

        var learn = TranslatorPage.ExportLearning(edits, new Dictionary<string, string>(), openedFromRepository: true);

        learn.Should().BeEmpty();
    }

    [Fact]
    public void An_export_of_a_file_from_elsewhere_teaches_the_memory_its_new_edits()
    {
        var edits = new Dictionary<string, TargetEdit>(StringComparer.Ordinal)
        {
            ["a"] = new("Beloeb", "translated"),
            ["b"] = new("Bogfoeringsdato", "translated"),
        };
        var learned = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "Beloeb" };

        var learn = TranslatorPage.ExportLearning(edits, learned, openedFromRepository: false);

        learn.Should().BeEquivalentTo(new Dictionary<string, string> { ["b"] = "Bogfoeringsdato" });
    }

    /// <summary>
    /// #1540, after a refresh: a restore does not reconnect to the repository, so
    /// the browser's session record has to say where the file came from, or the
    /// restored export would teach the memory unattributed pairs after all. This
    /// reads the record the way the page does, from the shape loadMeta returns.
    /// </summary>
    [Fact]
    public void A_restored_session_opened_from_a_repository_still_teaches_the_memory_nothing()
    {
        var meta = RestoredMeta("""
            {"fileName":"App.da-DK.xlf","sourceLang":"en-US","targetLang":"da-DK",
             "edits":{"a":{"t":"Beloeb","s":"translated"}},"learned":null,
             "repo":{"repository":"cronus/private-app","path":"App/Translations/App.da-DK.xlf"}}
            """);

        TranslatorPage.RestoredFromRepository(meta).Should().BeTrue();
        TranslatorPage.ExportLearning(
                new Dictionary<string, TargetEdit> { ["a"] = new("Beloeb", "translated") },
                new Dictionary<string, string>(),
                openedFromRepository: TranslatorPage.RestoredFromRepository(meta))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"fileName":"App.da-DK.xlf","edits":{},"repo":null}""")]
    [InlineData("""{"fileName":"App.da-DK.xlf","edits":{}}""")] // saved before the origin was recorded
    public void A_restored_session_of_a_file_from_elsewhere_is_not_tied_to_a_repository(string json) =>
        TranslatorPage.RestoredFromRepository(RestoredMeta(json)).Should().BeFalse();

    /// <summary>
    /// The browser half of the restore: the record keeps the origin a file was
    /// opened with and hands it back with the rest of the session.
    /// </summary>
    [Fact]
    public void The_browser_session_record_keeps_and_returns_the_repository_origin()
    {
        var js = File.ReadAllText(ALDevToolbox.Tests.Infrastructure.RepoRoot.Combine(
            "ALDevToolbox", "Components", "Pages", "Translator.razor.js"));

        js.Should().Contain("putBaseStream(fileName, sourceLang, targetLang, streamRef, repo, replaceOpenId)")
            .And.Contain("repo: repo || null")
            .And.Contain("repo: rec.repo || null")
            // An overlay is only ever written onto the tab's own record, read
            // and written in one transaction (#1574, #1609).
            .And.Contain("export async function putEdits(openId, editsJson, learnedJson)")
            .And.Contain("await updateRec(openId, (rec) => {");
    }

    // Blazor's JS interop deserialises with the web defaults.
    private static TranslatorPage.SessionMeta RestoredMeta(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<TranslatorPage.SessionMeta>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    private static TranslatorPage.UnitVm Unit(string id, string target, string state) => new()
    {
        Id = id,
        Short = id,
        Kind = "Caption",
        Source = "Source " + id,
        Target = target,
        State = state,
        Dirty = true,
    };
}
