using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>What the Gateways page says about a gateway.</summary>
public enum GatewayStatus
{
    /// <summary>Registered (or given a new key) and not yet approved by an Admin.</summary>
    WaitingForApproval,
    /// <summary>Approved, and heard from within <see cref="GatewayService.OnlineWindow"/>.</summary>
    Online,
    /// <summary>Approved, and silent for longer than <see cref="GatewayService.OnlineWindow"/> after having been heard from.</summary>
    Offline,
    /// <summary>
    /// Approved, and never heard from: the service on the server has not started yet.
    /// Kept apart from <see cref="Offline"/> because it is a setup step still to do,
    /// not something that went wrong.
    /// </summary>
    NotConnectedYet,
    /// <summary>Revoked: every request it makes is refused.</summary>
    Revoked,
}

/// <summary>One row of the Gateways page.</summary>
public sealed record GatewaySummary(
    int Id,
    string Name,
    string MachineName,
    GatewayStatus Status,
    string KeyFingerprint,
    DateTime RegisteredAt,
    DateTime KeyRegisteredAt,
    DateTime? LastSeenAt,
    string? ReportedVersion,
    DateTime? ApprovedAt,
    string? ApprovedBy,
    DateTime? RevokedAt);

/// <summary>
/// A registration code, in the clear, exactly once: straight after it is made. Only
/// its hash is stored.
/// </summary>
/// <param name="Code">The code to hand to whoever installs the gateway.</param>
/// <param name="ExpiresAt">When it stops working if it has not been used.</param>
/// <param name="GatewayId">Set for a rotation code: the gateway whose key it replaces.</param>
public sealed record IssuedGatewayCode(string Code, DateTime ExpiresAt, int? GatewayId);

/// <summary>
/// What the authentication handler needs to know about a gateway before any claim
/// exists: its key, and the organisation every read after it is scoped to.
/// </summary>
public sealed record GatewayCredential(
    int GatewayId, int OrganizationId, string Name, byte[] PublicKey, GatewayState State);

/// <summary>
/// On-premises gateways: handing out registration codes, exchanging one for a
/// pending registration, approving, renaming and revoking, and the two reads the
/// gateway's authentication needs. See <c>.design/on-prem-gateway.md</c>,
/// "Registration and authentication".
///
/// <para>Everything an Admin does re-checks that the caller is an org Admin (or a
/// SiteAdmin) here, not only on the page. The two methods a gateway reaches before
/// it has a principal - <see cref="RegisterAsync"/> and
/// <see cref="FindCredentialAsync"/> - are this file's two
/// <c>IgnoreQueryFilters()</c> call sites, both category 1 (pre-auth routing),
/// approved in #1359. Everything else runs under the organisation claim: the
/// heartbeat under the gateway's, the Admin actions under the Admin's.</para>
/// </summary>
public sealed class GatewayService
{
    /// <summary>Longest gateway name. Mirrored as <c>maxlength</c> on the rename input.</summary>
    public const int MaxNameLength = 80;

    /// <summary>Longest machine name or version string a gateway may report.</summary>
    public const int MaxReportedLength = 255;

    /// <summary>How long a registration code works.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// A gateway heard from within this long is online: three heartbeats, so one
    /// lost request does not flip it. See "Online and offline" in the design.
    /// </summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromTicks(GatewayProtocol.HeartbeatInterval.Ticks * 3);

    // RFC 4648 base32: no 0/1/8/9, so nothing to confuse with O, I or B when it
    // is read aloud or retyped from a screenshot.
    private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int CodeBytes = 20;

    private const string InvalidCodeMessage =
        "This registration code is not valid. It may have been used already or be more than an hour old. "
        + "Ask an Admin to create a new one under Administration → Gateways.";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<GatewayService> _logger;

