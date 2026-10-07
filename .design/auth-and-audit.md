# Auth and audit

## Auth model

Email/password accounts scoped to organisations. Three roles:

- `User` — can use the project generator.
- `Editor` — additionally sees the content-authoring admin pages (templates, modules, catalogue, cookbook, application versions, object explorer including releases import), but **not** the Administration tab, the Admin dashboard, the audit log, or the storage-quota footer.
- `Admin` — full control of the organisation, including users, invites, identity, MCP, OAuth clients, export, audit, and content authoring.

There is no superuser; an admin only ever sees their own organisation. The "last active admin" guard in `UserAdministrationService.ChangeRoleAsync` blocks any demotion away from `Admin` (to `Editor` or `User`) when the row is the sole active admin in the org — Editors do not count as admins for that guard.

**Multi-tenancy.** Every editable entity carries an `organization_id`. EF Core query filters on `AppDbContext` scope reads to the acting user's organisation; bypassing them with `IgnoreQueryFilters()` is allowed only inside six sanctioned categories, each with an invariant it must satisfy: **(1) pre-auth routing** — login, signup, password reset, magic link, invite acceptance, bearer-token validation, bootstrap and the Microsoft sign-in callback, where no cookie exists yet and the read's job is to route one sign-in to exactly one organisation; **(2) the SiteAdmin cross-org console**, which must call `RequireSiteAdmin()` (or sit behind `[Authorize(Roles = SiteAdminRole)]`) before crossing the fence; **(3) startup and schedulers with no request org**, which must pin the org id they act for (an `AmbientOrganizationScope` per org, or a predicate naming the row's own id); **(4) explicitly scoped org-id or user-id lookups derived from the authenticated principal**, where the predicate literally names the id (`o.Id == orgId`, `u.Id == userId`, `x.OrganizationId == actingOrgId`) and that id never comes from unvalidated input; **(5) system-org fork reads in `TemplateImportService`**, pinned to `OrganizationId == systemOrgId` with writes landing only in the acting org; and **(6) existence-only uniqueness probes**, which project a bool and never a row. Every call site carries a one-line justification comment naming its category, and `IgnoreQueryFiltersBaselineTests` holds a per-file baseline count so a new bypass cannot appear unnoticed. The mirror rule (#701): a bypass on a query **rooted at an entity that has no query filter** — `Organization`, `PendingSignup`, `LoginAttempt`, `SystemSettings`, `Backup`, `PerTenantBackup`, `OrganizationUsageSnapshot`, `OeFileContent`, `BcQualityArticle` and friends — suppresses nothing and must not be written, because it costs the reviewer the signal that a real bypass carries; the exception is a query that reaches a *filtered* entity from such a root (an `Include` of a `User`, say), where the bypass is doing real work and its comment must say so. Adding one still needs explicit maintainer confirmation. Cross-org reads from a signed-in session return nothing — verified by `CrossOrgIsolationTests`.

**Bootstrap.** A fresh deployment creates the "Default" organisation through the M13 migration and stamps it as the singleton **system org** (`organizations.is_system = true`) via the `MoveSeedToSystemOrg` migration. The system org hosts the canonical templates other organisations fork from; it starts empty until a SiteAdmin authors content via `/admin/templates`. The first admin user is created from `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env vars on first boot. After at least one user exists those variables are read and warned about (so a stale value in production logs gets surfaced) but never re-applied.

**New organisations.** Signups against a blank or unknown slug **auto-approve**. The new `Organization` is created (with `is_system = false`), the user is stored as `Active Admin` of that org, the cookie is issued, and the user lands on the home page in one step. The org starts empty — no templates, no modules, no catalogue. Admins populate it on demand by forking from the system catalogue via the "From the site catalogue" section of `/admin/templates`, or by authoring directly. We deliberately have no per-org superuser (the cross-org `SiteAdmin` operator role from M17 manages deployments, not individual orgs' data), so requiring approval here would leave new orgs unreachable. A `SignupRequest` row is still written with `Decision=Approved, DecidedByUserId=<self>` so `/admin/users` retains a complete history. Operators who don't want anyone to provision new orgs should hide the `/signup` route at the proxy or set up their deployment with `BOOTSTRAP_ADMIN_*` and never advertise public signup.

**Existing-org signups** still go through admin approval in `/admin/users` and email notification. Whether that approval can be skipped is a **per-organisation** decision (`OrganizationSettings.AutoJoinVerifiedDomainUsers`); there is no site-wide override, and the `system_settings.default_signup_auto_approve` column that once held one was removed once it was found to have no readers (#698). Admin-issued invites shipped in M19: an admin creates one at `/admin/administration/users/new`, the invitee receives an email (when SMTP is configured) or an inline link, and redeems it at `/accept-invite` — setting a password and joining the org `Active`. Invite tokens are stored as sha-256 hashes with a 7-day single-use expiry (`InviteService`). The older "ask the user to sign up with your slug and approve them" path still works for self-service.

**Email-first verified signup.** When SMTP is configured, `/signup` runs an email-first, verified flow instead of the single form above. Step 1 collects only an email and `POST /auth/signup/start` persists a `PendingSignup` (org-less, and outside the tenant query filter, so its reads need no `IgnoreQueryFilters()`) and emails a one-time link **and** a 6-digit code (`PendingSignupService`; link token via `TokenIssuer`, code hashed bound to the row's link-token hash, both 30-minute lifetime). The response is always the same generic "check your email" — it never reveals whether the address is new, already registered (no row, no email sent), rate-limited, or domain-disallowed. Step 2 verifies via `GET /auth/signup/verify` (link) or `POST /auth/signup/verify-code` (code), which stamps `verified_at` and sets a short-lived Data-Protected verified-email cookie. Step 3 (`/signup/details`, `POST /auth/signup/complete`) re-resolves the verified email **server-side** from a verified, uncompleted, unexpired row — the cookie is only a hint, so a forged cookie is inert — and branches: a claimed-domain email joins that org (Active immediately when the org has `OrganizationSettings.AutoJoinVerifiedDomainUsers` on, otherwise Pending for admin approval, the historical behaviour), and an unclaimed-domain email creates a brand-new org with the visitor as its Active Admin. There is no "join an arbitrary org by typing its slug" path in this flow. The verification row's `completed_at` is the single-use guard. When SMTP is **not** configured the original single-form `/auth/signup` flow is used unchanged, so a zero-config deployment can still get started. Mandatory two-factor is not bolted onto signup: `StrongAuthGate` already redirects a newly-Active member of a `RequireStrongAuth` org to `/account/security?required=1` on their next request.

## Pages

| Path | Audience | Purpose |
|------|----------|---------|
| `/login` | Anonymous | Email + password sign-in. |
| `/signup` | Anonymous | Email-first verified signup when SMTP is configured (verify, then name + password, plus org name/slug for a new org); falls back to the single all-fields form when SMTP is off. |
| `/signup/details` | Anonymous | Step 3 of the verified flow: collects the remaining fields for a verified email (gated by the verified-email cookie + a server-side `PendingSignup` re-check). |
| `/forgot-password` | Anonymous | Email a reset link if SMTP is configured. Always shows the same "if that email exists" message. |
| `/reset-password?token=…` | Anonymous | Single-use; expires after one hour. |
| `/account` | User+ | Self-service: change password / display name, two-factor, passkeys, assistant tokens, repository tokens. |
| `/admin/users` | Admin | Approve / reject pending signups, change roles, disable users in the same org. |

End-user generator pages (`/templates/workspace`, `/templates/extension`, `/templates*`) are now `[Authorize]` — anonymous users redirect to `/login` with a `return=` query.

## Cookie

```csharp
services.AddAuthentication("Cookie")
    .AddCookie("Cookie", options => {
        options.Cookie.Name = "alwb_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login";
    });
```

A 30-day sliding cookie is long, so two things keep it honest. `CookieSessionRevalidation` re-reads the user every five minutes and drops the cookie when the account has been disabled, deleted or demoted. And every sign-in stamps the moment the session started into the auth properties: a password change or a completed reset writes `users.credentials_changed_at`, and any cookie issued before that stamp is rejected at its next re-validation. The same change revokes the user's live personal access tokens, so recovering an account really does evict whoever else was holding it (issue #675).

The cookie carries the user's id, organisation id, role, display name and email as claims. `HttpOrganizationContext` reads `org_id` and `user_id` to drive EF query filters; the rest are for the top-bar caption ("Bob (CRONUS) — Admin") and audit attribution.

## Bearer credentials for /mcp

The MCP endpoint accepts two bearer flavours via the `McpBearer` authorisation policy. Both schemes mount the same downstream claim shape, so MCP tools see identical principals regardless of which credential authenticated the request.

* **Personal access tokens** (`Services/Account/PatAuthenticationHandler.cs`) — `aldt_pat_…` opaque tokens, SHA-256 hashed at rest, scoped to (user, organisation). The desktop/CLI path: Claude Desktop, Claude Code, Cursor, VS Code Copilot agent mode all paste one into a config file. Issued from `/account/ai`, revoked from the same page or from SiteAdmin oversight.
* **OAuth 2.1 access tokens** (OpenIddict server at `/oauth/*`, validation via `Services/OAuth/OAuthClaimsTransformer.cs`) — for clients that don't accept a pasted credential. Today that's Claude.ai on the web, Claude mobile, and Claude Cowork; their directory and custom-connector flow does Dynamic Client Registration (RFC 7591) on first connect, then the user clicks Allow on a consent screen at `/oauth/authorize`. The flow and table layout are documented in `.design/mcp-oauth.md`.

On a 401 from `/mcp`, the PAT handler emits `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"` so Claude can discover the OAuth server. The MCP spec only honours this discovery pointer on a real 401 — the per-handler challenge override in `PatAuthenticationHandler.HandleChallengeAsync` is where the contract lives.

The OpenIddict EF Core tables (`oauth_applications`, `oauth_authorizations`, `oauth_scopes`, `oauth_tokens`) are intentionally **outside** the multi-tenant query filter; pre-auth flows (`/oauth/token`, `/oauth/register`) have to read them before any organisation context exists. Org attribution lives in OpenIddict's free-form `properties` JSON column and is stamped during the consent step. `oauth_consents` is ours and is inside the standard filter.

## Microsoft Entra ID sign-in

Federated sign-in with Microsoft Entra ID is **opt-in per organisation** and sits alongside email/password rather than replacing it; the design decisions (app registration model, tenant validation, migration/link strategy, login-method policy) live in tracking issue #552 rather than being restated here. It was built in three slices:

* **Settings schema (slice 1).** `user_external_logins` links a local user to a stable external identity (`provider`, `issuer`, `subject` — the Entra object id, never email/UPN; unique across the three). Org-level knobs live on `organization_settings`: `entra_enabled`, `entra_allowed_tenant_ids` (the per-org tenant allow-list — the security boundary once sign-in ships), optional own-registration `entra_client_id` / `entra_client_secret_encrypted`, and `local_login_policy` (`AllowAll` or `EntraOnly`; enforcement landed in slice 3 below). The deployment-wide app registration (`entra_client_id` / `entra_client_secret_encrypted`) sits on `system_settings`, managed from `/site-admin/settings/entra`; org admins opt in from `/admin/administration/identity`. Both client-secret columns are Data-Protection ciphertext and audit-redacted, same pattern as the SMTP password and the machine-translation key.
* **Sign-in flow (slice 2).** One `OpenIdConnect` handler (scheme `EntraId`, callback **`/signin-microsoft`**, authority `login.microsoftonline.com/organizations/v2.0`) serves every org: the app-registration credentials live in the DB, so the handler's events inject the client id/secret per request from `AuthenticationProperties` stashed by `POST /auth/entra/challenge` (which routes by claimed email domain, or needs no email when exactly one org has Entra on). Because the multi-tenant metadata can't pin an issuer, the stock issuer/audience checks are replaced by explicit ones in `OnTokenValidated` (issuer must be `…/{tid}/v2.0` for the token's own `tid`; audience must equal the challenged client id) and the per-org **tenant allow-list check in `EntraSignInService.CompleteAsync` is the security boundary**. Resolution order: existing `user_external_logins` link (keyed on `oid`, so IdP email renames don't matter) → email match inside the routed org (creates the link) → JIT provisioning through the same `SignupRequest`/`AutoJoinVerifiedDomainUsers` machinery as verified signup (Entra-only users get a random BCrypt hash that can never verify). Success mints the standard `BuildIdentity` cookie — downstream can't tell a federated sign-in from a password one. **Binding a token to an *existing* local account additionally requires positive evidence that the tenant owns the address** (issue #674): either the id_token carries `xms_edov: true`, or the email's domain is claimed by the resolved org in `organization_email_domains`. Without one of those the sign-in is refused with `EmailNotVerified` and a structured warning naming the tenant and the domain (never the full email) — the allow-list is per *tenant*, but the account it lands on is chosen by an attacker-influenceable string (a B2B guest's UPN is their own external address), so an allow-listed partner tenant could otherwise take over the org's Admin. Existing `user_external_logins` links are keyed on `oid` and unaffected. Every attempt, refused or not, lands in `login_attempts`; local MFA is skipped (the tenant owns MFA) and a linked Microsoft account satisfies `RequireStrongAuth` in both places that ask — `AuthService.HasStrongAuthAsync` and the per-request `StrongAuthGate`. Only the **sign-in half** of `EntraSignInService` (`IsSignInAvailableAsync`, `GetLoginSurfaceAsync`, `ResolveChallengeAsync`, `GetClientSecretAsync`, `CompleteAsync`) reads with `IgnoreQueryFilters()`: it runs in the login page and the OIDC callback, before any cookie exists, and routing one sign-in to exactly one org is the whole job — the same sanctioned category as login/signup. The account-linking half stays inside the query filter; see slice 3.
* **Login-method policy + self-service linking (slice 3).** `local_login_policy = EntraOnly` is enforced server-side: `AuthService.TryLoginAsync` returns `LocalLoginDisabled` after password verification (SiteAdmins exempt — password break-glass, mirroring the reset-MFA precedent), and `PasswordResetService` refuses to issue password-reset and magic-link tokens for such orgs and refuses consuming magic links issued before the flip. Passkeys deliberately keep working. The policy can only be turned on together with `entra_enabled` and after at least one Active org Admin has a Microsoft link (lockout guard, same spirit as the last-admin guards); the login page collapses the password form behind a disclosure only when every org is Microsoft-only. Self-service linking lives on `/account` (Security section): `POST /auth/entra/link` runs the same OIDC handshake in link mode (the callback verifies the auth cookie's user matches the link target stashed in the protected properties), `LinkAsync` enforces the same tenant allow-list as sign-in plus one-identity-one-user, and `UnlinkAsync` refuses removing the last link in an `EntraOnly` org. The admin Users tab badges Microsoft-linked members, and JIT-pending signups now trigger the same admin-notification email as the password signup flow.

  Unlike the sign-in half, this whole flow runs **authenticated**, so `ResolveChallengeForCurrentOrgAsync`, `ListLinksAsync`, `LinkAsync`, and `UnlinkAsync` read **inside** the tenant query filter (`user_external_logins` is org-scoped through its `User` principal). A user therefore cannot see or remove a link outside their own organisation, and the acting org comes from the cookie's `org_id` claim rather than being passed in. The one deliberate exception is an existence-only probe in `LinkAsync`: `(provider, issuer, subject)` is unique deployment-wide, so answering "is this Microsoft identity already claimed?" has to see past the filter. It projects a bool, never a row, and only exists so the refusal reads as a message instead of a unique-violation 500. Because a remote-auth handler runs before the authentication middleware populates `HttpContext.User`, the link-mode callback re-authenticates the cookie scheme itself — that is what both the identity check and the query filter depend on.

## Password hashing

BCrypt with a work factor of 12 via `BCrypt.Net-Next`. We picked BCrypt over Argon2id for two reasons: (1) the `BCrypt.Net-Next` package is mature, ships pre-built, and has no native dependencies; (2) at our scale and with cookie-based sessions the BCrypt vs Argon2id practical difference is negligible. If the threat model changes, the swap is local to `AccountService.HashPassword` / `VerifyPassword`.

Password policy: minimum 12 characters; no other rules. Length beats classes.

## Login hardening

- **Per-email rate limit**: max 10 attempts per 15 minutes.
- **Per-IP rate limit**: max 30 attempts per 15 minutes.
- **Lockout**: five consecutive failures with no intervening success locks the account for 15 minutes. Successful sign-in clears the streak.
- **Forgot-password rate limit**: same per-email and per-IP windows so the SMTP relay isn't a spam vector, and every outcome is recorded in `login_attempts` so the counter is honest. Issuing a link records a *success* there, matching the magic-link path: five consecutive failures lock an account out of sign-in, so recording a failure for a valid address would let anyone who knows an email lock its owner out by asking for a reset five times. The response is identical regardless of whether the email is known. (This paragraph described the limit for some time before `PasswordResetService` actually applied one; #790 closed the gap.)
- **Reset tokens** are stored as `sha256(token)`, expire after 1 hour, and are single-use (`consumed_at` is stamped on first use).

Every login attempt — successful or not — writes a row to `login_attempts` keyed on email and IP. That table powers both the rate limit windows and the lockout query, against an injectable `TimeProvider` so tests can advance the clock without sleeping.

## Approvals and emails

When a signup arrives, `EmailService` (MailKit) emails every active admin in the target org. When an admin decides, the requester gets a one-line "approved" or "declined" email. SMTP is configured via env vars: `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD_FILE`, `SMTP_FROM`, `SMTP_USE_STARTTLS`. If any of those are missing, the page shows "Email is not configured; ask an admin." rather than swallowing the failure — fail loudly so misconfiguration is visible.

Email send failures do not roll back the underlying action. A failed approval email shouldn't unapprove the user.

**Sending goes through an outbox** (issue #790). `IEmailService` resolves to `OutboxEmailService`, which writes the message to `email_outbox` and returns; `EmailOutboxScheduler` (a `PolledScheduler`, opt out with `DISABLE_EMAIL_OUTBOX_SCHEDULER=1`) sends it through `SmtpEmailService`, backs off on failure across a little under three hours, and gives up after that. Given-up messages are listed at `/site-admin/email`, which is the point of the whole thing: the enumeration-resistant flows (signup verification, forgot password, magic link) must answer the user identically whether or not the mail went out, so before the outbox a dead relay was invisible to the user *and* to the operator, showing up only as a logged warning. Two purposes still send inline, because someone is waiting on the answer and the flow already shows it: the email-MFA code and the SiteAdmin test email.

Two consequences worth knowing. Message bodies carry live tokens — a queued reset body holds a working reset link, where `password_reset_tokens` holds only a hash — so the body column is Data-Protection ciphertext (same key ring as the SMTP password), sent rows are pruned within a day, and the body is dropped as soon as a message is sent or given up on; only messages still trying to deliver hold a secret. And the row is written through its own short-lived `DbContext` rather than the caller's, so a queued send is *not* atomic with the business change that triggered it; making it so means moving each enqueue inside its caller's transaction.

`email_outbox` sits outside the tenant query filter, like `login_attempts` and `pending_signups`: it is written by pre-auth flows with no organisation in scope and read by a cross-org SiteAdmin page. Its `organization_id` is a label for that page, not a fence, and with no filter on the table its reads carry no `IgnoreQueryFilters()` bypass.

## Account self-service

`/account`:

- **Change password** — requires the current password.
- **Change display name** — 2–80 chars.
- **Two-factor authentication** — TOTP (authenticator app) is the primary factor; email codes are an optional fallback. Both can be enrolled at once.
- **Passkeys** — WebAuthn credentials registered via `navigator.credentials.create`. A successful passkey assertion at login is full authentication; it replaces both the password and the 2FA challenge.
- **No self-service account deletion.** A user cannot delete their own account: erasing the row would take their audit trail with it, and in an organisation-run tool the history of who created what outlives any one member. The non-destructive path is an admin setting the account to `UserStatus.Disabled` under "Admin user management" below, which revokes access and keeps the history readable. `/account` therefore has no danger zone; it points the user at their admins instead.

Email change for a user is performed by another admin (not by the user themselves in this milestone). See "Admin user management" below.

## Two-factor authentication

### TOTP
- 20-byte secret, Base32-encoded, encrypted via `IDataProtector` (purpose `ALDevToolbox.UserTotpSecret`) before persistence. Decryption only happens inside `TotpService.VerifyAsync` to compute the rolling code.
- Stored in `user_totp_secrets` (one row per user). A row with `confirmed_at = null` is a pending enrollment that hasn't yet been validated by submitting a current code — repeating enrollment overwrites it.
- Confirmation flips `users.totp_enabled = true` and issues 10 recovery codes.
- The Data Protection key ring lives on the `app-keys` volume; losing it makes TOTP secrets unrecoverable (same blast radius as the SMTP password). Recovery: email-MFA fallback, recovery codes, or `UserAdministrationService.ResetMfaAsync` (the SiteAdmin break-glass path). The OAuth signing + encryption keys (`oauth-signing.key`, `oauth-encryption.key`, written by `Services/OAuth/OAuthKeyMaterial.cs`) sit in the same directory; losing them additionally forces every connected OAuth client to re-consent on its next request, but does not affect cookie sign-ins or PATs.

### Email codes
- 6-digit numeric, single-use, 10-minute lifetime. Stored hash is `SHA-256(code + ":" + user_id)`; the user-id binding stops the short numeric space being a global rainbow-table target.
- Reuses the `password_reset_tokens` table with `purpose = EmailMfaChallenge`. Per-user rate limit: 3 issues per 10 minutes, counted from that same table — survives process restart with no extra state.
- Always sent to `users.email`, never `pending_email`, so a still-unconfirmed email change can't redirect MFA codes to the new address.

### Recovery codes
- 10 codes per user, two five-char groups (`7HK3M-2QPRA`), drawn from an unambiguous alphabet. BCrypt-hashed (work factor 10).
- Shown once, immediately after TOTP enrollment or after regenerate. Regenerating wipes all prior codes.

### Login orchestration
- `AuthService.TryLoginAsync` returns `LoginOutcome.MfaRequired` once the password verifies and the user has any 2FA enrolled. `LastLoginAt` and the login-success row in `login_attempts` are deferred to `AuthService.CompleteMfaAsync`, called by the challenge endpoint after the second factor verifies.
- The intermediate "password OK, 2FA pending" state lives in a short-lived signed cookie (`alwb_mfa`, 10 min, `IDataProtector` purpose `ALDevToolbox.MfaPending`). No server-side session table.
- Magic-link login deliberately bypasses 2FA — the mailbox itself is treated as a second factor.

## Step-up for sensitive tools

The same page carries one agent rule that is not a tool switch: **Let AI assistants deploy to production environments** (`organization_settings.agents_may_deploy_to_production`, off by default, audited like the other settings columns). Off, the `deploy_build` MCP tool deploys to sandboxes only; see `.design/saas-delivery.md`, "MCP parity" (#1122).

An org Admin can mark tools on Administration → Tools as needing a **recent second factor** before use (`organizations.step_up_tools`, a `text[]` of `ToolKey` names next to `disabled_tools`). The point is the tools that spend a stored credential on the org's behalf: creating repositories and pull requests in GitHub, deploying to a customer's Business Central environment, and the page where an assistant that can do both gets connected. Only those tools are offered on the page; Piper, Diff and Object Explorer read what is already here.

- **What "recent" means.** Every cookie issue stamps how the session was established (`auth_method`) and, when that method is strong, when (`strong_auth_at`), in the cookie's auth properties beside `signed_in_at` (`StepUpAuth`). Strong methods: TOTP, email code, recovery code, a passkey whose assertion reports user verification, and Microsoft sign-in *with an `auth_time` claim*, in which case that claim is the moment. Password alone, a magic link, a passkey without user verification, and a Microsoft sign-in without `auth_time` (Microsoft may have reused its own browser session, so nothing proves the person was there) are not. A session is *fresh* while `strong_auth_at` is within the organisation's window (`organizations.step_up_window_minutes`, "Ask again after" on the Tools page, one minute to a day, 15 by default; it rides the cookie as `org_step_up_window` for the route gate and is read from the row everywhere else). The properties are the source of truth; the two values are mirrored onto the principal as claims so page code and the MCP filter can read them, and `CookieSessionRevalidation` re-applies them after it rebuilds the claims. A cookie re-issued for the same session (password change) keeps both stamps unchanged, since typing the current password proves nothing new; a step-up re-issue keeps `signed_in_at` so a session a reset should have ended cannot be rescued.
- **Where it is enforced.** (1) `ToolAccessGate` redirects a GET on a marked tool's page routes to `/auth/step-up?return=...` when the cookie session is not fresh (403 for other methods); the marked set rides the `org_step_up_tools` claim like the disabled set. (2) The MCP call-tool filter (`McpRegistration`) refuses a *writing* MCP tool that belongs to a marked tool when the caller is a PAT, because a token can never step up, and when the caller is an OAuth session whose consent was not approved from a fresh session (`oauth_consents.strong_auth_at`, stamped at consent), which is what makes an assistant approved before the org marked the tool approve again. The mapping from MCP tool to `ToolKey` is `McpToolDescriptor.Tool`, and the catalogue test requires every writing tool to carry one; read-only MCP tools are never gated. (3) `/oauth/consent` and `POST /oauth/authorize` require a fresh session when the org has marked any tool, since consenting hands an assistant the writing tools for the life of its refresh token; denying needs no confirmation. (4) Action-level checks (`ToolEnablement.EnsureStepUpAsync`) where a credential is spent from a page another tool's route owns: deploying and staging a release (reachable from a build pipeline's page) and applying a recipe (reachable from its admin page). (5) Changing the marked set or the window needs a fresh session while any tool is marked, so a stale admin session can neither switch the rule off nor stretch it. Those surfaces read the live row through `ToolEnablement`, which also answers the disabled-tools question.
- **How a person steps up.** `GET /auth/step-up` reuses the login challenge: it stashes an `MfaPending` with `StepUp = true` in the same `alwb_mfa` cookie and sends the user to `/login/challenge`, which changes its title to "Confirm it's you" and offers whatever the account has: the code tabs (posting to the existing challenge endpoints, which refuse a method the account never enrolled, require the cookie's user to match the pending state, and finish through `StepUpEndpoints.CompleteStepUpAsync` instead of a sign-in), a passkey (`/auth/step-up/passkey/*`, allow-list pinned to the user, user verification *required* and checked from the authenticator data, completed inside the tenant fence by `PasskeyService.CompleteStepUpAsync`), or Microsoft (`/auth/step-up/entra`, the ordinary handshake with `prompt=login` and `max_age=0`; the callback accepts only an identity already linked to the cookie's user from a tenant still on the allow-list, and when the token carries `auth_time` requires it to be no older than the step-up started, one minute of skew allowed. Entra only includes `auth_time` when the app registration lists it as an optional ID token claim, so without that the callback trusts `prompt=login` and logs that the re-sign-in was not verified server-side; adding the optional claim to the registration turns the check on). An emailed code is not offered to a session that began with a magic link: that would be the mailbox twice. Completion re-issues the cookie for the same user with the method that succeeded, which is what moves `strong_auth_at`, and records a successful login attempt so the MFA throttle's run of failures is broken. An account with no second factor at all is told to set one up.
- **Known limit.** The route gate runs on page loads. A Blazor circuit opened while the session was fresh keeps working after the window closes, until the next navigation, except where an action-level check applies. That is the same shape as the five-minute cookie revalidation and is accepted for now.
- **What it does not do.** It does not read Entra's `amr` claim: a tenant without Conditional Access would then lock its users out of every marked tool. The forced re-sign-in delegates the strength question to the tenant's policy, which is where it belongs.

Disabling a user (`UserAdministrationService.DisableUserAsync`) is the "this person has left" action and is deliberately total, in one transaction: it stamps `credentials_changed_at` so the cookie dies on its next revalidation, revokes the user's PATs and OAuth consents, and revokes the OpenIddict tokens issued under them. The PAT and OAuth request paths already refuse a non-active user on every call, so this is belt and braces there; it also means re-enabling the account does not bring old tokens back.

## Passkeys (WebAuthn)

- Backed by `Fido2.AspNet`. Configuration: `Auth:WebAuthn:RpId` (registrable suffix of the deployed hostname) and `Auth:WebAuthn:Origins` (allow-listed full origins). When `RpId` is empty the passkey UI hides and the service refuses to start ceremonies — set both before exposing passkeys publicly.
- `user_passkeys` holds the credential id (globally unique), CBOR-encoded public key, sign counter, AAGUID, nickname, and transport hints. One user can register many.
- Registration / assertion challenges round-trip via short-lived (5 min) `IDataProtector`-protected cookies; no extra state table.
- A non-monotonic sign counter (signal of a cloned authenticator) refuses the sign-in.

## Admin user management

`/admin/users` (Admin-only):

- **Create a user** — generates an invite without requiring SMTP; the magic link is shown inline once with a copy-to-clipboard button (via a 60s `IDataProtector`-protected cookie). If SMTP is configured and the admin ticked "Send email", the invite is also emailed.
- **Invite by email** — existing flow, kept for backwards compat.
- **Approve / reject pending signups** — unchanged.
- **Disable / re-enable / change role** — unchanged; last-admin guards on demote/disable.
- **Change a user's email** — stamps `users.pending_email` + `pending_email_at`, issues a 24-hour `EmailChangeConfirm` token via `TokenIssuer.Issue`, and sends the confirmation link to the **new** address. If SMTP isn't reachable the link is also shown inline. The swap happens when the new mailbox clicks the link (`/auth/account/email-change/confirm`), at which point the user is signed out so they re-login from the new address. An admin cannot change their own email via this path.

`/site-admin/users/{id}/reset-mfa` is the break-glass path for users locked out of every factor: clears the TOTP secret, every recovery code, every passkey, and flips both MFA flags off. Audited.

## Audit log

Every mutation to a template, template_folder, template_file, template_module_folder, template_module_file, runtime_template_default_module, module, module_dependency, well_known_dependency, application_version, user or signup_request writes a row through `AuditInterceptor`.

`audit_log.changed_by` is now `"display_name <email>"` of the acting user (or `"unknown"` for seed-time inserts that pre-date a signed-in user). `changed_by_user_id` and `organization_id` are stored as separate columns for queryability — `(organization_id, timestamp)` is the index that powers `/admin/audit`.

Snapshot rules unchanged from the original implementation:

- **Modified / Deleted**: snapshot `OriginalValues`.
- **Added**: snapshot is null (there's no "before"); written in `SavedChangesAsync` so the assigned id is in the audit row.
- Principal entities (`RuntimeTemplate`, `TemplateFolder`, `TemplateModuleFolder`, `Module`) inline their child collection's pre-save state.
- `TemplateFile` / `TemplateModuleFile` content is replaced with a SHA-256 hash to keep the audit log compact.

## What is *not* audited

- Workspace / extension generations — `ILogger` at Info level is enough.
- Reads.
- Login attempts (they go to `login_attempts`, which is not the audit log). This includes the `users.last_login_at` stamp a successful sign-in writes: `AuditInterceptor` skips a `User` save whose only modified column is that one, so a sign-in leaves no audit row. The gate is narrow — a save that also changes the account is audited in full. Until PR 10 the stamp *was* audited, as an `Updated` row attributed to `"unknown"` (the interceptor runs before the auth cookie exists), which made sign-in churn most of a busy org's audit log.

## Error handling

- Audit interceptor failure → `SaveChanges` rolls back. We'd rather refuse a change than apply it without a trace.
- A corrupted audit log table fails the app at startup — `MigrateAsync` tries to apply the schema and surfaces any drift.
