using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke tests for <c>/admin</c> on the dashboard archetype.
///
/// Two things here are only visible in the markup, which is why they are
/// pinned. First, the design system forbids status pills in favour of the edge
/// keyline, and an attention row is exactly the place a pill would creep back
/// in. Second, the cue row is six columns wide by construction — a seventh cue
/// wraps to a lonely second row, so the trim is load-bearing rather than
/// cosmetic and no gating combination may push past it.
/// </summary>
public sealed class AdminDashboardTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly MutableSingleTenantMode _singleTenant = new();

    /// <summary>
    /// The shipped <c>SingleTenantModeState</c> takes its value at construction,
    /// which is right for a boot-time singleton and useless for reaching all
    /// four corners of (system org × single-tenant) from one fixture.
    /// </summary>
    private sealed class MutableSingleTenantMode : ISingleTenantMode
    {
        public bool IsEnabled { get; set; }
    }

    public AdminDashboardTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@cronus.example");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        // Factory registration mirrors the app (#741); it also registers
        // AppDbContext as scoped, so the dashboard's other services are unaffected.
        _ctx.Services.AddDbContextFactory<AppDbContext>(opts => opts
            .UseNpgsql(_db.ConnectionString)
            .AddInterceptors(_db.CommandTracker)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
            ServiceLifetime.Scoped);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddScoped<DashboardService>();
        _ctx.Services.AddScoped<AuditService>();
        _ctx.Services.AddScoped(sp => _db.NewOrganizationConfigService(sp.GetRequiredService<AppDbContext>()));
        _ctx.Services.AddSingleton<ISingleTenantMode>(_singleTenant);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void A_quiet_organisation_says_nothing_is_waiting_rather_than_showing_a_bare_card()
    {
        SeedSomeContent();

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Scoped to the first card: both columns render .empty-state--quiet
            // in this scenario, so an unscoped selector would pass for the wrong
            // reason — the same trap as a substring check over the whole markup.
            var attention = cut.FindAll(".dash-cols .card")[0];
            attention.QuerySelector(".empty-state--quiet").Should().NotBeNull();
            attention.QuerySelector(".activity").Should().BeNull("a bare list is not an empty state");
            attention.TextContent.Should().Contain("Nothing is waiting on you");
        });
    }

    [Fact]
    public void A_waiting_signup_turns_its_cue_red_and_adds_a_row_with_a_keyline_not_a_pill()
    {
        SeedSomeContent();
        Seed(seed => seed.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "newcomer@cronus.example",
            RequestedAt = DateTime.UtcNow.AddDays(-3),
            Decision = SignupDecision.Pending,
        }));

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Attention is the fill, not a badge — that is the whole point of
            // the cue component, and a badge would be the easy thing to add.
            cut.FindAll(".cue--attention").Should().NotBeEmpty();

            var rows = cut.FindAll(".activity--edge .activity__row");
            rows.Should().HaveCount(1);
            rows[0].ClassList.Should().Contain("is-queued");
            rows[0].QuerySelector(".status-pill").Should().BeNull(
                "the design system carries row state on the 4px edge keyline; a pill "
                + "in a row is the thing RowStateIcon exists to prevent");
            // Non-negotiable 4: colour alone never carries meaning. The keyline
            // and the glyph both need the word, and it goes on the glyph rather
            // than the row because the row is a link whose accessible name
            // aria-label would replace.
            var glyph = rows[0].QuerySelector(".activity__icon")!;
            glyph.GetAttribute("aria-label").Should().Be("Waiting");
            glyph.GetAttribute("role").Should().Be("img");
            rows[0].GetAttribute("aria-label").Should().BeNull(
                "aria-label on the row would replace the link's own name");
            cut.Markup.Should().Contain("newcomer@cronus.example");

            // The cue's foot is text followed by a <Timestamp>; the space
            // between them is easy to lose to Razor's whitespace trimming.
            var foot = cut.Find(".cue--attention .cue__foot span").TextContent;
            foot.Should().Be("Oldest arrived 3 days ago");
            rows[0].QuerySelector(".activity__time time")!.TextContent.Should().Be("3 days ago");
        });
    }

    /// <summary>
    /// Identity and order, not just the count. `.Take(6)` trims a candidate list
    /// of eight, so reordering `BuildCues` silently drops a different pair — and
    /// the two that must never be dropped are the attention ones, which lead.
    /// </summary>
    [Fact]
    public void The_six_cues_are_the_right_six_in_the_right_order()
    {
        SeedSomeContent();

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() => CueLabels(cut).Should().Equal(
            "People waiting for an account",
            "Recipe suggestions to review",
            "Users",
            "Templates",
            "Modules",
            "Recipes"));
    }

    [Fact]
    public void The_system_organisation_drops_the_per_org_cues_and_still_fills_the_row()
    {
        // In multi-tenant hosting the system org curates content and has no user
        // list of its own, so the two user-shaped cues have nothing to point at.
        _db.OrgContext.CurrentOrganizationId = TestDb.SystemOrgId;
        SeedSomeContent();

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // The two user-shaped cues drop out and the row still fills, because
            // the candidate list is longer than the six columns it is trimmed to.
            CueLabels(cut).Should().Equal(
                "Recipe suggestions to review",
                "Templates",
                "Modules",
                "Recipes",
                "Application versions",
                "Catalogue entries");
            // Asserted on the cues themselves rather than on the page's text:
            // the attention card's empty-state copy names the same queues in
            // prose, so a substring check over the whole markup passes for the
            // wrong reason.
            cut.FindAll(".cue").Select(c => c.GetAttribute("href"))
                .Should().NotContain("/admin/administration/users");
            cut.Find(".page-head__sub").TextContent
                .Should().Contain("System organisation");
        });
    }

    /// <summary>
    /// The corner the two predicates exist to separate, and the only one the
    /// fixture could not reach before. In single-tenant hosting the lone org
    /// *is* the system org, so `ShowPerOrgContent` says yes (it manages its own
    /// users) while `IsCurationOrg` still says it authors rather than imports.
    /// Swap either predicate for the other and this fails.
    /// </summary>
    [Fact]
    public void The_single_tenant_organisation_manages_users_and_still_authors()
    {
        _db.OrgContext.CurrentOrganizationId = TestDb.SystemOrgId;
        _singleTenant.IsEnabled = true;

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Authors rather than imports: there is no other organisation to
            // import from, in either sense.
            var action = cut.FindAll(".empty-state__action a").Single();
            action.TextContent.Trim().Should().Be("Add a template");
            cut.Find(".empty-state__text").TextContent
                .Should().NotContain("every other organisation",
                    "single-tenant hosting has no other organisations to tell the operator about");
            // ...but it is still an ordinary org for its own people.
            cut.Find(".page-head__sub").TextContent.Should().NotContain("System organisation");
        });
    }

    [Fact]
    public void The_single_tenant_organisation_keeps_its_user_cues()
    {
        _db.OrgContext.CurrentOrganizationId = TestDb.SystemOrgId;
        _singleTenant.IsEnabled = true;
        SeedSomeContent();

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            CueLabels(cut).Should().Contain("People waiting for an account").And.Contain("Users");
            cut.FindAll(".page-head__actions a").Single()
                .GetAttribute("href").Should().Be("/admin/administration/users/new");
        });
    }

    [Fact]
    public void An_organisation_with_users_but_no_content_is_still_a_first_run()
    {
        // A plausible real state: an org that signed people up, then never
        // imported anything. It owns nothing and nothing is waiting, so the
        // page has the same one thing to say.
        Seed(seed => seed.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "someone@cronus.example",
            DisplayName = "Someone",
            PasswordHash = "not-a-real-hash",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        }));

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Your organisation is empty"));
    }

    /// <summary>
    /// The activity panel with data in it. Three separate implementations of the
    /// same "name &lt;email&gt;" split meet here - `Avatar.Initials`, `ActorName`
    /// and `ActorEmail` - and only the first had a test. This covers all three
    /// where the bug was actually seen.
    /// </summary>
    [Fact]
    public void A_recent_change_names_the_person_who_made_it()
    {
        SeedSomeContent();
        Seed(seed => seed.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-12),
            ChangedBy = "Mads Taanquist <admin@cronus.example>",
            EntityType = AuditEntityType.Module,
            EntityId = 4,
            Action = AuditAction.Updated,
            OrganizationId = TestDb.DefaultOrgId,
        }));

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll(".dash-cols .card")[1].QuerySelectorAll(".activity__row").Single();
            row.QuerySelector(".activity__avatar")!.TextContent.Trim().Should().Be("MT");
            row.QuerySelector(".activity__text b")!.TextContent.Trim().Should().Be("Mads Taanquist");
            row.QuerySelector(".activity__sub")!.TextContent.Trim().Should().Be("admin@cronus.example");
            // The label comes from FriendlyAuditType, not the enum's own name.
            // Unnamed here, so it words the kind rather than reaching for the id.
            row.QuerySelector(".activity__text")!.TextContent.Should().Contain("changed a module");
        });
    }

    /// <summary>
    /// The dashboard half of issue #554. The activity panel used to read
    /// "changed Module #4", where #4 is a primary key an admin has never seen —
    /// two rows about two different modules were indistinguishable at a glance.
    /// The id is still on the row, in the title attribute, because the audit
    /// page's entity-id filter takes one.
    /// </summary>
    [Fact]
    public void A_recent_change_names_the_thing_that_changed_not_its_row_id()
    {
        SeedSomeContent();
        Seed(seed => seed.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-12),
            ChangedBy = "Mads Taanquist <admin@cronus.example>",
            EntityType = AuditEntityType.Module,
            EntityId = 4,
            EntityName = "Sales Extensions",
            Action = AuditAction.Updated,
            OrganizationId = TestDb.DefaultOrgId,
        }));

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            var text = cut.FindAll(".dash-cols .card")[1]
                .QuerySelectorAll(".activity__row").Single()
                .QuerySelector(".activity__text")!;

            text.TextContent.Should().Contain("changed the module Sales Extensions");
            text.TextContent.Should().NotContain("#4", "the row id is what this issue is about");
            text.QuerySelector("[title]")!.GetAttribute("title").Should().Be("Module #4",
                "the id moves out of sight rather than away - the audit filter still takes one");
        });
    }

    [Fact]
    public void A_brand_new_organisation_gets_one_thing_to_do_instead_of_six_zeroes()
    {

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
        {
            // Six cues reading 0 over two "nothing" panels is accurate and
            // useless. Until the org owns something there is exactly one move.
            cut.FindAll(".cue").Should().BeEmpty();
            cut.FindAll(".dash-cols").Should().BeEmpty();
            cut.Markup.Should().Contain("Your organisation is empty");
            var action = cut.FindAll(".empty-state__action a").Single();
            action.GetAttribute("href").Should().Be("/admin/templates");
            action.ClassList.Should().Contain("btn--primary");
            action.TextContent.Trim().Should().Be("Import starter content");
            // And nothing competes with it: no "Invite user" into an
            // organisation that has nothing to invite anyone to, and no second
            // copy of the same button in the header.
            cut.FindAll(".page-head__actions").Should().BeEmpty();
        });
    }

    [Fact]
    public void The_system_organisation_is_told_to_author_rather_than_import()
    {
        // It is the source every other organisation copies from, so there is
        // nothing for it to import. Single-tenant hosting runs on this org too.
        _db.OrgContext.CurrentOrganizationId = TestDb.SystemOrgId;

        var cut = _ctx.Render<AdminDashboard>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".empty-state__action a").Single()
                .TextContent.Trim().Should().Be("Add a template"));
    }

    [Fact]
    public void One_person_waiting_is_enough_to_leave_the_first_run_state()
    {
        Seed(seed => seed.SignupRequests.Add(new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "newcomer@cronus.example",
            RequestedAt = DateTime.UtcNow.AddDays(-1),
            Decision = SignupDecision.Pending,
        }));

        var cut = _ctx.Render<AdminDashboard>();

        // An empty org that someone is waiting to join has something to say,
        // even though it owns no content.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain("Your organisation is empty");
            cut.FindAll(".activity--edge .activity__row").Should().HaveCount(1);
        });
    }

    private static IReadOnlyList<string> CueLabels(IRenderedComponent<AdminDashboard> cut) =>
        cut.FindAll(".cue .cue__label").Select(l => l.TextContent.Trim()).ToList();

    /// <summary>
    /// Just enough that the org is past its first run, seeded into whichever org
    /// the test is acting as, system or tenant.
    /// </summary>
    private void SeedSomeContent() => Seed(seed =>
    {
        var orgId = _db.OrgContext.CurrentOrganizationId!.Value;
        seed.RuntimeTemplates.Add(TemplateBuilder.Default("one", organizationId: orgId));
        seed.Recipes.Add(RecipeBuilder.Default("One recipe", organizationId: orgId));
    });

    private void Seed(Action<AppDbContext> arrange)
    {
        using var seed = _db.NewContext();
        arrange(seed);
        seed.SaveChanges();
    }
}
