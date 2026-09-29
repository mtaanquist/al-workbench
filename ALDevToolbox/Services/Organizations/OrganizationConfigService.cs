using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Read- and write-side service for the per-organisation configuration
/// introduced in Milestone P3.14: the cached read model every reader goes
/// through, and the core settings writes (publisher / id range / brief /
/// description defaults, the workspace JSON, the Cookbook guidance, the
/// release auto-import block, and the always-included file list). The
/// branding, machine-translation, repository-provider and TOML-import
/// concerns live in their own sibling services and invalidate this cache.
///
/// Generation reads from this service rather than embedded resources or
/// hardcoded values so two organisations can have completely different
/// pre-fills, logos and sidecar files. A small in-memory cache keyed by
/// <c>organization_id</c> keeps the hot path off the database; cache entries
/// are invalidated on save. The cache is an <see cref="IMemoryCache"/>
/// resolved through DI (Singleton in production, per-fixture in tests) so
/// parallel xUnit fixtures hitting their own databases can't race on a
/// shared cache slot (issue #45).
/// </summary>
public class OrganizationConfigService
{
    private static readonly Regex PathRegex = new(@"^[A-Za-z0-9._\-]+(?:/[A-Za-z0-9._\-]+)*$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly ILogger<OrganizationConfigService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public OrganizationConfigService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        ILogger<OrganizationConfigService> logger,
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _logger = logger;
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Returns the tracked <see cref="OrganizationSettings"/> row for
    /// <paramref name="orgId"/>, inserting a fresh one (added to the change
    /// tracker) when none exists yet. Runs under the normal tenant query filter
    /// — every caller already holds the acting org id from
    /// <see cref="RequireOrganizationId"/>, so there is no cross-org read here.
    /// </summary>
    internal async Task<OrganizationSettings> GetOrCreateSettingsAsync(int orgId, CancellationToken ct)
    {
        var row = await _db.OrganizationSettings
            .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        return row;
    }

    /// <summary>
    /// Copies the publisher / id-range / brief / description / url / logo
    /// defaults from <paramref name="input"/> onto <paramref name="row"/> and
    /// stamps <see cref="OrganizationSettings.UpdatedAt"/>. Shared by the
    /// per-section save and the TOML import so the two write the settings block
    /// identically. <see cref="OrganizationSettings.CodeWorkspaceJson"/> is left
    /// to the caller — only the import overwrites it.
    /// </summary>
    internal static void ApplySettingsFields(OrganizationSettings row, OrganizationSettingsInput input, DateTime now)
    {
        row.DefaultPublisher = input.DefaultPublisher.Trim();
        row.DefaultIdRangeFrom = input.DefaultIdRangeFrom;
        row.DefaultIdRangeTo = input.DefaultIdRangeTo;
        row.DefaultBrief = input.DefaultBrief?.Trim() ?? string.Empty;
        row.DefaultCoreDescription = input.DefaultCoreDescription?.Trim() ?? string.Empty;
        row.DefaultUrl = string.IsNullOrWhiteSpace(input.DefaultUrl) ? null : input.DefaultUrl.Trim();
        row.DefaultLogo = string.IsNullOrWhiteSpace(input.DefaultLogo) ? null : input.DefaultLogo.Trim();
        // DefaultSupportedCountries is no longer surfaced in the admin form or
        // the TOML import (AppSourceCop.json moved into Always-included files or
        // per-template overrides). The entity column stays so older rows keep
        // their values.
        row.UpdatedAt = now;
    }

    /// <summary>
    /// Builds a new <see cref="OrganizationFile"/> row from an input at the
    /// given <paramref name="ordering"/>. Shared by the reconciling save and the
    /// wipe-and-replace import so both insert files identically.
    /// </summary>
    internal static OrganizationFile NewOrganizationFile(int orgId, OrganizationFileInput input, int ordering, DateTime now) =>
        new()
        {
            OrganizationId = orgId,
            Path = input.Path.Trim(),
            Content = input.Content ?? string.Empty,
            MustacheEnabled = input.MustacheEnabled,
            Scope = input.Scope,
            Ordering = ordering,
            UpdatedAt = now,
        };

    private static string CacheKey(int organizationId) => $"org-config:{organizationId}";
    private static string NameCacheKey(int organizationId) => $"org-name:{organizationId}";

    /// <summary>
    /// Returns the current display name of <paramref name="organizationId"/>.
    /// Cached per-org so the layout can render the name in the top bar without
    /// a DB hit on every navigation. The entry is refreshed in place by
    /// <see cref="OrganizationBrandingService.RenameOrganizationAsync"/> so rename takes effect immediately
    /// for every active circuit — not only after the renaming admin re-logs in.
    /// Bypasses query filters: layout calls hit this with the claim-derived
    /// org id and don't run under tenant scope.
    /// </summary>
    public async Task<string?> GetOrganizationNameAsync(int organizationId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(NameCacheKey(organizationId), out string? cached)) return cached;

        // Deliberately NOT _db. This is the one query in this service that runs
        // from MainLayout, which renders above every page -- and Blazor does not
        // wait for a layout's OnInitializedAsync before starting its children's.
        // Sharing the request's scoped AppDbContext therefore means the layout
        // and the page can have two commands in flight on one context, which EF
        // refuses ("A second operation was started on this context instance").
        //
        // It only ever bit after a rename, because every other render served
        // this from cache and issued no command at all -- see issue #551, where
        // warming the cache elsewhere first made the crash disappear 3/3 while
        // going straight to the page crashed 3/3.
        //
        // A child scope gives this one read its own context. The read is by
        // explicit org id rather than under tenant scope, and organizations is the
        // tenant table itself, so no query filter is being crossed here.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var name = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.Name)
            .FirstOrDefaultAsync(ct);
        _cache.Set(NameCacheKey(organizationId), name);
        return name;
    }

