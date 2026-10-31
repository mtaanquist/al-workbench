using ALDevToolbox.Services.Al;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Al;

/// <summary>
/// <see cref="AlGoToDefinitionLocator.ResolveVariableObjectType"/> names the
/// object a right-clicked variable is declared as, for Find built-in calls.
/// OEX-4: it must find a quoted variable name, and it must not follow a
/// declaration that sits in a comment or a string.
/// </summary>
public sealed class AlVariableObjectTypeTests
{
    [Fact]
    public void An_unquoted_variable_resolves_to_its_record()
    {
        const string code = """
            var
                Cust: Record Customer;
            """;

        AlGoToDefinitionLocator.ResolveVariableObjectType(code, "Cust").Should().Be(("table", "Customer"));
    }

    [Fact]
    public void A_quoted_variable_name_resolves_to_its_record()
    {
        const string code = """
            var
                "Temp Sales Line": Record "Sales Line" temporary;
            """;

        AlGoToDefinitionLocator.ResolveVariableObjectType(code, "Temp Sales Line").Should().Be(("table", "Sales Line"));
    }

    [Fact]
    public void A_commented_out_declaration_above_the_real_one_is_skipped()
    {
        const string code = """
            var
                // Cust: Record "Cust. Ledger Entry";
                /* Cust: Page "Customer Card"; */
                Cust: Record Customer;
            """;

        AlGoToDefinitionLocator.ResolveVariableObjectType(code, "Cust").Should().Be(("table", "Customer"));
    }

    [Fact]
    public void A_declaration_only_in_a_comment_resolves_to_nothing()
    {
        const string code = """
            // Cust: Record "Cust. Ledger Entry";
            procedure Foo()
            """;

        AlGoToDefinitionLocator.ResolveVariableObjectType(code, "Cust").Should().BeNull();
    }

    [Fact]
    public void A_longer_name_that_starts_with_the_variable_is_not_taken_for_it()
    {
        const string code = """
            var
                CustLedger: Record "Cust. Ledger Entry";
                Cust: Record Customer;
            """;

        AlGoToDefinitionLocator.ResolveVariableObjectType(code, "Cust").Should().Be(("table", "Customer"));
    }

    [Theory]
    [InlineData("Temp Sales Line")]
    [InlineData("\"Temp Sales Line\"")]
    public void A_quoted_variable_is_found_as_a_declaration_with_or_without_its_quotes(string clicked)
    {
        const string code = """
            var
                // "Temp Sales Line": Record "Sales Header";
                "Temp Sales Line": Record "Sales Line" temporary;
            """;

        AlGoToDefinitionLocator.ResolveVariableDeclarationLine(code, clicked).Should().Be(3);
        AlGoToDefinitionLocator.ResolveVariableObjectType(code, clicked).Should().Be(("table", "Sales Line"));
    }
}
