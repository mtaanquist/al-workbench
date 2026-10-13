using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Data;

/// <summary>
/// Writes an <see cref="AuditLogEntry"/> for every change to the entities listed in
/// <see cref="AuditedTypes"/>. The interceptor is scoped (it shares the lifetime of the
/// <see cref="AppDbContext"/> it intercepts) so per-request state — the pending list of
/// created entities — never leaks between concurrent SaveChanges calls.
///
/// Rationale and snapshot rules: see <c>.design/auth-and-audit.md</c>. Modified and
/// deleted rows snapshot their <c>OriginalValues</c> before the save. Created rows
/// don't have a "before", so their <c>SnapshotJson</c> is null and the row is written
/// in <see cref="SavedChangesAsync"/> after the database has assigned the primary key.
/// </summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = PersistenceJson.Options;

    // LoginAttempt is intentionally excluded: it's append-only telemetry written
    // on every login/forgot-password attempt and is already its own record. An
    // audit row for each insert would just duplicate the table.
    //
    // Single source of truth — both the audit gate and the entity-type
    // discriminator read from this dictionary (#78). Adding a new audited
    // entity needs one entry here, not two.
    // NOTE: only int-keyed entities may be added here. AuditLogEntry.EntityId is
    // an int and the EntityId stamps below narrow the "Id" value to int. The
    // long-keyed types (TranslationMemory*, every Object Explorer entity) would
    // need EntityId widened to long before they could be audited. #400
    internal static readonly IReadOnlyDictionary<Type, AuditEntityType> AuditedTypeMap =
        new Dictionary<Type, AuditEntityType>
        {
            [typeof(RuntimeTemplate)] = AuditEntityType.RuntimeTemplate,
            [typeof(WorkspaceExtension)] = AuditEntityType.WorkspaceExtension,
            [typeof(WorkspaceExtensionFolder)] = AuditEntityType.WorkspaceExtensionFolder,
            [typeof(WorkspaceExtensionFile)] = AuditEntityType.WorkspaceExtensionFile,
            [typeof(WorkspaceExtensionDependency)] = AuditEntityType.WorkspaceExtensionDependency,
            [typeof(ModuleExtensionFolder)] = AuditEntityType.ModuleExtensionFolder,
            [typeof(ModuleExtensionFile)] = AuditEntityType.ModuleExtensionFile,
            [typeof(RuntimeTemplateDefaultModule)] = AuditEntityType.RuntimeTemplateDefaultModule,
            [typeof(Module)] = AuditEntityType.Module,
            [typeof(ModuleDependency)] = AuditEntityType.ModuleDependency,
            [typeof(WellKnownDependency)] = AuditEntityType.WellKnownDependency,
            [typeof(ApplicationVersion)] = AuditEntityType.ApplicationVersion,
            [typeof(User)] = AuditEntityType.User,
            [typeof(SignupRequest)] = AuditEntityType.SignupRequest,
            [typeof(OrganizationSettings)] = AuditEntityType.OrganizationSettings,
            [typeof(OrganizationAsset)] = AuditEntityType.OrganizationAsset,
            [typeof(OrganizationFile)] = AuditEntityType.OrganizationFile,
            [typeof(SystemSettings)] = AuditEntityType.SystemSettings,
            [typeof(Backup)] = AuditEntityType.Backup,
            [typeof(Invite)] = AuditEntityType.Invite,
            [typeof(Recipe)] = AuditEntityType.Recipe,
            [typeof(RecipeFile)] = AuditEntityType.RecipeFile,
            [typeof(RecipeSuggestion)] = AuditEntityType.RecipeSuggestion,
            [typeof(RecipeSuggestionFile)] = AuditEntityType.RecipeSuggestionFile,
            [typeof(PersonalAccessToken)] = AuditEntityType.PersonalAccessToken,
            [typeof(OeReleasePipeline)] = AuditEntityType.ReleasePipeline,
            // Project is audited only for its BC connection/secret columns — see the
            // IsAuditableProjectChange gate below (discovery-cache writes from the
            // background worker and plain name edits are filtered out).
            [typeof(OeProject)] = AuditEntityType.Project,
            // Teams and membership are audited in full — who could see what, and
            // when that changed, is the whole point of the feature. No column gate:
            // a team has only a name, and a membership row only its manager flag.
            [typeof(Team)] = AuditEntityType.Team,
            [typeof(TeamMember)] = AuditEntityType.TeamMember,
            // Assigning a team to a project (or taking it away) changes who can see
            // the customer's source and builds — the single most audit-worthy write
            // in this feature.
            [typeof(OeProjectTeam)] = AuditEntityType.ProjectTeam,
            // An environment row is audited only for the settings a user changed in the
            // customer's tenant — see the IsAuditableEnvironmentChange gate below. Every
            // other column on it is fetched cache that a Refresh rewrites wholesale.
            [typeof(OeProjectEnvironment)] = AuditEntityType.ProjectEnvironment,
            // Deliberately absent: OeEnvironmentUpgradeAction
            // (oe_environment_upgrade_actions). That table is itself a log — who asked
            // for which platform-update action, when it fired, and what came back — and
            // it is what the per-environment activity feed reads. Auditing a log would
            // record the same event twice, and its own rows change several times as one
            // action moves pending → sent. The write that actually reaches the
            // customer's tenant still writes its own audit row from
            // ProjectConnectionService. See .design/saas-delivery.md and issue #657.
            // Also absent: OeEnvironmentUpgrade and OeEnvironmentUpgradeLine (#984). A
            // planned upgrade is a working list that stamps its own who-and-when on the
            // moves that matter (created, checked, marked done), and every action run from
            // it lands in the action log above; auditing the list's edits on top would
            // record a tick-and-untick as noise. See .design/environment-updates.md.
        };

    /// <summary>
    /// The Business Central connection/secret columns on <see cref="OeProject"/>. A
    /// change to any of these is the only reason a <c>Project</c> row is audited — the
    /// rest of the entity churns from the background discovery cache
    /// (<c>DiscoveredExtensionsJson</c>/<c>DiscoveredAt</c>/<c>DiscoveryError</c>, written
    /// by <c>ProjectDiscoveryWorker</c> with no HTTP user) and from ordinary name edits,
    /// none of which belongs in the audit log. The secret ciphertext itself is redacted
    /// by <see cref="OriginalValuesToDict"/>; this list is about <em>whether</em> to
    /// record a row, not what it contains. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    private static readonly HashSet<string> ProjectConnectionColumns = new()
    {
        nameof(OeProject.BcTenantId),
        nameof(OeProject.BcClientId),
        nameof(OeProject.BcClientSecretEncrypted),
        nameof(OeProject.BcClientSecretExpiresAt),
        nameof(OeProject.BcCredentialsUpdatedAt),
        nameof(OeProject.BcTimeZone),
        nameof(OeProject.BcConnectionVerifiedAt),
        // Visibility rides on the same gate: it isn't a connection column, but it is
        // the other thing about a project worth recording, and it changes through
        // ProjectService.SetAccessAsync alongside the oe_project_teams rows above.
        nameof(OeProject.Visibility),
    };

    /// <summary>
    /// The columns on <see cref="OeProjectEnvironment"/> worth an audit row: the ones a
    /// user changes deliberately, on someone else's production tenant, through this tool.
    /// <para>
    /// Everything else on this entity is <em>fetched cache</em> — status, version, family,
    /// the mirrored Business Central update window, the timestamps beside them — rewritten
    /// wholesale every time someone clicks Refresh environments. Auditing those would put
    /// a row per environment per refresh into the log and bury the changes that matter,
    /// which is the same reason <see cref="ProjectConnectionColumns"/> exists for Project.
    /// </para>
    /// <para>
    /// The delivery window is ours and is set by a person, so it is audited. The AppSource
    /// update cadence is audited because the write goes to the customer's tenant and the
    /// column is only refreshed as a consequence. Writes that never touch one of our rows
    /// at all — the platform target version, Microsoft 365 licence access, Microsoft's own
    /// update window — leave no audit row by this route and are logged instead; see
    /// <c>.design/saas-delivery.md</c>.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> EnvironmentSettingColumns = new()
    {
        nameof(OeProjectEnvironment.UpdateWindowStart),
        nameof(OeProjectEnvironment.UpdateWindowEnd),
        nameof(OeProjectEnvironment.AppSourceAppsUpdateCadence),
    };

    /// <summary>
    /// Sign-in bookkeeping on <see cref="User"/>. A successful login stamps
    /// <c>LastLoginAt</c> and nothing else, and <c>.design/auth-and-audit.md</c>
    /// already puts logins outside the audit log — they belong to
    /// <c>login_attempts</c>. Without this gate every sign-in wrote a <c>User</c>
    /// row attributed to <c>"unknown"</c>, because the interceptor runs before the
    /// auth cookie exists; on a busy org that is most of the audit log, and it
    /// crowded every real change off the /admin dashboard's activity panel.
    ///
    /// Deliberately narrow: a save that touches any other column on the user is
    /// a real edit and is still audited in full.
    /// </summary>
    private static readonly HashSet<string> UserSignInColumns = new()
    {
        nameof(User.LastLoginAt),
    };

    private readonly IHttpContextAccessor _http;
    private List<PendingAddition> _pendingAdditions = new();

    public AuditInterceptor(IHttpContextAccessor http)
    {
        _http = http;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var ctx = eventData.Context;
        if (ctx is null)
        {
            return new ValueTask<InterceptionResult<int>>(result);
        }

        // Reset on every save so we never carry rows across two SaveChanges calls.
        _pendingAdditions = new List<PendingAddition>();

        var changedBy = ResolveChangedBy();
        var changedByUserId = ResolveUserId();
        var organizationId = ResolveOrganizationId();
        var timestamp = DateTime.UtcNow;
        var entries = ctx.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            if (!AuditedTypeMap.ContainsKey(entry.Entity.GetType()))
            {
                continue;
            }

            // Column-scoped: a Project row is only worth an audit entry when its BC
            // connection/secret actually changed. Skip discovery-cache churn, name
            // edits, and soft-deletes (and creation, which has no connection yet).
            if (entry.Entity is OeProject && !IsAuditableProjectChange(entry))
            {
                continue;
            }

            // Column-scoped, same idea: an environment row churns on every Refresh,
            // and only the settings a person deliberately changed are worth a row.
            if (entry.Entity is OeProjectEnvironment && !IsAuditableEnvironmentChange(entry))
            {
                continue;
            }

            // Column-scoped, same idea: stamping LastLoginAt is a sign-in, not an
            // edit to the account, and sign-ins are recorded in login_attempts.
            if (entry.Entity is User && IsSignInBookkeeping(entry))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    // We can't write the audit row yet — the primary key hasn't been
                    // assigned. Stash the entry and emit the row in SavedChangesAsync.
                    _pendingAdditions.Add(new PendingAddition(entry, timestamp, changedBy, changedByUserId, organizationId));
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    // The reconciliation services rewrite UpdatedAt unconditionally on
                    // every save, so an admin clicking Save with no real edits would
                    // otherwise add a noise row to the audit log. Treat "only UpdatedAt
                    // changed" as a no-op for audit purposes; deletions always pass
                    // through because their OriginalValues represent the row that's
                    // about to disappear. Only apply this filter to entity types that
                    // actually declare an UpdatedAt property — otherwise the predicate
                    // collapses to "no properties modified" and accidentally suppresses
                    // legitimate single-field updates on other entities.
                    if (entry.State == EntityState.Modified
                        && entry.Metadata.FindProperty(nameof(RuntimeTemplate.UpdatedAt)) is not null
                        && !entry.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(RuntimeTemplate.UpdatedAt)))
                    {
                        break;
                    }
                    var action = entry.State == EntityState.Modified
                        ? AuditAction.Updated
                        : AuditAction.Deleted;
                    var snapshot = BuildOriginalSnapshot(entry, entries);
                    ctx.Add(new AuditLogEntry
                    {
                        Timestamp = timestamp,
                        ChangedBy = changedBy,
                        ChangedByUserId = changedByUserId,
                        OrganizationId = ResolveEntityOrganizationId(entry, organizationId),
                        EntityType = MapEntityType(entry.Entity.GetType()),
                        // Convert (not unbox-cast) so a long-keyed entity added to
                        // AuditedTypeMap by mistake degrades to an overflow at the
                        // edge instead of an InvalidCastException unboxing long→int. #400
                        EntityId = Convert.ToInt32(entry.OriginalValues["Id"]!),
                        Action = action,
                        // Original, not current: on a rename, the audit row for
                        // that rename should say what the thing was called going
                        // in. The row after it carries the new name.
                        EntityName = ResolveEntityName(entry.OriginalValues),
                        SnapshotJson = snapshot,
                    });
                    break;
            }
        }

        return new ValueTask<InterceptionResult<int>>(result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        // Take a local snapshot and replace the field. The follow-up SaveChanges below
        // re-enters SavingChangesAsync which resets the field; iterating against a
        // captured local keeps that interaction safe.
        var pending = _pendingAdditions;
        _pendingAdditions = new List<PendingAddition>();

        if (pending.Count == 0)
        {
            return result;
        }

        var ctx = eventData.Context;
        if (ctx is null)
        {
            return result;
        }

        foreach (var addition in pending)
        {
            ctx.Add(new AuditLogEntry
            {
                Timestamp = addition.Timestamp,
                ChangedBy = addition.ChangedBy,
                ChangedByUserId = addition.ChangedByUserId,
                OrganizationId = ResolveEntityOrganizationId(addition.Entry, addition.OrganizationId),
                EntityType = MapEntityType(addition.Entry.Entity.GetType()),
                // See the EntityId note above — Convert, not unbox-cast. #400
                EntityId = Convert.ToInt32(addition.Entry.CurrentValues["Id"]!),
                Action = AuditAction.Created,
                EntityName = ResolveEntityName(addition.Entry.CurrentValues),
                SnapshotJson = null,
            });
        }

        await ctx.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Builds the JSON "before" snapshot for a modified or deleted row. Parent
    /// entities (template, module, extension) inline their child collection's
    /// pre-save state so an investigator can read one snapshot row instead of
    /// joining several. <see cref="WorkspaceExtensionFile"/> rows replace their
    /// (potentially large) <c>Content</c> column with a SHA-256 hash so the
    /// audit log doesn't inflate with every AL file edit — see
    /// <c>.design/domain-model.md</c>.
    /// </summary>
    private static string BuildOriginalSnapshot(EntityEntry entry, List<EntityEntry> allEntries)
    {
        var snapshot = OriginalValuesToDict(entry);
        var parentId = (int)entry.OriginalValues["Id"]!;

        switch (entry.Entity)
        {
            case RuntimeTemplate:
                snapshot["extensions"] = CollectChildren<WorkspaceExtension>(allEntries, "TemplateId", parentId);
                break;
            case WorkspaceExtension:
                snapshot["folders"] = CollectChildren<WorkspaceExtensionFolder>(allEntries, "WorkspaceExtensionId", parentId);
                snapshot["dependencies"] = CollectChildren<WorkspaceExtensionDependency>(allEntries, "WorkspaceExtensionId", parentId);
                break;
            case WorkspaceExtensionFolder:
                snapshot["files"] = CollectChildren<WorkspaceExtensionFile>(allEntries, "WorkspaceExtensionFolderId", parentId);
                break;
            case ModuleExtensionFolder:
                snapshot["files"] = CollectChildren<ModuleExtensionFile>(allEntries, "ModuleExtensionFolderId", parentId);
                break;
            case Module:
                snapshot["dependencies"] = CollectChildren<ModuleDependency>(allEntries, "ModuleId", parentId);
                snapshot["extension_folders"] = CollectChildren<ModuleExtensionFolder>(allEntries, "ModuleId", parentId);
                break;
            case Recipe:
                snapshot["files"] = CollectChildren<RecipeFile>(allEntries, "RecipeId", parentId);
                break;
            case RecipeSuggestion:
                snapshot["files"] = CollectChildren<RecipeSuggestionFile>(allEntries, "RecipeSuggestionId", parentId);
                break;
        }

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    /// <summary>
    /// Pre-save snapshot of every modified-or-deleted child of <typeparamref name="TChild"/>
    /// pointing at <paramref name="parentId"/> via <paramref name="fkName"/>,
    /// ordered by the child's <c>Ordering</c> column. Six places used to
    /// inline this; one method now.
    /// </summary>
    private static List<Dictionary<string, object?>> CollectChildren<TChild>(
        IReadOnlyList<EntityEntry> entries, string fkName, int parentId) =>
        entries
            .Where(e => e.Entity is TChild
                        && e.State != EntityState.Added
                        && (int)e.OriginalValues[fkName]! == parentId)
            .OrderBy(e => (int)e.OriginalValues["Ordering"]!)
            .Select(OriginalValuesToDict)
            .ToList();

    /// <summary>
    /// Materialises an entry's original values into a dictionary, replacing
    /// <see cref="WorkspaceExtensionFile.Content"/> with a SHA-256 hash so the audit log
    /// stays compact even when files contain large AL bodies. Secret columns —
    /// the encrypted SMTP password, off-site keys and GitHub App credentials on
    /// <see cref="SystemSettings"/>, the org MT API key, a user's repository PAT and
    /// BCrypt password hash, and a
    /// project's BC client secret — are replaced with a fixed sentinel so the audit
    /// log never captures secret history (ciphertext would leak the structure of the
    /// protected blob; the password hash is offline-cracking material). See #476/#485.
    /// </summary>
    internal static Dictionary<string, object?> OriginalValuesToDict(EntityEntry entry)
    {
        var dict = new Dictionary<string, object?>();
        var hashContent = entry.Entity is WorkspaceExtensionFile or ModuleExtensionFile or OrganizationFile;
        var hashRecipeContent = entry.Entity is RecipeFile or RecipeSuggestionFile;
        var hashAssetBytes = entry.Entity is OrganizationAsset;
        // SystemSettings carries the encrypted SMTP password, the encrypted
        // off-site storage access/secret keys, and the GitHub App's client secret
        // and private key. None of the ciphertext lands in
        // audit history — capturing it would leak the structure of the protected
        // blob and preserve it long after a SiteAdmin clears the keys. See #485.
        var redactSystemSecrets = entry.Entity is SystemSettings;
        // OrganizationSettings carries the machine-translation API key, which must
        // never land in audit history.
        var redactOrgSecrets = entry.Entity is OrganizationSettings;
        // A user's encrypted repository PAT is redacted the same way.
        var redactRepoToken = entry.Entity is UserRepositoryToken;
        // A GitHub account link carries the user-to-server token pair. The row type
        // is not in AuditedTypeMap today, so nothing reaches this method through it
        // yet - the branch is here so adding it later cannot quietly start copying
        // ciphertext into history. See .design/github-integration.md.
        var redactGitHubUserTokens = entry.Entity is UserExternalLogin;
        // A customer's encrypted BC S2S client secret on the project never lands in history.
        var redactProjectBcSecret = entry.Entity is OeProject;
        // A user's BCrypt password hash is offline-cracking material — redact it so
        // org Admins can't harvest it (including old hashes after a reset) from the
        // audit log. See #476.
        var redactPasswordHash = entry.Entity is User;
        foreach (var property in entry.OriginalValues.Properties)
        {
            var value = entry.OriginalValues[property.Name];
            if (hashContent && property.Name == nameof(WorkspaceExtensionFile.Content) && value is string s)
            {
                dict["ContentSha256"] = Sha256(s);
            }
            else if (hashRecipeContent && property.Name == nameof(RecipeFile.Content) && value is string sc)
            {
                dict["ContentSha256"] = Sha256(sc);
            }
            else if (hashAssetBytes && property.Name == nameof(OrganizationAsset.Content) && value is byte[] bytes)
            {
                dict["ContentSha256"] = Sha256Bytes(bytes);
            }
            else if (redactSystemSecrets && property.Name is nameof(SystemSettings.SmtpPasswordEncrypted)
                         or nameof(SystemSettings.OffsiteAccessKeyEncrypted)
                         or nameof(SystemSettings.OffsiteSecretKeyEncrypted)
                         or nameof(SystemSettings.EntraClientSecretEncrypted)
                         or nameof(SystemSettings.GitHubClientSecretEncrypted)
                         or nameof(SystemSettings.GitHubPrivateKeyEncrypted)
                         or nameof(SystemSettings.GitHubWebhookSecretEncrypted))
            {
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else if (redactPasswordHash && property.Name == nameof(User.PasswordHash))
            {
                dict[property.Name] = string.IsNullOrEmpty(value as string) ? value : "[redacted]";
            }
            else if (redactOrgSecrets && property.Name is nameof(OrganizationSettings.MachineTranslationApiKeyEncrypted)
                         or nameof(OrganizationSettings.EntraClientSecretEncrypted)
                         or nameof(OrganizationSettings.BcClientSecretEncrypted))
            {
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else if (redactRepoToken && property.Name == nameof(UserRepositoryToken.TokenEncrypted))
            {
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else if (redactGitHubUserTokens && property.Name is nameof(UserExternalLogin.AccessTokenEncrypted)
                         or nameof(UserExternalLogin.RefreshTokenEncrypted)
                         or nameof(UserExternalLogin.AccessTokenExpiresAt))
            {
                // The expiry is not itself a secret, but it is part of the same
                // token record and turns over on every silent refresh; keeping it
                // would fill history with churn that means nothing on its own.
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else if (redactProjectBcSecret && property.Name == nameof(OeProject.BcClientSecretEncrypted))
            {
                dict[property.Name] = value is null ? null : "[redacted]";
            }
            else
            {
                dict[property.Name] = value;
            }
        }
        return dict;
    }

    private static string Sha256Bytes(byte[] value)
    {
        var bytes = SHA256.HashData(value);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// True when a tracked <see cref="User"/> change is nothing but sign-in
    /// bookkeeping — every modified column is in
    /// <see cref="UserSignInColumns"/>. Added and Deleted rows are never
    /// bookkeeping, so they fall through and are audited as usual.
    /// </summary>
    private static bool IsSignInBookkeeping(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified) return false;
        var modified = entry.Properties.Where(p => p.IsModified).ToList();
        return modified.Count > 0 && modified.All(p => UserSignInColumns.Contains(p.Metadata.Name));
    }

    /// <summary>
    /// True when a tracked <see cref="OeProject"/> change should be audited: a Modified
    /// row where at least one <see cref="ProjectConnectionColumns">BC connection or
    /// visibility column</see> actually changed. Everything else about a project — creation, deletion,
    /// discovery-cache writes, name edits — is deliberately not audited (see the map note).
    /// </summary>
    private static bool IsAuditableProjectChange(EntityEntry entry) =>
        entry.State == EntityState.Modified
        && entry.Properties.Any(p => p.IsModified && ProjectConnectionColumns.Contains(p.Metadata.Name));

    private static bool IsAuditableEnvironmentChange(EntityEntry entry) =>
        entry.State == EntityState.Modified
        && entry.Properties.Any(p => p.IsModified && EnvironmentSettingColumns.Contains(p.Metadata.Name));

    private static AuditEntityType MapEntityType(Type t) =>
        AuditedTypeMap.TryGetValue(t, out var kind)
            ? kind
            : throw new InvalidOperationException($"Entity type {t.Name} is not audited.");

    /// <summary>
    /// Composes <c>"display_name &lt;email&gt;"</c> for the audit row when both
    /// claims are present, falling back to the display name alone, then to
    /// <c>"unknown"</c> for seed-time inserts (no HttpContext).
    /// </summary>
    private string ResolveChangedBy()
    {
        var principal = _http.HttpContext?.User;
        var name = principal?.Identity?.Name;
        var email = principal?.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email))
        {
            return $"{name} <{email}>";
        }
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private int? ResolveUserId()
    {
        var value = _http.HttpContext?.User?.FindFirst(HttpOrganizationContext.UserIdClaim)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    private int? ResolveOrganizationId()
    {
        var value = _http.HttpContext?.User?.FindFirst(HttpOrganizationContext.OrganizationIdClaim)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// The affected row's human name, for the audit log's sentence — see
    /// <see cref="AuditEntityName"/> and issue #554. Reads through
    /// <see cref="PropertyValues"/> rather than off the entity so it works the
    /// same for a deleted row, whose original values are all that is left of it.
    ///
    /// <para>Guarded on <c>Properties</c> because the indexer throws for a
    /// property the entity does not have, and the candidate list is deliberately
    /// wider than any one entity.</para>
    /// </summary>
    private static string? ResolveEntityName(PropertyValues values)
    {
        var present = values.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return AuditEntityName.From(field =>
            present.Contains(field) ? values[field] as string : null);
    }

    /// <summary>
    /// Pulls the entity's own <c>OrganizationId</c>, falling back to the
    /// request-scoped value when the column is still unset. An audited type
    /// with no organisation column at all (<see cref="SystemSettings"/>,
    /// <see cref="Backup"/>) belongs to the whole deployment, so its row is
    /// filed under no organisation: filing it under the acting SiteAdmin's
    /// home organisation showed the SMTP and off-site storage settings to
    /// that organisation's Admins on their audit page (#1260). Rows with no
    /// organisation are read only by the SiteAdmin audit view.
    /// </summary>
    private static int? ResolveEntityOrganizationId(EntityEntry entry, int? fallback)
    {
        var values = entry.State == EntityState.Added ? entry.CurrentValues : entry.OriginalValues;
        if (!values.Properties.Any(p => p.Name == "OrganizationId")) return null;
        var value = values["OrganizationId"];
        if (value is int orgId && orgId > 0) return orgId;
        return fallback;
    }

    private sealed record PendingAddition(
        EntityEntry Entry,
        DateTime Timestamp,
        string ChangedBy,
        int? ChangedByUserId,
        int? OrganizationId);
}
