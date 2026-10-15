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
