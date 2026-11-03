using System.Text.Json.Nodes;
using ALDevToolbox.Services.Migration;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// A moved extension's app.json is rebuilt on the template's (#1700): the
/// template wins on house style, the source keeps what makes the app itself,
/// and versions only go up.
/// </summary>
public sealed class MigrationAppJsonTests
{
    private const string Template = """
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "name": "PARTNER Core",
          "publisher": "Partner",
          "version": "0.0.0.1",
          "brief": "",
          "url": "https://partner.example",
          "logo": "../.assets/logo.png",
          "dependencies": [ { "id": "dep", "name": "Template dep", "publisher": "Partner", "version": "1.0.0.0" } ],
          "platform": "1.0.0.0",
          "application": "26.0.0.0",
          "target": "Cloud",
          "idRanges": [ { "from": 90000, "to": 90999 } ],
          "resourceExposurePolicy": { "allowDebugging": true },
          "runtime": "15.0",
          "features": [ "NoImplicitWith", "TranslationFile" ]
        }
        """;

    [Fact]
    public void Properties_follow_the_templates_order_and_spelling_whatever_order_the_source_used()
    {
        var source = Parse("""
            {
              "features": [ "TranslationFile" ],
              "runtime": "16.0",
              "supportedLocales": [ "da-DK" ],
              "IdRanges": [ { "from": 50000, "to": 50099 } ],
              "version": "2.3.0.0",
              "publisher": "CRONUS",
              "name": "CRONUS Base",
              "id": "11111111-1111-1111-1111-111111111111",
              "brief": "Base app for CRONUS"
            }
            """);

        var (result, _) = MigrationAppJson.Normalize(Parse(Template), source);

        var templateKeys = Parse(Template).Select(p => p.Key).ToList();
        var keys = result.Select(p => p.Key).ToList();
        keys.Where(k => templateKeys.Contains(k)).Should().Equal(
            templateKeys.Where(k => k != "dependencies"),
            "every property the template has sits where the template puts it");
        keys.Should().Contain("idRanges").And.NotContain("IdRanges");
        keys.Last().Should().Be("supportedLocales", "a kept setting the template lacks follows the template's");
        result["idRanges"]![0]!["from"]!.GetValue<int>().Should().Be(50000);
    }

    [Fact]
    public void Identity_and_wiring_stay_the_sources_and_house_style_comes_from_the_template()
    {
        var source = Parse("""
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "CRONUS Base",
              "publisher": "CRONUS",
              "version": "2.3.0.0",
              "brief": "Base app for CRONUS",
              "url": "https://old.example",
              "idRanges": [ { "from": 50000, "to": 50099 } ],
              "resourceExposurePolicy": { "allowDebugging": false },
              "showMyCode": true,
              "application": "24.0.0.0",
              "runtime": "12.0",
              "target": "OnPrem",
              "features": [ "TranslationFile", "GenerateCaptions" ]
            }
            """);

        var (result, changes) = MigrationAppJson.Normalize(Parse(Template), source);

        result["id"]!.GetValue<string>().Should().Be("11111111-1111-1111-1111-111111111111");
        result["name"]!.GetValue<string>().Should().Be("CRONUS Base");
        result["publisher"]!.GetValue<string>().Should().Be("CRONUS");
        result["version"]!.GetValue<string>().Should().Be("2.3.0.0");
        result["brief"]!.GetValue<string>().Should().Be("Base app for CRONUS");
        result["idRanges"]![0]!["from"]!.GetValue<int>().Should().Be(50000);
        result.ContainsKey("dependencies").Should().BeFalse("the template's dependencies belong to its own extension");
        result["target"]!.GetValue<string>().Should().Be("OnPrem");
        result["url"]!.GetValue<string>().Should().Be("https://partner.example");
        result["resourceExposurePolicy"]!["allowDebugging"]!.GetValue<bool>().Should().BeTrue();
        result.ContainsKey("showMyCode").Should().BeFalse("a setting only the source has is house style the template leaves out");
        result["application"]!.GetValue<string>().Should().Be("26.0.0.0", "a lower version is raised to the template's");
        result["runtime"]!.GetValue<string>().Should().Be("15.0");
        result["features"]!.AsArray().Select(f => f!.GetValue<string>())
            .Should().Equal("NoImplicitWith", "TranslationFile", "GenerateCaptions");

        changes.Select(c => c.Property).Should().BeEquivalentTo(
            "url", "logo", "platform", "application", "resourceExposurePolicy", "runtime", "features", "showMyCode");
        changes.Single(c => c.Property == "showMyCode").After.Should().BeNull();
        changes.Single(c => c.Property == "logo").Before.Should().BeNull();
    }

    [Fact]
    public void A_higher_version_in_the_source_is_kept()
    {
        var source = Parse("""{ "id": "x", "application": "27.1.0.0", "platform": "27.0.0.0", "runtime": "16" }""");

        var (result, _) = MigrationAppJson.Normalize(Parse(Template), source);

        result["application"]!.GetValue<string>().Should().Be("27.1.0.0");
        result["platform"]!.GetValue<string>().Should().Be("27.0.0.0");
        result["runtime"]!.GetValue<string>().Should().Be("16");
    }

    [Fact]
    public void An_old_idRange_replaces_the_templates_idRanges()
    {
        var source = Parse("""{ "id": "x", "idRange": { "from": 50000, "to": 50099 } }""");

        var (result, _) = MigrationAppJson.Normalize(Parse(Template), source);

        result.ContainsKey("idRanges").Should().BeFalse();
        result["idRange"]!["to"]!.GetValue<int>().Should().Be(50099);
    }

    [Fact]
    public void Dependencies_are_the_sources_exactly()
    {
        var source = Parse("""{ "id": "x", "dependencies": [ { "id": "a", "name": "A", "publisher": "CRONUS", "version": "1.0.0.0" } ] }""");

        var (result, _) = MigrationAppJson.Normalize(Parse(Template), source);

        JsonNode.DeepEquals(result["dependencies"], source["dependencies"]).Should().BeTrue();
    }

    [Fact]
    public void Neither_input_is_changed()
    {
        var template = Parse(Template);
        var source = Parse("""{ "id": "x", "features": [ "GenerateCaptions" ] }""");
        var before = (template.ToJsonString(), source.ToJsonString());

        MigrationAppJson.Normalize(template, source);

        (template.ToJsonString(), source.ToJsonString()).Should().Be(before);
    }

    private static JsonObject Parse(string json) => MigrationAppJson.Parse(json)!;
}
