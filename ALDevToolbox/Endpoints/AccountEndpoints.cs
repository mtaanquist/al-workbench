using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Endpoints;

internal static class AccountEndpoints
{
    /// <summary>
    /// Public entry point kept stable for <c>Program.cs</c>. The registrations
    /// are split across sibling extension classes along the original banner
    /// boundaries (auth/signup, MFA, passkeys + PATs); this method just chains
    /// them so every route still lands on the same group with the same filters.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAccountAuthEndpoints();
        app.MapAccountMfaEndpoints();
        app.MapAccountPasskeyEndpoints();
        return app;
    }

    /// <summary>
    /// Notifies an organisation's active admins that a signup is awaiting
    /// approval. Shared verbatim between the unverified <c>/auth/signup</c> and
    /// the email-first <c>/auth/signup/complete</c> flows. SMTP failures log a
    /// warning and never roll back the signup.
    /// </summary>
    /// <remarks>
    /// The <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
    /// read is a sanctioned pre-auth cross-org lookup: the signing-up visitor is
    /// not yet a member of <paramref name="org"/>, so the tenant filter would
    /// otherwise hide its admins. Kept exactly as the original call sites had it.
    /// </remarks>
    internal static async Task NotifyAdminsOfPendingSignupAsync(
        HttpContext ctx,
        AppDbContext db,
        IEmailService email,
        EmailRenderer emailRenderer,
        PublicOrigin publicOrigin,
        Organization org,
        User user,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            // Fence category 1 (pre-auth routing): pinned to u.OrganizationId == org.Id, the org
            // the visitor just signed up into.
            var admins = await db.Users.IgnoreQueryFilters()
                .Where(u => u.OrganizationId == org.Id
                            && u.Role == UserRole.Admin
                            && u.Status == UserStatus.Active)
                .ToListAsync(ct);
            foreach (var admin in admins)
            {
                var url = $"{publicOrigin.For(ctx)}{RouteConstants.AdminUsers}";
                var content = await SignupPendingEmail.RenderAsync(emailRenderer, admin.DisplayName, user.Email, org.Name, url, ct);
                await email.SendAsync(admin.Email, content, EmailPurpose.SignupPendingNotice, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to email admins about pending signup {Email}.", user.Email);
        }
    }

    /// <summary>
    /// Loads the current user (ignoring the tenant filter so the self-service
    /// row is always reachable) and verifies the supplied password. On mismatch,
    /// writes the standard "Password is incorrect." redirect to the account page
    /// and returns <c>null</c>; otherwise returns the loaded user. Shared by the
    /// TOTP-disable, email-MFA-disable and recovery-regenerate handlers. A
    /// Microsoft-linked account with a fresh step-up skips the password check
    /// (<see cref="MayConfirmWithoutPasswordAsync"/>).
    /// </summary>
    internal static async Task<User?> LoadUserAndVerifyPasswordAsync(
        HttpContext ctx,
        AppDbContext db,
        AuthService auth,
        IOrganizationContext org,
        string password,
        CancellationToken ct)
    {
        // Fence category 4 (explicitly scoped user-id lookup): pinned to the signed-in
        // user's own id.
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == org.CurrentUserId!.Value, ct);
        if (await MayConfirmWithoutPasswordAsync(ctx, db, user.Id, ct)) return user;
        if (!auth.VerifyPassword(password, user.PasswordHash))
        {
            ctx.Response.Redirect($"{RouteConstants.AccountSecurity}?{RouteConstants.ErrQuery}=Password&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Password is incorrect.")}");
            return null;
        }
        return user;
    }

    /// <summary>
    /// True when the signed-in user may confirm a two-step change without
    /// their password: the account has a Microsoft sign-in linked and the
    /// session proved a second factor within the organisation's step-up
    /// window. An account created on first Microsoft sign-in has a random
    /// password nobody knows, so without this it could turn on an
    /// authenticator app but never turn it off again (#1259). A fresh step-up
    /// is a stronger proof than the password it stands in for. Accounts with
    /// no Microsoft link keep the password check alone.
    /// </summary>
    internal static async Task<bool> MayConfirmWithoutPasswordAsync(
        HttpContext ctx, AppDbContext db, int userId, CancellationToken ct)
    {
        var tools = ctx.RequestServices.GetRequiredService<Services.Tools.ToolEnablement>();
        if (!await tools.IsFreshAsync(ctx.User, ct)) return false;
        // Filtered: the signed-in user's own links, inside their org.
        return await db.UserExternalLogins.AsNoTracking()
            .AnyAsync(l => l.UserId == userId && l.Provider == EntraSignInService.ProviderName, ct);
    }
}
