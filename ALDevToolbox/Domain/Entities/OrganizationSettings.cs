using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Per-organisation defaults used to pre-fill the New Workspace and New
/// Extension forms (Milestone P3.14). Exactly one row per organisation;
/// validation matches the rules in <see cref="Services.GenerationService"/>.
/// </summary>
public class OrganizationSettings
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Default <c>app.json</c> publisher for new templates and extensions.</summary>
    public string DefaultPublisher { get; set; } = string.Empty;

    /// <summary>Default <c>app.json</c> <c>url</c> for every generated extension.</summary>
    public string? DefaultUrl { get; set; }

    /// <summary>Default <c>app.json</c> <c>logo</c> path for every generated extension.</summary>
    public string? DefaultLogo { get; set; }

    /// <summary>
    /// Org-wide list of country codes that get spliced into every generated
    /// <c>AppSourceCop.json</c>'s <c>supportedCountries</c> array. Org-wide
    /// because the supported markets are an organisation policy, not a
    /// per-template choice.
    /// </summary>
    public List<string> DefaultSupportedCountries { get; set; } = new();

    /// <summary>Lower bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeFrom { get; set; }

    /// <summary>Upper bound of the default Core / standalone id range.</summary>
    public int DefaultIdRangeTo { get; set; }

    /// <summary>One-line default brief copied into the form's <c>Brief</c> field.</summary>
    public string DefaultBrief { get; set; } = string.Empty;

    /// <summary>Longer default description copied into the form's <c>Description</c> field.</summary>
    public string DefaultCoreDescription { get; set; } = string.Empty;

    /// <summary>
    /// Admin-editable JSON template for the workspace's
    /// <c>{{workspace_folder}}.code-workspace</c> file. The generator runs mustache
    /// substitution over it, then overlays a computed <c>folders</c> array
    /// before writing the file — so the admin owns <c>settings</c> and any
    /// other top-level keys, and the generator owns the folder list.
    /// </summary>
    public string CodeWorkspaceJson { get; set; } = OrganizationDefaults.CodeWorkspaceJson;

    /// <summary>
    /// How the customer's name is turned into the workspace folder and the
    /// <c>.code-workspace</c> file name. Defaults to
    /// <see cref="NamingStyle.PascalCase"/> - what the generator did before the
    /// style was a setting. See <c>.design/customer-naming.md</c>.
    /// </summary>
    public NamingStyle NamingFolderStyle { get; set; } = NamingStyle.PascalCase;

    /// <summary>
    /// How the customer's name is turned into the suggested repository name.
    /// Defaults to <see cref="NamingStyle.KebabCase"/>.
    /// <see cref="NamingStyle.None"/> is never offered here - a repository name
    /// cannot contain spaces.
    /// </summary>
    public NamingStyle NamingRepositoryStyle { get; set; } = NamingStyle.KebabCase;

    /// <summary>
    /// Whether the extension prefix is asked for per workspace, fixed for the
    /// whole organisation, or not used at all. See
    /// <see cref="ValueObjects.ExtensionPrefixMode"/> for what each means.
    /// </summary>
    public ExtensionPrefixMode ExtensionPrefixMode { get; set; } = ExtensionPrefixMode.PerWorkspace;

    /// <summary>
    /// The organisation's own extension prefix: the value used for every
    /// workspace under <see cref="ValueObjects.ExtensionPrefixMode.Fixed"/>, and
    /// the pre-fill under
    /// <see cref="ValueObjects.ExtensionPrefixMode.PerWorkspace"/>. Null means
    /// the organisation has no such value, in which case the customer's short
    /// name stands in.
    /// </summary>
    public string? ExtensionPrefix { get; set; }

    /// <summary>
    /// Admin-authored Markdown shown to MCP agents by the
    /// <c>get_cookbook_guidance</c> tool before they call
    /// <c>suggest_recipe</c>. Empty by default; the guidance tool always
    /// returns built-in copy describing what each <c>RecipeType</c> means
    /// so an empty org-level guidance still steers the agent.
    /// </summary>
    public string CookbookGuidance { get; set; } = string.Empty;

    /// <summary>
    /// When <see langword="true"/>, every active member of this organisation
    /// must have at least one strong-auth method enrolled (TOTP, email-MFA,
    /// or a passkey). Users without one land on <c>/account/security?required=1</c>
    /// on their next request and can't reach anything else until they
    /// enrol. The toggle itself refuses to flip on if the saving admin
    /// doesn't yet satisfy the requirement — a small foot-gun guard so an
    /// admin can't lock themselves out by accident.
    /// </summary>
    public bool RequireStrongAuth { get; set; }

    /// <summary>
    /// When <see langword="true"/>, a visitor who verifies an email whose
    /// domain this organisation has claimed (see
    /// <see cref="OrganizationEmailDomain"/>) joins as an Active
    /// <see cref="UserRole.User"/> immediately — no admin approval. When
    /// <see langword="false"/> (the default) such a signup lands as Pending and
    /// waits for an admin to approve it via <c>/admin/administration/users</c>,
    /// the historical existing-org behaviour. Only consulted by the verified,
    /// email-first signup flow; it has no effect on the SMTP-off fallback.
    /// </summary>
    public bool AutoJoinVerifiedDomainUsers { get; set; }

    /// <summary>
    /// Which third-party machine-translation backend this org uses (discriminator
    /// for <c>MachineTranslationProviderFactory</c>). Defaults to <c>"deepl"</c>,
    /// the only backend today.
    /// </summary>
    public string MachineTranslationProvider { get; set; } = "deepl";

    /// <summary>
    /// The org's machine-translation API key, encrypted with the Data Protection
    /// key ring (purpose
    /// <see cref="Services.Translation.MachineTranslationSettingsService.MachineTranslationApiKeyProtectionPurpose"/>).
    /// Null when unset. Losing <c>app-keys</c> requires re-entering it. The audit
    /// interceptor redacts this column so ciphertext never lands in history.
    /// </summary>
    public string? MachineTranslationApiKeyEncrypted { get; set; }

    /// <summary>
    /// When the Translator calls the provider. <see cref="MtTrigger.Off"/> (the
    /// default) disables the feature entirely — it doubles as the master switch,
    /// so there is no separate enabled flag.
    /// </summary>
    public MtTrigger MachineTranslationTrigger { get; set; } = MtTrigger.Off;

    /// <summary>
    /// When <see langword="true"/>, the <c>ReleaseAutoImportScheduler</c> imports
    /// the newest Microsoft <em>OnPrem</em> Business Central release for this org
    /// once a day (skipping versions already in the catalogue). Doubles as the
    /// feature's master switch — there is no separate enabled flag. Only OnPrem
    /// artifacts ship the loose <c>.app</c> files the Object Explorer walks, so
    /// the artifact type isn't configurable. See <c>.design/object-explorer.md</c>.
    /// </summary>
    public bool AutoImportReleasesEnabled { get; set; }

    /// <summary>
    /// BC localisation/country code(s) the auto-import fetches — a single code
    /// (<c>dk</c>) or a comma-separated list (<c>w1,dk,nl</c>), stored in the
    /// canonical trimmed/lower-cased/de-duplicated form written by
    /// <c>OrganizationConfigService.SaveAutoImportAsync</c> and parsed with
    /// <c>ParseAutoImportCountries</c>. Required (at least one code) when
    /// <see cref="AutoImportReleasesEnabled"/>; each code is uppercased into its
    /// generated release label "Business Central {Major}.{Minor} ({CC})".
    /// </summary>
    public string? AutoImportCountry { get; set; }

    /// <summary>
    /// When <see langword="true"/> (and <see cref="AutoImportReleasesEnabled"/>),
    /// the daily sweep also imports Microsoft's pre-release builds of upcoming
    /// majors off the insider channel for the same countries, marks them as
    /// previews, refreshes them every fortnight, and retires each once the
    /// version ships. Off by default: previews are large downloads of builds
    /// Microsoft publishes under its insider terms. See
    /// <c>.design/object-explorer.md</c>, "Preview builds".
    /// </summary>
    public bool AutoImportPreviewsEnabled { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the <c>deploy_build</c> MCP tool may deploy to a
    /// Production (or any other non-sandbox) environment, immediately and with no
    /// confirmation step. Off by default: an agent is then refused anything but a
    /// sandbox, and Production deployments are made on the deployment pipeline's page,
    /// where a person confirms them (#1122). See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public bool AgentsMayDeployToProduction { get; set; }

    /// <summary>
    /// When the daily auto-import sweep last ran for this org (UTC), stamped by
    /// <c>ReleaseAutoImportScheduler</c> after each per-org pass — including
    /// passes that found nothing new. Null until the first sweep; shown on the
    /// artifacts import page so admins can see the feature is alive.
    /// </summary>
    public DateTime? AutoImportLastRunAt { get; set; }

    /// <summary>
    /// Which Git hosting providers this org allows project repositories on, stored
    /// as discriminators (<c>github</c> / <c>azure_devops</c>). Gates the add-repo
    /// picker and which per-user repository-token fields a member sees — most orgs
    /// keep their code in one place. An empty list means "not configured yet" and
    /// is treated as <em>all providers allowed</em> so the tool isn't broken before
    /// an admin sets it. Repository credentials themselves are per-user (see
    /// <see cref="UserRepositoryToken"/>), no longer per-org. See
    /// <c>.design/artifacts.md</c>.
    /// </summary>
    public List<string> AllowedRepositoryProviders { get; set; } = new();

    /// <summary>
    /// Master switch for Microsoft (Entra ID) sign-in for this organisation.
    /// Requires at least one entry in <see cref="EntraAllowedTenantIds"/>;
    /// the service refuses to enable it otherwise. See issue #552.
    /// </summary>
    public bool EntraEnabled { get; set; }

    /// <summary>
    /// Entra tenant ids (GUID strings) whose accounts may sign in to this
    /// organisation. The sign-in callback validates the token's <c>tid</c>
    /// claim against this list — with a multi-tenant app registration this
    /// check is the only thing keeping arbitrary Microsoft accounts out, so
    /// it must never be skipped. Stored lowercased and de-duplicated.
    /// </summary>
    public List<string> EntraAllowedTenantIds { get; set; } = new();

    /// <summary>
    /// Optional per-org app registration client id. Null means "use the
    /// deployment-wide registration from <see cref="SystemSettings"/>" —
    /// the default; orgs only fill this when their security policy requires
    /// an app registration in their own tenant.
    /// </summary>
    public string? EntraClientId { get; set; }

    /// <summary>
    /// Data-Protection-encrypted client secret paired with
    /// <see cref="EntraClientId"/>. Decryption is contained in
    /// <see cref="Services.OrganizationAdminService"/>; the audit
    /// interceptor redacts this column.
    /// </summary>
    public string? EntraClientSecretEncrypted { get; set; }

    /// <summary>
    /// The app registration this organisation uses to reach its customers' Business
    /// Central by default. One registration can serve every customer, because each
    /// customer authorises it in their own admin centre; a solution that needs a
    /// different one overrides it with its own (<c>OeProject.BcClientId</c>). Null
    /// until an Admin sets one. See <c>.design/saas-delivery.md</c> ("Authentication").
    /// </summary>
    public string? BcClientId { get; set; }

    /// <summary>
    /// Data-Protection-encrypted client secret paired with <see cref="BcClientId"/>,
    /// under the same purpose as a solution's own secret so
    /// <c>ProjectConnectionService</c> reads either. The audit interceptor redacts it.
    /// </summary>
    public string? BcClientSecretEncrypted { get; set; }

    /// <summary>When the secret in <see cref="BcClientSecretEncrypted"/> expires, as Entra reported it (UTC).</summary>
    public DateTime? BcClientSecretExpiresAt { get; set; }

    /// <summary>
    /// Which sign-in methods this org's members may use. Defaults to
    /// <see cref="ValueObjects.LocalLoginPolicy.AllowAll"/>; enforcement of
    /// <see cref="ValueObjects.LocalLoginPolicy.EntraOnly"/> ships with the
    /// Entra sign-in flow (issue #552).
    /// </summary>
    public ValueObjects.LocalLoginPolicy LocalLoginPolicy { get; set; }

    /// <summary>
    /// The GitHub App installation this organisation connected. Null means "not
    /// connected" and doubles as the master switch for every GitHub feature,
    /// matching how <see cref="AutoImportReleasesEnabled"/> and
    /// <see cref="MtTrigger.Off"/> work. Set by the install callback; cleared by
    /// Disconnect (which leaves the installation itself in place on GitHub).
    /// See <c>.design/github-integration.md</c>.
    /// </summary>
    public long? GitHubInstallationId { get; set; }

    /// <summary>
    /// Login of the connected GitHub organisation — shown to admins and used as
    /// the <c>{org}</c> in <c>POST /orgs/{org}/repos</c>. Null when not connected.
    /// </summary>
    public string? GitHubOrgLogin { get; set; }

    /// <summary>
    /// The permissions GitHub reported for the installation at connect time, as
    /// a JSON object of <c>permission -&gt; read|write</c>. Kept so the
    /// Repositories tab can say "this installation cannot create repositories"
    /// before someone hits that wall from New Workspace. Null when not connected.
    /// </summary>
    public string? GitHubInstallationPermissions { get; set; }

    /// <summary>When the connection was made (UTC). Null when not connected.</summary>
    public DateTime? GitHubConnectedAt { get; set; }

    /// <summary>
    /// The branch rules applied to every repository the workbench creates for
    /// this organisation (issue #628). Null means "no ruleset configured", which
    /// is the default and is not the same as a ruleset with nothing ticked -
    /// the second is a row an admin emptied and is treated as nothing to apply.
    /// See <c>.design/github-integration-phase2.md</c>.
    /// </summary>
    public GitHubRepositoryRuleset? GitHubRepositoryRuleset { get; set; }

    /// <summary>
    /// The IANA time zone (e.g. <c>Europe/Copenhagen</c>) every time in the app is
    /// shown in for this organisation, with the UTC instant on hover. Null means
    /// UTC. Read through <c>DisplayTimeZone</c>; not the same thing as a
    /// solution's <c>BcTimeZone</c>, which is the customer's zone for booking
    /// Business Central update windows. See issue #942.
    /// </summary>
    public string? DisplayTimeZoneId { get; set; }

    /// <summary>
    /// The delivery window a Production environment starts with the first time
    /// discovery meets it (issue #962), copied onto
    /// <c>OeProjectEnvironment.UpdateWindowStart/End</c> as clock digits in the
    /// customer's zone, never converted. Both null means new environments start at
    /// "any time". Only ever applied at creation: an existing row is never touched,
    /// and setting a default later does not backfill. Both-or-neither per pair,
    /// like the environment's own editor. See <c>.design/saas-delivery.md</c>
    /// ("Update window (per environment)").
    /// </summary>
    public TimeOnly? DefaultDeliveryWindowProductionStart { get; set; }

    /// <summary>End of <see cref="DefaultDeliveryWindowProductionStart"/>'s window; may wrap past midnight.</summary>
    public TimeOnly? DefaultDeliveryWindowProductionEnd { get; set; }

    /// <summary>As <see cref="DefaultDeliveryWindowProductionStart"/>, for Sandbox environments.</summary>
    public TimeOnly? DefaultDeliveryWindowSandboxStart { get; set; }

    /// <summary>End of <see cref="DefaultDeliveryWindowSandboxStart"/>'s window; may wrap past midnight.</summary>
    public TimeOnly? DefaultDeliveryWindowSandboxEnd { get; set; }

    public DateTime UpdatedAt { get; set; }
}
