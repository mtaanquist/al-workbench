using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Customer basics on a solution (.design/solution-customer-info.md, slice 1): every
/// field optional, on-premises derived from the hosting type, and the tenant id owned by
/// this form only when the Business Central tab is gone.
/// </summary>
public sealed class ProjectCustomerInfoServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int OwnerUserId = 9890;
    private const int OtherUserId = 9891;

    public ProjectCustomerInfoServiceTests()
    {
        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@example.com"), NewUser(OtherUserId, "other@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose() => _db.Dispose();

    private static User NewUser(int id, string email) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = email,
        Role = UserRole.Editor, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private ProjectCustomerInfoService Svc(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ProjectCustomerInfoService>.Instance);

    private async Task<int> SeedAsync(Guid? tenantId = null, bool withEnvironment = false, string name = "CRONUS Denmark")
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedByUserId = OwnerUserId,
            BcTenantId = tenantId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        if (withEnvironment)
        {
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
                FetchedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        return project.Id;
    }

    private static CustomerBasicsInput Input(
        ProjectHostingType? hosting = null, string? version = null, string? url = null,
        string? voice = null, string? tenant = null) =>
        new(hosting, version, null, null, url, voice, tenant);

    [Fact]
    public async Task A_solution_nobody_has_described_reads_as_empty_and_online()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var basics = await Svc(ctx).GetBasicsAsync(id);

        basics!.IsEmpty.Should().BeTrue();
        basics.IsOnPremises.Should().BeFalse("every solution that existed before this did is online, and must keep its tabs");
    }

    [Fact]
    public async Task The_basics_round_trip_trimmed_and_blank_means_not_said()
    {
        var id = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SaveBasicsAsync(id, new CustomerBasicsInput(
                ProjectHostingType.CustomerHardware, "  NAV 2018 CU12 ", ProjectLicenseType.Purchased,
                ProjectUserExperience.Premium, " https://bc.cronus.example/BC250 ", " 5123456 ", "   "));
        }

        await using var read = _db.NewContext();
        var basics = await Svc(read).GetBasicsAsync(id);

        basics.Should().BeEquivalentTo(new CustomerBasics(
            ProjectHostingType.CustomerHardware, "NAV 2018 CU12", ProjectLicenseType.Purchased,
            ProjectUserExperience.Premium, "https://bc.cronus.example/BC250", "5123456", null));
        basics!.IsOnPremises.Should().BeTrue();
    }

    [Fact]
    public async Task An_on_premises_customer_has_a_microsoft_tenant_too_and_it_is_set_from_here()
    {
        var id = await SeedAsync();
        var tenant = Guid.NewGuid();
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.OurCloud, tenant: tenant.ToString()));

        await using var read = _db.NewContext();
        (await Svc(read).GetBasicsAsync(id))!.TenantId.Should().Be(tenant);
    }

    [Fact]
    public async Task An_online_solutions_tenant_belongs_to_the_business_central_tab_and_is_left_alone()
    {
        var connected = Guid.NewGuid();
        var id = await SeedAsync(tenantId: connected);
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.MicrosoftCloud, tenant: Guid.NewGuid().ToString()));

        await using var read = _db.NewContext();
        (await Svc(read).GetBasicsAsync(id))!.TenantId.Should().Be(connected,
            "changing the tenant of a live connection resets it, which is that tab's job");
    }

    [Fact]
    public async Task A_solution_business_central_online_reports_environments_for_cannot_be_on_premises()
    {
        var id = await SeedAsync(tenantId: Guid.NewGuid(), withEnvironment: true);
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.HostingPartner));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("HostingType").WhoseValue.Should().Contain("reports an environment");
    }

    [Fact]
    public async Task What_was_typed_wrong_comes_back_keyed_to_its_field()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveBasicsAsync(id, Input(
            ProjectHostingType.CustomerHardware, version: new string('x', 51), url: "bc.cronus.example",
            voice: new string('9', 31), tenant: "not-a-guid"));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Keys.Should().BeEquivalentTo("BcVersion", "ClientUrl", "VoiceAccountNumber", "TenantId");
    }

    /// <summary>
    /// Where a solution is hosted decides which tabs it has, so it was once narrower than
    /// the rest of this page: everything else was anyone's to correct, that field was the
    /// managers'. On a Public solution the two are now the same set - managing one is
    /// everyone in the organisation - so the carve-out has nobody left to exclude here.
    /// It still bites at the levels where managing means something narrower, which
    /// <see cref="A_read_only_solution_keeps_its_word_and_is_edited_by_its_managers_only"/>
    /// is the other half of.
    /// </summary>
    [Fact]
    public async Task Anyone_in_the_org_may_correct_a_public_solutions_details_including_where_it_is_hosted()
    {
        var id = await SeedAsync();
        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await svc.SaveBasicsAsync(id, Input(version: "BC 25.3"));
        (await svc.GetBasicsAsync(id))!.BcVersion.Should().Be("BC 25.3",
            "the people who learn a detail has changed are the ones answering the phone");

        await svc.SaveBasicsAsync(id, Input(ProjectHostingType.CustomerHardware, version: "BC 25.3"));
        (await svc.GetBasicsAsync(id))!.HostingType.Should().Be(ProjectHostingType.CustomerHardware,
            "a Public solution is managed by everyone, and hosting is a manage-level field");
    }

    [Fact]
    public async Task A_read_only_solution_keeps_its_word_and_is_edited_by_its_managers_only()
    {
        var id = await SeedAsync();
        await using (var seed = _db.NewContext())
        {
            var team = new Team { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS team", CreatedAt = DateTime.UtcNow };
            seed.Teams.Add(team);
            await seed.SaveChangesAsync();
            seed.OeProjectTeams.Add(new OeProjectTeam { OrganizationId = TestDb.DefaultOrgId, ProjectId = id, TeamId = team.Id, CreatedAt = DateTime.UtcNow });
            (await seed.OeProjects.SingleAsync(p => p.Id == id)).Visibility = ProjectVisibility.ReadOnly;
            await seed.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        (await svc.GetBasicsAsync(id)).Should().NotBeNull("everyone still reads it");
        var act = () => svc.SaveBasicsAsync(id, Input(version: "BC 25.3"));
        (await act.Should().ThrowAsync<ProjectAccessDeniedException>()).Which.Message.Should().Contain("read-only");
        await ((Func<Task>)(() => svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, "A", null, "a@cronus.example", null))))
            .Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    // ── Notes and the three lists ───────────────────────────────────────

    [Fact]
    public async Task Notes_round_trip_and_blank_clears()
    {
        var id = await SeedAsync();
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveNotesAsync(id, new CustomerNotes("  VPN, then CRONUS-BC01 ", "   ", null));

        await using var read = _db.NewContext();
        var notes = await Svc(read).GetNotesAsync(id);

        notes.Should().Be(new CustomerNotes("VPN, then CRONUS-BC01", null, null));
    }

    [Fact]
    public async Task Notes_longer_than_the_column_are_refused_by_field()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveNotesAsync(id, new CustomerNotes(new string('x', 4001), null, null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Keys.Should().Equal("AccessDescription");
    }

    [Fact]
    public async Task Contacts_list_the_customers_own_people_first_and_can_be_changed_and_removed()
    {
        var id = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var svc = Svc(ctx);
            await svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.HostingPartner, "Peter Saddow", "CRONUS Hosting", null, "+45 87 65 43 21"));
            await svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, "Annette Hill", null, "annette@cronus.example", null));
        }

        await using var read = _db.NewContext();
        var contacts = await Svc(read).ListContactsAsync(id);
        contacts.Select(c => c.Name).Should().Equal("Annette Hill", "Peter Saddow");

        await Svc(read).SaveContactAsync(id, contacts[0].Id, new CustomerContactInput(ProjectContactType.Customer, "Annette Hill-Jensen", null, "annette@cronus.example", null));
        await Svc(read).DeleteContactAsync(id, contacts[1].Id);

        (await Svc(read).ListContactsAsync(id)).Select(c => c.Name).Should().Equal("Annette Hill-Jensen");
    }

    [Theory]
    [InlineData(null, "a@cronus.example", null, "Name")]
    [InlineData("Annette Hill", null, null, "Email")]
    [InlineData("Annette Hill", "not an email", null, "Email")]
    public async Task A_contact_needs_a_name_and_a_way_to_reach_them(string? name, string? email, string? phone, string field)
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, name, null, email, phone));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task A_colleague_is_listed_once_and_their_role_is_edited_rather_than_stacked()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.SavePersonAsync(id, null, new CustomerPersonInput(OtherUserId, ProjectPersonRole.Consultant, "finance"));

        var again = () => svc.SavePersonAsync(id, null, new CustomerPersonInput(OtherUserId, ProjectPersonRole.Developer, null));
        (await again.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");

        var row = (await svc.ListPeopleAsync(id)).Single();
        await svc.SavePersonAsync(id, row.Id, new CustomerPersonInput(OtherUserId, ProjectPersonRole.Architect, "finance, warehouse"));

        (await svc.ListPeopleAsync(id)).Single().Should().BeEquivalentTo(
            new { UserId = OtherUserId, Role = ProjectPersonRole.Architect, Areas = "finance, warehouse", Email = "other@example.com" });
    }

    [Fact]
    public async Task A_colleague_has_to_be_someone_in_the_organisation()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();

        var act = () => Svc(ctx).SavePersonAsync(id, null, new CustomerPersonInput(424242, ProjectPersonRole.Consultant, null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
    }

    [Fact]
    public async Task Integrations_round_trip_with_their_direction_and_need_a_name()
    {
        var id = await SeedAsync();
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);
        await svc.SaveIntegrationAsync(id, null, new CustomerIntegrationInput(" Webshop orders ", ProjectIntegrationDirection.Inbound));

        (await svc.ListIntegrationsAsync(id)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = "Webshop orders", Direction = ProjectIntegrationDirection.Inbound });
        var blank = () => svc.SaveIntegrationAsync(id, null, new CustomerIntegrationInput("  ", ProjectIntegrationDirection.Both));
        (await blank.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task The_lists_of_a_public_solution_can_be_kept_up_by_anyone_who_can_see_it()
    {
        var id = await SeedAsync();
        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var ctx = _db.NewContext();
        var svc = Svc(ctx);

        await svc.SaveNotesAsync(id, new CustomerNotes("VPN, then CRONUS-BC01", null, null));
        await svc.SaveContactAsync(id, null, new CustomerContactInput(ProjectContactType.Customer, "Annette Hill", null, "annette@cronus.example", null));
        await svc.SavePersonAsync(id, null, new CustomerPersonInput(OwnerUserId, ProjectPersonRole.Consultant, null));
        await svc.SaveIntegrationAsync(id, null, new CustomerIntegrationInput("Webshop", ProjectIntegrationDirection.Both));

        var all = await svc.GetAllAsync(id);
        all!.Notes.AccessDescription.Should().Be("VPN, then CRONUS-BC01");
        (all.Contacts.Count, all.People.Count, all.Integrations.Count).Should().Be((1, 1, 1));
    }

    // ── The version and address Business Central reports (#907) ────────────

    private static readonly DateTime ReadAt = DateTime.UtcNow.AddHours(-3);

    /// <summary>
    /// A solution that looks connected - a tenant, its own client id and a stored secret
    /// that is deliberately not a real ciphertext: the resolution must never decrypt it.
    /// </summary>
    private async Task<int> SeedConnectedAsync(
        ProjectHostingType? hosting = ProjectHostingType.MicrosoftCloud, bool connected = true,
        string environmentType = "Production", string? envVersion = "26.1.30000.0",
        string? envUrl = "https://businesscentral.dynamics.com/tenant/Production",
        bool missing = false, bool softDeleted = false, string? typedVersion = "BC 25.3",
        string? typedUrl = "https://bc.cronus.example/BC250", bool addEnvironment = true)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = $"CRONUS {Guid.NewGuid():N}", CreatedByUserId = OwnerUserId,
            HostingType = hosting, BcVersion = typedVersion, ClientUrl = typedUrl,
            BcTenantId = Guid.NewGuid(),
            BcClientId = connected ? "11111111-2222-3333-4444-555555555555" : null,
            BcClientSecretEncrypted = connected ? "not-a-real-ciphertext" : null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        if (addEnvironment)
        {
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = environmentType, Type = environmentType,
                Version = envVersion, WebClientLoginUrl = envUrl, FetchedAt = ReadAt,
                MissingSince = missing ? DateTime.UtcNow : null,
                SoftDeletedOn = softDeleted ? DateTime.UtcNow : null,
            });
            await ctx.SaveChangesAsync();
        }
        return project.Id;
    }

    [Fact]
    public async Task A_connected_online_solution_shows_what_its_production_environment_reports()
    {
        var id = await SeedConnectedAsync();
        await using var ctx = _db.NewContext();

        var basics = (await Svc(ctx).GetBasicsAsync(id))!;

        basics.FromBusinessCentral.Should().BeTrue();
        basics.EffectiveVersion.Should().Be("26.1.30000.0");
        basics.EffectiveClientUrl.Should().Be("https://businesscentral.dynamics.com/tenant/Production");
        basics.FetchedAt.Should().BeCloseTo(ReadAt, TimeSpan.FromSeconds(1));
        basics.Production!.EnvironmentName.Should().Be("Production");
        basics.BcVersion.Should().Be("BC 25.3", "the typed value is kept, only hidden");
        basics.ClientUrl.Should().Be("https://bc.cronus.example/BC250");
    }

    [Fact]
    public async Task A_solution_that_has_not_said_where_it_is_hosted_counts_as_online()
    {
        var id = await SeedConnectedAsync(hosting: null);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).GetBasicsAsync(id))!.FromBusinessCentral.Should().BeTrue();
    }

    [Fact]
    public async Task The_organisations_registration_counts_as_a_connection_too()
    {
        var id = await SeedConnectedAsync(connected: false);
        await using (var seed = _db.NewContext())
        {
            var settings = await seed.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                seed.OrganizationSettings.Add(settings);
            }
            settings.BcClientId = "99999999-2222-3333-4444-555555555555";
            settings.BcClientSecretEncrypted = "not-a-real-ciphertext";
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();

        (await Svc(ctx).GetBasicsAsync(id))!.FromBusinessCentral.Should().BeTrue();
    }

    [Theory]
    [InlineData("no connection")]
    [InlineData("sandbox only")]
    [InlineData("no environment")]
    [InlineData("environment gone")]
    [InlineData("environment deleted")]
    [InlineData("nothing reported")]
    public async Task Otherwise_what_was_typed_is_what_shows(string why)
    {
        var id = why switch
        {
            "no connection" => await SeedConnectedAsync(connected: false),
            "sandbox only" => await SeedConnectedAsync(environmentType: "Sandbox"),
            "no environment" => await SeedConnectedAsync(addEnvironment: false),
            "environment gone" => await SeedConnectedAsync(missing: true),
            "environment deleted" => await SeedConnectedAsync(softDeleted: true),
            _ => await SeedConnectedAsync(envVersion: null, envUrl: null),
        };
        await using var ctx = _db.NewContext();

        var basics = (await Svc(ctx).GetBasicsAsync(id))!;

        basics.FromBusinessCentral.Should().BeFalse(why);
        basics.EffectiveVersion.Should().Be("BC 25.3");
        basics.EffectiveClientUrl.Should().Be("https://bc.cronus.example/BC250");
    }

    [Fact]
    public async Task An_on_premises_solution_keeps_what_was_typed()
    {
        // Can't normally happen - on-premises is refused while environments exist - but
        // the stored hosting type is what decides, not whether rows happen to be there.
        var id = await SeedConnectedAsync(hosting: ProjectHostingType.OurCloud);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).GetBasicsAsync(id))!.FromBusinessCentral.Should().BeFalse();
    }

    [Fact]
    public async Task A_customer_with_only_business_centrals_values_is_not_an_empty_tab()
    {
        var id = await SeedConnectedAsync(typedVersion: null, typedUrl: null);
        await using var ctx = _db.NewContext();

        (await Svc(ctx).GetBasicsAsync(id))!.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_while_business_central_reports_them_leaves_the_typed_version_and_address_alone()
    {
        var id = await SeedConnectedAsync();
        await using (var ctx = _db.NewContext())
        {
            // A stale form: the values it carries - even invalid ones - neither land nor fail the save.
            await Svc(ctx).SaveBasicsAsync(id, new CustomerBasicsInput(
                ProjectHostingType.MicrosoftCloud, "BC 99", ProjectLicenseType.Cloud,
                ProjectUserExperience.Premium, "not an address", "5123456"));
        }

        await using var read = _db.NewContext();
        var row = await read.OeProjects.AsNoTracking().FirstAsync(p => p.Id == id);
        row.BcVersion.Should().Be("BC 25.3");
        row.ClientUrl.Should().Be("https://bc.cronus.example/BC250");
        row.LicenseType.Should().Be(ProjectLicenseType.Cloud, "the rest of the form still saves");
        row.VoiceAccountNumber.Should().Be("5123456");
    }

    [Fact]
    public async Task Saving_without_a_connection_writes_the_typed_version_and_address()
    {
        var id = await SeedConnectedAsync(connected: false);
        await using (var ctx = _db.NewContext())
            await Svc(ctx).SaveBasicsAsync(id, Input(ProjectHostingType.MicrosoftCloud, "BC 26.0", "https://bc.cronus.example/BC260"));

        await using var read = _db.NewContext();
        var row = await read.OeProjects.AsNoTracking().FirstAsync(p => p.Id == id);
        row.BcVersion.Should().Be("BC 26.0");
        row.ClientUrl.Should().Be("https://bc.cronus.example/BC260");
    }

    [Fact]
    public async Task The_list_facts_resolve_the_same_way_in_two_reads_whatever_the_number_of_rows()
    {
        var connected = await SeedConnectedAsync();
        var typed = await SeedConnectedAsync(connected: false);
        var sandbox = await SeedConnectedAsync(environmentType: "Sandbox");

        var counter = new CommandCounter();
        await using var ctx = _db.NewContext(counter);
        var facts = await Svc(ctx).ListFactsAsync([connected, typed, sandbox]);

        facts[connected].Should().Be(new CustomerListFacts(
            ProjectHostingType.MicrosoftCloud, "26.1.30000.0", "https://businesscentral.dynamics.com/tenant/Production", true));
        facts[typed].Should().Be(new CustomerListFacts(
            ProjectHostingType.MicrosoftCloud, "BC 25.3", "https://bc.cronus.example/BC250", false));
        facts[sandbox].BcVersion.Should().Be("BC 25.3", "a sandbox's version is not the customer's");
        counter.Count.Should().Be(2, "one read for the rows and one for Business Central's facts - no read per row");
    }

    // ── The one contact per customer, for many solutions at once (#984) ──

    [Fact]
    public async Task The_contact_to_ring_is_the_customers_own_preferring_one_with_a_phone()
    {
        var withPhone = await SeedAsync(name: "CRONUS Denmark");
        var noPhone = await SeedAsync(name: "CRONUS UK");
        var nobody = await SeedAsync(name: "CRONUS Sverige");
        await using (var ctx = _db.NewContext())
        {
            var svc = Svc(ctx);
            // A partner with a phone is not the customer, and a customer without one comes
            // after a customer with one even when the name sorts first.
            await svc.SaveContactAsync(withPhone, null, new CustomerContactInput(ProjectContactType.HostingPartner, "Aaron Partner", "CRONUS Hosting", null, "+45 11 11 11 11"));
            await svc.SaveContactAsync(withPhone, null, new CustomerContactInput(ProjectContactType.Customer, "Anne Holm", null, "anne@cronus.example", null));
            await svc.SaveContactAsync(withPhone, null, new CustomerContactInput(ProjectContactType.Customer, "Sara Lind", null, null, "+45 42 18 77 03"));
            await svc.SaveContactAsync(noPhone, null, new CustomerContactInput(ProjectContactType.Customer, "Tom Ellis", null, "tom@cronus.example", null));
            await svc.SaveContactAsync(nobody, null, new CustomerContactInput(ProjectContactType.MicrosoftPartner, "Eva Berg", null, null, "+46 8 123 456 70"));
        }

        var counter = new CommandCounter();
        await using var counted = _db.NewContext(counter);
        var contacts = await Svc(counted).ListCustomerContactsAsync([withPhone, noPhone, nobody]);

        contacts[withPhone].Name.Should().Be("Sara Lind");
        contacts[noPhone].Name.Should().Be("Tom Ellis");
        contacts.Should().NotContainKey(nobody, "a solution with no customer contact has nobody to ring");
        counter.Count.Should().BeLessThanOrEqualTo(3, "the access snapshot and one read of every contact - never a read per solution");
    }

    [Fact]
    public async Task A_solution_the_caller_cannot_see_gives_no_contact()
    {
        var hidden = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            await Svc(ctx).SaveContactAsync(hidden, null, new CustomerContactInput(ProjectContactType.Customer, "Anne Holm", null, null, "+45 31 22 40 18"));
        }
        await using (var seed = _db.NewContext())
        {
            (await seed.OeProjects.SingleAsync(p => p.Id == hidden)).Visibility = ProjectVisibility.Private;
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentUserId = OtherUserId;
        await using var read = _db.NewContext();
        (await Svc(read).ListCustomerContactsAsync([hidden])).Should().BeEmpty();
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
