# MCP OAuth — design

## Why

Claude.ai's directory and custom-connector flow does **not** accept a
user-pasted bearer token. From [Anthropic's connector docs][anthropic-auth]:

> User-pasted bearer tokens (`static_bearer`) are not yet supported.

The two out-of-the-box methods Claude does support are **DCR** (Dynamic
Client Registration, RFC 7591) and **CIMD** (Client ID Metadata Document).
Anything else (`oauth_anthropic_creds`, `custom_connection`) requires
emailing `mcp-review@anthropic.com` and an Anthropic-side review.

Today AL Workbench gates `/mcp` with a single bearer flavour: the
PAT (`aldt_pat_…`) handler in
`Services/Account/PatAuthenticationHandler.cs`. That works for Claude
Desktop, Cursor, Copilot agent mode, and Claude Code, all of which let you
paste the token into a config file. It does not work for Claude on the web
or on mobile. To make those clients addable as a Claude custom connector,
we ship an OAuth 2.1 authorization server alongside the PAT path.

PAT stays. OAuth is added. The MCP endpoint accepts either.

## Auth modes

| Mode  | What the client does                                          | When                                                                 |
|-------|---------------------------------------------------------------|----------------------------------------------------------------------|
| PAT   | Reads `Authorization: Bearer aldt_pat_…` from its config file | Desktop apps, CLI tools, scripts                                     |
| OAuth | DCR-registers itself as a public client, runs the auth-code   | claude.ai, Claude mobile, Cowork — the hosted Claude surfaces        |
| OAuth | Identifies itself with a CIMD URL (`token_endpoint_auth_method=none`)            | Claude prefers this when the AS metadata advertises CIMD support     |
| OAuth | Identifies itself with a CIMD URL (`token_endpoint_auth_method=private_key_jwt`) | ChatGPT custom connectors. The CIMD document points at a `jwks_uri` we fetch every authorize so key rotation is self-healing. |

Hosted MCP clients pick CIMD over DCR only when the AS metadata advertises
`client_id_metadata_document_supported: true` **and** the client-auth method
they want to use in `token_endpoint_auth_methods_supported`. ALDevToolbox
advertises both `"none"` and `"private_key_jwt"`, plus `"RS256"` in
`token_endpoint_auth_signing_alg_values_supported` for the JWT-assertion
path, so high-traffic CIMD clients (both Anthropic's and OpenAI's flavours)
avoid the per-connection DCR registration.

`client_credentials` is **not** supported. The MCP authorization spec
forbids pure machine-to-machine grants — every connection needs user
consent.

## Architecture

```
 Claude (browser / mobile)               AL Workbench
 ──────────────────────────              ──────────────
   GET /mcp ─────────────────────────────► McpEndpoints
                                           │ McpBearerPolicy → 401
   ← 401 WWW-Authenticate: Bearer            (PatAuthenticationHandler.HandleChallengeAsync)
       resource_metadata="…/.well-known/oauth-protected-resource"

   GET /.well-known/oauth-protected-resource ──► OAuthEndpoints (hand-rolled)
   ← { resource, authorization_servers, scopes_supported, … }

   GET /.well-known/oauth-authorization-server ──► OpenIddict + customisation
   ← { issuer, registration_endpoint, code_challenge_methods_supported:[S256],
       client_id_metadata_document_supported: true, … }

   POST /oauth/register (DCR) ──► OAuthEndpoints.MapDynamicClientRegistration
   ← 201 { client_id, redirect_uris, token_endpoint_auth_method: "none", … }

   Browser-redirect GET /oauth/authorize?… ──► OAuthEndpoints.MapAuthorizeGet
                                              ↓ 302 to /oauth/consent?<same params>
                                              ↓
   GET /oauth/consent?… ────────────────────► OAuthConsent.razor
                                              [Authorize] forces cookie sign-in
                                              ↓
                                              Consent form
   POST /oauth/authorize ──► OAuthEndpoints.MapAuthorizeComplete
                              ↓ stamps oauth_consents row
                              ↓ SignIn(OpenIddictServerAspNetCoreScheme, principal)
   ← 302 redirect_uri?code=…

   POST /oauth/token ──► OpenIddict default handler
   ← { access_token, refresh_token, expires_in, token_type: "Bearer" }

   GET /mcp Authorization: Bearer <access_token> ──► OpenIddict validation
                                                     ↓ claims transformer
                                                     ↓ McpBearerPolicy OK
                                                     MCP tool runs as user
```

## Persistence

Five tables, all snake_case:

| Table                | Owner       | Notes                                                                 |
|----------------------|-------------|-----------------------------------------------------------------------|
| `oauth_applications` | OpenIddict  | DCR-registered & CIMD-resolved clients. Org attribution in `properties`. |
| `oauth_authorizations` | OpenIddict | Short-lived grant tickets.                                            |
| `oauth_scopes`       | OpenIddict  | Registered scope catalogue.                                           |
| `oauth_tokens`       | OpenIddict  | Access + refresh tokens. Reference + payload columns are DP-wrapped.  |
| `oauth_consents`     | ALDevToolbox | One row per (user, client, org). Drives the consent auto-skip + revoke. |

The four OpenIddict tables sit **outside** the multi-tenant query filter
because pre-auth flows (`/oauth/token`, `/oauth/register`) must read them
before any `IOrganizationContext` is mounted. Org attribution lives in
OpenIddict's free-form `properties` JSON column, stamped during the
consent step. `oauth_consents` is inside the filter — it's a normal
per-org table.

## Key ring

Two layers, both pinned to the `app-keys` volume:

1. **Token format wrapping** uses ASP.NET Core's existing Data Protection
   key ring (`o.UseDataProtection()`). Losing `app-keys` already
   invalidates auth cookies and the `system_settings` SMTP password;
   OAuth tokens sharing its fate isn't a new failure mode.
2. **OpenIddict signing + encryption keys** for the JWKS endpoint and the
   internal token-format fallback. `UseDataProtection()` doesn't supply
   these — they're a separate piece of key material. Persisted by
   `Services/OAuth/OAuthKeyMaterial.cs` as two PKCS#1 DER files under
   the same directory the Data Protection ring uses
   (`OAUTH_KEY_DIR` overrides; falls back to `DATA_PROTECTION_KEY_DIR`,
   then `/var/lib/aldevtoolbox/dp-keys`). File mode tightened to 0600
   on Unix; threat model matches the existing DP keys (anyone who can
   read the volume can already read the cookie keys and the SMTP
   password).

   On first start a fresh 2048-bit RSA pair is generated for each
   purpose and written. Subsequent starts load the same bytes. A
   container restart no longer invalidates every issued access +
   refresh token, so Claude doesn't need to re-consent on every
   redeploy — which was the original "deferred risk" from the first
   OAuth PR.

   If the directory isn't writable we fall back to in-memory keys with
   a logged warning. That's a strict superset of the prior behaviour
   (which was always ephemeral) — the only difference is the warning.

## Why OpenIddict and not hand-rolled

Hand-rolling an OAuth 2.1 authorization server is roughly 1000–1500 lines
of crypto + spec correctness we'd own forever. OpenIddict is BSD-licensed,
ships `net10.0` targets in v7.5.0, and already implements PKCE S256, RFC
6749 error codes, refresh-token rotation, and discovery metadata. The
remaining pieces — DCR endpoint, AS metadata customisation, resource
metadata, consent UI — are small enough to live in `OAuthEndpoints.cs`
plus one Razor page.

## DCR endpoint

OpenIddict 7.5.0's server builder does **not** expose a first-class
registration endpoint. We hand-roll it as a minimal API at
`POST /oauth/register` and write through `IOpenIddictApplicationManager`.
Anonymous (Claude registers without a user); stamps registration provenance
("dcr") into the application's `properties` JSON.

The discovery metadata customisation in `Program.cs` adds
`registration_endpoint` to the AS metadata so Claude finds it.

### Allowed redirect URIs

One rule, `Services/OAuth/OAuthRedirectUriPolicy.cs`, applies to every
`redirect_uri` a client registers or uses:

- `https` with a host — hosted clients;
- `http` on loopback only — native clients (Claude Code uses
  `http://127.0.0.1:<port>/callback`, RFC 8252 §7.3);
- a private-use scheme in reverse-domain form (`com.example.app:/callback`,
  RFC 8252 §7.1).

Everything else is refused, including any URI with a fragment. DCR rejects
the whole registration with `invalid_redirect_uri`; the CIMD resolver drops
the refused entries and rejects the document if none are left; and an
authorize request whose `redirect_uri` is missing or fails the rule is
rejected before the CIMD resolver runs and before OpenIddict validates it,
so a row stored before the rule existed cannot be used either. (Without the
parameter OpenIddict would fall back to a client's single stored URI; OAuth
2.1 and MCP clients always send it.) Dot-less custom schemes such as
`cursor://` or `vscode://` are refused on purpose; Cursor connects with a
personal access token (`docs/mcp-clients.md`). The `form_post` response mode is switched off — MCP clients use
the code flow's default `query` mode.

## CIMD resolver

Hosted MCP clients (claude.ai, Claude mobile, ChatGPT custom connectors)
skip DCR entirely and identify themselves with an HTTPS URL as their
`client_id` — for example `https://claude.ai/oauth/mcp-oauth-client-metadata`
or `https://chatgpt.com/oauth/<id>/client.json`. The URL is the identity;
the JSON document at that URL is the client's metadata.

`Services/OAuth/CimdClientResolver.cs` is an
`IOpenIddictServerHandler<ValidateAuthorizationRequestContext>` registered
with `int.MinValue + 100_000` so it runs ahead of every built-in
OpenIddict validator. On every authorize whose `client_id` is an HTTPS URL,
the resolver:

1. Fetches the URL with a 5 s timeout and a 64 KB body cap.
2. Validates the document's `client_id` self-reference, `redirect_uris`
   (against the rule above),
   and accepts one of two `token_endpoint_auth_method` values:
   - `none` — registers as a public PKCE client (Claude).
   - `private_key_jwt` — fetches the document's `jwks_uri` (same timeout
     and body cap), builds a `JsonWebKeySet` from the JWKS, and registers
     as a confidential client with that key material attached
     (ChatGPT). Other values are rejected with `invalid_client`.
