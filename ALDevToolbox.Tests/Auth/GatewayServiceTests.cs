using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// <see cref="GatewayService"/>: registration codes, the anonymous exchange, key
/// rotation, the Admin gate, revoke, and the heartbeat. See
/// <c>.design/on-prem-gateway.md</c>, "Registration and authentication".
/// </summary>
public sealed class GatewayServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private User _admin = null!;
    private User _editor = null!;

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        _admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, "admin@cronus.example");
        _editor = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "editor@cronus.example");
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        _db.OrgContext.CurrentUserId = _admin.Id;
    }

    private GatewayService Service(AppDbContext ctx) => GatewayTestKit.NewService(ctx, _db.OrgContext, _clock);

    private async Task<IssuedGatewayCode> NewCodeAsync()
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).CreateRegistrationCodeAsync();
    }

    /// <summary>The exchange as the gateway makes it: no organisation, no user.</summary>
    private async Task<GatewayRegistrationResponse> ExchangeAsync(GatewayRegistrationRequest request)
    {
        var anonymous = new AmbientOrganizationContext();
        await using var ctx = _db.NewContext(anonymous);
        return await GatewayTestKit.NewService(ctx, anonymous, _clock).RegisterAsync(request);
    }

    [Fact]
    public async Task A_code_is_stored_hashed_and_registers_a_pending_gateway()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        code.ExpiresAt.Should().Be(_clock.GetUtcNow().UtcDateTime + GatewayService.CodeLifetime);

        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.GatewayRegistrationCodes.SingleAsync();
            row.CodeHash.Should().NotContain(GatewayService.NormaliseCode(code.Code));
            row.CodeHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(GatewayService.NormaliseCode(code.Code)))));
            row.CreatedByUserId.Should().Be(_admin.Id);
        }

        using var key = GatewayTestKit.NewKey();
        var registered = await ExchangeAsync(GatewayTestKit.Request(code.Code, key));

        registered.Approved.Should().BeFalse();
        registered.Name.Should().Be("CRONUS-BC01");
        registered.KeyFingerprint.Should().Be(GatewayKeyFingerprint.Compute(key.ExportSubjectPublicKeyInfo()));
        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.SingleAsync();
            gateway.Id.Should().Be(registered.GatewayId);
            gateway.OrganizationId.Should().Be(TestDb.DefaultOrgId);
            gateway.State.Should().Be(GatewayState.Pending);
            gateway.PublicKey.Should().Equal(key.ExportSubjectPublicKeyInfo());
        }
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        using var first = GatewayTestKit.NewKey();
        using var second = GatewayTestKit.NewKey();
        await ExchangeAsync(GatewayTestKit.Request(code.Code, first));

        var again = () => ExchangeAsync(GatewayTestKit.Request(code.Code, second, "CRONUS-BC02"));

        (await again.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("code");
        await using var ctx = _db.NewContext();
        (await ctx.Gateways.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_code_is_accepted_typed_in_lower_case_without_dashes()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        using var key = GatewayTestKit.NewKey();

        var registered = await ExchangeAsync(GatewayTestKit.Request(code.Code.Replace("-", " ").ToLowerInvariant(), key));

        registered.GatewayId.Should().BePositive();
    }

    [Fact]
    public async Task A_code_expires_after_an_hour()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        _clock.Advance(GatewayService.CodeLifetime + TimeSpan.FromSeconds(1));
        using var key = GatewayTestKit.NewKey();

        var late = () => ExchangeAsync(GatewayTestKit.Request(code.Code, key));

        (await late.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("code");
    }

    [Fact]
    public async Task The_exchange_refuses_a_bad_key_an_old_protocol_and_a_missing_machine_name()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = GatewayTestKit.Request(code.Code, p384) with { ProtocolVersion = 0, MachineName = " " };

        var refused = () => ExchangeAsync(request);

        var errors = (await refused.Should().ThrowAsync<PlanValidationException>()).Which.Errors;
        errors.Keys.Should().BeEquivalentTo(["publicKey", "protocolVersion", "machineName"]);
        // Field errors are checked before the code is spent, so the code still works.
        using var good = GatewayTestKit.NewKey();
        (await ExchangeAsync(GatewayTestKit.Request(code.Code, good))).GatewayId.Should().BePositive();
    }

    [Fact]
    public async Task A_rotation_code_replaces_the_key_and_sets_the_gateway_back_to_pending()
    {
        await SeedAsync();
        using var oldKey = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, oldKey, approve: true);

        IssuedGatewayCode rotation;
        await using (var ctx = _db.NewContext())
        {
            rotation = await Service(ctx).CreateRotationCodeAsync(registered.GatewayId);
        }
        rotation.GatewayId.Should().Be(registered.GatewayId);

        // The old key still works until the new code is used.
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).FindCredentialAsync(registered.GatewayId))!.PublicKey.Should().Equal(oldKey.ExportSubjectPublicKeyInfo());
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        using var newKey = GatewayTestKit.NewKey();
        var rotated = await ExchangeAsync(GatewayTestKit.Request(rotation.Code, newKey));

        rotated.GatewayId.Should().Be(registered.GatewayId);
        rotated.KeyFingerprint.Should().Be(GatewayKeyFingerprint.Compute(newKey.ExportSubjectPublicKeyInfo()));
        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.SingleAsync();
            gateway.State.Should().Be(GatewayState.Pending);
            gateway.ApprovedAt.Should().BeNull();
            gateway.ApprovedByUserId.Should().BeNull();
            gateway.PublicKey.Should().Equal(newKey.ExportSubjectPublicKeyInfo());
            gateway.KeyRegisteredAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
            gateway.RegisteredAt.Should().BeBefore(gateway.KeyRegisteredAt);
        }
    }

    [Fact]
    public async Task A_rotation_code_for_a_gateway_revoked_since_is_refused()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        IssuedGatewayCode rotation;
        await using (var ctx = _db.NewContext())
        {
            rotation = await Service(ctx).CreateRotationCodeAsync(registered.GatewayId);
            await Service(ctx).RevokeAsync(registered.GatewayId);
        }

        using var newKey = GatewayTestKit.NewKey();
        var refused = () => ExchangeAsync(GatewayTestKit.Request(rotation.Code, newKey));

        (await refused.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("code");
        await using (var ctx = _db.NewContext())
        {
            var again = () => Service(ctx).CreateRotationCodeAsync(registered.GatewayId);
            await again.Should().ThrowAsync<PlanValidationException>();
        }
    }

    [Fact]
    public async Task Approving_records_who_and_when()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);

        await using (var ctx = _db.NewContext())
        {
            await Service(ctx).ApproveAsync(registered.GatewayId, registered.KeyFingerprint);
        }

        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.SingleAsync();
            gateway.State.Should().Be(GatewayState.Approved);
            gateway.ApprovedByUserId.Should().Be(_admin.Id);
            gateway.ApprovedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        }
    }

    private async Task<IssuedGatewayCode> RotationCodeAsync(int gatewayId)
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).CreateRotationCodeAsync(gatewayId);
    }

    [Fact]
    public async Task Approving_refuses_a_key_that_changed_after_the_list_was_read()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        GatewaySummary seen;
        await using (var ctx = _db.NewContext())
        {
            seen = (await Service(ctx).ListAsync()).Single();
        }

        // The gateway registers again, with another key, while the Admin compares.
        var rotation = await RotationCodeAsync(registered.GatewayId);
        using var otherKey = GatewayTestKit.NewKey();
        await ExchangeAsync(GatewayTestKit.Request(rotation.Code, otherKey));

        await using (var ctx = _db.NewContext())
        {
            var approve = () => Service(ctx).ApproveAsync(seen.Id, seen.KeyFingerprint);
            (await approve.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("gateway")
                .WhoseValue.Should().Contain("check code has changed");
        }
        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.SingleAsync();
            gateway.State.Should().Be(GatewayState.Pending);
            gateway.ApprovedByUserId.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_key_that_changes_between_reading_and_saving_an_approval_fails_the_save()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        var rotation = await RotationCodeAsync(registered.GatewayId);

        // What ApproveAsync does, held open across the rotation: the row is read with
        // the fingerprint the Admin compared, then the gateway re-keys, then the save.
        await using var approving = _db.NewContext();
        var row = await approving.Gateways.SingleAsync(g => g.Id == registered.GatewayId);
        row.KeyFingerprint.Should().Be(registered.KeyFingerprint);
        using var otherKey = GatewayTestKit.NewKey();
        await ExchangeAsync(GatewayTestKit.Request(rotation.Code, otherKey));
        row.State = GatewayState.Approved;

        await FluentActions.Awaiting(() => approving.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var read = _db.NewContext();
        (await read.Gateways.SingleAsync()).State.Should().Be(GatewayState.Pending);
    }

    [Fact]
    public async Task Revoking_still_works_when_the_key_changes_between_reading_and_saving()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve: true);

        await using (var ctx = _db.NewContext())
        {
            // A tracked, now stale copy in the same context RevokeAsync will use.
            await ctx.Gateways.SingleAsync(g => g.Id == registered.GatewayId);
            var rotation = await Service(ctx).CreateRotationCodeAsync(registered.GatewayId);
            using var otherKey = GatewayTestKit.NewKey();
            await ExchangeAsync(GatewayTestKit.Request(rotation.Code, otherKey));

            await Service(ctx).RevokeAsync(registered.GatewayId);
        }

        await using var read = _db.NewContext();
        (await read.Gateways.SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_older_rotation_code_is_refused_once_a_newer_one_is_issued()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve: true);
        var older = await RotationCodeAsync(registered.GatewayId);
        var newer = await RotationCodeAsync(registered.GatewayId);

        using var attacker = GatewayTestKit.NewKey();
        var refused = () => ExchangeAsync(GatewayTestKit.Request(older.Code, attacker));

        (await refused.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("code");
        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.SingleAsync();
            gateway.PublicKey.Should().Equal(key.ExportSubjectPublicKeyInfo());
            gateway.State.Should().Be(GatewayState.Approved);
        }
        // The newest one still works.
        using var newKey = GatewayTestKit.NewKey();
        (await ExchangeAsync(GatewayTestKit.Request(newer.Code, newKey))).GatewayId.Should().Be(registered.GatewayId);
    }

    [Fact]
    public async Task An_older_rotation_code_is_refused_once_another_has_been_used()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve: true);
        var older = await RotationCodeAsync(registered.GatewayId);
        var newer = await RotationCodeAsync(registered.GatewayId);
        using var newKey = GatewayTestKit.NewKey();
        await ExchangeAsync(GatewayTestKit.Request(newer.Code, newKey));

        using var attacker = GatewayTestKit.NewKey();
        var refused = () => ExchangeAsync(GatewayTestKit.Request(older.Code, attacker));

        (await refused.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("code");
        await using var ctx = _db.NewContext();
        (await ctx.Gateways.SingleAsync()).PublicKey.Should().Equal(newKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public async Task A_rotation_code_for_one_gateway_does_not_supersede_another_gateways_code()
    {
        await SeedAsync();
        using var firstKey = GatewayTestKit.NewKey();
        using var secondKey = GatewayTestKit.NewKey();
        var first = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, firstKey, approve: true);
        var second = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, secondKey, approve: true, machineName: "CRONUS-BC02");
        var forFirst = await RotationCodeAsync(first.GatewayId);
        await RotationCodeAsync(second.GatewayId);
        var newGateway = await NewCodeAsync();

        using var newKey = GatewayTestKit.NewKey();
        (await ExchangeAsync(GatewayTestKit.Request(forFirst.Code, newKey))).GatewayId.Should().Be(first.GatewayId);
        using var thirdKey = GatewayTestKit.NewKey();
        (await ExchangeAsync(GatewayTestKit.Request(newGateway.Code, thirdKey, "CRONUS-BC03"))).GatewayId.Should().BePositive();
    }

    [Fact]
    public async Task Two_exchanges_of_one_code_at_once_register_one_gateway()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        using var first = GatewayTestKit.NewKey();
        using var second = GatewayTestKit.NewKey();

        var attempts = new[] { first, second }.Select(async (key, i) =>
        {
            try
            {
                await ExchangeAsync(GatewayTestKit.Request(code.Code, key, $"CRONUS-BC0{i + 1}"));
                return true;
            }
            catch (PlanValidationException ex) when (ex.Errors.ContainsKey("code"))
            {
                return false;
            }
        }).ToList();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(won => won).Should().Be(1);
        await using var ctx = _db.NewContext();
        (await ctx.Gateways.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Null_fields_from_a_gateway_are_validation_errors_not_crashes()
    {
        await SeedAsync();
        var code = await NewCodeAsync();
        using var key = GatewayTestKit.NewKey();
        var request = GatewayTestKit.Request(code.Code, key) with { MachineName = null!, Version = null! };

        var refused = () => ExchangeAsync(request);

        (await refused.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Keys.Should().BeEquivalentTo(["machineName"]);

        var registered = await ExchangeAsync(GatewayTestKit.Request(code.Code, key) with { Version = null! });
        var asGateway = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId };
        await using var ctx = _db.NewContext(asGateway);
        var beat = await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(
            registered.GatewayId, new GatewayHeartbeatRequest { Version = null!, MachineName = null! });
        beat.Should().NotBeNull();
        (await ctx.Gateways.AsNoTracking().SingleAsync()).MachineName.Should().Be("CRONUS-BC01");
    }

    [Fact]
    public async Task Only_an_admin_can_list_issue_codes_approve_rename_or_revoke()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        _db.OrgContext.CurrentUserId = _editor.Id;

        await using var ctx = _db.NewContext();
        var service = Service(ctx);
        await FluentActions.Awaiting(() => service.ListAsync()).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Awaiting(() => service.CreateRegistrationCodeAsync()).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Awaiting(() => service.CreateRotationCodeAsync(registered.GatewayId)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Awaiting(() => service.ApproveAsync(registered.GatewayId, registered.KeyFingerprint)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Awaiting(() => service.RenameAsync(registered.GatewayId, "Renamed")).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Awaiting(() => service.RevokeAsync(registered.GatewayId)).Should().ThrowAsync<UnauthorizedAccessException>();

        (await ctx.Gateways.AsNoTracking().SingleAsync()).State.Should().Be(GatewayState.Pending);
    }

    [Fact]
    public async Task A_revoked_gateway_has_no_credential()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key, approve: true);

        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).FindCredentialAsync(registered.GatewayId)).Should().NotBeNull();
            await Service(ctx).RevokeAsync(registered.GatewayId);
        }

        var anonymous = new AmbientOrganizationContext();
        await using (var ctx = _db.NewContext(anonymous))
        {
            (await GatewayTestKit.NewService(ctx, anonymous, _clock).FindCredentialAsync(registered.GatewayId)).Should().BeNull();
        }
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).ListAsync()).Single().Status.Should().Be(GatewayStatus.Revoked);
        }
    }

    [Fact]
    public async Task Rename_validates_the_name()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        await using var ctx = _db.NewContext();
        var service = Service(ctx);

        (await FluentActions.Awaiting(() => service.RenameAsync(registered.GatewayId, "  ")).Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("name");
        (await FluentActions.Awaiting(() => service.RenameAsync(registered.GatewayId, new string('x', GatewayService.MaxNameLength + 1))).Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("name");

        await service.RenameAsync(registered.GatewayId, " CRONUS server room ");
        (await ctx.Gateways.AsNoTracking().SingleAsync()).Name.Should().Be("CRONUS server room");
    }

    [Fact]
    public async Task Heartbeat_records_last_seen_and_says_whether_the_gateway_is_approved()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);

        // As the gateway's own principal: its organisation, and no user.
        var asGateway = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId };
        GatewayHeartbeatResponse? pending;
        await using (var ctx = _db.NewContext(asGateway))
        {
            pending = await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(
                registered.GatewayId, new GatewayHeartbeatRequest { Version = "1.2.0", MachineName = "CRONUS-BC01" });
        }
        pending!.Approved.Should().BeFalse();
        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.SingleAsync();
            gateway.LastSeenAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
            gateway.ReportedVersion.Should().Be("1.2.0");
            (await Service(ctx).ListAsync()).Single().Status.Should().Be(GatewayStatus.WaitingForApproval);
            await Service(ctx).ApproveAsync(registered.GatewayId, registered.KeyFingerprint);
            (await Service(ctx).ListAsync()).Single().Status.Should().Be(GatewayStatus.Online);
        }

        await using (var ctx = _db.NewContext(asGateway))
        {
            (await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(
                registered.GatewayId, new GatewayHeartbeatRequest { Version = "1.2.0" }))!.Approved.Should().BeTrue();
        }

        _clock.Advance(GatewayService.OnlineWindow + TimeSpan.FromSeconds(1));
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).ListAsync()).Single().Status.Should().Be(GatewayStatus.Offline);
        }
    }

    [Fact]
    public async Task Heartbeat_stores_the_policy_report_when_it_carries_one_and_keeps_it_when_it_does_not()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        var asGateway = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId };
        var report = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01",
                    Name = "BC140",
                    Environment = "production",
                    Allow = ["inventory", "publish"],
                    ChangeHours = "20:00-06:00",
                    Tenants = ["default"],
                },
            ],
        };

        await using (var ctx = _db.NewContext(asGateway))
        {
            await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(
                registered.GatewayId, new GatewayHeartbeatRequest { Version = "1.3.0", Policy = report });
        }
        var reportedAt = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await using (var ctx = _db.NewContext(asGateway))
        {
            await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(
                registered.GatewayId, new GatewayHeartbeatRequest { Version = "1.3.0" });
        }

        await using (var ctx = _db.NewContext())
        {
            var gateway = await ctx.Gateways.AsNoTracking().SingleAsync();
            gateway.PolicyReportedAt.Should().Be(reportedAt, "a heartbeat without a report keeps the stored one");
            var stored = System.Text.Json.JsonSerializer.Deserialize<GatewayPolicyReport>(gateway.EffectivePolicyJson!)!;
            stored.Should().BeEquivalentTo(report);
        }
    }

    [Fact]
    public async Task Heartbeat_is_not_audited_but_an_approval_is()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        var asGateway = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId };
        var interceptor = new AuditInterceptor(new HttpContextAccessor());

        await using (var ctx = _db.NewContextWithAudit(interceptor))
        {
            await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(registered.GatewayId, new GatewayHeartbeatRequest());
            await GatewayTestKit.NewService(ctx, _db.OrgContext, _clock).ApproveAsync(registered.GatewayId, registered.KeyFingerprint);
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.Where(a => a.EntityType == AuditEntityType.Gateway).ToListAsync();
        rows.Should().ContainSingle().Which.Action.Should().Be(AuditAction.Updated);
    }

    [Fact]
    public async Task Heartbeat_clears_this_gateways_spent_token_ids()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        GatewayCredential credential;
        await using (var ctx = _db.NewContext())
        {
            credential = (await Service(ctx).FindCredentialAsync(registered.GatewayId))!;
            var now = _clock.GetUtcNow().UtcDateTime;
            (await Service(ctx).TryRecordTokenAsync(credential, "spent", now.AddMinutes(-1))).Should().BeTrue();
            (await Service(ctx).TryRecordTokenAsync(credential, "live", now.AddMinutes(1))).Should().BeTrue();
        }

        var asGateway = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId };
        await using (var ctx = _db.NewContext(asGateway))
        {
            await GatewayTestKit.NewService(ctx, asGateway, _clock).HeartbeatAsync(registered.GatewayId, new GatewayHeartbeatRequest());
        }

        await using (var ctx = _db.NewContext())
        {
            (await ctx.GatewaySeenTokens.Select(t => t.TokenId).ToListAsync()).Should().Equal("live");
        }
    }

    [Fact]
    public async Task A_seen_token_id_is_refused_the_second_time_from_a_fresh_context()
    {
        await SeedAsync();
        using var key = GatewayTestKit.NewKey();
        var registered = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.DefaultOrgId, _admin.Id, key);
        var anonymous = new AmbientOrganizationContext();
        var keepUntil = _clock.GetUtcNow().UtcDateTime.AddMinutes(2);

        await using (var ctx = _db.NewContext(anonymous))
        {
            var service = GatewayTestKit.NewService(ctx, anonymous, _clock);
            var credential = (await service.FindCredentialAsync(registered.GatewayId))!;
            (await service.TryRecordTokenAsync(credential, "jti-1", keepUntil)).Should().BeTrue();
        }
        await using (var ctx = _db.NewContext(anonymous))
        {
            var service = GatewayTestKit.NewService(ctx, anonymous, _clock);
            var credential = (await service.FindCredentialAsync(registered.GatewayId))!;
            (await service.TryRecordTokenAsync(credential, "jti-1", keepUntil)).Should().BeFalse();
            // The context is still usable after the refused insert.
            (await service.TryRecordTokenAsync(credential, "jti-2", keepUntil)).Should().BeTrue();
        }

        await using (var ctx = _db.NewContext())
        {
            (await ctx.GatewaySeenTokens.Select(t => t.OrganizationId).Distinct().ToListAsync()).Should().Equal(TestDb.DefaultOrgId);
        }
    }

    [Fact]
    public async Task One_organisation_cannot_see_or_heartbeat_another_organisations_gateway()
    {
        await SeedAsync();
        var otherAdmin = await GatewayTestKit.SeedUserAsync(_db, TestDb.OtherOrgId, UserRole.Admin, "admin@other.example");
        using var key = GatewayTestKit.NewKey();
        var theirs = await GatewayTestKit.RegisterAsync(_db, _clock, TestDb.OtherOrgId, otherAdmin.Id, key, approve: true);

        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).ListAsync()).Should().BeEmpty();
            await FluentActions.Awaiting(() => Service(ctx).ApproveAsync(theirs.GatewayId, theirs.KeyFingerprint)).Should().ThrowAsync<PlanValidationException>();
            await FluentActions.Awaiting(() => Service(ctx).RevokeAsync(theirs.GatewayId)).Should().ThrowAsync<PlanValidationException>();
            await FluentActions.Awaiting(() => Service(ctx).CreateRotationCodeAsync(theirs.GatewayId)).Should().ThrowAsync<PlanValidationException>();
        }

        var asDefaultGateway = new AmbientOrganizationContext { CurrentOrganizationId = TestDb.DefaultOrgId };
        await using (var ctx = _db.NewContext(asDefaultGateway))
        {
            (await GatewayTestKit.NewService(ctx, asDefaultGateway, _clock).HeartbeatAsync(theirs.GatewayId, new GatewayHeartbeatRequest()))
                .Should().BeNull();
        }
        await using (var ctx = _db.NewContext(new AmbientOrganizationContext { CurrentOrganizationId = TestDb.OtherOrgId }))
        {
            (await ctx.Gateways.SingleAsync()).LastSeenAt.Should().BeNull();
        }
    }

    [Fact]
    public void Status_follows_revocation_approval_and_the_online_window()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        GatewayService.StatusOf(GatewayState.Approved, now, now, now).Should().Be(GatewayStatus.Revoked);
        GatewayService.StatusOf(GatewayState.Pending, now, null, now).Should().Be(GatewayStatus.WaitingForApproval);
        GatewayService.StatusOf(GatewayState.Approved, null, null, now).Should().Be(GatewayStatus.NotConnectedYet);
        GatewayService.StatusOf(GatewayState.Approved, now - TimeSpan.FromMinutes(3), null, now).Should().Be(GatewayStatus.Online);
        GatewayService.StatusOf(GatewayState.Approved, now - TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1), null, now).Should().Be(GatewayStatus.Offline);
    }
}
