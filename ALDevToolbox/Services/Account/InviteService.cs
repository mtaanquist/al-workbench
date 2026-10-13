using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Owner of the invite-by-email flow (Milestone P4.19). Admins create
/// single-use invite tokens scoped to their organisation; invitees accept by
/// supplying a display name and password and are activated directly into the
/// inviting organisation with the assigned role — no admin re-approval.
///
/// Tokens are random 32-byte hex strings; only their SHA-256 hash lands in
/// the database (see <c>invites.token_hash</c>). The raw token is returned
/// from <see cref="CreateAsync"/> for the caller to email and never persisted.
/// </summary>
public sealed class InviteService
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<InviteService> _logger;

    public InviteService(
        AppDbContext db,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<InviteService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Issues a single-use invite for the given email + role, scoped to the
    /// acting admin's organisation. Returns the plaintext token (the caller
    /// emails it) and the persisted row's id. Rejects invites for emails that
    /// already have a user in the same organisation; the admin should use the
    /// existing <c>/admin/users</c> controls for that account instead.
    /// </summary>
    public async Task<(string Token, int InviteId)> CreateAsync(
        string email,
        UserRole role,
        string? welcomeMessage,
        CancellationToken ct = default)
    {
        var actingOrgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("InviteService.CreateAsync requires a signed-in admin.");
        var invitedByUserId = _orgContext.CurrentUserId
            ?? throw new InvalidOperationException("InviteService.CreateAsync requires a signed-in admin.");

        var errors = new Dictionary<string, string>();
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (!EmailAddress.HasValidShape(normalised))
        {
            errors["Email"] = "Enter a valid email address.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        // Fence category 4 (explicitly scoped org-id lookup): existence-only probe pinned to
        // u.OrganizationId == actingOrgId from the signed-in admin's context.
        var existing = await _db.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.OrganizationId == actingOrgId && u.Email == normalised, ct);
        if (existing)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Email"] = "Someone with that email is already in your organisation."
            });
        }

        // Supersede any still-pending invite for the same email in this org so
        // the most recent admin click is the canonical invite. Older rows stay
        // for audit history with their RevokedAt stamped.
        var now = _clock.GetUtcNow().UtcDateTime;
        var stale = await _db.Invites.IgnoreQueryFilters()
            .Where(i => i.OrganizationId == actingOrgId
                        && i.Email == normalised
                        && i.AcceptedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var s in stale)
        {
            s.RevokedAt = now;
        }

        var (raw, hash) = TokenIssuer.Issue();
        var trimmedMessage = string.IsNullOrWhiteSpace(welcomeMessage) ? null : welcomeMessage.Trim();
        var invite = new Invite
        {
            OrganizationId = actingOrgId,
            Email = normalised,
            Role = role,
            WelcomeMessage = trimmedMessage,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = now + InviteLifetime,
            InvitedByUserId = invitedByUserId,
        };
        _db.Invites.Add(invite);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Invite {InviteId} created for {Email} in org {OrgId} as {Role}.",
            invite.Id, normalised, actingOrgId, role);
        return (raw, invite.Id);
    }

    /// <summary>Soft-revokes a still-pending invite. Idempotent for already-revoked or accepted rows.</summary>
    public async Task RevokeAsync(int inviteId, CancellationToken ct = default)
    {
        var actingOrgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("InviteService.RevokeAsync requires a signed-in admin.");
        // Fence category 4 (explicitly scoped org-id lookup): the row is re-checked against
        // actingOrgId immediately below, so a foreign invite id is refused.
        var invite = await _db.Invites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null || invite.OrganizationId != actingOrgId)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["InviteId"] = "Invite not found in this organisation." });
        }
        if (invite.AcceptedAt is not null || invite.RevokedAt is not null) return;
        invite.RevokedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Invite {InviteId} revoked by org {OrgId}.", invite.Id, actingOrgId);
    }

    /// <summary>
    /// Looks up a still-valid invite by its plaintext token. Returns null for
    /// unknown / expired / consumed / revoked tokens. Bypasses the org query
    /// filter — the invitee is not signed in yet.
    /// </summary>
    public async Task<Invite?> FindValidByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = TokenIssuer.Sha256Hex(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 1 (pre-auth routing): the invitee is not signed in yet; pinned to
        // the invite's own token hash.
        var row = await _db.Invites.IgnoreQueryFilters()
            .Include(i => i.Organization)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (row is null) return null;
        if (row.AcceptedAt is not null || row.RevokedAt is not null) return null;
        if (row.ExpiresAt <= now) return null;
        return row;
    }

    /// <summary>
    /// Consumes an invite token: creates an Active user inside the inviting
    /// organisation, marks the invite accepted. Returns the new user. Throws
    /// <see cref="PlanValidationException"/> for invalid tokens or when the
    /// supplied display name / password fail validation, and when any account
    /// in any organisation already uses the invited email: emails are unique
    /// deployment-wide (issue #1207). That check runs here rather than in
    /// <see cref="CreateAsync"/> so that only the invitee, who owns the
    /// address, learns it is taken; an admin inviting addresses learns nothing
    /// about other organisations. The invite stays valid, so it works once the
    /// clash is settled.
    /// </summary>
    public async Task<User> AcceptAsync(
        string token,
        string displayName,
        string password,
        CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var trimmedName = (displayName ?? string.Empty).Trim();
        if (trimmedName.Length is < 2 or > 80) errors["DisplayName"] = "Full name must be 2-80 characters.";
        ALDevToolbox.Services.Account.AuthService.ValidatePassword(password, errors);
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var invite = await FindValidByTokenAsync(token, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Token"] = "This invite link is no longer valid. Ask the admin for a new one."
            });

        var now = _clock.GetUtcNow().UtcDateTime;
        // Fence category 6 (existence-only uniqueness probe): emails are unique
        // deployment-wide; projects a bool, never a row.
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == invite.Email, ct))
        {
            throw EmailTaken();
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(password, ALDevToolbox.Services.Account.AuthService.BcryptWorkFactor);
        var user = new User
        {
            OrganizationId = invite.OrganizationId,
            Organization = invite.Organization,
            Email = invite.Email,
            PasswordHash = passwordHash,
            DisplayName = trimmedName,
            Role = invite.Role,
            Status = UserStatus.Active,
            CreatedAt = now,
            LastLoginAt = now,
        };
        _db.Users.Add(user);
        invite.AcceptedAt = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, "IX_users_email"))
        {
            // Lost a race to a signup or another invite for the same address.
            throw EmailTaken();
        }
        _logger.LogInformation("Invite {InviteId} accepted; user {UserId} ({Email}) activated in org {OrgId}.",
            invite.Id, user.Id, user.Email, invite.OrganizationId);
        return user;
    }

    private static PlanValidationException EmailTaken() => new(new Dictionary<string, string>
    {
        ["Email"] = "You already have an account with this email. Sign in instead.",
    });
}
