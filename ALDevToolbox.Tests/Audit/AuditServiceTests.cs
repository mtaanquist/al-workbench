using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Covers <see cref="AuditService"/>'s read-side queries: cross-org isolation
/// (audit rows from another org must never leak through any of the four query
/// methods), newest-first ordering with id as the tie-breaker, and the
/// "next-newer for entity" lookup the diff viewer depends on.
/// </summary>
public sealed class AuditServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetRecentAsync_only_returns_rows_from_acting_org()
    {
        await SeedAuditRowsAsync();

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var rows = await NewService().GetRecentAsync();

        rows.Should().OnlyContain(r => r.OrganizationId == TestDb.DefaultOrgId);
        rows.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task GetRecentAsync_orders_newest_first_with_id_tiebreaker()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var ctx = _db.NewContext())
        {
            ctx.AuditLog.AddRange(
                NewEntry(ts, TestDb.DefaultOrgId, "A"),
                NewEntry(ts, TestDb.DefaultOrgId, "B"),
                NewEntry(ts.AddMinutes(1), TestDb.DefaultOrgId, "C"));
            await ctx.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var rows = await NewService().GetRecentAsync();

        rows.Select(r => r.ChangedBy).Should().Equal("C", "B", "A");
    }

    [Fact]
    public async Task GetForEntityAsync_filters_by_entity_and_org()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var ctx = _db.NewContext())
        {
            ctx.AuditLog.Add(NewEntry(ts, TestDb.DefaultOrgId, "default-tpl-1",
                entityType: AuditEntityType.RuntimeTemplate, entityId: 42));
            ctx.AuditLog.Add(NewEntry(ts, TestDb.DefaultOrgId, "default-mod-1",
                entityType: AuditEntityType.Module, entityId: 42));
            ctx.AuditLog.Add(NewEntry(ts, TestDb.OtherOrgId, "other-tpl-1",
                entityType: AuditEntityType.RuntimeTemplate, entityId: 42));
            await ctx.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var rows = await NewService().GetForEntityAsync(AuditEntityType.RuntimeTemplate, 42);

        rows.Should().HaveCount(1);
        rows[0].ChangedBy.Should().Be("default-tpl-1");
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_a_row_in_another_org()
    {
        int otherOrgRowId;
        await using (var ctx = _db.NewContext())
        {
            var row = NewEntry(DateTime.UtcNow, TestDb.OtherOrgId, "alice");
            ctx.AuditLog.Add(row);
            await ctx.SaveChangesAsync();
            otherOrgRowId = row.Id;
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        (await NewService().GetByIdAsync(otherOrgRowId)).Should().BeNull();

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        (await NewService().GetByIdAsync(otherOrgRowId)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetNextForEntityAsync_returns_next_newer_row_for_same_entity()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        AuditLogEntry first, second, third;
        await using (var ctx = _db.NewContext())
        {
            first = NewEntry(t0, TestDb.DefaultOrgId, "alice",
                entityType: AuditEntityType.RuntimeTemplate, entityId: 7);
            second = NewEntry(t0.AddMinutes(5), TestDb.DefaultOrgId, "bob",
                entityType: AuditEntityType.RuntimeTemplate, entityId: 7);
            third = NewEntry(t0.AddMinutes(10), TestDb.DefaultOrgId, "carol",
                entityType: AuditEntityType.RuntimeTemplate, entityId: 7);
            ctx.AuditLog.AddRange(first, second, third);
            await ctx.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var next = await NewService().GetNextForEntityAsync(first);
        next.Should().NotBeNull();
        next!.Id.Should().Be(second.Id);

        var nextOfThird = await NewService().GetNextForEntityAsync(third);
        nextOfThird.Should().BeNull("nothing after the most recent");
    }

    /// <summary>
    /// "_" and "%" typed into "Changed by" match themselves, not any character
    /// (#1550): j_doe@ must not list jxdoe@, and a lone % must not list everything.
    /// </summary>
    [Fact]
    public async Task The_changed_by_filter_takes_wildcard_characters_literally()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var ctx = _db.NewContext())
        {
            ctx.AuditLog.AddRange(
                NewEntry(ts, TestDb.DefaultOrgId, "j_doe@cronus.example"),
                NewEntry(ts, TestDb.DefaultOrgId, "jxdoe@cronus.example"),
                NewEntry(ts, TestDb.DefaultOrgId, "100%@cronus.example"));
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        async Task<IEnumerable<string>> ByAsync(string actor) =>
            (await NewService().GetPagedAsync(new AuditFilter(null, null, null, actor, null, null), 0, 50)).Items.Select(r => r.ChangedBy);

        (await ByAsync("j_doe")).Should().Equal("j_doe@cronus.example");
        (await ByAsync("%")).Should().Equal("100%@cronus.example");
        (await ByAsync("JXDOE")).Should().Equal(["jxdoe@cronus.example"], "the match is still case-insensitive");
    }

    private AuditService NewService() => new(_db.NewContextFactory(), _db.OrgContext);

    private async Task SeedAuditRowsAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.AuditLog.Add(NewEntry(DateTime.UtcNow, TestDb.DefaultOrgId, "default-1"));
        ctx.AuditLog.Add(NewEntry(DateTime.UtcNow, TestDb.OtherOrgId, "other-1"));
        await ctx.SaveChangesAsync();
    }

    private static AuditLogEntry NewEntry(
        DateTime timestamp,
        int organizationId,
        string changedBy,
        AuditEntityType entityType = AuditEntityType.RuntimeTemplate,
        int entityId = 1)
        => new()
        {
            OrganizationId = organizationId,
            EntityType = entityType,
            EntityId = entityId,
            Action = AuditAction.Updated,
            Timestamp = timestamp,
            ChangedBy = changedBy,
            SnapshotJson = null,
        };
}
