using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Uploading a licence to an on-premises environment and queueing its import (#1369):
/// who may, which files, what the customer's gateway settings must allow, the
/// <c>shared_restart</c> refusal that only AL Workbench can make, and what the page reads
/// back. See <c>.design/on-prem-gateway.md</c>, "Licences, as built".
/// </summary>
public sealed class OnPremisesLicenceServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Licence = "CRONUS licence, object pack 50000..50099"u8.ToArray();

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private User _owner = null!;
    private User _stranger = null!;
    private int _projectId;
    private int _gatewayId;
    private int _bc01;
    private int _bc02;
    private int _environmentId;

    public OnPremisesLicenceServiceTests()
    {
        SetUpAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- uploading

    [Fact]
    public async Task An_upload_is_stored_and_its_import_queued_on_the_publishing_tier()
    {
        await SetPolicyAsync();

        var id = await UploadAsync("CRONUS.bclicense", Licence);

        await using var ctx = _db.NewContext();
        var row = await ctx.OeProjectLicenses.AsNoTracking().SingleAsync();
        row.Id.Should().Be(id);
        row.ProjectId.Should().Be(_projectId);
        row.ProjectEnvironmentId.Should().Be(_environmentId);
        row.FileName.Should().Be("CRONUS.bclicense");
        row.Content.Should().Equal(Licence);
        row.Size.Should().Be(Licence.Length);
        row.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Licence)));
        row.LicenceTarget.Should().Be(GatewayLicenceTargets.NavDatabase);
        row.UploadedByUserId.Should().Be(_owner.Id);
        row.UploadedAt.Should().Be(Start.UtcDateTime);

        var command = await ctx.GatewayCommands.AsNoTracking().Include(c => c.Files).SingleAsync();
        row.GatewayCommandId.Should().Be(command.Id);
        command.GatewayId.Should().Be(_gatewayId);
        command.Verb.Should().Be(GatewayVerb.ImportLicence);
        command.RequestedByUserId.Should().Be(_owner.Id);
        var parameters = JsonNode.Parse(command.ParametersJson)!.AsObject();
        parameters["machine"]!.GetValue<string>().Should().Be("CRONUS-BC01");
        parameters["instance"]!.GetValue<string>().Should().Be("BC250");
        parameters[GatewayImportLicenceParameters.LicenceTargetParameter]!.GetValue<string>().Should().Be("NavDatabase");
        parameters.ContainsKey("tenant").Should().BeFalse("only a tenant import names the tenant");
        var file = command.Files.Single();
        file.FileName.Should().Be("CRONUS.bclicense");
        file.Content.Should().Equal(Licence);
        file.Sha256.Should().Be(row.Sha256, "the gateway checks the download against the hash of what was uploaded");
    }

    [Fact]
    public async Task An_upload_is_audited_by_its_hash_not_its_bytes()
    {
        await SetPolicyAsync();

        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            await Service(ctx).UploadAndImportAsync(_environmentId, "CRONUS.bclicense", Licence);
        }

        await using var read = _db.NewContext();
        var audit = await read.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityType.ProjectLicense);
        audit.Action.Should().Be(AuditAction.Created);
        audit.EntityName.Should().Be("CRONUS.bclicense");
        (audit.SnapshotJson ?? string.Empty).Should().NotContain(Convert.ToBase64String(Licence), "the file itself never lands in history");

        // A row removed one by one keeps the file's hash in its snapshot, not the file.
        await using (var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor()))
        {
            ctx.OeProjectLicenses.Remove(await ctx.OeProjectLicenses.SingleAsync());
            await ctx.SaveChangesAsync();
        }
        var deleted = await read.AuditLog.AsNoTracking()
            .SingleAsync(a => a.EntityType == AuditEntityType.ProjectLicense && a.Action == AuditAction.Deleted);
        deleted.SnapshotJson.Should().Contain(Convert.ToHexStringLower(SHA256.HashData(Licence)))
            .And.NotContain(Convert.ToBase64String(Licence));
    }

    [Fact]
    public async Task A_tenant_import_names_the_environments_tenant()
    {
        await SetPolicyAsync(target: GatewayLicenceTargets.Tenant);

        await UploadAsync("CRONUS.bclicense", Licence);

        await using var ctx = _db.NewContext();
        var command = await ctx.GatewayCommands.AsNoTracking().SingleAsync();
        var parameters = JsonNode.Parse(command.ParametersJson)!.AsObject();
        parameters["tenant"]!.GetValue<string>().Should().Be("default");
        parameters[GatewayImportLicenceParameters.LicenceTargetParameter]!.GetValue<string>().Should().Be("Tenant");
        var row = await ctx.OeProjectLicenses.AsNoTracking().SingleAsync();
        row.LicenceTarget.Should().Be("Tenant");
        row.Tenant.Should().Be("default");
    }

    [Fact]
    public async Task The_last_upload_names_the_tenant_it_was_sent_to_even_after_the_environment_moves()
    {
        await SetPolicyAsync(target: GatewayLicenceTargets.Tenant);
        await UploadAsync("CRONUS.bclicense", Licence);
        await using (var write = _db.NewContext())
        {
            await write.OeProjectEnvironments.Where(e => e.Id == _environmentId)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.TenantId, "cronus-dk"));
        }

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.Where.Should().Be("the tenant default only");
    }

    [Fact]
    public async Task A_server_wide_import_stores_no_tenant()
    {
        await SetPolicyAsync();

        await UploadAsync("CRONUS.bclicense", Licence);

        await using var ctx = _db.NewContext();
        var row = await ctx.OeProjectLicenses.AsNoTracking().SingleAsync();
        row.LicenceTarget.Should().Be("NavDatabase");
        row.Tenant.Should().BeNull();
        (await Service(ctx).GetViewAsync(_environmentId))!.LastUpload!.Where.Should().Be("the application database");
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_cannot_upload()
    {
        await SetPolicyAsync();
        await SetVisibilityAsync(ProjectVisibility.ReadOnly);
        ActAs(_stranger);

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_file_over_one_megabyte_is_refused()
    {
        await SetPolicyAsync();

        var act = () => UploadAsync("CRONUS.bclicense", new byte[GatewayLicenceFormats.MaxBytes + 1]);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["file"].Should().Contain("larger than 1 MB");
        await NothingSentAsync();
    }

    [Fact]
    public async Task An_flf_licence_is_refused_for_a_tier_on_Business_Central_22_or_later_naming_both_formats()
    {
        await SetPolicyAsync();

        var act = () => UploadAsync("CRONUS.flf", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["file"].Should().Be(
            "This is an .flf licence, but Business Central 25 needs a .bclicense file. Ask the customer for the .bclicense version of their licence.");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_bclicense_is_refused_for_a_tier_before_Business_Central_22()
    {
        await SetPolicyAsync();
        await SetVersionAsync(_bc01, "14.0.48916.0");

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["file"].Should().Be(
            "This is a .bclicense licence, but Business Central 14 needs an .flf file. Ask the customer for the .flf version of their licence.");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_file_that_is_not_a_licence_is_refused()
    {
        await SetPolicyAsync();

        var act = () => UploadAsync("CRONUS.app", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["file"].Should().Contain("is not a licence file");
        await NothingSentAsync();
    }

    [Fact]
    public async Task An_instance_whose_settings_do_not_allow_licences_is_refused()
    {
        await SetPolicyAsync(bc01Allow: ["inventory", "publish", "restart"]);

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().StartWith(OnPremisesDeployFacts.NotAllowed)
            .And.Contain("licences may not be imported on CRONUS-BC01 / BC250");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_database_without_a_licence_target_is_refused()
    {
        await SetPolicyAsync(target: null);

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().Contain("do not say where licences are imported");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_restart_group_tier_that_may_not_restart_is_refused()
    {
        await SetPolicyAsync(bc02Allow: ["inventory"]);

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().Contain("CRONUS-BC02 / BC250 may not be restarted");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_restart_that_reaches_another_solution_is_refused_before_anything_is_sent()
    {
        await SetPolicyAsync();
        await LinkAnotherSolutionAsync(_bc02);

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().StartWith("This import would restart service tiers that another Solution also uses, and sign out its users.")
            .And.EndWith("Nothing was sent.");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_tenant_import_is_refused_too_when_its_restart_reaches_another_solution()
    {
        // The import lands in one tenant, but the restart that follows signs out every
        // tenant on the tiers, so shared_restart applies all the same.
        await SetPolicyAsync(target: GatewayLicenceTargets.Tenant);
        await LinkAnotherSolutionAsync(_bc02);

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        await act.Should().ThrowAsync<PlanValidationException>();
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_shared_restart_the_settings_allow_goes_through()
    {
        await SetPolicyAsync(shared: true);
        await LinkAnotherSolutionAsync(_bc02);

        await UploadAsync("CRONUS.bclicense", Licence);

        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_offline_gateway_is_refused()
    {
        await SetPolicyAsync();
        await using (var ctx = _db.NewContext())
        {
            await ctx.Gateways.Where(g => g.Id == _gatewayId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, Start.UtcDateTime.AddHours(-2)));
        }

        var act = () => UploadAsync("CRONUS.bclicense", Licence);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().Contain("The gateway is offline");
        await NothingSentAsync();
    }

    // ---------------------------------------------------------------- what the dialog and the page read

    [Fact]
    public async Task The_plan_names_the_target_the_tier_the_format_and_the_tiers_that_restart()
    {
        await SetPolicyAsync();

        await using var ctx = _db.NewContext();
        var plan = await Service(ctx).GetImportPlanAsync(_environmentId);

        plan!.Refusal.Should().BeNull();
        plan.LicenceTarget.Should().Be("NavDatabase");
        plan.Where.Should().Be("the application database");
        plan.ImportTier.Should().Be("CRONUS-BC01 / BC250");
        plan.Version!.Major.Should().Be(25);
        plan.Format.Should().Be(".bclicense");
        plan.Restart!.Tiers.Should().Equal("CRONUS-BC01 / BC250", "CRONUS-BC02 / BC250");
    }

    [Fact]
    public async Task The_plan_for_the_dialog_says_the_shared_restart_refusal_with_what_to_do()
    {
        await SetPolicyAsync();
        await LinkAnotherSolutionAsync(_bc02);

        await using var ctx = _db.NewContext();
        var plan = await Service(ctx).GetImportPlanAsync(_environmentId);

        plan!.Refusal.Should().Be(
            "This import would restart service tiers (CRONUS-BC01 / BC250, CRONUS-BC02 / BC250) that another Solution also uses, and sign out its users. "
            + "Ask the customer's IT to allow shared restarts for this database in the gateway's settings file (shared_restart), or to give each Solution its own service tiers, then try again.");
    }

    [Fact]
    public async Task The_view_shows_the_installed_licence_and_how_the_last_upload_went()
    {
        await SetPolicyAsync();
        await SeedInstalledLicenceAsync(new DateOnly(2027, 3, 31));
        await UploadAsync("CRONUS.bclicense", Licence);
        await FinishCommandAsync(GatewayCommandStatus.Succeeded, null, new GatewayImportLicenceResult
        {
            Target = "NavDatabase",
            Imported = true,
            Restart = new GatewayRestartGroupResult { Mode = "stop-all-then-start-all", Restarted = true },
            Refreshed = true,
        });

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.CanManage.Should().BeTrue();
        view.Installed!.Licensee.Should().Be("CRONUS A/S");
        view.Installed.ExpiresOn.Should().Be(new DateOnly(2027, 3, 31));
        view.Installed.Ranges.Select(r => (r.ObjectType, r.FromId, r.ToId)).Should().Equal(
            ("Codeunit", 50000, 50099), ("Table", 50000, 50099));
        view.LastUpload!.FileName.Should().Be("CRONUS.bclicense");
        view.LastUpload.UploadedBy.Should().Be("owner@cronus.example");
        view.LastUpload.State.Should().Be(LicenceImportState.Imported);
    }

    [Fact]
    public async Task A_failed_import_reads_as_not_imported_with_the_gateways_reason()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "Business Central did not import the licence: The license file is not valid. Nothing was restarted, so the service tiers keep the licence they had.",
            new GatewayImportLicenceResult { Target = "NavDatabase", Imported = false });

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.Failed);
        view.LastUpload.Message.Should().Contain("The license file is not valid.");
    }

    [Fact]
    public async Task An_import_whose_restart_failed_reads_as_imported_with_problems()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "The licence was imported, but CRONUS-BC02 / BC250 did not start again.",
            new GatewayImportLicenceResult
            {
                Target = "NavDatabase",
                Imported = true,
                Restart = new GatewayRestartGroupResult { Mode = "stop-all-then-start-all", Restarted = false },
            });

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.ImportedWithProblems);
        view.LastUpload.Message.Should().StartWith("The licence was imported, but");
    }

    [Fact]
    public async Task An_import_the_gateway_was_cut_off_in_reads_as_not_known()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "It is not known whether the licence was imported: the import did not finish in time and was stopped. Nothing was restarted.",
            new GatewayImportLicenceResult { Target = "NavDatabase", ImportUnknown = true });

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.Unknown);
        view.LastUpload.Message.Should().Contain("it is not known whether the licence was imported").And.NotContain("Not imported");
    }

    [Fact]
    public async Task An_import_swept_as_failed_with_no_result_reads_as_not_known()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        // The gateway went silent while running it, and the sweep failed it.
        await FinishCommandAsync(GatewayCommandStatus.Failed, "The gateway stopped reporting on this command.", null);

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.Unknown);
        view.LastUpload.Message.Should().StartWith("The gateway stopped before it reported how the import went, so it is not known");
    }

    [Fact]
    public async Task An_import_whose_command_is_gone_reads_as_not_known()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        await using (var write = _db.NewContext())
        {
            await write.GatewayCommandFiles.ExecuteDeleteAsync();
            await write.GatewayCommands.ExecuteDeleteAsync();
        }

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.Unknown);
        view.LastUpload.Message.Should().Contain("is not known whether the licence was imported");
    }

    [Fact]
    public async Task A_refusal_by_the_customers_gateway_is_said_for_the_consultant_not_in_the_settings_files_words()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "Not allowed by the customer's gateway settings: no [[database]] section with a licence_target includes CRONUS-BC01 / BC250. Nothing was changed.",
            null);

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.Failed);
        view.LastUpload.Message.Should().Be(OnPremisesLicenceService.GatewayRefusedMessage);
        view.LastUpload.Message.Should().NotContain("[[database]]").And.NotContain("gateway.toml").And.NotContain("licence_target");
    }

    [Fact]
    public async Task A_restart_the_settings_stopped_after_the_import_says_so_in_plain_words()
    {
        await SetPolicyAsync();
        await UploadAsync("CRONUS.bclicense", Licence);
        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "The licence was imported, but the customer's gateway settings no longer allow it: CRONUS-BC02 / BC250 does not allow 'restart' (gateway.toml).",
            new GatewayImportLicenceResult
            {
                Target = "NavDatabase",
                Imported = true,
                Restart = new GatewayRestartGroupResult
                {
                    Mode = "stop-all-then-start-all",
                    Restarted = false,
                    Message = "The customer's gateway settings no longer allow it (gateway.toml).",
                    Tiers =
                    [
                        new GatewayRestartTierResult { Machine = "CRONUS-BC01", Instance = "BC250", Outcome = GatewayRestartTierOutcomes.NotRestarted },
                        new GatewayRestartTierResult { Machine = "CRONUS-BC02", Instance = "BC250", Outcome = GatewayRestartTierOutcomes.NotRestarted },
                    ],
                },
            });

        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);

        view!.LastUpload!.State.Should().Be(LicenceImportState.ImportedWithProblems);
        view.LastUpload.Message.Should().StartWith("The customer's gateway settings did not allow the restart").And.NotContain("gateway.toml");
    }

    [Fact]
    public void Today_for_a_licence_is_the_solutions_calendar_day()
    {
        var lateUtc = new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero);

        OnPremisesLicenceService.TodayFor(lateUtc, "Europe/Copenhagen").Should().Be(new DateOnly(2026, 10, 16));
        OnPremisesLicenceService.TodayFor(lateUtc, null).Should().Be(new DateOnly(2026, 10, 15), "no zone is UTC");
        OnPremisesLicenceService.TodayFor(lateUtc, "Not/AZone").Should().Be(new DateOnly(2026, 10, 15), "an unknown zone is UTC");
    }

    [Fact]
    public async Task Only_someone_who_manages_the_solution_can_download_an_upload()
    {
        await SetPolicyAsync();
        var id = await UploadAsync("CRONUS.bclicense", Licence);
        await SetVisibilityAsync(ProjectVisibility.ReadOnly);

        await using (var ctx = _db.NewContext())
        {
            var file = await Service(ctx).DownloadAsync(id);
            file!.FileName.Should().Be("CRONUS.bclicense");
            file.Content.Should().Equal(Licence);
        }

        ActAs(_stranger);
        await using (var ctx = _db.NewContext())
        {
            var view = await Service(ctx).GetViewAsync(_environmentId);
            view!.CanManage.Should().BeFalse("a read-only solution is seen, not managed, by someone off its team");
            var act = () => Service(ctx).DownloadAsync(id);
            await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        }
    }

    // ---------------------------------------------------------------- building blocks

    private OnPremisesLicenceService Service(AppDbContext ctx) => new(
        ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
        new GatewayCommandService(ctx, _db.OrgContext, new GatewayCommandSignal(), _clock, NullLogger<GatewayCommandService>.Instance),
        _clock, NullLogger<OnPremisesLicenceService>.Instance);

    [Fact]
    public async Task An_upload_whose_save_fails_after_the_command_was_queued_leaves_nothing_behind_for_the_next_one()
    {
        await SetPolicyAsync();
        var failing = new FailFirstSaveOf<OeProjectLicense>();
        await using var ctx = _db.NewContext(failing);
        var service = Service(ctx);

        var first = () => service.UploadAndImportAsync(_environmentId, "CRONUS.bclicense", Licence);
        await first.Should().ThrowAsync<InvalidOperationException>();
        failing.Failed.Should().BeTrue();

        await service.UploadAndImportAsync(_environmentId, "CRONUS.bclicense", Licence);
        await using var check = _db.NewContext();
        var command = await check.GatewayCommands.SingleAsync();
        (await check.OeProjectLicenses.SingleAsync()).GatewayCommandId.Should().Be(command.Id);
    }

    private async Task<int> UploadAsync(string fileName, byte[] content)
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).UploadAndImportAsync(_environmentId, fileName, content);
    }

    private async Task NothingSentAsync()
    {
        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(0, "nothing may reach the gateway");
        (await ctx.OeProjectLicenses.CountAsync()).Should().Be(0, "nothing is stored either");
    }

    private void ActAs(User user)
    {
        _db.OrgContext.CurrentUserId = user.Id;
        _db.OrgContext.IsSiteAdmin = false;
    }

    private async Task SetUpAsync()
    {
        _owner = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "owner@cronus.example");
        _stranger = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.User, "stranger@cronus.example");
        ActAs(_owner);

        var now = Start.UtcDateTime;
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", Visibility = ProjectVisibility.Public,
            CreatedByUserId = _owner.Id, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        var gateway = new Gateway
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS server room", MachineName = "CRONUS-BC01",
            KeyFingerprint = Guid.NewGuid().ToString("N"),
            State = GatewayState.Approved, RegisteredAt = now, KeyRegisteredAt = now, LastSeenAt = now,
        };
        ctx.Gateways.Add(gateway);
        await ctx.SaveChangesAsync();
        var bc01 = Tier(gateway.Id, "CRONUS-BC01");
        var bc02 = Tier(gateway.Id, "CRONUS-BC02");
        ctx.GatewayInstances.AddRange(bc01, bc02);
        await ctx.SaveChangesAsync();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = bc01.Id, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        _projectId = project.Id;
        _gatewayId = gateway.Id;
        _bc01 = bc01.Id;
        _bc02 = bc02.Id;
        _environmentId = env.Id;
    }

    private GatewayInstance Tier(int gatewayId, string machine) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, GatewayId = gatewayId, Machine = machine, Name = "BC250", State = "Running",
        Version = "25.4.29661.29927", DatabaseServer = "CRONUS-SQL01", DatabaseName = "CRONUS", Multitenant = false,
        FirstReportedAt = Start.UtcDateTime, ReportedAt = Start.UtcDateTime, ReadAt = Start.UtcDateTime,
    };

    /// <summary>
    /// The customer's settings: one database published on BC01 that restarts BC01 and BC02,
    /// with licences imported into <paramref name="target"/>.
    /// </summary>
    private async Task SetPolicyAsync(
        string? target = GatewayLicenceTargets.NavDatabase,
        string[]? bc01Allow = null,
        string[]? bc02Allow = null,
        bool shared = false)
    {
        var policy = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01", Name = "BC250", Environment = "production",
                    Allow = bc01Allow ?? ["inventory", "publish", "restart", "licence"], Tenants = ["default"],
                },
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC02", Name = "BC250", Environment = "production",
                    Allow = bc02Allow ?? ["inventory", "restart"], Tenants = ["default"],
                },
            ],
            Databases =
            [
                new GatewayPolicyDatabase
                {
                    Name = "CRONUS-SQL01/CRONUS",
                    PublishOn = new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" },
                    Restart =
                    [
                        new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" },
                        new GatewayPolicyTier { Machine = "CRONUS-BC02", Instance = "BC250" },
                    ],
                    SharedRestart = shared,
                    LicenceTarget = target,
                },
            ],
        };
        await using var ctx = _db.NewContext();
        await ctx.Gateways.Where(g => g.Id == _gatewayId).ExecuteUpdateAsync(s => s
            .SetProperty(g => g.EffectivePolicyJson, JsonSerializer.Serialize(policy)));
    }

    private async Task SetVisibilityAsync(ProjectVisibility visibility)
    {
        await using var ctx = _db.NewContext();
        await ctx.OeProjects.Where(p => p.Id == _projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, visibility));
    }

    private async Task SetVersionAsync(int instanceId, string version)
    {
        await using var ctx = _db.NewContext();
        await ctx.GatewayInstances.Where(i => i.Id == instanceId).ExecuteUpdateAsync(s => s.SetProperty(i => i.Version, version));
    }

    private async Task LinkAnotherSolutionAsync(int instanceId)
    {
        await using var ctx = _db.NewContext();
        var now = Start.UtcDateTime;
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Retail", CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "CRONUS Retail", Type = "Production",
            Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = instanceId, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedInstalledLicenceAsync(DateOnly expiresOn)
    {
        await using var ctx = _db.NewContext();
        ctx.GatewayInstanceLicences.Add(new GatewayInstanceLicence
        {
            OrganizationId = TestDb.DefaultOrgId, InstanceId = _bc01, Licensee = "CRONUS A/S", SerialNumber = "1234567",
            ExpiresOn = expiresOn, Text = "licence", TextSha256 = new string('0', 64), ReadAt = Start.UtcDateTime,
            Ranges =
            [
                new GatewayInstanceLicenceRange { OrganizationId = TestDb.DefaultOrgId, ObjectType = "Table", FromId = 50000, ToId = 50099 },
                new GatewayInstanceLicenceRange { OrganizationId = TestDb.DefaultOrgId, ObjectType = "Codeunit", FromId = 50000, ToId = 50099 },
            ],
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Ends the one queued command as the gateway would have.</summary>
    private async Task FinishCommandAsync(GatewayCommandStatus status, string? failure, GatewayImportLicenceResult? result)
    {
        await using var ctx = _db.NewContext();
        await ctx.GatewayCommands.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, status)
            .SetProperty(c => c.FailureMessage, failure)
            .SetProperty(c => c.ResultJson, result == null ? null : result.ToJson().ToJsonString())
            .SetProperty(c => c.FinishedAt, Start.UtcDateTime));
    }
}
