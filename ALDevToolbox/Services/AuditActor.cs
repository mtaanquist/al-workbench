using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// Composes the <c>"display name &lt;email&gt;"</c> string the audit log records an
/// actor as, looked up from the database rather than from claims, and names the
/// actor when no person is behind a write.
///
/// <para>Why from the database: <see cref="Data.AuditInterceptor"/> reads the signing-in
/// user's claims off <c>HttpContext</c>, which a background worker lacks. Anything that
/// records an actor outside a plain HTTP request resolves it here instead, so one
/// environment's history reads alike whichever route wrote it.</para>
///
/// <para>Who wrote a row with no user behind it: a background worker runs inside
/// <see cref="EnterBackground"/> (the worker base classes do this for every tick and
/// job), so its writes read <c>"System (BackupScheduler)"</c>; any other write with no
/// request and no worker reads <see cref="System"/>; and a request from someone not yet
/// signed in (sign-up, a password reset, an invite being accepted) reads
/// <see cref="NotSignedIn"/>.</para>
/// </summary>
public static class AuditActor
{
    /// <summary>
    /// What the log called every actor it could not identify before system writes were
    /// named. Kept because rows written before then still carry it; see <see cref="Display"/>.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>A write with no person behind it and no named worker in scope.</summary>
    public const string System = "System";

    /// <summary>A write from a web request made before anyone signed in.</summary>
    public const string NotSignedIn = "Not signed in";

    private static readonly AsyncLocal<string?> _background = new();

    /// <summary>The background worker in scope for this async flow, or null.</summary>
    public static string? CurrentBackground => _background.Value;

    /// <summary>
    /// Names the background worker for every write in this async flow until the
    /// returned scope is disposed. Nested scopes restore the outer name on dispose.
    /// </summary>
    public static IDisposable EnterBackground(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var previous = _background.Value;
        _background.Value = name;
        return new Restore(previous);
    }

    /// <summary>
    /// The actor for a write with no user behind it: <c>"System (worker)"</c> inside
    /// <see cref="EnterBackground"/>, else <see cref="System"/>.
    /// </summary>
    public static string SystemActor() =>
        _background.Value is { } name ? $"{System} ({name})" : System;

    /// <summary>
    /// The user in <c>"display name &lt;email&gt;"</c> form, falling back to the display
    /// name or the email alone. With no user, a background worker in scope is named
    /// (see <see cref="SystemActor"/>); otherwise, or when the account is gone, it is
    /// <see cref="Unknown"/>.
    /// </summary>
    public static async Task<string> ResolveAsync(AppDbContext db, int? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null) return _background.Value is null ? Unknown : SystemActor();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.DisplayName, u.Email })
            .FirstOrDefaultAsync(ct);
        if (user is null) return Unknown;

        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email : user.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return Unknown;
        return string.IsNullOrWhiteSpace(user.Email) ? name : $"{name} <{user.Email}>";
    }

    /// <summary>
    /// How a stored actor reads on screen. Rows written before system writes were named
    /// say <see cref="Unknown"/> with no user id; those are shown as <see cref="System"/>,
    /// except on the three kinds of record a signed-out visitor writes (a sign-up, an
    /// account, an invite), where <see cref="NotSignedIn"/> is the likelier truth. A row
    /// that carries a user id keeps its stored text: that user existed and was deleted.
    /// </summary>
    public static string Display(string changedBy, int? changedByUserId, AuditEntityType entityType)
    {
        if (changedBy != Unknown || changedByUserId is not null) return changedBy;
        return entityType is AuditEntityType.SignupRequest or AuditEntityType.User or AuditEntityType.Invite
            ? NotSignedIn
            : System;
    }

    /// <summary>
    /// Narrows an audit query to rows whose actor matches <paramref name="likePattern"/>
    /// (escaped with a backslash) as stored, or reads as <paramref name="search"/> on screen: an old <see cref="Unknown"/>
    /// row is shown as <see cref="System"/> or <see cref="NotSignedIn"/> (see
    /// <see cref="Display(string, int?, AuditEntityType)"/>), so a search for either finds it.
    /// </summary>
    public static IQueryable<AuditLogEntry> WhereActorMatches(
        IQueryable<AuditLogEntry> query, string search, string likePattern)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = search.Trim();
        var system = System.Contains(text, StringComparison.OrdinalIgnoreCase);
        var notSignedIn = NotSignedIn.Contains(text, StringComparison.OrdinalIgnoreCase);
        return query.Where(e =>
            EF.Functions.ILike(e.ChangedBy, likePattern, "\\")
            || (e.ChangedBy == Unknown && e.ChangedByUserId == null
                && ((notSignedIn && (e.EntityType == AuditEntityType.SignupRequest
                                     || e.EntityType == AuditEntityType.User
                                     || e.EntityType == AuditEntityType.Invite))
                    || (system && e.EntityType != AuditEntityType.SignupRequest
                               && e.EntityType != AuditEntityType.User
                               && e.EntityType != AuditEntityType.Invite))));
    }

    /// <inheritdoc cref="Display(string, int?, AuditEntityType)"/>
    public static string Display(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Display(entry.ChangedBy, entry.ChangedByUserId, entry.EntityType);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Restore(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _background.Value = _previous;
        }
    }
}
