using System.Reflection;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Covers the name an audit row carries for the thing that changed (issue #554)
/// and the sentence built from it.
///
/// <para>The interesting failure here is silent. A picker that returns the
/// wrong field still produces a plausible-looking audit log — "changed the
/// module <c>text/plain</c>" reads as a slightly odd name, not as a bug — and
/// the value is stamped into the database at write time, so a bad pick is
/// permanent for every row written while it was wrong. That is why the coverage
/// below is per audited type rather than a couple of happy paths.</para>
/// </summary>
public sealed class AuditEntityNameTests
{
    private static string? Pick(params (string Field, string? Value)[] fields)
    {
        var map = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        return AuditEntityName.From(f => map.TryGetValue(f, out var v) ? v : null);
    }

    [Fact]
    public void The_first_candidate_the_entity_actually_has_wins()
    {
        // A template has both, and "CRONUS Standard" is the one an admin knows.
        Pick(("Key", "bc26"), ("Name", "CRONUS Standard")).Should().Be("CRONUS Standard");
        // A recipe file has both, and FileName is the one that names the file:
        // RelativePath holds the FOLDER it sits in, which is why it is not a
        // candidate at all. Pinned here because the first cut had it winning and
        // rendered "changed the module file src/Posting".
        Pick(("FileName", "Post.Codeunit.al"), ("RelativePath", "src/Sales"))
            .Should().Be("Post.Codeunit.al");
        AuditEntityName.CandidateFields.Should().NotContain("RelativePath",
            "it is a folder, not the name of the thing that changed");
    }

    [Fact]
    public void A_field_that_is_absent_null_or_blank_falls_through_to_the_next()
    {
        Pick(("Name", null), ("Key", "bc26")).Should().Be("bc26");
        Pick(("Name", "   "), ("Key", "bc26")).Should().Be("bc26");
        Pick(("Key", "bc26")).Should().Be("bc26");
    }

    [Fact]
    public void An_entity_with_no_candidate_field_has_no_name()
    {
        // RuntimeTemplateDefaultModule: a join row of two foreign keys.
        Pick(("TemplateId", "1"), ("ModuleId", "3")).Should().BeNull();
    }

    [Fact]
    public void A_name_is_trimmed_and_capped()
    {
        Pick(("Name", "  Sales Extensions  ")).Should().Be("Sales Extensions");
        Pick(("Name", new string('x', AuditEntityName.MaxLength + 50)))
            .Should().HaveLength(AuditEntityName.MaxLength);
    }

    /// <summary>
    /// The candidate list is shared by the write path and the snapshot fallback,
    /// and it is a denylist by omission for anything secret. An entity's secrets
    /// are named <c>*Encrypted</c> / <c>*Hash</c> / <c>*Token</c>; if a field
    /// like that ever gets added to the list, every audit row written afterwards
    /// puts it on the admin dashboard.
    /// </summary>
    [Fact]
    public void No_candidate_field_names_a_secret()
    {
        var forbidden = new[] { "Encrypted", "Hash", "Password", "Secret", "TokenHash" };
        foreach (var field in AuditEntityName.CandidateFields)
        {
            forbidden.Should().NotContain(
                f => field.Contains(f, StringComparison.OrdinalIgnoreCase),
                $"'{field}' would be stamped into audit_log.entity_name and rendered to admins");
        }
    }

    /// <summary>
    /// Reads the audited entity types out of the interceptor's own map and
    /// checks each one against the picker, rather than restating the list. A new
    /// audited type whose name field is not on the candidate list fails here
    /// with the type named, instead of quietly logging unnamed rows forever.
    ///
    /// <para>The seven exceptions are deliberate and are listed so that adding an
    /// eighth is a decision: two singletons the reading side words as "the system
    /// settings", three join rows with no name of their own
    /// (<c>RuntimeTemplateDefaultModule</c>, <c>TeamMember</c>, <c>OeProjectTeam</c> —
    /// a membership or an assignment is named by the two things it joins, both of
    /// which the snapshot carries), the org's single logo asset, and a gateway
    /// registration code, which is a secret and so has nothing to show but the
    /// gateway it led to.</para>
    /// </summary>
    [Fact]
    public void Every_audited_entity_type_either_resolves_a_name_or_is_a_known_exception()
    {
        var interceptor = typeof(AuditLogEntry).Assembly
            .GetType("ALDevToolbox.Data.AuditInterceptor")!;
        var map = (System.Collections.IDictionary)interceptor
            .GetField("AuditedTypeMap", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var nameless = new List<string>();
        foreach (System.Collections.DictionaryEntry pair in map)
        {
            var clrType = (Type)pair.Key!;
            var props = clrType.GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            // Every string property carries a value, so this asks only "does the
            // picker find any field on this type", not "which one".
            var resolved = AuditEntityName.From(f => props.Contains(f) ? "value" : null);
            if (resolved is null) nameless.Add(clrType.Name);
        }

        nameless.Should().BeEquivalentTo(new[]
        {
            "RuntimeTemplateDefaultModule",
            "OrganizationSettings",
            "SystemSettings",
            "OrganizationAsset",
            "TeamMember",
            "OeProjectTeam",
            "GatewayRegistrationCode",
        }, "adding an audited type whose name field is not a candidate would log unnamed rows silently");
    }

    [Fact]
    public void The_snapshot_fallback_reads_the_same_fields()
    {
        AuditEntityName.FromSnapshot("""{"Id":4,"Key":"bc26","Name":"CRONUS Standard"}""")
            .Should().Be("CRONUS Standard");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]                       // valid JSON, wrong shape
    [InlineData("""{"Name":42}""")]              // right key, not a string
    [InlineData("""{"Description":"no name"}""")]
    public void The_snapshot_fallback_never_throws_and_returns_null_when_it_cannot_tell(string? json) =>
        AuditEntityName.FromSnapshot(json).Should().BeNull();

    // ---- the sentence ----

    [Fact]
    public void A_named_row_reads_as_the_thing_it_changed()
    {
        var subject = AdminPageHelpers.AuditSubjectOf(
            AuditEntityType.Module, "Sales Extensions", snapshotJson: null);

        subject.Lead.Should().Be("the module");
        subject.Name.Should().Be("Sales Extensions");
    }

    [Fact]
    public void The_stamped_name_wins_over_the_snapshot()
    {
        AdminPageHelpers.AuditSubjectOf(
                AuditEntityType.Module, "Stamped", """{"Name":"FromSnapshot"}""")
            .Name.Should().Be("Stamped");
    }

    [Fact]
    public void A_row_written_before_the_column_existed_falls_back_to_its_snapshot()
    {
        AdminPageHelpers.AuditSubjectOf(
                AuditEntityType.Module, entityName: null, """{"Name":"FromSnapshot"}""")
            .Name.Should().Be("FromSnapshot");
    }

    [Fact]
    public void An_unnamed_row_says_what_kind_of_thing_it_was_and_never_the_id()
    {
        var subject = AdminPageHelpers.AuditSubjectOf(
            AuditEntityType.WellKnownDependency, entityName: null, snapshotJson: null);

        subject.Lead.Should().Be("a catalogue entry");
        subject.Name.Should().BeNull();
    }

    [Theory]
    [InlineData(AuditEntityType.WorkspaceExtension, "an extension")]
    [InlineData(AuditEntityType.ApplicationVersion, "an application version")]
    [InlineData(AuditEntityType.OrganizationFile, "an organisation file")]
    [InlineData(AuditEntityType.PersonalAccessToken, "an access token")]
    [InlineData(AuditEntityType.Invite, "an invite")]
    [InlineData(AuditEntityType.Module, "a module")]
    [InlineData(AuditEntityType.Recipe, "a recipe")]
    [InlineData(AuditEntityType.Backup, "a backup")]
    // The trap: 'u' is a vowel letter that opens on a consonant sound. "an user"
    // is the kind of thing a naive vowel test ships and nobody re-reads.
    [InlineData(AuditEntityType.User, "a user")]
    public void The_article_matches_how_the_label_is_said(AuditEntityType type, string expected) =>
        AdminPageHelpers.AuditSubjectOf(type, entityName: null, snapshotJson: null)
            .Lead.Should().Be(expected);

    [Theory]
    [InlineData(AuditEntityType.OrganizationSettings, "the organisation settings")]
    [InlineData(AuditEntityType.SystemSettings, "the system settings")]
    public void A_singleton_takes_the_and_never_a_name(AuditEntityType type, string expected)
    {
        // Even handed a name, since the settings row's DefaultPublisher or
        // SmtpHost is not what that row is called.
        var subject = AdminPageHelpers.AuditSubjectOf(type, "CRONUS A/S", snapshotJson: null);
        subject.Lead.Should().Be(expected);
        subject.Name.Should().BeNull();
    }

    /// <summary>
    /// The whole point of #554: no audit sentence may put a database row id in
    /// front of a reader. Checked across every type in both the named and
    /// unnamed shapes, because the id used to be the fallback.
    /// </summary>
    [Fact]
    public void No_sentence_contains_a_row_id_in_any_shape()
    {
        foreach (var type in Enum.GetValues<AuditEntityType>())
        {
            foreach (var name in new[] { null, "Sales Extensions" })
            {
                var subject = AdminPageHelpers.AuditSubjectOf(type, name, snapshotJson: null);
                var sentence = $"{subject.Lead} {subject.Name}";
                sentence.Should().NotContain("#", $"{type} put an id in its sentence");
            }
        }
    }
}
