using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Seed;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Tomlyn;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Restores an organisation's configuration from the TOML snapshot
/// <see cref="ExportService"/> writes. Wipe-and-replace, and the only writer
/// that touches settings, always-included files and the logo in one go — which
/// is why it lives beside <see cref="OrganizationConfigService"/> rather than
/// inside it, reusing that service's validation and row-building helpers so the
/// import and the per-section saves can't drift apart.
/// </summary>
public class OrganizationConfigTomlImporter
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly OrganizationConfigService _config;
    private readonly ILogger<OrganizationConfigTomlImporter> _logger;

    public OrganizationConfigTomlImporter(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        OrganizationConfigService config,
        ILogger<OrganizationConfigTomlImporter> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _config = config;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>
    /// Wipe-and-replace import for the per-org configuration block written by
    /// <see cref="ExportService"/>. Replaces the org's settings row, all
    /// always-included files, and the logo with whatever the TOML carries —
    /// callers must confirm the overwrite at the UI layer (same modal pattern
    /// as a destructive delete). Validation reuses the same rules as the
    /// per-section saves on <see cref="OrganizationConfigService"/>.
    /// </summary>
    public async Task ImportFromTomlAsync(string toml, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toml))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Toml"] = "Paste the contents of organization-config.toml.",
            });
        }

        await _quotaGuard.EnsureCanWriteAsync(ct);

        OrganizationConfigSeedFile seed;
        try
        {
            seed = TomlSerializer.Deserialize<OrganizationConfigSeedFile>(toml, TomlImportOptions)
                ?? throw new InvalidDataException("Empty TOML.");
        }
        catch (Exception ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Toml"] = $"Failed to parse TOML: {ex.Message}",
            });
        }

        var settingsInput = new OrganizationSettingsInput(
            DefaultPublisher: seed.Settings.DefaultPublisher,
            DefaultIdRangeFrom: seed.Settings.DefaultIdRangeFrom,
            DefaultIdRangeTo: seed.Settings.DefaultIdRangeTo,
            DefaultBrief: seed.Settings.DefaultBrief,
            DefaultCoreDescription: seed.Settings.DefaultCoreDescription,
            DefaultUrl: seed.Settings.DefaultUrl,
            DefaultLogo: seed.Settings.DefaultLogo);
        OrganizationConfigService.Validate(settingsInput);

        // Pre-Issue-#61 exports omit the field — fall back to the in-app default
        // so old archives still import. New imports go through the same
        // server-side JSON-object validation the Workspace settings form uses.
        var codeWorkspaceJson = string.IsNullOrWhiteSpace(seed.Settings.CodeWorkspaceJson)
            ? OrganizationDefaults.CodeWorkspaceJson
            : seed.Settings.CodeWorkspaceJson;
        OrganizationConfigService.ValidateCodeWorkspaceJson(codeWorkspaceJson);

        var fileInputs = seed.File
            .Select(f => new OrganizationFileInput(
                Id: null,
                Path: f.Path,
                Content: f.Content,
                MustacheEnabled: f.MustacheEnabled,
                Scope: Enum.TryParse<OrganizationFileScope>(f.Scope, out var s)
                    ? s
                    : OrganizationFileScope.WorkspaceRoot))
            .ToList();
        OrganizationConfigService.ValidateFiles(fileInputs);

        byte[]? logoBytes = null;
        string? logoContentType = null;
        if (seed.Logo is not null)
        {
            if (!OrganizationBrandingService.AllowedLogoContentTypes.Contains(seed.Logo.ContentType))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Logo.ContentType"] = "Logo must be an SVG or a PNG.",
                });
            }
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(seed.Logo.ContentBase64);
            }
            catch (FormatException)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Logo.ContentBase64"] = "Logo bytes are not valid base64.",
                });
            }
            if (decoded.Length > OrganizationBrandingService.MaxLogoBytes)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Logo.ContentBase64"] = $"Logo must be {OrganizationBrandingService.MaxLogoBytes / 1024} KB or smaller.",
                });
            }
            logoBytes = OrganizationBrandingService.SanitiseLogo(seed.Logo.ContentType, decoded);
            logoContentType = seed.Logo.ContentType;
        }

        var orgId = RequireOrganizationId();
        var now = DateTime.UtcNow;

        // Settings: upsert the single row.
        var settings = await GetOrCreateSettingsAsync(orgId, ct);
        // An export from before the URL and logo path were carried leaves them
        // null; keep what the org already has instead of wiping it.
        settingsInput = settingsInput with
        {
            DefaultUrl = seed.Settings.DefaultUrl ?? settings.DefaultUrl,
            DefaultLogo = seed.Settings.DefaultLogo ?? settings.DefaultLogo,
        };
        OrganizationConfigService.ApplySettingsFields(settings, settingsInput, now);
        settings.CodeWorkspaceJson = codeWorkspaceJson;

        // Files: drop everything and re-insert. Wipe-and-replace import means
        // callers have already confirmed the destructive operation.
        var existingFiles = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == orgId)
            .ToListAsync(ct);
        _db.OrganizationFiles.RemoveRange(existingFiles);
        for (var i = 0; i < fileInputs.Count; i++)
        {
            _db.OrganizationFiles.Add(OrganizationConfigService.NewOrganizationFile(orgId, fileInputs[i], i, now));
        }

        // Logo: upsert if the TOML carries one; otherwise leave the existing
        // row alone (an admin who imports a logo-less TOML probably didn't
        // intend to wipe their logo, only the settings/files).
        if (logoBytes is not null)
        {
            var logo = await _db.OrganizationAssets
                .FirstOrDefaultAsync(a => a.OrganizationId == orgId
                                          && a.Kind == OrganizationAssetKind.Logo, ct);
            if (logo is null)
            {
                logo = new OrganizationAsset
                {
                    OrganizationId = orgId,
                    Kind = OrganizationAssetKind.Logo,
                };
                _db.OrganizationAssets.Add(logo);
            }
            logo.ContentType = logoContentType!;
            logo.Content = logoBytes;
            logo.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation(
            "Imported organisation configuration into org {OrgId}: {Files} file(s){LogoSuffix}.",
            orgId, fileInputs.Count, logoBytes is null ? string.Empty : " + logo");
    }

    /// <summary>
    /// Returns the tracked <see cref="OrganizationSettings"/> row for
    /// <paramref name="orgId"/>, inserting a fresh one (added to the change
    /// tracker) when none exists yet. Runs under the normal tenant query filter
    /// — the caller already holds the acting org id from
    /// <see cref="RequireOrganizationId"/>, so there is no cross-org read here.
    /// </summary>
    private async Task<OrganizationSettings> GetOrCreateSettingsAsync(int orgId, CancellationToken ct)
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

    /// <summary>TOML deserialiser options used by the per-org config import.</summary>
    private static readonly TomlSerializerOptions TomlImportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };
}
