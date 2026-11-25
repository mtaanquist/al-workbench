using Microsoft.AspNetCore.DataProtection;
using System.Text.RegularExpressions;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/templates/extension</c>. Pins the second of the two
/// form/server-regex parity contracts (the ExtensionName one) plus the
/// three-state contract. The sibling-extension hidden-input rendering
/// (<c>_workspaceContext is not null</c>) is a separate branch covered by
/// its own test.
/// </summary>
public sealed class NewExtensionTests : IDisposable
{
    private const string ServerExtensionNameRegex = @"^[A-Za-z][A-Za-z0-9 ]*$";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;

    public NewExtensionTests()
    {
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<ApplicationVersionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        // The generator pages validate against the real service before they
        // let their native POST through (#546), so it has to be resolvable
        // here or the page cannot even render.
        _ctx.Services.AddScoped<WorkspaceConfigService>();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.Generation.MustacheRenderer>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.Generation.WorkspaceZipBuilder>();
        _ctx.Services.AddScoped<GenerationService>();
        // The page carries the shared repository picker (#623), so its
        // services have to resolve even on a deployment with no GitHub App.
        _db.AddGitHubServices(_ctx.Services);
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddScoped<WorkspaceConfigService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Server_side_extension_name_regex_matches_the_compiled_constant_used_by_this_test()
    {
        var serverSource = typeof(GenerationService).GetField(
            "ExtensionNameRegex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as Regex;

        serverSource.Should().NotBeNull();
        serverSource!.ToString().Should().Be(ServerExtensionNameRegex,
            "if GenerationService.ExtensionNameRegex changes, update ServerExtensionNameRegex here "
            + "so the form-vs-server parity assertion remains meaningful");
    }

    [Fact]
    public async Task Extension_name_input_pattern_attribute_matches_the_server_regex()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[name='ExtensionName']");
            input.GetAttribute("pattern").Should().Be(ServerExtensionNameRegex,
                "the HTML pattern= must mirror GenerationService.ExtensionNameRegex");
            input.HasAttribute("required").Should().BeTrue();
        });
    }

    [Fact]
    public void Empty_template_set_tells_a_member_who_to_ask_rather_than_linking_to_admin()
    {
        // A User cannot open the importer (#1266, L35).
        var cut = _ctx.Render<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No workspace templates yet");
            cut.FindAll("a[href^='/admin/']").Should().BeEmpty();
            cut.Find(".empty-state__text").TextContent.Should().Contain("Ask one of your administrators");
        });
    }

    [Fact]
    public void Empty_template_set_renders_the_recovery_copy_pointing_at_admin()
    {
        _auth.SetRoles("Editor");
        var cut = _ctx.Render<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("No workspace templates yet");
            cut.Find(".empty-state__action").GetAttribute("href").Should().Be("/admin/templates",
                "an extension is scaffolded to sit alongside a template, so the empty "
                + "state has to point at where templates come from");
            cut.FindAll("form").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Populated_template_set_renders_the_form_with_a_single_primary_generate_button()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("form[action='/generate/extension']").Should().NotBeNull();
            cut.FindAll("button.btn--primary").Should().HaveCount(1,
                "CLAUDE.md §\"Visual hierarchy\": the Generate button is the only "
                + "primary action on the page");
        });
    }

    [Fact]
    public async Task Publisher_input_is_absent_so_org_defaults_drive_the_value()
    {
        // The Publisher input was removed from the form: there's exactly one
        // publisher per org (curated under /admin/configuration/defaults) and
        // /generate/extension resolves it server-side from
        // OrganizationSettings.DefaultPublisher. Pinning the absence here so
        // a future regression doesn't quietly reintroduce the typo surface.
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewExtension>();
        // The form first: the loading render has no fields at all.
        cut.WaitForElement("input[name='ExtensionName']");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("input[name='Publisher']").Should().BeEmpty(
                "Publisher is org-level configuration, not a per-extension form field — "
                + "the endpoint reads OrganizationSettings.DefaultPublisher instead.");
        });
    }

    /// <summary>
    /// The example-files option sits in the preview card head so its effect is
    /// visible in the tree beside it. That moves it off the posted checkbox it
    /// used to be, so the value now rides a hidden input — pinned here because
    /// losing it would silently generate example files the user turned off.
    /// </summary>
    [Fact]
    public async Task The_example_files_switch_sits_in_the_preview_head_and_still_posts_its_value()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card__head .switch").TextContent.Should().Contain("Include example files");
            cut.Find("input[type='hidden'][name='IncludeExamples']")
                .GetAttribute("value").Should().Be("true", "the option defaults to on");
        });

        cut.Find(".card__head .switch input[type='checkbox']").Change(false);

        cut.WaitForAssertion(() =>
            cut.Find("input[type='hidden'][name='IncludeExamples']")
                .GetAttribute("value").Should().Be("false",
                    "the endpoint reads IncludeExamples from the POST, so the switch "
                    + "has to carry the user's choice into it"));
    }

    /// <summary>
    /// The handoff between the page's validation and generate.js. The page
    /// cancels every submit and posts the form itself once the plan is clean
    /// (#546), which only works if two things hold: the form carries the id
    /// <c>aldtGenerate.submit</c> looks up, and it does <em>not</em> carry
    /// <c>data-loading-form</c> — that listener would start the spinner on a
    /// submit the page is about to cancel, leaving it stuck for 30 seconds on
    /// every validation error. Neither is visible in a screenshot and neither
    /// breaks the build.
    /// </summary>
    [Fact]
    public async Task The_form_hands_off_to_generate_js_rather_than_posting_itself()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewExtension>();

        cut.WaitForAssertion(() =>
        {
            var form = cut.Find("form[action='/generate/extension']");
            form.Id.Should().Be("gen-extension-form",
                "generate.js posts the form by this id once validation passes");
            form.HasAttribute("data-loading-form").Should().BeFalse(
                "the listener that attribute binds would start the spinner on a "
                + "submit the page cancels, and nothing would ever clear it");
        });
    }
}
