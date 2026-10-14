using System.Reflection;
using System.Text.Json.Serialization;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The rows <c>get_solution_build</c>, <c>list_pipelines</c> and
/// <c>list_deployment_pipelines</c> hand to an agent say "solution", as every
/// other tool does (#1344, MCP-6). <c>list_deployment_pipelines</c> used to take a
/// <c>solutionId</c> and answer with a <c>projectId</c> for the same thing. The C#
/// members keep the Project name (see CLAUDE.md, "Solutions in the product,
/// Project in the code"), so what is pinned is the name on the wire.
/// </summary>
public sealed class McpSolutionVocabularyTests
{
    [Theory]
    [InlineData(typeof(ProjectBuildDetailResult))]
    [InlineData(typeof(PipelineArtifactsRow))]
    [InlineData(typeof(ReleasePipelineRow))]
    public void No_field_an_agent_reads_is_named_after_a_project(Type row)
    {
        var leaking = row.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .Where(name => name.Contains("project", StringComparison.OrdinalIgnoreCase))
            .ToList();

        leaking.Should().BeEmpty();
    }

    [Fact]
    public void The_solution_fields_carry_the_names_the_tool_descriptions_use()
    {
        WireName<ReleasePipelineRow>(nameof(ReleasePipelineRow.ProjectId)).Should().Be("solutionId");
        WireName<ReleasePipelineRow>(nameof(ReleasePipelineRow.ProjectName)).Should().Be("solutionName");
        WireName<ReleasePipelineRow>(nameof(ReleasePipelineRow.ProjectEnvironmentId)).Should().Be("environmentId");
        WireName<PipelineArtifactsRow>(nameof(PipelineArtifactsRow.ProjectId)).Should().Be("solutionId");
        WireName<PipelineArtifactsRow>(nameof(PipelineArtifactsRow.ProjectName)).Should().Be("solutionName");
    }

    private static string? WireName<T>(string property) =>
        typeof(T).GetProperty(property)!.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
}
