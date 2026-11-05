using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Audit;

/// <summary>
/// Covers <see cref="AuditActor"/>: who a write with no user behind it is attributed
/// to, and how rows written before that distinction read on screen.
/// </summary>
public sealed class AuditActorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(AuditEntityType.Backup, "System")]
    [InlineData(AuditEntityType.OrganizationSettings, "System")]
    [InlineData(AuditEntityType.SignupRequest, "Not signed in")]
    [InlineData(AuditEntityType.User, "Not signed in")]
    [InlineData(AuditEntityType.Invite, "Not signed in")]
    public void An_old_unknown_row_reads_as_who_most_likely_wrote_it(AuditEntityType type, string expected) =>
        AuditActor.Display("unknown", changedByUserId: null, type).Should().Be(expected);

    [Fact]
    public void An_unknown_row_with_a_user_id_keeps_its_text() =>
        // The user existed and has since been deleted; that is not the system.
        AuditActor.Display("unknown", changedByUserId: 42, AuditEntityType.Backup).Should().Be("unknown");

    [Fact]
    public void A_named_actor_is_shown_as_stored() =>
        AuditActor.Display("Anna Jensen <anna@cronus.example>", 7, AuditEntityType.Backup)
            .Should().Be("Anna Jensen <anna@cronus.example>");

    [Fact]
    public void Background_scopes_nest_and_restore()
    {
        AuditActor.SystemActor().Should().Be("System");
        using (AuditActor.EnterBackground("Outer"))
        {
            using (AuditActor.EnterBackground("Inner"))
            {
                AuditActor.SystemActor().Should().Be("System (Inner)");
            }
            AuditActor.SystemActor().Should().Be("System (Outer)");
        }
        AuditActor.CurrentBackground.Should().BeNull();
    }

    [Fact]
    public async Task Resolving_no_user_inside_a_worker_names_the_worker()
    {
        await using var ctx = _db.NewContext();
        var ct = TestContext.Current.CancellationToken;

        (await AuditActor.ResolveAsync(ctx, null, ct)).Should().Be("unknown");
        using (AuditActor.EnterBackground("UpgradeActionWorker"))
        {
            (await AuditActor.ResolveAsync(ctx, null, ct)).Should().Be("System (UpgradeActionWorker)");
        }
    }
}
