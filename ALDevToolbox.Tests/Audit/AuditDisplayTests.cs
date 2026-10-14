using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// The audit log's entity labels are read by admins on three surfaces - both
/// audit pages and the /admin dashboard's activity panel - and they are
/// produced by a switch with a <c>ToString()</c> fallback. That fallback is the
/// hazard: a new enum member reaches the reader as its own identifier, which is
/// how "ApplicationVersion" and "PersonalAccessToken" were on screen for
/// months. CLAUDE.md's jargon test forbids exactly that.
/// </summary>
public sealed class AuditDisplayTests
{
    [Fact]
    public void No_audit_entity_label_reaches_the_reader_as_a_camel_case_identifier()
    {
        var glued = new Regex("[a-z][A-Z]");
        // A brand name is not an identifier: "GitHub repository" is written for
        // the reader and only trips the check because of how GitHub spells
        // itself. Folding the known proper nouns first keeps the rule aimed at
        // enum names that reached the screen, which is what it is for.
        var properNouns = new Regex("GitHub");

        var offenders = Enum.GetValues<AuditEntityType>()
            .Select(t => (Type: t, Label: AdminPageHelpers.FriendlyAuditType(t)))
            .Where(x => glued.IsMatch(properNouns.Replace(x.Label, "Github")))
            .Select(x => $"{x.Type} -> \"{x.Label}\"")
            .ToList();

        offenders.Should().BeEmpty(
            "every audited type needs a label written for the admin reading it; add a case "
            + "to FriendlyAuditType rather than letting the enum name fall through");
    }

    /// <summary>
    /// The stronger form of the camel-case check above: it names the exact set
    /// of types allowed to fall through to <c>ToString()</c>. Those are
    /// single words that already read as the word a person would use; anything
    /// else added to the enum fails here until someone decides its label, which
    /// is the real rule. The seam check alone would let through a new
    /// <c>Xliff</c> or a hand-written "Personalaccesstoken".
    /// </summary>
    [Fact]
    public void Only_the_types_whose_own_name_is_already_the_right_word_fall_through()
    {
        Enum.GetValues<AuditEntityType>()
            .Where(t => AdminPageHelpers.FriendlyAuditType(t) == t.ToString())
            .Should().BeEquivalentTo(new[]
            {
                AuditEntityType.Module,
                AuditEntityType.User,
                AuditEntityType.Backup,
                AuditEntityType.Invite,
                AuditEntityType.Recipe,
                AuditEntityType.Team,
                AuditEntityType.Passkey,
            });
    }

    [Fact]
    public void Every_audit_entity_label_is_non_empty_and_starts_with_a_capital()
    {
        foreach (var type in Enum.GetValues<AuditEntityType>())
        {
            var label = AdminPageHelpers.FriendlyAuditType(type);
            label.Should().NotBeNullOrWhiteSpace();
            char.IsUpper(label[0]).Should().BeTrue($"'{label}' opens a sentence in an audit row");
        }
    }
}