3. Creates or **updates** the application via `IOpenIddictApplicationManager`
   with `registration_source: "cimd"` and the auth method stamped into
   `properties` JSON.

The resolver re-fetches on authorize requests, not just the first one.
That's what lets ChatGPT (or any issuer) rotate its signing keys or amend
its `redirect_uris` without an admin nuking the application row: a stale
snapshot would otherwise break every refresh attempt until someone deleted
the row by hand.

The authorize request is anonymous, though, so a fetch is also a way for
anyone to make the server dial a URL and write an `oauth_applications`
row (#1336). Two limits bound it. A row the resolver created or refreshed
within the last ten minutes (`refreshed_at` in its `properties`,
`CimdClientResolver.RefreshInterval`) is used as it is with no fetch,
which also covers the consent form posting the same request back. And
every fetch spends a permit from `CimdFetchLimiter`, ten per ten minutes
per client IP, the same budget `/oauth/register` has; past it the request
is refused with `temporarily_unavailable`. A row registered another way
(DCR or by an admin) is refused before any fetch.

Recovery flow when the issuer rotates: the user's refresh fails because
the JWKS attached to the application no longer matches the assertion key
→ the client prompts the user to reconnect → /authorize fires → the
resolver re-fetches the CIMD document and the JWKS (once the ten-minute
reuse window since the last fetch has passed) → the row is updated
in place via `UpdateAsync` → consent screen → fresh tokens issued. No
admin needed.

Resolver failures reject the authorise request with RFC 6749
`invalid_client`, which the client surfaces with the metadata-fetch
error rather than a generic timeout.

Without the resolver, OpenIddict rejects every CIMD client with
`ID2052: The specified 'client_id' is invalid` — see
<https://documentation.openiddict.com/errors/ID2052>.

## Resource indicators (RFC 8707)

The MCP 2025-11-25 spec requires clients to include a `resource=<canonical
URL>` parameter on every authorise + token request (RFC 8707), and Claude's
hosted surfaces send `resource=https://<host>/mcp` accordingly. OpenIddict
7.5's stock `ValidateResources` handler compares that value against the
in-memory `OpenIddictServerOptions.Resources` set populated at startup via
`o.RegisterResources(...)`, and rejects unrecognised values with `ID2190
invalid_target`.

OpenIddict actually gates resource use in two places, both of which we
opt out of:

* `DisableResourceValidation()` removes the global allowlist check
  described above (`ID2190 invalid_target`).
* `IgnoreResourcePermissions()` removes the per-client check that
  requires the application's `Permissions` collection to carry
  `"rsrc:" + <resource_url>` (`ID2192 invalid_request — client not
  allowed to use the resource`). CIMD- and DCR-registered clients are
  created without that permission, and back-filling it would require
  the same dynamic resource-URL resolution that defeated us above.

The public host isn't known when the host builds (deployments use the
request's `X-Forwarded-*` headers as the source of truth and there is no
`PublicUrl` configuration). Two attempts to mutate the global allowlist
on the fly from a pre-validator event handler — first by upserting the
`mcp` scope row's `Resources` column (#191), then by adding to
`Options.Resources` directly (#192) — failed to take effect for reasons
we couldn't diagnose without deeper instrumentation. Rather than pile on
more workarounds, both validators are turned off.

This is safe in our threat model: ALDevToolbox only ever issues tokens
for the `/mcp` resource, the audience is still recorded on the issued
token, and `Services/OAuth/McpBearerPolicy.cs` enforces the audience on
every incoming `/mcp` request. Both validators were defence-in-depth for
servers fronting multiple resources, which we are not.

Revisit when OpenIddict adds native DCR / CIMD support
(openiddict/openiddict-core#2404, targeted at 7.6.0) — that release will
likely surface a more idiomatic way to register resources from a CIMD
application descriptor, at which point both opt-outs can come back on.

## Consent screen

`Components/Pages/AccountSecurity/OAuthConsent.razor` — static SSR Razor
page at `GET /oauth/consent`, gated by `[Authorize]` on the cookie
scheme. The page lives at `/oauth/consent` (not `/oauth/authorize`)
because Blazor's `@page` directive registers a route with no HTTP-method
constraint, so co-locating the page and the consent-POST handler at the
same URL trips `EndpointAmbiguityTests`. Instead, the OpenIddict-validated
`GET /oauth/authorize` is a thin redirect endpoint (`MapAuthorizeGet`)
that forwards every query parameter verbatim to `/oauth/consent`.

The consent page reads its OAuth parameters from the query string
(since OpenIddict middleware doesn't run on `/oauth/consent`) and
re-emits them as hidden inputs on a form that POSTs to `/oauth/authorize`.
OpenIddict's middleware runs on the POST, validates the form body as an
authorization request, and passes through to `MapAuthorizeComplete`,
which stamps an `oauth_consents` row and calls `SignIn(...)`. OpenIddict
turns the SignIn into the auth-code redirect.

Auto-skip when a matching consent row already covers the requested
scopes is recorded but the user still clicks Allow — a security
trade-off in favour of the user always seeing what they're approving.
A v2 could shorten this to a silent auto-submit.

## Claim bridge

`Services/OAuth/OAuthClaimsTransformer.cs` runs after OpenIddict
validation succeeds and stamps the ALDevToolbox claim names
(`HttpOrganizationContext.UserIdClaim` / `OrganizationIdClaim` / etc.)
on the principal. The MCP endpoint (and every other consumer of
`IOrganizationContext`) is then identical whether the bearer was a PAT
or an OAuth access token.

The transformer re-checks the user's status against the live row —
deactivating a user kills their OAuth tokens on the next request.

## Configuration

Everything has a sensible default; the operator knobs are:

| Key                                  | Default                | Purpose                                                    |
|--------------------------------------|------------------------|------------------------------------------------------------|
| `OAuth:Issuer`                       | request-derived        | Override when the public URL differs from `Request.Host`.  |
| `OAuth:AccessTokenLifetimeMinutes`   | 60                     | Refresh fires ~5 min before expiry.                        |
| `OAuth:RefreshTokenLifetimeDays`     | 30                     | Rolling — each refresh extends the window.                 |

DCR + CIMD kill-switches will land alongside the SiteAdmin runtime toggle
for MCP in a follow-up — same shape as `system_settings.mcp_enabled`.

## Risks

- **Reverse-proxy `X-Forwarded-Proto` missing in prod** → OpenIddict
  emits `http://` as the issuer → Claude rejects discovery. Add a
  startup self-check that logs a hard error when the resolved issuer is
  plain `http://` outside `Development`. (Open.)
- **CIMD open-client surface** — anyone with a public JSON metadata
  document can act as a client. The plan reserves a
  `OAuth:CimdAllowedHosts` allow-list; default is permissive to match
  Claude's expected behaviour. (Open.)
- **Multi-org users** — `User.OrganizationId` is scalar today, so the
  consent screen doesn't need an org selector. When multi-org lands, the
  consent page must show the chosen org name prominently, bind the
  consent row to it, and re-prompt when the user picks a different org.
- **Refresh-token reuse detection** — OpenIddict raises an event when a
  rotated refresh token is replayed. Wire that event to revoke the
  entire grant + audit `OAuthTokenReuseDetected`. (Open.)

[anthropic-auth]: https://claude.com/docs/claude/connectors/authentication
