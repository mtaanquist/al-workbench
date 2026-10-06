using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The shape rules of <see cref="SolutionSlug"/> and the addresses <see cref="SolutionLinks"/>
/// builds from it. The service tests cover uniqueness; these cover what a slug may look like.
/// </summary>
public sealed class SolutionSlugTests
{
    [Theory]
    [InlineData("CRONUS A/S", "cronus-a-s")]
    [InlineData("Jørgensen Møbler", "jorgensen-mobler")]
    [InlineData("Århus Æbler", "aarhus-aebler")]
    [InlineData("  Café -- Crème  ", "cafe-creme")]
    [InlineData("2024", "solution-2024")]
    [InlineData("New", "solution-new")]
    [InlineData("!!!", "solution")]
    [InlineData("Москва", "solution")]
    public void Derive_folds_a_name_into_a_valid_slug(string name, string expected)
    {
        var slug = SolutionSlug.Derive(name);

        slug.Should().Be(expected);
        SolutionSlug.IsValid(slug).Should().BeTrue();
    }

    [Fact]
    public void Derive_cuts_a_long_name_without_leaving_a_dash()
    {
        var slug = SolutionSlug.Derive(string.Join(' ', Enumerable.Repeat("word", 30)));

        slug.Length.Should().BeLessThanOrEqualTo(SolutionSlug.MaxLength);
        slug.Should().NotEndWith("-");
        SolutionSlug.IsValid(slug).Should().BeTrue();
    }

    [Theory]
    [InlineData("cronus", true)]
    [InlineData("cronus-dk-2", true)]
    [InlineData("a1", true)]
    [InlineData("", false)]
    [InlineData("42", false)]
    [InlineData("new", false)]
    [InlineData("Cronus", false)]
    [InlineData("cronus--dk", false)]
    [InlineData("-cronus", false)]
    [InlineData("cronus-", false)]
    [InlineData("cronus dk", false)]
    [InlineData("cronus/dk", false)]
    [InlineData("jørgensen", false)]
    public void IsValid_accepts_lowercase_words_joined_by_dashes_only(string slug, bool expected) =>
        SolutionSlug.IsValid(slug).Should().Be(expected);

    [Fact]
    public void IsValid_refuses_a_slug_longer_than_the_column() =>
        SolutionSlug.IsValid(new string('a', SolutionSlug.MaxLength + 1)).Should().BeFalse();

    [Fact]
    public void WithCounter_keeps_the_result_within_the_limit()
    {
        var full = new string('a', SolutionSlug.MaxLength);

        SolutionSlug.WithCounter("cronus", 1).Should().Be("cronus");
        SolutionSlug.WithCounter("cronus", 2).Should().Be("cronus-2");
        var cut = SolutionSlug.WithCounter(full, 12);
        cut.Should().EndWith("-12");
        cut.Length.Should().Be(SolutionSlug.MaxLength);
    }

    [Fact]
    public void Links_are_readable_when_the_slug_is_known_and_numeric_otherwise()
    {
        SolutionLinks.Solution("cronus", 7).Should().Be("/solutions/cronus");
        SolutionLinks.Solution(null, 7).Should().Be("/solutions/7");
        SolutionLinks.Solution("cronus", 7, "repositories").Should().Be("/solutions/cronus/repositories");
        SolutionLinks.Solution(null, 7, "bc").Should().Be("/solutions/7/bc");

        SolutionLinks.Environment("cronus", "Production", 89).Should().Be("/environments/cronus/Production");
        SolutionLinks.Environment("cronus", "Production", 89, "apps").Should().Be("/environments/cronus/Production/apps");
        SolutionLinks.Environment(null, "Production", 89, "apps").Should().Be("/environments/89/apps");
        SolutionLinks.Environment("cronus", "UAT Test", 89).Should().Be("/environments/cronus/UAT%20Test");
    }
}