    /// <summary>
    /// Returns the cached or freshly-loaded <see cref="OrganizationConfig"/>
    /// for the acting user's organisation. Generation, the form pre-fill, and
    /// the admin page all read through this entry point so a save in one
    /// invalidates every reader.
    /// </summary>
    public Task<OrganizationConfig> GetCurrentAsync(CancellationToken ct = default)
        => GetForAsync(RequireOrganizationId(), ct);

    /// <summary>
    /// Loads the configuration for an arbitrary organisation. Used by seed and
    /// import flows that need to populate a specific org regardless of the
    /// current sign-in. Reads bypass query filters because seed-time and
    /// bootstrap callers may not have an organisation in scope yet.
    /// </summary>
    public async Task<OrganizationConfig> GetForAsync(int organizationId, CancellationToken ct = default)
    {
        // Tenant-isolation guard: the reads below bypass the EF query filter, so
        // a caller running inside an authenticated request must only ever ask for
        // its own org's config. Pre-auth / seed / bootstrap callers (no org in
        // scope) may target any org. Blocks a latent IDOR should a future caller
        // pass a user-influenced org id. See #489.
        if (_orgContext.CurrentOrganizationId is int scoped && scoped != organizationId)
        {
            throw new InvalidOperationException(
                $"Refusing cross-organisation config read: request scoped to org {scoped} asked for org {organizationId}.");
        }

        if (_cache.TryGetValue(CacheKey(organizationId), out OrganizationConfig? cached) && cached is not null)
            return cached;

        var settings = await _db.OrganizationSettings
            // Fence category 4 (explicitly scoped org-id lookup): the three reads below are all
            // pinned to OrganizationId == organizationId, and the guard above refuses a request
            // scoped to a different org.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);

        var logo = await _db.OrganizationAssets
            // Same category 4 read, pinned to a.OrganizationId == organizationId.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Kind == OrganizationAssetKind.Logo, ct);

        var files = await _db.OrganizationFiles
            // Same category 4 read, pinned to f.OrganizationId == organizationId.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.OrganizationId == organizationId)
            .OrderBy(f => f.Ordering)
            .ToListAsync(ct);

        // The transient fallback initialises CodeWorkspaceJson from the
        // property initialiser (OrganizationDefaults.CodeWorkspaceJson) so a
        // fresh org's Workspace settings page renders the seeded JSON.
        var config = new OrganizationConfig(
            Settings: settings ?? new OrganizationSettings { OrganizationId = organizationId },
            Logo: logo,
            Files: files);

        _cache.Set(CacheKey(organizationId), config);
        return config;
    }

    /// <summary>Drops the cached configuration for one organisation. Called after every write.</summary>
    public void InvalidateCache(int organizationId) => _cache.Remove(CacheKey(organizationId));

    /// <summary>
    /// Refreshes the cached display name for one organisation, so the next
    /// render of the top bar serves the new name without a read. Written by
    /// <see cref="OrganizationBrandingService.RenameOrganizationAsync"/>, which
    /// is the only thing that changes it — the name cache lives here because
    /// <see cref="GetOrganizationNameAsync"/> is its only reader.
    /// </summary>
    internal void CacheOrganizationName(int organizationId, string name) =>
        _cache.Set(NameCacheKey(organizationId), name);

