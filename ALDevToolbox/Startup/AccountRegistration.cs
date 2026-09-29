using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Startup;

/// <summary>
/// Accounts and sign-in support: users, invites, MFA, passkeys and personal
/// access tokens. See .design/auth-and-audit.md.
/// </summary>
public static class AccountRegistration
{
    /// <summary>Registers the account services and the WebAuthn (passkey) configuration.</summary>
    public static IServiceCollection AddAccountServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ALDevToolbox.Services.Account.AuthService>();
        services.AddScoped<ALDevToolbox.Services.Account.EntraSignInService>();
        services.AddScoped<ALDevToolbox.Services.Account.UserAdministrationService>();
        services.AddScoped<ALDevToolbox.Services.Account.PasswordResetService>();
        services.AddScoped<ALDevToolbox.Services.Account.RecoveryCodeService>();
        services.AddScoped<ALDevToolbox.Services.Account.TotpService>();
        services.AddScoped<ALDevToolbox.Services.Account.EmailMfaService>();
        services.AddScoped<ALDevToolbox.Services.Account.PendingSignupService>();
        services.AddScoped<ALDevToolbox.Services.Account.PasskeyService>();
        services.AddScoped<ALDevToolbox.Services.Account.PersonalAccessTokenService>();
        services.AddScoped<ALDevToolbox.Services.Account.UserRepositoryTokenService>();
        // WebAuthn (passkeys). RP id / origins live in configuration; if RpId isn't
        // set the passkey routes refuse with a clear error and the /account UI hides
        // the section. See .design/auth-and-audit.md for the deployment requirement.
        var webAuthnConfig = ALDevToolbox.Services.Account.WebAuthnConfig.FromConfiguration(configuration);
        services.AddSingleton(webAuthnConfig);
        services.AddFido2(options =>
        {
            options.ServerDomain = string.IsNullOrEmpty(webAuthnConfig.RpId) ? "localhost" : webAuthnConfig.RpId;
            options.ServerName = webAuthnConfig.RpName;
            options.Origins = webAuthnConfig.Origins.Count > 0
                ? new HashSet<string>(webAuthnConfig.Origins)
                : new HashSet<string> { "https://localhost" };
            options.TimestampDriftTolerance = 300_000;
        });
        services.AddScoped<AccountService>();
        services.AddScoped<InviteService>();
        return services;
    }
}
