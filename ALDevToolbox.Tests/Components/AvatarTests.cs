using ALDevToolbox.Components.Shared;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The initials shown in the round avatar chips. Callers hand this whatever
/// they have, and the three shapes are genuinely different: an audit row's
/// <c>"display name &lt;email&gt;"</c>, a bare display name, and a bare address.
/// </summary>
public sealed class AvatarTests
{
    [Theory]
    // The audit-row form. This was rendering "M&lt;" on every audit page, because
    // splitting on '@' first leaves "Mads Taanquist <admin" and the last word
    // starts with the angle bracket.
    [InlineData("Mads Taanquist <admin@cronus.example>", "MT")]
    [InlineData("Kirsten Jensen <kirsten@cronus.example>", "KJ")]
    // A single-word display name with an address attached.
    [InlineData("Administrator <admin@cronus.example>", "A")]
    // Bare display names.
    [InlineData("Mads Taanquist", "MT")]
    [InlineData("Administrator", "A")]
    // Bare addresses, where the local part carries the separators.
    [InlineData("peter.hansen@cronus.example", "PH")]
    [InlineData("lise_moeller@cronus.example", "LM")]
    [InlineData("admin@cronus.example", "A")]
    // Degenerate: an address with no display name in front of it. Not produced
    // by ResolveChangedBy today, which only brackets when a name is present,
    // but this is a general-purpose helper.
    [InlineData("<admin@cronus.example>", "A")]
    // Seed-time rows have no actor at all.
    [InlineData("unknown", "U")]
    // System writes: the worker in brackets is not a second name.
    [InlineData("System (BackupScheduler)", "S")]
    [InlineData("System", "S")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_reads_the_person_out_of_whatever_shape_it_is_given(string? who, string expected)
        => Avatar.Initials(who).Should().Be(expected);
}