    /// <summary>
    /// Persists the publisher / id-range / brief / description defaults block.
    /// Validation matches <see cref="GenerationService"/>'s rules: the id range
    /// must be a positive ascending pair and the publisher must be non-empty.
    /// </summary>
    public async Task SaveSettingsAsync(OrganizationSettingsInput input, CancellationToken ct = default)
    {
        Validate(input);
        var orgId = RequireOrganizationId();

        var row = await GetOrCreateSettingsAsync(orgId, ct);

        ApplySettingsFields(row, input, DateTime.UtcNow);

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated organisation settings for org {OrgId} (publisher={Publisher}, range={From}-{To}).",
            orgId, row.DefaultPublisher, row.DefaultIdRangeFrom, row.DefaultIdRangeTo);
    }

    /// <summary>
    /// Persists the organisation's naming conventions: how a customer's name
    /// becomes the workspace folder and the suggested repository name, and how
    /// the extension prefix is decided. Kept off
    /// <see cref="SaveSettingsAsync"/> so the configuration import - which
    /// carries no naming block - cannot quietly reset them.
    /// See <c>.design/customer-naming.md</c>.
    /// </summary>
    public async Task SaveNamingAsync(OrganizationNamingInput input, CancellationToken ct = default)
    {
        ValidateNaming(input);
        var orgId = RequireOrganizationId();

        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.NamingFolderStyle = input.FolderStyle;
        row.NamingRepositoryStyle = input.RepositoryStyle;
        row.ExtensionPrefixMode = input.ExtensionPrefixMode;
        row.ExtensionPrefix = string.IsNullOrWhiteSpace(input.ExtensionPrefix)
            ? null
            : input.ExtensionPrefix.Trim();
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated naming settings for org {OrgId} (folder={FolderStyle}, repository={RepositoryStyle}, prefixMode={PrefixMode}).",
            orgId, row.NamingFolderStyle, row.NamingRepositoryStyle, row.ExtensionPrefixMode);
    }

    internal static void ValidateNaming(OrganizationNamingInput input)
    {
        var errors = new Dictionary<string, string>();
        // A repository name cannot contain spaces, which is the one thing the
        // None style produces.
        if (input.RepositoryStyle == NamingStyle.None)
        {
            errors[nameof(input.RepositoryStyle)] =
                "Repository names cannot contain spaces. Pick one of the other styles.";
        }
        var prefix = input.ExtensionPrefix?.Trim() ?? string.Empty;
        if (prefix.Length > MaxExtensionPrefixLength)
        {
            errors[nameof(input.ExtensionPrefix)] = $"At most {MaxExtensionPrefixLength} characters.";
        }
        if (input.ExtensionPrefixMode == ExtensionPrefixMode.Fixed && prefix.Length == 0)
        {
            errors[nameof(input.ExtensionPrefix)] =
                "Enter the prefix every extension name should start with, for example JM.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    /// <summary>
    /// Cap on the organisation's own extension prefix. Matches the short name's
    /// ceiling - both end up in front of an extension name.
    /// </summary>
    public const int MaxExtensionPrefixLength = 50;

    /// <summary>
    /// Persists the admin-edited <c>.code-workspace</c> JSON template. The
    /// generator overlays the computed <c>folders</c> array onto whatever is
    /// stored here, so the admin owns <c>settings</c> and any other top-level
    /// keys but never has to manage <c>folders</c>. Validation refuses empty
    /// input and anything that doesn't parse as a JSON object.
    /// </summary>
    public async Task SaveCodeWorkspaceJsonAsync(string codeWorkspaceJson, CancellationToken ct = default)
    {
        ValidateCodeWorkspaceJson(codeWorkspaceJson);
        var orgId = RequireOrganizationId();

        var row = await GetOrCreateSettingsAsync(orgId, ct);
        var now = DateTime.UtcNow;
        row.CodeWorkspaceJson = codeWorkspaceJson;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Updated organisation code-workspace JSON for org {OrgId} ({Bytes} bytes).",
            orgId, codeWorkspaceJson.Length);
    }

    /// <summary>Cap on the Cookbook authoring guidance markdown body.</summary>
    public const int MaxCookbookGuidanceLength = 10_000;

    /// <summary>
    /// Persists the Cookbook authoring guidance returned by the
    /// <c>get_cookbook_guidance</c> MCP tool. Markdown; empty allowed
    /// (the tool still returns the built-in type descriptions).
    /// </summary>
    public async Task SaveCookbookGuidanceAsync(string? guidance, CancellationToken ct = default)
    {
        var body = guidance?.Trim() ?? string.Empty;
        if (body.Length > MaxCookbookGuidanceLength)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["CookbookGuidance"] = $"Guidance must be {MaxCookbookGuidanceLength} characters or fewer.",
            });
        }

        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.CookbookGuidance = body;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);
        _logger.LogInformation(
            "Saved cookbook authoring guidance for org {OrgId} ({Bytes} chars).",
            orgId, body.Length);
    }

    /// <summary>
    /// Persists the per-org automatic-release-import settings (enable toggle +
    /// countries). At least one country is required to enable it; the value may
    /// be a comma-separated list (e.g. <c>w1,dk,nl</c>) — codes are trimmed,
    /// lower-cased, and de-duplicated, and the canonical joined form is stored.
    /// Throws <see cref="PlanValidationException"/> (field key
    /// <c>AutoImportCountry</c>) so the form renders the error inline.
    /// </summary>
    public async Task SaveAutoImportAsync(bool enabled, string? country, bool includePreviews = false, CancellationToken ct = default)
    {
        var codes = ParseAutoImportCountries(country);
        if (enabled && codes.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AutoImportCountry"] = "Pick at least one country code (e.g. 'dk' or 'w1,dk,nl') to enable automatic import.",
            });
        }
        if (codes.Any(c => !AutoImportCountryRegex.IsMatch(c)))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AutoImportCountry"] = "Use BC country codes like 'dk' or 'w1', separated by commas.",
            });
        }

        var normalized = codes.Count == 0 ? null : string.Join(",", codes);
        var orgId = RequireOrganizationId();
        var row = await GetOrCreateSettingsAsync(orgId, ct);
        row.AutoImportReleasesEnabled = enabled;
        row.AutoImportCountry = normalized;
        // Previews ride on the daily sweep, so they can't be on while it's off —
        // the form disables the box in that state and we mirror it here.
        row.AutoImportPreviewsEnabled = enabled && includePreviews;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);
        _logger.LogInformation(
            "Updated release auto-import settings for org {OrgId} (enabled={Enabled}, countries={Countries}, previews={Previews}).",
            orgId, enabled, normalized ?? "(none)", row.AutoImportPreviewsEnabled);
    }

    /// <summary>
    /// BC OnPrem artifact country codes are two-letter markets plus the
    /// <c>w1</c>/<c>w2</c>-style worldwide bases — two lower-case alphanumerics
    /// covers the whole CDN index.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AutoImportCountryRegex =
        new("^[a-z0-9]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Splits a stored or submitted auto-import country value into its canonical
    /// code list: comma-separated, trimmed, lower-cased, de-duplicated in first-seen
    /// order. Shared by the writer above and <c>ReleaseAutoImportScheduler</c> so
    /// the two never disagree on the format.
    /// </summary>
    public static List<string> ParseAutoImportCountries(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>
    /// Replaces the always-included file list. Existing rows are matched by
    /// primary key (preserved through <see cref="OrganizationFileInput.Id"/>);
    /// rows missing from the input are deleted. Same reconciliation pattern as
    /// <see cref="CatalogService"/>.
    /// </summary>
    public async Task SaveFilesAsync(IReadOnlyList<OrganizationFileInput> inputs, CancellationToken ct = default)
    {
        ValidateFiles(inputs);
        await _quotaGuard.EnsureCanWriteAsync(ct);
        var orgId = RequireOrganizationId();

        var existing = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == orgId)
            .ToListAsync(ct);
        var existingById = existing.ToDictionary(e => e.Id);
        var inputIds = inputs.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();

        var now = DateTime.UtcNow;
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var path = input.Path.Trim();
            if (input.Id is int id && existingById.TryGetValue(id, out var row))
            {
                row.Path = path;
                row.Content = input.Content ?? string.Empty;
                row.MustacheEnabled = input.MustacheEnabled;
                row.Scope = input.Scope;
                row.Ordering = i;
                row.UpdatedAt = now;
            }
            else
            {
                _db.OrganizationFiles.Add(NewOrganizationFile(orgId, input, i, now));
            }
        }

        foreach (var row in existing)
        {
            if (!inputIds.Contains(row.Id)) _db.OrganizationFiles.Remove(row);
        }

        await _db.SaveChangesAsync(ct);
        InvalidateCache(orgId);

        _logger.LogInformation(
            "Saved {Count} always-included file(s) for org {OrgId}.", inputs.Count, orgId);
    }

    internal static void Validate(OrganizationSettingsInput input)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(input.DefaultPublisher))
            errors[nameof(input.DefaultPublisher)] = "Publisher is required.";
        if (input.DefaultIdRangeFrom <= 0)
            errors[nameof(input.DefaultIdRangeFrom)] = "Must be greater than zero.";
        if (input.DefaultIdRangeTo <= input.DefaultIdRangeFrom)
            errors[nameof(input.DefaultIdRangeTo)] = "Must be greater than 'from'.";
        // The logo path is written verbatim into the generated app.json and a ZIP
        // entry path. Validate it with the same single-relative-path / no-'..'
        // rule as org files so a traversal value can't escape the extraction root
        // on the end user's machine. See issue #369.
        if (!string.IsNullOrWhiteSpace(input.DefaultLogo))
        {
            var logo = input.DefaultLogo.Trim().Replace('\\', '/');
            if (!PathRegex.IsMatch(logo) || logo.Contains(".."))
            {
                errors[nameof(input.DefaultLogo)] =
                    "Use a relative path with letters, digits, '_', '-', '.' and '/'. No '..' segments.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    /// <summary>
    /// Server-side validation for the workspace-settings JSON: must be
    /// non-empty and parse to a JSON object (not array / string / number).
    /// Errors are keyed on <c>codeWorkspaceJson</c> so the Workspace settings
    /// form can render the message inline next to the editor.
    /// </summary>
    internal static void ValidateCodeWorkspaceJson(string? input)
    {
        const string field = "codeWorkspaceJson";
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [field] = "JSON is required. The file is always written, so the template can't be blank.",
            });
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(input);
        }
        catch (JsonException ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [field] = $"Could not parse as JSON: {ex.Message}",
            });
        }

        if (parsed is not JsonObject)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [field] = "JSON root must be an object — a workspace file has top-level keys like 'settings'.",
            });
        }
    }

    internal static void ValidateFiles(IReadOnlyList<OrganizationFileInput> inputs)
    {
        var errors = new Dictionary<string, string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < inputs.Count; i++)
        {
            var path = inputs[i].Path?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                errors[$"Files[{i}].Path"] = "Path is required.";
            }
            else if (!PathRegex.IsMatch(path) || path.Contains(".."))
            {
                errors[$"Files[{i}].Path"] = "Use a relative path with letters, digits, '_', '-', '.' and '/'. No '..' segments.";
            }
            else if (!seenPaths.Add(path))
            {
                errors[$"Files[{i}].Path"] = $"Duplicate path '{path}'.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }
}

