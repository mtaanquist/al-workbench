using ALDevToolbox.Services.Migration;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// Reading the Azure DevOps project address an Admin pastes on Migrate
/// repositories (#1699): every form Azure DevOps shows in a browser, and nothing
/// that could point a call at another host.
/// </summary>
public sealed class AzureDevOpsProjectTests
{
    [Theory]
    [InlineData("https://dev.azure.com/cronus/Sales", "cronus", "Sales")]
    [InlineData("https://dev.azure.com/cronus/Sales/", "cronus", "Sales")]
    [InlineData("https://dev.azure.com/cronus/Sales/_git/Payroll", "cronus", "Sales")]
    [InlineData("https://dev.azure.com/cronus/Sales%20and%20Service/_boards", "cronus", "Sales and Service")]
    [InlineData("https://cronus.visualstudio.com/Sales", "cronus", "Sales")]
    [InlineData("https://cronus.visualstudio.com/DefaultCollection/Sales/_git/Payroll", "cronus", "Sales")]
    [InlineData("  cronus/Sales  ", "cronus", "Sales")]
    public void Reads_the_organisation_and_project(string input, string organization, string project)
    {
        AzureDevOpsProject.TryParse(input, out var parsed).Should().BeTrue();
        parsed.Should().Be(new AzureDevOpsProject(organization, project));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://dev.azure.com/cronus")]
    [InlineData("https://dev.azure.com/cronus/_git/Payroll")]
    [InlineData("https://github.com/cronus/Sales")]
    [InlineData("https://dev.azure.com.evil.example/cronus/Sales")]
    [InlineData("ftp://dev.azure.com/cronus/Sales")]
    [InlineData("cronus")]
    [InlineData("cronus/Sales/extra")]
    [InlineData("-cronus/Sales")]
    [InlineData("https://dev.azure.com/cronus/Sales%2Fother")]
    [InlineData("cronus/..")]
    [InlineData("https://dev.azure.com/cronus/%2E%2E")]
    public void Refuses_what_is_not_a_project(string? input)
    {
        AzureDevOpsProject.TryParse(input, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void The_web_address_always_points_at_dev_azure_com()
    {
        AzureDevOpsProject.TryParse("https://cronus.visualstudio.com/Sales and Service", out var parsed).Should().BeTrue();
        parsed!.WebUrl.Should().Be("https://dev.azure.com/cronus/Sales%20and%20Service");
        parsed.DisplayName.Should().Be("cronus/Sales and Service");
    }
}
