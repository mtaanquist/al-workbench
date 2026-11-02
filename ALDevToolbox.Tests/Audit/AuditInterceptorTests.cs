using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Covers <see cref="AuditInterceptor"/>: created/updated/deleted rows produce
/// the right action, modified rows snapshot OriginalValues (not the new state),
/// principal entities inline their child collections, file content is hashed
/// rather than copied verbatim, and a "save with no real edits" doesn't write
/// audit rows.
/// </summary>
public sealed class AuditInterceptorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Adding_a_template_writes_a_created_row_with_no_snapshot()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-x"));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.ToListAsync();
        rows.Should().ContainSingle(r =>
            r.EntityType == AuditEntityType.RuntimeTemplate
            && r.Action == AuditAction.Created
            && r.SnapshotJson == null
            && r.ChangedBy == "admin");
    }

    [Fact]
    public async Task Modifying_a_template_writes_an_updated_row_with_original_values()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "Original Name";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var template = await ctx.RuntimeTemplates.FirstAsync(t => t.Id == templateId);
            template.Name = "New Name";
            template.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.RuntimeTemplate && r.Action == AuditAction.Updated)
            .SingleAsync();
        row.ChangedBy.Should().Be("alice");
        row.SnapshotJson.Should().NotBeNullOrEmpty();
        // The snapshot captures pre-save state — "Original Name", not "New Name".
        var snapshot = JsonDocument.Parse(row.SnapshotJson!);
        snapshot.RootElement.GetProperty("Name").GetString().Should().Be("Original Name");
    }

    [Fact]
    public async Task Modifying_only_updated_at_is_treated_as_a_no_op()
    {
        // Reconciliation services rewrite UpdatedAt unconditionally on save;
        // the interceptor must filter that case so admins don't see noise rows
        // every time they click Save without making real edits.
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var template = await ctx.RuntimeTemplates.FirstAsync(t => t.Id == templateId);
            template.UpdatedAt = template.UpdatedAt.AddSeconds(1);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync(r => r.Action == AuditAction.Updated))
            .Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_template_writes_a_deleted_row_with_pre_delete_snapshot()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "Doomed Template";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = await ctx.RuntimeTemplates.FirstAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.RuntimeTemplate && r.Action == AuditAction.Deleted)
            .SingleAsync();
        row.SnapshotJson.Should().NotBeNullOrEmpty();
        JsonDocument.Parse(row.SnapshotJson!)
            .RootElement.GetProperty("Name").GetString()
            .Should().Be("Doomed Template");
    }

    [Fact]
    public async Task Extension_snapshot_inlines_folders()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-ext-snapshot")
                .WithCoreFolder("Source", ("Hello.al", "codeunit 50000 H { }"))
                .WithCoreFolder("Translations");
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = await ctx.RuntimeTemplates
                .Include(t => t.WorkspaceExtensions)
                    .ThenInclude(e => e.Folders)
                        .ThenInclude(f => f.Files)
                .FirstAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.WorkspaceExtension && r.Action == AuditAction.Deleted)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(row.SnapshotJson!).RootElement;
        snapshot.GetProperty("folders").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Workspace_extension_file_snapshot_hashes_content()
    {
        const string content = "codeunit 50000 LargeAlBody { trigger OnRun() begin Message('hi'); end; }";
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-hash")
                .WithCoreFolder("Source", ("Hello.al", content));
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = await ctx.RuntimeTemplates
                .Include(t => t.WorkspaceExtensions)
                    .ThenInclude(e => e.Folders)
                        .ThenInclude(f => f.Files)
                .FirstAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.WorkspaceExtensionFile && r.Action == AuditAction.Deleted)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(row.SnapshotJson!).RootElement;
        snapshot.TryGetProperty("Content", out _).Should().BeFalse();
        snapshot.GetProperty("ContentSha256").GetString()
            .Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Fact]
    public async Task Module_snapshot_inlines_dependencies()
    {
        int moduleId;
        await using (var seed = _db.NewContext())
        {
            var module = ModuleBuilder.Default("alpha", "Alpha")
                .WithDependency("00000000-0000-0000-0000-000000000001", "Base App", "Microsoft", "24.0.0.0")
                .WithDependency("00000000-0000-0000-0000-000000000002", "System App", "Microsoft", "24.0.0.0");
            seed.Modules.Add(module);
            await seed.SaveChangesAsync();
            moduleId = module.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var module = await ctx.Modules.Include(m => m.Dependencies).FirstAsync(m => m.Id == moduleId);
            ctx.Modules.Remove(module);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.Module && r.Action == AuditAction.Deleted)
            .SingleAsync();
        var snapshot = JsonDocument.Parse(row.SnapshotJson!).RootElement;
        snapshot.GetProperty("dependencies").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Unauthenticated_save_records_unknown_actor()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor(name: null)))
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-anon"));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        // TemplateBuilder.Default seeds both the RuntimeTemplate row and its
        // required Core WorkspaceExtension (the unified-extensions model
        // requires at least one extension); pick the parent row to assert on.
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.RuntimeTemplate).SingleAsync())
            .ChangedBy.Should().Be("unknown");
    }

    // ── SaaS-delivery: release pipelines are audited; projects only for their
    //    BC connection/secret (discovery-cache churn and creation are filtered out).

    [Fact]
    public async Task Changing_a_project_bc_connection_writes_an_updated_row()
    {
        int projectId;
        await using (var seed = _db.NewContext())
        {
            var p = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS A/S", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var p = await ctx.OeProjects.FirstAsync(x => x.Id == projectId);
            p.BcClientId = "client-123";
            p.BcClientSecretExpiresAt = DateTime.UtcNow.AddYears(1);
            p.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.Project && r.Action == AuditAction.Updated).SingleAsync())
            .ChangedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Changing_a_project_bc_secret_redacts_it_in_the_snapshot()
    {
        int projectId;
        await using (var seed = _db.NewContext())
        {
            var p = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "CRONUS",
                BcClientSecretEncrypted = "cipher-old",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var p = await ctx.OeProjects.FirstAsync(x => x.Id == projectId);
            p.BcClientSecretEncrypted = "cipher-new";
            p.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.Project && r.Action == AuditAction.Updated)
            .SingleAsync();
        // The before-snapshot records that the secret changed, not the ciphertext.
        JsonDocument.Parse(row.SnapshotJson!).RootElement
            .GetProperty(nameof(OeProject.BcClientSecretEncrypted)).GetString()
            .Should().Be("[redacted]");
    }

    [Fact]
    public async Task Changing_only_project_discovery_cache_is_not_audited()
    {
        // The background ProjectDiscoveryWorker rewrites these columns with no HTTP
        // user; those writes must not flood the audit log with "unknown" churn.
        int projectId;
        await using (var seed = _db.NewContext())
        {
            var p = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor(name: null)))
        {
            var p = await ctx.OeProjects.FirstAsync(x => x.Id == projectId);
            p.DiscoveredExtensionsJson = "[]";
            p.DiscoveredAt = DateTime.UtcNow;
            p.DiscoveryError = null;
            p.UpdatedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync(r => r.EntityType == AuditEntityType.Project)).Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_project_is_not_audited()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            ctx.OeProjects.Add(new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS new", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync(r => r.EntityType == AuditEntityType.Project)).Should().Be(0);
    }

    [Fact]
    public async Task Creating_a_release_pipeline_writes_a_created_row()
    {
        int projectId, pipelineId, envId;
        await using (var seed = _db.NewContext())
        {
            var now = DateTime.UtcNow;
            var p = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = now, UpdatedAt = now };
            seed.OeProjects.Add(p);
            await seed.SaveChangesAsync();
            projectId = p.Id;

            var pipe = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build", CreatedAt = now, UpdatedAt = now };
            seed.OePipelines.Add(pipe);
            await seed.SaveChangesAsync();
            pipelineId = pipe.Id;

            var env = new OeProjectEnvironment { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Production", Type = "Production", FetchedAt = now };
            seed.OeProjectEnvironments.Add(env);
            await seed.SaveChangesAsync();
            envId = env.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            ctx.OeReleasePipelines.Add(new OeReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                Name = "CRONUS App → Production",
                BuildPipelineId = pipelineId,
                ProjectEnvironmentId = envId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.ReleasePipeline && r.Action == AuditAction.Created).SingleAsync())
            .ChangedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Changing_a_user_redacts_the_password_hash_in_the_snapshot()
    {
        int userId;
        await using (var seed = _db.NewContext())
        {
            var u = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "user@cronus.example",
                DisplayName = "CRONUS User",
                PasswordHash = "$2a$11$oldhasholdhasholdhasholdhasholdhasholdhasholdhasholdha",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            };
            seed.Users.Add(u);
            await seed.SaveChangesAsync();
            userId = u.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var u = await ctx.Users.FirstAsync(x => x.Id == userId);
            u.PasswordHash = "$2a$11$newhashnewhashnewhashnewhashnewhashnewhashnewhashnewha";
            u.Role = UserRole.Editor;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .Where(r => r.EntityType == AuditEntityType.User && r.Action == AuditAction.Updated)
            .SingleAsync();
        // The before-snapshot records the role change but never the BCrypt hash —
        // it's offline-cracking material and must not reach org Admins. See #476.
        row.SnapshotJson.Should().NotContain("oldhash");
        JsonDocument.Parse(row.SnapshotJson!).RootElement
            .GetProperty(nameof(User.PasswordHash)).GetString()
            .Should().Be("[redacted]");
    }

    /// <summary>
    /// The counter-test to the sign-in gate, and the one that matters most: the
    /// gate <em>suppresses</em> audit rows, so the failure mode is it quietly
    /// widening. Add any of these column names to <c>UserSignInColumns</c> and
    /// account disablement, privilege escalation to SiteAdmin, an email change
    /// or a password change all vanish from the audit log on a one-word edit.
    /// </summary>
    [Theory]
    [InlineData(nameof(User.Status))]
    [InlineData(nameof(User.IsSiteAdmin))]
    [InlineData(nameof(User.Email))]
    [InlineData(nameof(User.PasswordHash))]
    [InlineData(nameof(User.Role))]
    [InlineData(nameof(User.DisplayName))]
    public async Task A_lone_change_to_a_security_column_is_still_audited(string column)
    {
        var userId = await SeedUserAsync();
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var u = await ctx.Users.FirstAsync(x => x.Id == userId);
            switch (column)
            {
                case nameof(User.Status): u.Status = UserStatus.Disabled; break;
                case nameof(User.IsSiteAdmin): u.IsSiteAdmin = true; break;
                case nameof(User.Email): u.Email = "moved@cronus.example"; break;
                case nameof(User.PasswordHash): u.PasswordHash = "$2a$11$brandnewhashbrandnewhashbrandnewhashbrandnewhashbrandn"; break;
                case nameof(User.Role): u.Role = UserRole.Admin; break;
                case nameof(User.DisplayName): u.DisplayName = "Renamed"; break;
                default: throw new ArgumentOutOfRangeException(nameof(column), column, "unmapped column");
            }
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.Where(r => r.EntityType == AuditEntityType.User).ToListAsync();
        rows.Should().ContainSingle(r => r.Action == AuditAction.Updated && r.ChangedBy == "alice",
            $"a change to {column} is an edit to the account, not sign-in bookkeeping");
    }

    /// <summary>
    /// The behaviour the gate exists to produce, driven through the service that
    /// actually signs people in rather than by setting the column by hand. This
    /// is the version that survives someone adding a second column to the login
    /// save: the white-box tests below would stay green while the audit log
    /// quietly refilled with "unknown changed User #1".
    /// </summary>
    [Fact]
    public async Task A_real_sign_in_through_AuthService_writes_no_audit_row()
    {
        const string password = "Cronus!2345";
        await using (var seed = _db.NewContext())
        {
            var auth = NewAuthService(seed);
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "signs-in@cronus.example",
                DisplayName = "CRONUS User",
                // The real hasher on purpose, not TestPasswords: this sign-in runs
                // end to end through AuthService, so it starts from the hash
                // AuthService itself makes.
                PasswordHash = auth.HashPassword(password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor(name: null)))
        {
            var (outcome, user) = await NewAuthService(ctx)
                .TryLoginAsync("signs-in@cronus.example", password, "127.0.0.1");
            outcome.Should().Be(ALDevToolbox.Services.Account.LoginOutcome.Success);
            user!.LastLoginAt.Should().NotBeNull("the login must actually have stamped the column");
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync()).Should().Be(0);
    }

    private AuthService NewAuthService(AppDbContext ctx) =>
        new(ctx, NullLogger<AuthService>.Instance, TimeProvider.System);

    [Fact]
    public async Task An_invited_user_is_still_audited_as_created_despite_its_login_stamp()
    {
        // InviteService creates the User with LastLoginAt already set, so the
        // entry is Added rather than Modified. The gate returns early on state,
        // and this pins that it keeps doing so.
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            ctx.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "invited@cronus.example",
                DisplayName = "Invited",
                PasswordHash = "$2a$11$oldhasholdhasholdhasholdhasholdhasholdhasholdhasholdha",
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.Where(r => r.EntityType == AuditEntityType.User).SingleAsync())
            .Action.Should().Be(AuditAction.Created);
    }

    [Fact]
    public async Task Stamping_last_login_alone_writes_no_audit_row()
    {
        var userId = await SeedUserAsync();
        await ClearAuditAsync();

        // What a successful sign-in does, and all it does. The interceptor runs
        // before the auth cookie exists, so these rows were attributed to
        // "unknown" and buried every real change under sign-in churn.
        // .design/auth-and-audit.md already places logins in login_attempts,
        // outside the audit log.
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor(name: null)))
        {
            var u = await ctx.Users.FirstAsync(x => x.Id == userId);
            u.LastLoginAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_real_edit_alongside_the_login_stamp_is_still_audited()
    {
        var userId = await SeedUserAsync();
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var u = await ctx.Users.FirstAsync(x => x.Id == userId);
            u.LastLoginAt = DateTime.UtcNow;
            u.Role = UserRole.Editor;
            await ctx.SaveChangesAsync();
        }

        // The gate is narrow on purpose: it skips a save that is *only*
        // bookkeeping, never one that also changes the account.
        await using var read = _db.NewContext();
        var row = await read.AuditLog.Where(r => r.EntityType == AuditEntityType.User).SingleAsync();
        row.Action.Should().Be(AuditAction.Updated);
        row.ChangedBy.Should().Be("alice");
    }

    private async Task<int> SeedUserAsync()
    {
        await using var seed = _db.NewContext();
        var u = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "signs-in@cronus.example",
            DisplayName = "CRONUS User",
            PasswordHash = "$2a$11$oldhasholdhasholdhasholdhasholdhasholdhasholdhasholdha",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(u);
        await seed.SaveChangesAsync();
        return u.Id;
    }

    private async Task ClearAuditAsync()
    {
        await using var ctx = _db.NewContext();
        var rows = await ctx.AuditLog.ToListAsync();
        ctx.AuditLog.RemoveRange(rows);
        await ctx.SaveChangesAsync();
    }


    /// <summary>
    /// The write half of #554. Named on all three actions, and on the two that
    /// matter most it is the name at the time of the change: a rename records
    /// what the thing was called going in, and a deletion records the name of
    /// something that no longer exists to be looked up.
    /// </summary>
    [Fact]
    public async Task A_created_row_is_stamped_with_the_new_entity_name()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "CRONUS Standard";
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog.SingleAsync(r =>
            r.Action == AuditAction.Created && r.EntityType == AuditEntityType.RuntimeTemplate);
        row.EntityName.Should().Be("CRONUS Standard",
            "a Created row has no snapshot to fall back on, so the column is the only place the name can come from");
    }

    [Fact]
    public async Task A_rename_records_the_name_the_thing_had_going_in()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "Old Name";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var template = await ctx.RuntimeTemplates.SingleAsync(t => t.Id == templateId);
            template.Name = "New Name";
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog.SingleAsync(r => r.Action == AuditAction.Updated);
        row.EntityName.Should().Be("Old Name",
            "the row for a rename says what was renamed; the next row carries the new name");
    }

    [Fact]
    public async Task A_deleted_row_keeps_the_name_of_something_that_no_longer_exists()
    {
        int templateId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            template.Name = "Doomed Template";
            seed.RuntimeTemplates.Add(template);
            await seed.SaveChangesAsync();
            templateId = template.Id;
        }

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var template = await ctx.RuntimeTemplates.SingleAsync(t => t.Id == templateId);
            ctx.RuntimeTemplates.Remove(template);
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog.SingleAsync(r => r.Action == AuditAction.Deleted);
        row.EntityName.Should().Be("Doomed Template",
            "the table row is gone, so nothing downstream could resolve this name later");
    }

    /// <summary>
    /// An entity with no candidate name field must not fail the save. The picker
    /// reads through PropertyValues and its candidate list is wider than any one
    /// entity, so indexing a property the entity does not have would throw
    /// inside SaveChanges - taking the user's actual write down with it.
    /// </summary>
    [Fact]
    public async Task An_entity_with_no_name_field_still_saves_and_records_a_null_name()
    {
        int templateId, moduleId, orgId;
        await using (var seed = _db.NewContext())
        {
            var template = TemplateBuilder.Default("runtime-x");
            var module = ModuleBuilder.Default("mod-x");
            seed.RuntimeTemplates.Add(template);
            seed.Modules.Add(module);
            await seed.SaveChangesAsync();
            templateId = template.Id;
            moduleId = module.Id;
            orgId = template.OrganizationId;
        }

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("admin")))
        {
            ctx.RuntimeTemplateDefaultModules.Add(new RuntimeTemplateDefaultModule
            {
                OrganizationId = orgId,
                RuntimeTemplateId = templateId,
                ModuleId = moduleId,
                Ordering = 1,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog
            .SingleAsync(r => r.EntityType == AuditEntityType.RuntimeTemplateDefaultModule);
        row.EntityName.Should().BeNull("a join row of two foreign keys has no name of its own");
    }

    [Fact]
    public async Task Saving_system_settings_files_the_audit_row_under_no_organisation()
    {
        // The settings belong to the whole deployment. Filed under the acting
        // SiteAdmin's home org, they were readable by that org's Admins on
        // their own audit page (#1260).
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("root", TestDb.DefaultOrgId)))
        {
            var settings = await ctx.SystemSettings.FirstOrDefaultAsync();
            if (settings is null)
            {
                settings = new SystemSettings { Id = 1 };
                ctx.SystemSettings.Add(settings);
                await ctx.SaveChangesAsync();
            }
            settings.SmtpHost = "smtp.cronus.example";
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var rows = await read.AuditLog.IgnoreQueryFilters()
            .Where(r => r.EntityType == AuditEntityType.SystemSettings).ToListAsync();
        rows.Should().NotBeEmpty();
        rows.Should().AllSatisfy(r => r.OrganizationId.Should().BeNull());
    }

    [Fact]
    public async Task An_org_owned_row_is_still_filed_under_its_own_organisation()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("root", TestDb.OtherOrgId)))
        {
            ctx.RuntimeTemplates.Add(TemplateBuilder.Default("runtime-org"));
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.IgnoreQueryFilters()
                .SingleAsync(r => r.EntityType == AuditEntityType.RuntimeTemplate)).OrganizationId
            .Should().Be(TestDb.DefaultOrgId, "the row's own organisation wins over the request's");
    }

    [Fact]
    public async Task Changing_an_organisations_step_up_policy_writes_an_updated_row_filed_under_it()
    {
        // Loosening step-up has to leave a trace of who did it and when
        // (#1337). The acting SiteAdmin's home org differs from the subject so
        // the test also pins that the row is filed under the organisation
        // itself, not the request's.
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("root", TestDb.DefaultOrgId)))
        {
            var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.OtherOrgId);
            org.StepUpTools = ["Deliveries"];
            org.StepUpWindowMinutes = 30;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog.IgnoreQueryFilters()
            .SingleAsync(r => r.EntityType == AuditEntityType.Organization);
        row.Action.Should().Be(AuditAction.Updated);
        row.EntityId.Should().Be(TestDb.OtherOrgId);
        row.OrganizationId.Should().Be(TestDb.OtherOrgId);
        row.ChangedBy.Should().Be("root");
        using var snapshot = JsonDocument.Parse(row.SnapshotJson!);
        snapshot.RootElement.GetProperty(nameof(Organization.StepUpWindowMinutes)).GetInt32()
            .Should().Be(15, "the snapshot is the policy before the change");
    }

    [Theory]
    [InlineData(nameof(Organization.McpEnabled))]
    [InlineData(nameof(Organization.DisabledTools))]
    [InlineData(nameof(Organization.StorageQuotaMb))]
    public async Task Changing_an_organisations_mcp_tools_or_quota_is_audited(string property)
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
            switch (property)
            {
                case nameof(Organization.McpEnabled): org.McpEnabled = !org.McpEnabled; break;
                case nameof(Organization.DisabledTools): org.DisabledTools = ["Translator"]; break;
                case nameof(Organization.StorageQuotaMb): org.StorageQuotaMb = 512; break;
            }
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.IgnoreQueryFilters()
                .CountAsync(r => r.EntityType == AuditEntityType.Organization && r.EntityId == TestDb.DefaultOrgId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Renaming_an_organisation_is_not_audited()
    {
        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.OtherOrgId);
            org.Name = "CRONUS International";
            org.IsPending = !org.IsPending;
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        (await read.AuditLog.IgnoreQueryFilters().AnyAsync(r => r.EntityType == AuditEntityType.Organization))
            .Should().BeFalse("only the policy and quota columns are worth a row");
    }

    [Theory]
    [InlineData(nameof(OrganizationSettings.MachineTranslationApiKeyEncrypted))]
    [InlineData(nameof(OrganizationSettings.EntraClientSecretEncrypted))]
    [InlineData(nameof(OrganizationSettings.BcClientSecretEncrypted))]
    public async Task An_organisation_secret_is_redacted_in_the_audit_snapshot(string property)
    {
        int settingsId;
        await using (var seed = _db.NewContext())
        {
            var settings = await seed.OrganizationSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                seed.OrganizationSettings.Add(settings);
            }
            typeof(OrganizationSettings).GetProperty(property)!.SetValue(settings, "CfDJ8-ciphertext");
            await seed.SaveChangesAsync();
            settingsId = settings.Id;
        }
        await ClearAuditAsync();

        await using (var ctx = _db.NewContextWithAudit(NewInterceptor("alice")))
        {
            var settings = await ctx.OrganizationSettings.IgnoreQueryFilters().SingleAsync(s => s.Id == settingsId);
            typeof(OrganizationSettings).GetProperty(property)!.SetValue(settings, "CfDJ8-rotated");
            await ctx.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var row = await read.AuditLog.IgnoreQueryFilters()
            .SingleAsync(r => r.EntityType == AuditEntityType.OrganizationSettings);
        row.SnapshotJson.Should().NotContain("CfDJ8");
        using var snapshot = JsonDocument.Parse(row.SnapshotJson!);
        snapshot.RootElement.GetProperty(property).GetString().Should().Be("[redacted]");
    }

    [Fact]
    public void Every_encrypted_column_on_an_audited_type_is_redacted()
    {
        // A new *Encrypted column on an audited entity, without a matching line
        // in the redaction list, would copy its ciphertext into history.
        using var ctx = _db.NewContext();
        var checkedAny = false;
        foreach (var type in AuditInterceptor.AuditedTypeMap.Keys)
        {
            var secrets = type.GetProperties()
                .Where(p => p.Name.EndsWith("Encrypted", StringComparison.Ordinal) && p.PropertyType == typeof(string))
                .ToList();
            if (secrets.Count == 0) continue;

            var entity = Activator.CreateInstance(type)!;
            foreach (var secret in secrets) secret.SetValue(entity, "CfDJ8-ciphertext");
            type.GetProperty("Id")?.SetValue(entity, 1);
            var entry = ctx.Attach(entity); // tracked, never saved: the snapshot reads its original values
            var snapshot = AuditInterceptor.OriginalValuesToDict(entry);

            foreach (var secret in secrets)
            {
                snapshot.Should().ContainKey(secret.Name);
                snapshot[secret.Name].Should().Be("[redacted]", $"{type.Name}.{secret.Name} must not reach the audit log");
            }
            checkedAny = true;
        }
        checkedAny.Should().BeTrue("at least the settings types carry secrets today");
    }

    private static AuditInterceptor NewInterceptor(string name, int organizationId)
    {
        var http = new HttpContextAccessor();
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(ALDevToolbox.Services.HttpOrganizationContext.OrganizationIdClaim, organizationId.ToString()),
        }, "test"));
        http.HttpContext = ctx;
        return new AuditInterceptor(http);
    }

    private static AuditInterceptor NewInterceptor(string? name)
    {
        var http = new HttpContextAccessor();
        if (name is not null)
        {
            var ctx = new DefaultHttpContext();
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, "test"));
            http.HttpContext = ctx;
        }
        return new AuditInterceptor(http);
    }
}