/// <summary>
/// Snapshot returned by <see cref="OrganizationConfigService.GetCurrentAsync"/>.
/// The <see cref="Settings"/> row is always non-null so callers don't need a
/// secondary check; missing rows surface as a transient default that's still
/// safe to read.
/// </summary>
public record OrganizationConfig(
    OrganizationSettings Settings,
    OrganizationAsset? Logo,
    IReadOnlyList<OrganizationFile> Files);

/// <summary>
/// The Naming section of the Defaults page: the two derived-name styles and the
/// extension-prefix policy. See <c>.design/customer-naming.md</c>.
/// </summary>
public record OrganizationNamingInput(
    NamingStyle FolderStyle,
    NamingStyle RepositoryStyle,
    ExtensionPrefixMode ExtensionPrefixMode,
    string? ExtensionPrefix = null);

/// <summary>Form-post shape for the Defaults section of <c>/admin/configuration</c>.</summary>
public record OrganizationSettingsInput(
    string DefaultPublisher,
    int DefaultIdRangeFrom,
    int DefaultIdRangeTo,
    string DefaultBrief,
    string DefaultCoreDescription,
    string? DefaultUrl = null,
    string? DefaultLogo = null);

/// <summary>One row submitted by the always-included files editor.</summary>
public record OrganizationFileInput(
    int? Id,
    string Path,
    string Content,
    bool MustacheEnabled,
    ALDevToolbox.Domain.ValueObjects.OrganizationFileScope Scope = ALDevToolbox.Domain.ValueObjects.OrganizationFileScope.WorkspaceRoot);
