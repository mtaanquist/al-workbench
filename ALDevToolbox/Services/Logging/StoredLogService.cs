using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Logging;

/// <summary>Which lines <see cref="StoredLogService.SearchAsync"/> returns.</summary>
/// <param name="Worker">
/// <see cref="StoredLogService.AnySource"/> for every line, <see cref="StoredLogService.AnyWorker"/>
/// for lines from any background worker, <see cref="StoredLogService.NoWorker"/> for lines from
/// web requests and startup, or a worker's name. A name also matches its numbered siblings, so
/// <c>ProjectBuildWorker</c> finds <c>ProjectBuildWorker 3</c>.
/// </param>
/// <param name="MinimumLevel">The least severe level returned.</param>
/// <param name="Text">Words the message, the error or the source must contain; case does not matter.</param>
/// <param name="FromUtc">The earliest line returned.</param>
/// <param name="ToUtc">The latest line returned, inclusive.</param>
/// <param name="BeforeId">Return only lines older than this one: the pager's cursor.</param>
public sealed record StoredLogQuery(
    string Worker,
    LogLevel MinimumLevel,
    string? Text,
    DateTime? FromUtc,
    long? BeforeId,
    DateTime? ToUtc = null);

/// <summary>One page of stored lines, newest first, and whether older ones exist.</summary>
public sealed record StoredLogPage(IReadOnlyList<AppLogEntry> Rows, bool HasOlder);

/// <summary>
/// Reads <c>app_log_entries</c> for <c>/site-admin/logs</c>. SiteAdmin only. The table
/// carries no organisation and no query filter, so nothing here crosses the tenant fence.
/// </summary>
public sealed class StoredLogService
{
    public const string AnySource = "any";
    public const string AnyWorker = "workers";
    public const string NoWorker = "none";

    /// <summary>Lines per page. Paged by id rather than counted: a count over millions of rows is slow.</summary>
    public const int PageSize = 100;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    public StoredLogService(AppDbContext db, IOrganizationContext orgContext)
    {
        _db = db;
        _orgContext = orgContext;
    }

    /// <summary>The newest lines matching <paramref name="query"/>.</summary>
    public async Task<StoredLogPage> SearchAsync(StoredLogQuery query, CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        IQueryable<AppLogEntry> rows = _db.AppLogEntries.AsNoTracking();

        switch (query.Worker)
        {
            case AnySource:
                break;
            case AnyWorker:
                rows = rows.Where(e => e.Worker != null);
                break;
            case NoWorker:
                rows = rows.Where(e => e.Worker == null);
                break;
            default:
                var sibling = EscapeLike(query.Worker) + " %";
                rows = rows.Where(e => e.Worker == query.Worker || EF.Functions.Like(e.Worker!, sibling, "\\"));
                break;
        }

        var minimum = StoredLogBuffer.Normalise(query.MinimumLevel);
        if (minimum > LogLevel.Information && minimum != LogLevel.None)
        {
            rows = rows.Where(e => e.Level >= minimum);
        }
        if (query.FromUtc is { } from) rows = rows.Where(e => e.Timestamp >= from);
        if (query.ToUtc is { } to) rows = rows.Where(e => e.Timestamp <= to);
        if (query.BeforeId is { } before) rows = rows.Where(e => e.Id < before);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var pattern = "%" + EscapeLike(query.Text.Trim()) + "%";
            rows = rows.Where(e =>
                EF.Functions.ILike(e.Message, pattern, "\\")
                || EF.Functions.ILike(e.Category, pattern, "\\")
                || (e.Exception != null && EF.Functions.ILike(e.Exception, pattern, "\\")));
        }

        var page = await rows
            .OrderByDescending(e => e.Id)
            .Take(PageSize + 1)
            .ToListAsync(ct);
        var hasOlder = page.Count > PageSize;
        if (hasOlder) page.RemoveAt(page.Count - 1);
        return new StoredLogPage(page, hasOlder);
    }

    /// <summary>How much disk the stored lines take, indexes included, in bytes.</summary>
    public async Task<long> TableSizeBytesAsync(CancellationToken ct = default)
    {
        _orgContext.RequireSiteAdmin();
        return await _db.Database
            .SqlQueryRaw<long>("SELECT pg_total_relation_size('app_log_entries') AS \"Value\"")
            .SingleAsync(ct);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