    public GatewayService(AppDbContext db, IOrganizationContext orgContext, TimeProvider clock, ILogger<GatewayService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; gateway call made outside an authenticated request.");

    // ---------------------------------------------------------------- Admin reads

    /// <summary>
    /// Every gateway in the organisation, revoked ones last. Admin only: the list
    /// carries key fingerprints, which are what an Admin approves against.
    /// </summary>
    public async Task<List<GatewaySummary>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct).ConfigureAwait(false);
        var now = Now;
        var rows = await _db.Gateways.AsNoTracking()
            .OrderBy(g => g.RevokedAt != null)
            .ThenBy(g => g.Name)
            .ThenBy(g => g.Id)
            .Select(g => new
            {
                g.Id, g.Name, g.MachineName, g.State, g.KeyFingerprint, g.RegisteredAt, g.KeyRegisteredAt,
                g.LastSeenAt, g.ReportedVersion, g.ApprovedAt, g.RevokedAt,
                ApprovedBy = g.ApprovedByUser == null ? null : g.ApprovedByUser.DisplayName,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(g => new GatewaySummary(
                g.Id, g.Name, g.MachineName, StatusOf(g.State, g.LastSeenAt, g.RevokedAt, now), g.KeyFingerprint,
                g.RegisteredAt, g.KeyRegisteredAt, g.LastSeenAt, g.ReportedVersion, g.ApprovedAt, g.ApprovedBy, g.RevokedAt))
            .ToList();
    }

    /// <summary>What the Gateways page shows for a gateway in this state at <paramref name="now"/>.</summary>
    public static GatewayStatus StatusOf(GatewayState state, DateTime? lastSeenAt, DateTime? revokedAt, DateTime now)
    {
        if (revokedAt is not null) return GatewayStatus.Revoked;
        if (state != GatewayState.Approved) return GatewayStatus.WaitingForApproval;
        if (lastSeenAt is not { } seen) return GatewayStatus.NotConnectedYet;
        return now - seen <= OnlineWindow ? GatewayStatus.Online : GatewayStatus.Offline;
    }

    // ---------------------------------------------------------------- codes

    /// <summary>
    /// Makes a code that registers a new gateway in the caller's organisation. The
    /// returned code is the only time it exists in the clear.
    /// </summary>
    public async Task<IssuedGatewayCode> CreateRegistrationCodeAsync(CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct).ConfigureAwait(false);
        return await IssueCodeAsync(gatewayId: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a code that gives gateway <paramref name="gatewayId"/> a new key. The old
    /// key keeps working until the code is used; from then on only the new key does,
    /// and the gateway waits for an Admin to approve it again.
    /// </summary>
    public async Task<IssuedGatewayCode> CreateRotationCodeAsync(int gatewayId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct).ConfigureAwait(false);
        var gateway = await _db.Gateways.AsNoTracking()
            .Where(g => g.Id == gatewayId)
            .Select(g => new { g.RevokedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (gateway is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["gateway"] = "That gateway no longer exists." });
        }
        if (gateway.RevokedAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["gateway"] = "This gateway has been revoked. Add a new gateway instead.",
            });
        }
        return await IssueCodeAsync(gatewayId, ct).ConfigureAwait(false);
    }

    private async Task<IssuedGatewayCode> IssueCodeAsync(int? gatewayId, CancellationToken ct)
    {
        var orgId = RequireOrganizationId();
        var code = NewCode();
        var now = Now;
        var row = new GatewayRegistrationCode
        {
            OrganizationId = orgId,
            CodeHash = HashCode(code),
            CreatedByUserId = _orgContext.CurrentUserId,
            CreatedAt = now,
            ExpiresAt = now + CodeLifetime,
            GatewayId = gatewayId,
        };
        _db.GatewayRegistrationCodes.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Issued gateway registration code {CodeId} for organisation {OrganizationId} (rotation of gateway {GatewayId}), expires {ExpiresAt}.",
            row.Id, orgId, gatewayId, row.ExpiresAt);
        return new IssuedGatewayCode(code, row.ExpiresAt, gatewayId);
    }

    // ---------------------------------------------------------------- the exchange

    /// <summary>
    /// Exchanges a registration code for a pending gateway, or - for a rotation code -
    /// replaces that gateway's key and sets it back to pending. Anonymous: the code is
    /// the only thing that says which organisation the caller belongs to.
    /// </summary>
    /// <exception cref="PlanValidationException">Keyed by the request field at fault.</exception>
    public async Task<GatewayRegistrationResponse> RegisterAsync(GatewayRegistrationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string>();
        if (request.ProtocolVersion != GatewayProtocol.Version)
        {
            errors["protocolVersion"] =
                $"This gateway speaks protocol version {request.ProtocolVersion}, and this AL Workbench speaks version {GatewayProtocol.Version}. "
                + "Install the gateway version offered on the Gateways page.";
        }
        var normalised = NormaliseCode(request.Code);
        if (normalised.Length == 0)
        {
            errors["code"] = "Enter the registration code shown under Administration → Gateways.";
        }
        var publicKey = ParsePublicKey(request.PublicKey);
        if (publicKey is null)
        {
            errors["publicKey"] = "The gateway's public key could not be read. It must be an ECDSA P-256 key.";
        }
        var machineName = request.MachineName.Trim();
        if (machineName.Length == 0)
        {
            errors["machineName"] = "The gateway did not say which computer it runs on.";
        }
        else if (machineName.Length > MaxReportedLength)
        {
            errors["machineName"] = $"The computer name is longer than {MaxReportedLength} characters.";
        }
        var version = request.Version.Trim();
        if (version.Length > MaxReportedLength)
        {
            errors["version"] = $"The version is longer than {MaxReportedLength} characters.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var now = Now;
        var hash = HashCode(normalised);
        // Category 1 (pre-auth routing): code_hash == hash && used_at == null && expires_at > now routes this anonymous exchange to the one org that issued the code.
        var code = await _db.GatewayRegistrationCodes.IgnoreQueryFilters()
            .Include(c => c.Gateway)
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (code is null || (code.GatewayId is not null && (code.Gateway is null || code.Gateway.RevokedAt is not null)))
        {
            _logger.LogWarning("Refused a gateway registration from {MachineName}: the code was unknown, used, expired or for a revoked gateway.", machineName);
            throw new PlanValidationException(new Dictionary<string, string> { ["code"] = InvalidCodeMessage });
        }

        var fingerprint = GatewayKeyFingerprint.Compute(publicKey!);
        Gateway gateway;
        if (code.Gateway is { } existing)
        {
            existing.PublicKey = publicKey!;
            existing.KeyFingerprint = fingerprint;
            existing.State = GatewayState.Pending;
            existing.KeyRegisteredAt = now;
            existing.ApprovedAt = null;
            existing.ApprovedByUserId = null;
            existing.MachineName = machineName;
            existing.ReportedVersion = NullIfEmpty(version);
            gateway = existing;
        }
        else
        {
            gateway = new Gateway
            {
                OrganizationId = code.OrganizationId,
                Name = machineName.Length > MaxNameLength ? machineName[..MaxNameLength] : machineName,
                PublicKey = publicKey!,
                KeyFingerprint = fingerprint,
                State = GatewayState.Pending,
                RegisteredAt = now,
                KeyRegisteredAt = now,
                MachineName = machineName,
                ReportedVersion = NullIfEmpty(version),
            };
            _db.Gateways.Add(gateway);
        }
        code.UsedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else exchanged the same code between our read and this save.
            _logger.LogWarning("Refused a gateway registration from {MachineName}: the code was used by a concurrent registration.", machineName);
            throw new PlanValidationException(new Dictionary<string, string> { ["code"] = InvalidCodeMessage });
        }

        _logger.LogInformation(
            "Gateway {GatewayId} ({MachineName}) registered a {Kind} key {Fingerprint} for organisation {OrganizationId}; waiting for approval.",
            gateway.Id, machineName, code.GatewayId is null ? "new" : "replacement", fingerprint, code.OrganizationId);
        return new GatewayRegistrationResponse
        {
            GatewayId = gateway.Id,
            Name = gateway.Name,
            KeyFingerprint = fingerprint,
            Approved = false,
        };
    }

    // ---------------------------------------------------------------- Admin writes

    /// <summary>
    /// Approves the gateway's current key. The page asks the Admin to compare the
    /// fingerprint with the one the gateway printed first; this records who did.
    /// </summary>
    public async Task ApproveAsync(int gatewayId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct).ConfigureAwait(false);
        var gateway = await LoadLiveAsync(gatewayId, ct).ConfigureAwait(false);
        if (gateway.State == GatewayState.Approved) return;
        gateway.State = GatewayState.Approved;
        gateway.ApprovedAt = Now;
        gateway.ApprovedByUserId = _orgContext.CurrentUserId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Gateway {GatewayId} key {Fingerprint} approved by user {UserId}.",
            gatewayId, gateway.KeyFingerprint, _orgContext.CurrentUserId);
    }

    /// <summary>Revokes the gateway: its next request is refused. There is no undo.</summary>
    public async Task RevokeAsync(int gatewayId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct).ConfigureAwait(false);
        var gateway = await _db.Gateways.FirstOrDefaultAsync(g => g.Id == gatewayId, ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["gateway"] = "That gateway no longer exists." });
        if (gateway.RevokedAt is not null) return;
        gateway.RevokedAt = Now;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Gateway {GatewayId} revoked by user {UserId}.", gatewayId, _orgContext.CurrentUserId);
    }

    /// <summary>Renames the gateway on the Gateways page. The machine name is the gateway's to report.</summary>
    public async Task RenameAsync(int gatewayId, string name, CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct).ConfigureAwait(false);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["name"] = "Enter a name, e.g. CRONUS server room." });
        }
        if (trimmed.Length > MaxNameLength)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["name"] = $"Keep the name to {MaxNameLength} characters or fewer." });
        }
        var gateway = await LoadLiveAsync(gatewayId, ct).ConfigureAwait(false);
        if (gateway.Name == trimmed) return;
        gateway.Name = trimmed;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Gateway {GatewayId} renamed to {Name}.", gatewayId, trimmed);
    }

    private async Task<Gateway> LoadLiveAsync(int gatewayId, CancellationToken ct)
    {
        var gateway = await _db.Gateways.FirstOrDefaultAsync(g => g.Id == gatewayId, ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["gateway"] = "That gateway no longer exists." });
        if (gateway.RevokedAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["gateway"] = "This gateway has been revoked." });
        }
        return gateway;
    }

    // ---------------------------------------------------------------- the gateway's own calls

    /// <summary>
    /// The key and organisation of a gateway that is not revoked, read before any
    /// claim exists so the handler can verify the token and build the principal.
    /// </summary>
    public async Task<GatewayCredential?> FindCredentialAsync(int gatewayId, CancellationToken ct = default)
    {
        // Category 1 (pre-auth routing): id == the token's iss && revoked_at == null routes one signed request to exactly one org; no claim exists yet.
        return await _db.Gateways.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.Id == gatewayId && g.RevokedAt == null)
            .Select(g => new GatewayCredential(g.Id, g.OrganizationId, g.Name, g.PublicKey, g.State))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remembers a token id so it cannot be used again. Returns false when it has
    /// been seen before. An insert, not a read: the unique index on (gateway, jti)
    /// decides, so nothing crosses the query filter, and the row is stamped with the
    /// organisation of the gateway that signed it. <c>ON CONFLICT DO NOTHING</c>
    /// rather than catching the violation, so a replay attempt is one warning from
    /// the handler instead of a database error in the log.
    /// </summary>
    public async Task<bool> TryRecordTokenAsync(GatewayCredential gateway, string tokenId, DateTime keepUntil, CancellationToken ct = default)
    {
        var inserted = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO gateway_seen_tokens (organization_id, gateway_id, token_id, expires_at)
            VALUES ({gateway.OrganizationId}, {gateway.GatewayId}, {tokenId}, {keepUntil})
            ON CONFLICT (gateway_id, token_id) DO NOTHING
            """, ct).ConfigureAwait(false);
        return inserted == 1;
    }

    /// <summary>
    /// Records that the calling gateway is alive, and which version it runs. Runs
    /// under the gateway's own organisation claim. Returns null when the gateway is
    /// gone or was revoked since its token was checked.
    /// </summary>
    public async Task<GatewayHeartbeatResponse?> HeartbeatAsync(int gatewayId, GatewayHeartbeatRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOrganizationId();
        var errors = new Dictionary<string, string>();
        var version = request.Version.Trim();
        var machineName = request.MachineName.Trim();
        if (version.Length > MaxReportedLength) errors["version"] = $"The version is longer than {MaxReportedLength} characters.";
        if (machineName.Length > MaxReportedLength) errors["machineName"] = $"The computer name is longer than {MaxReportedLength} characters.";
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var now = Now;
        // ExecuteUpdate on purpose: a heartbeat a minute is not an audit row a minute.
        var live = _db.Gateways.Where(g => g.Id == gatewayId && g.RevokedAt == null);
        var updated = machineName.Length > 0
            ? await live.ExecuteUpdateAsync(s => s
                .SetProperty(g => g.LastSeenAt, now)
                .SetProperty(g => g.ReportedVersion, NullIfEmpty(version))
                .SetProperty(g => g.MachineName, machineName), ct).ConfigureAwait(false)
            : await live.ExecuteUpdateAsync(s => s
                .SetProperty(g => g.LastSeenAt, now)
                .SetProperty(g => g.ReportedVersion, NullIfEmpty(version)), ct).ConfigureAwait(false);
        if (updated == 0) return null;

        var state = await live.AsNoTracking().Select(g => g.State).FirstAsync(ct).ConfigureAwait(false);

        // The replay table only needs a row until its token could no longer be
        // accepted; each heartbeat clears this gateway's spent ones. The command
        // sweep (#1361) will take this over for gateways that stop calling.
        await _db.GatewaySeenTokens
            .Where(t => t.GatewayId == gatewayId && t.ExpiresAt < now)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return new GatewayHeartbeatResponse { Approved = state == GatewayState.Approved };
    }

    // ---------------------------------------------------------------- helpers

    private async Task EnsureAdminAsync(CancellationToken ct)
    {
        if (_orgContext.IsSiteAdmin && _orgContext.CurrentOrganizationId is not null) return;
        var userId = _orgContext.CurrentUserId;
        var isAdmin = userId is not null && await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) == UserRole.Admin;
        if (!isAdmin)
        {
            throw new UnauthorizedAccessException("Only an organisation admin can manage gateways.");
        }
    }

    /// <summary>A fresh code, in groups of four for reading and typing: <c>ABCD-EFGH-...</c>.</summary>
    private static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(CodeBytes);
        var chars = new StringBuilder(CodeBytes * 8 / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars.Append(CodeAlphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        var raw = chars.ToString();
        return string.Join('-', Enumerable.Range(0, raw.Length / 4).Select(i => raw.Substring(i * 4, 4)));
    }

    /// <summary>Upper case, without the dashes and spaces a person may type or paste.</summary>
    internal static string NormaliseCode(string? code) =>
        new((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    internal static string HashCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormaliseCode(code))));

    /// <summary>The DER <c>SubjectPublicKeyInfo</c> when it is a P-256 ECDSA key, else null.</summary>
    private static byte[]? ParsePublicKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var der = Convert.FromBase64String(base64.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out var read);
            if (read != der.Length) return null;
            var curve = key.ExportParameters(includePrivateParameters: false).Curve;
            return curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value ? der : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
