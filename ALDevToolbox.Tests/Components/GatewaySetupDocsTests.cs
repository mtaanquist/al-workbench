using System.Text.RegularExpressions;
using ALDevToolbox.Components.Pages.Docs;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Core.Policy;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The setup guide for customer IT (#1376, <c>/docs/gateway-setup</c>). Customer IT
/// configures the gateway from this page alone, so what it says about the settings
/// file has to be what the gateway accepts: every setting the parser knows is in the
/// guide's tables, nothing the parser refuses is, and both examples are files the
/// parser reads without a mistake. The settings the parser knows are read from its
/// own answer to an unknown key ("The settings are: ..."), so a setting added to the
/// parser later fails here until the guide documents it.
/// </summary>
public sealed class GatewaySetupDocsTests : IDisposable
{
    private static readonly string Hash = "0123456789abcdef".PadRight(64, 'e');

    private readonly BunitContext _ctx = new();

    public GatewaySetupDocsTests()
    {
        _ctx.Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<GatewaySetupDocs> Render(GatewayBuild? build = null)
    {
        _ctx.Services.AddSingleton(build ?? new GatewayBuild("12.1.0", Hash, signed: false));
        return _ctx.Render<GatewaySetupDocs>();
    }

    [Theory]
    [InlineData("gs-defaults", "[defaults.production]")]
    [InlineData("gs-instance", "[[instance]]")]
    [InlineData("gs-database", "[[database]]")]
    [InlineData("gs-machine", "[[machine]]")]
    public void Each_settings_table_lists_exactly_the_settings_the_parser_knows(string heading, string section)
    {
        var page = Render();

        SettingsIn(TableAfter(page, heading)).Should().BeEquivalentTo(KnownSettings(section),
            "customer IT writes {0} from this table, and the gateway refuses a setting it does not know", section);
    }

    [Fact]
    public void The_permissions_table_lists_exactly_the_permissions_the_gateway_knows()
    {
        var page = Render();

        SettingsIn(TableAfter(page, "gs-permissions")).Should().BeEquivalentTo(PolicyWords.Permissions.Values);
    }

    [Fact]
    public void Every_value_the_gateway_accepts_is_named_in_the_guide()
    {
        var page = Render();
        // A value may be written as it goes in the file, in quotes ("5.1").
        var code = page.FindAll(".prose code").Select(c => c.TextContent.Trim().Trim('"')).ToHashSet(StringComparer.Ordinal);

        var words = PolicyWords.Environments.Values
            .Concat(PolicyWords.Scopes.Values)
            .Concat(PolicyWords.RestartModes.Values)
            .Concat(PolicyWords.LicenceTargets.Values)
            .Concat(PolicyWords.MachineMethods.Values)
            .Concat(PolicyWords.PowerShellEditions.Values);
        foreach (var word in words)
        {
            code.Should().Contain(word, "the values are case-sensitive, so the guide spells each one out");
        }
    }

    [Theory]
    [InlineData("gs-example-single-toml")]
    [InlineData("gs-example-group-toml")]
    public void Each_example_is_a_settings_file_the_gateway_accepts(string id)
    {
        var page = Render();
        var text = page.Find($"#{id}").TextContent;

        var result = GatewayPolicyParser.Parse(text, "CRONUS-BC01");

        result.Errors.Should().BeEmpty("customer IT copies this example as the start of their own file");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_unsigned_release_says_plainly_that_it_is_not_code_signed_and_shows_the_hash()
    {
        var page = Render(new GatewayBuild("12.1.0", Hash, signed: false));

        // The download block is the Gateways page's own, so it reads the same in both places.
        page.Find(".gs-download").TextContent.Should().Contain("not code-signed yet").And.Contain("Get-FileHash");
        page.Find("#gs-sha256").TextContent.Should().Be(Hash.ToUpperInvariant());
        page.Find(".gs-article").TextContent.Should().Contain("ALWorkbench.Gateway.exe.sha256");
        page.FindAll("#gs-signature-command").Should().BeEmpty("there is no signature to check");
    }

    [Fact]
    public void A_signed_release_says_how_to_check_the_signature()
    {
        var page = Render(new GatewayBuild("12.1.0", Hash, signed: true));

        page.Find("#gs-signature-command").TextContent.Should().Contain("Get-AuthenticodeSignature");
        page.Markup.Should().NotContain("not code-signed yet");
    }

    [Fact]
    public void Without_a_release_the_guide_still_says_how_to_check_the_file()
    {
        var page = Render(new GatewayBuild(null, null, signed: false));

        page.Markup.Should().Contain("There is no gateway download for this AL Workbench");
        page.Find("#gs-hash-command").TextContent.Should().Contain("Get-FileHash");
        page.Find("#gs-download ~ .note").TextContent.Should().Contain("not code-signed yet");
    }

    [Fact]
    public void The_guide_never_calls_the_gateway_an_agent_and_has_one_primary_button_at_most()
    {
        var page = Render();

        // .design/on-prem-gateway.md, "Vocabulary": an agent is an AI assistant here.
        Regex.IsMatch(page.Find(".docs__main").TextContent, @"\bagents?\b", RegexOptions.IgnoreCase).Should().BeFalse();
        page.FindAll(".btn--primary").Should().BeEmpty("a docs page has nothing to commit to");
    }

    [Fact]
    public void The_gateways_page_links_to_the_guide_in_the_add_flow_the_empty_state_and_the_download_card()
    {
        // The page needs the database to render, so this reads its source: three links,
        // one per place a person setting up a gateway lands.
        var source = File.ReadAllText(RepoRoot.Combine(
            "ALDevToolbox", "Components", "Pages", "Admin", "Administration", "AdminAdministrationGateways.razor"));

        Regex.Matches(source, "href=\"/docs/gateway-setup\"").Count.Should().Be(3);
    }

    [Fact]
    public void The_guide_and_the_gateways_page_give_the_fingerprint_one_name()
    {
        // The gateway prints "Key fingerprint"; customer IT reads it out and the Admin
        // compares it on the Gateways page, so both say the same words.
        var source = File.ReadAllText(RepoRoot.Combine(
            "ALDevToolbox", "Components", "Pages", "Admin", "Administration", "AdminAdministrationGateways.razor"));
        var page = Render();

        source.Should().NotContainEquivalentOf("check code").And.Contain("Key fingerprint");
        page.Find(".docs__main").TextContent.Should().NotContainEquivalentOf("check code").And.Contain("key fingerprint");
    }

    [Fact]
    public void The_guide_says_a_revoke_is_permanent()
    {
        var page = Render();

        // GatewayService refuses a new key for a revoked gateway: reconnecting is a new gateway.
        Regex.Replace(page.Find("#gs-offline").ParentElement!.TextContent, @"\s+", " ")
            .Should().Contain("a revoke is permanent").And.Contain("the Admin adds a new gateway");
    }

    // ------------------------------------------------------------ helpers

    /// <summary>The first table after the heading with this id.</summary>
    private static IElement TableAfter(IRenderedComponent<GatewaySetupDocs> page, string headingId)
    {
        var element = page.Find($"#{headingId}").NextElementSibling;
        while (element is not null && element.TagName != "TABLE") element = element.NextElementSibling;
        element.Should().NotBeNull("a table follows #{0}", headingId);
        return element!;
    }

    /// <summary>The setting names in a table's first column.</summary>
    private static List<string> SettingsIn(IElement table) =>
        table.QuerySelectorAll("tbody tr td:first-child code").Select(c => c.TextContent.Trim()).ToList();

    /// <summary>
    /// The settings the parser knows for a section, from the mistake it reports for a
    /// key it does not know, which lists them.
    /// </summary>
    private static List<string> KnownSettings(string section)
    {
        var result = GatewayPolicyParser.Parse($"{section}\nnot_a_setting = 1\n", "CRONUS-BC01");
        var message = result.Errors.Should().Contain(e => e.Contains("The settings are: ")).Which;
        var list = message[(message.IndexOf("The settings are: ", StringComparison.Ordinal) + "The settings are: ".Length)..].TrimEnd('.');
        return list.Split(", ").ToList();
    }
}
