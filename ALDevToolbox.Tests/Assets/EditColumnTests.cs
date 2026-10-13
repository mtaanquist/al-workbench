using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the second half of #549: <c>.edit-col</c> was ported and then never
/// applied, so the admin edit forms ran to the full width of their card.
///
/// <para>The design system is unusually explicit about this one
/// (<c>PagesForms.dc.html</c>): <i>"single column capped at 900px; forms do not
/// get wider than they can be read. The audit panel sits <b>outside</b> that
/// column, at full content width: it is a log, not a form, and the diff lines
/// are the one thing on the page that genuinely wants the extra
/// characters."</i> Both halves of that sentence are load-bearing, so both are
/// checked: a page that wrapped its audit panel in the column would look tidy
/// and would have thrown away the width the log needs.</para>
///
/// <para>Measured before the fix, at 1600px: the module and recipe edit cards
/// were 1304px with text inputs stretched to 625px, while the caption beside
/// them capped at 68ch and left 864px empty. Two pages had already reached
/// ~890-905px on their own by hand, which is the best evidence that 900 is the
/// right number.</para>
/// </summary>
public sealed class EditColumnTests
{
    public static TheoryData<string> EditFormPages => new()
    {
        "Components/Pages/Admin/AdminTemplateEdit.razor",
        "Components/Pages/Admin/AdminModuleEdit.razor",
        "Components/Pages/Admin/AdminRecipeEdit.razor",
        "Components/Pages/Admin/Administration/AdminAdministrationUsersInvite.razor",
    };

    [Theory]
    [MemberData(nameof(EditFormPages))]
    public void An_admin_edit_form_is_inside_the_capped_column(string page) =>
        Read("ALDevToolbox/" + page).Should().Contain("edit-col",
            $"{page} is an admin edit form; the archetype caps it at 900px so the fields "
            + "do not stretch to the full width of the card (#549)");

    /// <summary>
    /// The half that is easy to get wrong while tidying: an audit panel pulled
    /// into the column would lose exactly the width its diff lines need, and it
    /// would look neater for it.
    /// </summary>
    [Theory]
    [InlineData("Components/Pages/Admin/AdminTemplateEdit.razor")]
    [InlineData("Components/Pages/Admin/AdminModuleEdit.razor")]
    public void The_audit_panel_stays_outside_the_column(string page)
    {
        var body = Read("ALDevToolbox/" + page);
        var column = body.IndexOf("edit-col", StringComparison.Ordinal);
        var panel = body.IndexOf("<AuditHistoryPanel", StringComparison.Ordinal);

        column.Should().BeGreaterThan(-1);
        panel.Should().BeGreaterThan(column,
            $"{page}'s audit panel has to render after the form column closes");

        // The form element the column is on must have closed first. Both pages
        // put the panel after </form>, which is the structural version of the
        // same claim and is what a careless re-indent would break.
        var lastFormClose = body.LastIndexOf("</form>", panel, StringComparison.Ordinal);
        lastFormClose.Should().BeGreaterThan(column,
            $"{page} must close its .edit-col form before the audit panel, not wrap it");
    }

    /// <summary>
    /// CLAUDE.md allows one primary action per page. The module editor's
    /// dependency "Add" was a second one beside "Save changes" (#1266, L36).
    /// </summary>
    [Fact]
    public void The_module_editor_has_one_primary_button()
    {
        var body = Read("ALDevToolbox/Components/Pages/Admin/AdminModuleEdit.razor");
        System.Text.RegularExpressions.Regex.Matches(body, "btn--primary").Count.Should().Be(1,
            "only Save changes / Create module is primary; every other button is the outline style");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
