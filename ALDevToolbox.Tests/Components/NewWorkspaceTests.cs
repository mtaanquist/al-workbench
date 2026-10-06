using Microsoft.AspNetCore.DataProtection;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Domain.ValueObjects;
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
/// Smoke test for <c>/templates/workspace</c>. The headline assertion is that
/// the WorkspaceName input carries the rules the server actually applies and
/// no others — CLAUDE.md §"Always have the end user in mind" requires the
/// client-side rule to mirror the server source of truth, and since #755 that
/// rule is a length, not a character class (see
/// <c>.design/customer-naming.md</c>). Three-state loading / empty /
/// populated covered as well.
/// </summary>
public sealed class NewWorkspaceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public NewWorkspaceTests()
    {
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
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
        // The page offers to create the workspace as a GitHub repository
        // (#622), so its services have to resolve even on a deployment with no
        // GitHub App - which is exactly the state these tests render in.
        // The Customer field is a picker over the org's solutions (#758), and it
        // reads through its own service scope, so its chain has to resolve here.
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Delivery.PipelineService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue());
        _db.AddGitHubServices(_ctx.Services);
        _ctx.Services.AddDataProtection();
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
    public async Task Workspace_name_input_carries_the_rules_the_server_actually_applies()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[name='WorkspaceName']");
            input.HasAttribute("pattern").Should().BeFalse(
                "the customer's name may be written in any script since #755 — a pattern= "
                + "here would refuse names the server accepts (see .design/customer-naming.md)");
            input.GetAttribute("maxlength").Should().Be("100",
                "CLAUDE.md §\"Always have the end user in mind\": the form mirrors the "
                + "server's rules, and 100 characters is the one length rule left");
            input.HasAttribute("required").Should().BeTrue(
                "the server rejects null/whitespace; the form must surface that to the user");
        });
    }

    /// <summary>
    /// The record being named here is a Solution, and that is what the rest of
    /// the app calls it (CLAUDE.md, "Solutions in the product, Project in the
    /// code"). The heading and the label said Customer until #813. Hints may
    /// still speak of the customer - that is the person the solution is for.
    /// </summary>
    [Fact]
    public async Task The_section_and_its_field_are_called_Solution()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".section-label").Select(e => e.TextContent.Trim())
                .Should().Contain("Solution").And.NotContain("Customer");

            cut.Find("label[for='ws-name']").TextContent.Trim()
                .Should().StartWith("Solution", "the field names the record it fills in");

            cut.FindAll("label.field__label").Select(e => e.TextContent.Trim())
                .Should().NotContain(t => t.StartsWith("Customer", StringComparison.Ordinal),
                    "no field on this page is labelled Customer any more");
        });
    }

    /// <summary>
    /// The picker's own way back out says Solution too, wherever it is used -
    /// the button is the component's, not the page's.
    /// </summary>
    [Fact]
    public async Task The_picker_offers_to_change_the_solution()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }
        await SeedSolutionAsync("CRONUS Denmark", "CRO");

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']");
        cut.Find("input[name='WorkspaceName']").Focus();
        cut.Find("input[name='WorkspaceName']").Input("CRONUS");
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll("[role='option']")[0].Click());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("button").Select(b => b.TextContent.Trim())
                .Should().Contain("Change solution").And.NotContain("Change customer");
        });
    }

    [Fact]
    public async Task The_short_name_field_sits_under_the_customer_field_and_is_optional()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            var fields = cut.FindAll("input[name='WorkspaceName'], input[name='ShortName']");
            // In document order: the abbreviation is read after the name it
            // abbreviates.
            fields.Select(f => f.GetAttribute("name"))
                .Should().Equal(new[] { "WorkspaceName", "ShortName" });

            var shortName = cut.Find("input[name='ShortName']");
            shortName.HasAttribute("required").Should().BeFalse(
                "a customer whose name is short enough needs no abbreviation");
            shortName.GetAttribute("maxlength").Should().Be("50",
                "the form mirrors the server's ceiling (see .design/customer-naming.md)");
        });
    }

    [Fact]
    public async Task The_prefix_follows_the_short_name_until_the_user_types_one()
    {
        // An organisation with no prefix of its own should still get "CRO Core"
        // rather than "Core", so the field starts out following the short name -
        // until the user says otherwise.
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='ShortName']");

        cut.Find("input[name='ShortName']").Input("CRO");
        cut.WaitForAssertion(() =>
            cut.Find("input[name='ExtensionPrefix']").GetAttribute("value").Should().Be("CRO"));

        cut.Find("input[name='ExtensionPrefix']").Input("MINE");
        cut.Find("input[name='ShortName']").Input("OTHER");

        cut.WaitForAssertion(() =>
            cut.Find("input[name='ExtensionPrefix']").GetAttribute("value").Should().Be("MINE",
                "renaming the customer must not quietly undo a prefix the user chose"));
    }

    [Theory]
    // The prefix field is only asked for when the organisation leaves the
    // choice per workspace (#757). Under the other two policies the answer is
    // already known, so the field is not on the form at all.
    [InlineData(ExtensionPrefixMode.Hidden, false)]
    [InlineData(ExtensionPrefixMode.Fixed, false)]
    [InlineData(ExtensionPrefixMode.PerWorkspace, true)]
    public async Task The_prefix_field_follows_the_organisations_prefix_policy(
        ExtensionPrefixMode mode, bool expectField)
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            seed.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                ExtensionPrefixMode = mode,
                ExtensionPrefix = mode == ExtensionPrefixMode.Fixed ? "PARTNER" : null,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']");

        cut.WaitForAssertion(() =>
            cut.FindAll("input[name='ExtensionPrefix']").Any().Should().Be(expectField));
    }

    [Fact]
    public async Task The_prefix_hint_names_the_value_an_empty_field_will_generate()
    {
        // The fallback is stated in words rather than smuggled into the
        // placeholder, so a consultant can see what leaving the field alone
        // will actually name the extensions (#757 review).
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='ShortName']");
        cut.Find("input[name='ShortName']").Input("JM");

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name='ExtensionPrefix']").GetAttribute("placeholder")
                .Should().Be("e.g. CRONUS");
            cut.Find("#ws-prefix").ParentElement!.QuerySelector(".field__hint")!
                .TextContent.Should().Contain("Leave blank to use").And.Contain("JM");
        });
    }

    [Fact]
    public void Empty_template_set_renders_the_recovery_copy_pointing_at_admin()
    {
        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("No workspace templates yet");
            cut.Find(".empty-state__action").GetAttribute("href").Should().Be("/admin/templates",
                "CLAUDE.md §\"three states\" rule — the empty state must tell the user "
                + "how to recover and give them the button to do it");
            cut.FindAll("form").Should().BeEmpty(
                "the form is gated by templates being available — rendering it with "
                + "an empty dropdown would hide the actual problem");
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

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("form[action='/generate/workspace']").Should().NotBeNull();
            cut.FindAll("button.btn--primary").Should().HaveCount(1,
                "CLAUDE.md §\"Visual hierarchy\": the Generate button is the only "
                + "primary action on the page");
        });
    }

    [Fact]
    public async Task The_repository_name_input_pattern_matches_the_rule_the_service_enforces()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }
        await ConnectGitHubAsync();
        await LinkGitHubAccountAsync();

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input#ws-repo-name");
            // CLAUDE.md: the HTML rule must mirror the server's. Both sides read
            // the same constant, so they cannot drift - this pins that they do.
            input.GetAttribute("pattern").Should()
                .Be(ALDevToolbox.Services.GitHub.GitHubWorkspaceRepositoryService.NamePattern);
            input.HasAttribute("required").Should().BeTrue();
            // Defaulted from the workspace name, in the shape GitHub keeps.
            input.GetAttribute("value").Should().BeEmpty("the workspace has no name yet");
        });
    }

    [Fact]
    public async Task The_card_says_nothing_about_standards_when_the_organisation_has_none()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }
        await ConnectGitHubAsync();
        await LinkGitHubAccountAsync();

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input#ws-repo-name").Should().NotBeNull();
            cut.Markup.Should().NotContain("repository standards",
                "an organisation that has set none should not be told there are none");
        });
    }

    /// <summary>
    /// The one line #628 adds to this card: said before the button is pressed,
    /// because the repository is created with the organisation's standards
    /// whether or not the person creating it knows they exist.
    /// </summary>
    [Fact]
    public async Task Configured_standards_are_named_in_the_card_before_the_button_is_pressed()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }
        await ConnectGitHubAsync();
        await LinkGitHubAccountAsync();
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubRepositoryStandardsService(ctx).SaveAsync(
                ruleset: null,
                files: [new ALDevToolbox.Services.GitHub.GitHubStandardFileInput(
                    null, "CODEOWNERS", "* @cronus-dk/al-team")]);
        }

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Your administrators have set files and branch rules");
        });
    }

    [Fact]
    public async Task Without_a_github_organisation_the_card_says_what_to_set_up_rather_than_offering_a_field()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default(key: "runtime-15"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Create it on GitHub");
            cut.Markup.Should().Contain("GitHub is not set up on this server yet");
            cut.FindAll("input#ws-repo-name").Should().BeEmpty(
                "a field that cannot lead anywhere is worse than a sentence saying why");
            // The rule from CLAUDE.md that this card is most likely to break.
            cut.FindAll("button.btn--primary").Should().HaveCount(1,
                "Generate is still the page's only primary action");
        });
    }

    /// <summary>
    /// The GitHub connection this organisation would have made from
    /// Administration -> Repositories. The link half is deliberately left
    /// undone: it is the state a first-time user is actually in, and it is the
    /// one the card has to explain.
    /// </summary>
    private async Task ConnectGitHubAsync()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(
            new ALDevToolbox.Services.Operations.GitHubAppInput(
                AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));

        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new ALDevToolbox.Domain.Entities.OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = 42,
            GitHubOrgLogin = "cronus-dk",
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The signed-in user's own GitHub link, which is the last thing standing
    /// between the card's guidance and its form.
    /// </summary>
    private async Task LinkGitHubAccountAsync()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new ALDevToolbox.Domain.Entities.User
            {
                Id = 621,
                OrganizationId = TestDb.DefaultOrgId,
                Email = "tester@example.com",
                DisplayName = "tester@example.com",
                PasswordHash = "x",
                Role = ALDevToolbox.Domain.Entities.UserRole.User,
                Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = 621;

        var api = new ALDevToolbox.Tests.GitHub.FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", System.Net.HttpStatusCode.OK,
                ALDevToolbox.Tests.GitHub.FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", System.Net.HttpStatusCode.OK,
                ALDevToolbox.Tests.GitHub.FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
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

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card__head .switch").TextContent.Should().Contain("Include example files");
            cut.Find("input[type='hidden'][name='IncludeExamples']")
                .GetAttribute("value").Should().Be("true", "the option defaults to on");
            cut.Markup.Should().NotContain("Include example AL files",
                "the option moved out of the Options section into the preview head");
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

        var cut = _ctx.Render<NewWorkspace>();

        cut.WaitForAssertion(() =>
        {
            var form = cut.Find("form[action='/generate/workspace']");
            form.Id.Should().Be("gen-workspace-form",
                "generate.js posts the form by this id once validation passes");
            form.HasAttribute("data-loading-form").Should().BeFalse(
                "the listener that attribute binds would start the spinner on a "
                + "submit the page cancels, and nothing would ever clear it");
        });
    }

    // --- The customer as a solution (#758) ---------------------------------

    /// <summary>
    /// Seeds one solution with everything a pick can carry over, so the test
    /// below can say the whole handover happened rather than part of it.
    /// </summary>
    private async Task<Guid> SeedSolutionAsync(string name, string shortName)
    {
        var tenantId = Guid.NewGuid();
        await using var seed = _db.NewContext();
        seed.Users.Add(new ALDevToolbox.Domain.Entities.User
        {
            Id = 9770,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "tester@example.com",
            DisplayName = "tester@example.com",
            PasswordHash = "x",
            Role = ALDevToolbox.Domain.Entities.UserRole.User,
            Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.OeProjects.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            ShortName = shortName,
            BcTenantId = tenantId,
            DefaultArtifactCountry = "dk",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Repositories =
            {
                new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub,
                    Url = "https://github.com/cronus-dk/core",
                    DisplayName = "core",
                },
            },
        });
        await seed.SaveChangesAsync();
        return tenantId;
    }

    /// <summary>
    /// The point of the picker: a customer already on file is typed once. What
    /// the solution knows - the abbreviation and the tenant - arrives with it,
    /// and its repositories are on screen before a second one gets created by
    /// accident.
    /// </summary>
    [Fact]
    public async Task Picking_a_customer_fills_in_what_that_solution_already_knows()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }
        var tenantId = await SeedSolutionAsync("CRONUS Denmark", "CRO");

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']");
        cut.Find("input[name='WorkspaceName']").Focus();
        cut.Find("input[name='WorkspaceName']").Input("CRONUS");
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll("[role='option']")[0].Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name='WorkspaceName']").GetAttribute("value").Should().Be("CRONUS Denmark");
            cut.Find("input[name='ShortName']").GetAttribute("value").Should().Be("CRO");
            cut.Find("input[name='TenantId']").GetAttribute("value").Should().Be(tenantId.ToString());
            cut.Find("input[name='SolutionId']").GetAttribute("value").Should().NotBeEmpty(
                "the choice has to survive a failed submit");
            cut.Markup.Should().Contain("Existing work for CRONUS Denmark",
                "a second workspace for the same customer should be a visible choice, not an accident");
            cut.Markup.Should().Contain("cronus-dk/core", "the repository is named the way GitHub names it");
            // The two fields the pick filled in say where their value came from,
            // rather than looking like something this person typed.
            cut.Markup.Should().Contain("From CRONUS Denmark's saved details.");
        });

        // Clearing takes back what the pick filled in, and nothing else.
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Change solution")).Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name='WorkspaceName']").GetAttribute("value").Should().BeEmpty();
            cut.Find("input[name='ShortName']").GetAttribute("value").Should().BeEmpty();
            cut.Find("input[name='TenantId']").GetAttribute("value").Should().BeEmpty();
            cut.Find("input[name='SolutionId']").GetAttribute("value").Should().BeEmpty();
        });
    }

    // --- Organisations that do not use Solutions (#772) --------------------

    /// <summary>
    /// A consultant whose organisation uses the workbench only to generate
    /// workspaces gets the field as it was before the picker: type the
    /// customer's name and carry on. Offering to pick or create a solution
    /// would point at a tool their organisation has switched off, and nothing
    /// would be registered even if they used it.
    /// </summary>
    [Fact]
    public async Task With_solutions_switched_off_the_customer_field_is_a_plain_box()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }
        await SeedSolutionAsync("CRONUS Denmark", "CRO");
        SwitchSolutionsOff();

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']");

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input[name='WorkspaceName']");
            // The rules the server applies, unchanged from the picker's own box.
            input.HasAttribute("required").Should().BeTrue();
            input.GetAttribute("maxlength").Should().Be("100");
            input.GetAttribute("placeholder").Should().Be("e.g. CRONUS A/S");
            input.HasAttribute("role").Should().BeFalse("a plain text box is not a combobox");
            cut.FindAll(".solution-picker").Should().BeEmpty();
            cut.FindAll("input[name='SolutionId']").Should().BeEmpty(
                "nothing is registered, so there is nothing to post");
        });
    }

    [Fact]
    public async Task With_solutions_switched_off_typing_a_known_customer_offers_nothing_to_pick()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }
        await SeedSolutionAsync("CRONUS Denmark", "CRO");
        SwitchSolutionsOff();

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']");
        cut.Find("input[name='WorkspaceName']").Input("CRONUS");

        // Typing is just typing: no list opens, because there is no list to
        // open, and the value the form posts is what was typed.
        cut.WaitForAssertion(() =>
            cut.Find("input[name='WorkspaceName']").GetAttribute("value").Should().Be("CRONUS"));
        cut.FindAll("[role='option']").Should().BeEmpty();
    }

    /// <summary>
    /// Switches Solutions off for the signed-in user's organisation, the way an
    /// org Admin does - the page reads it off the auth claim, as the sidebar
    /// does.
    /// </summary>
    private void SwitchSolutionsOff() => _auth.SetClaims(
        new System.Security.Claims.Claim(
            ALDevToolbox.Endpoints.EndpointHelpers.DisabledToolsClaim,
            nameof(ALDevToolbox.Domain.Tools.ToolKey.Projects)));

    /// <summary>
    /// A short name typed after the pick is this person's, not the solution's,
    /// so clearing the pick must leave it where it is.
    /// </summary>
    [Fact]
    public async Task Clearing_the_customer_leaves_a_short_name_the_user_typed_themselves()
    {
        await using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default());
            await seed.SaveChangesAsync();
        }
        await SeedSolutionAsync("CRONUS Denmark", "CRO");

        var cut = _ctx.Render<NewWorkspace>();
        cut.WaitForElement("input[name='WorkspaceName']");
        cut.Find("input[name='WorkspaceName']").Focus();
        cut.Find("input[name='WorkspaceName']").Input("CRONUS");
        cut.WaitForAssertion(() => cut.FindAll("[role='option']").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll("[role='option']")[0].Click());
        cut.WaitForAssertion(() =>
            cut.Find("input[name='ShortName']").GetAttribute("value").Should().Be("CRO"));

        cut.Find("input[name='ShortName']").Input("MINE");
        await cut.InvokeAsync(() => cut.FindAll("button").First(b => b.TextContent.Contains("Change solution")).Click());

        cut.WaitForAssertion(() =>
            cut.Find("input[name='ShortName']").GetAttribute("value").Should().Be("MINE"));
    }
}
