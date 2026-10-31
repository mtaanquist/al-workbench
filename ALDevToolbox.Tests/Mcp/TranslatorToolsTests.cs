using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Services.Translation.Providers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP boundary of <see cref="TranslatorTools"/>. The class comment promises
/// that <c>remove_translation</c> "is gated to the Editor / Admin role like the
/// web page" — if that gate regresses, a plain User with a PAT can soft-delete
/// the organisation's translation memory while the web page's own gate keeps
/// working, so the drift is invisible. These tests pin the gate, the direction
/// parsing of <c>vote_translation</c>, and that another organisation's entry id
/// surfaces as a plain "not found" rather than a distinct refusal.
///
/// <para>
/// Note: <c>vote_translation</c> deliberately carries <em>no</em> role gate
/// today, unlike its sibling — the tests below pin the current behaviour (a
/// User can vote) rather than assert a desired one. Whether a write like that
/// should require Editor is a maintainer decision; changing it means changing
/// <c>Vote_as_a_plain_User_is_allowed_today</c> too.
/// </para>
/// </summary>
public sealed class TranslatorToolsTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    // Only machine_translate touches the machine-translation service; the tests
    // that call it build their own with NewMachineTranslationTools.
    private TranslatorTools NewTools(AppDbContext ctx) =>
        new(new TranslationMemoryService(ctx, _db.OrgContext, NullLogger<TranslationMemoryService>.Instance),
            machineTranslation: null!,
            ctx,
            _db.OrgContext);

    private async Task<int> SeedUserAsync(int userId, UserRole role, bool isSiteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = userId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"tr{userId}@example.com",
            PasswordHash = "x",
            DisplayName = $"Translator {userId}",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = isSiteAdmin;
        return userId;
    }

    private async Task<long> SeedEntryAsync(
        int organizationId = TestDb.DefaultOrgId,
        string? sourceRepository = null,
        string? sourcePath = null)
    {
        await using var ctx = _db.NewContext();
        var entry = new TranslationMemoryEntry
        {
            OrganizationId = organizationId,
            SourceLanguage = "en-US",
            TargetLanguage = "da-DK",
            SourceText = "Posting Date",
            TargetText = "Bogføringsdato",
            SourceHash = Guid.NewGuid().ToString("N"),
            TargetHash = Guid.NewGuid().ToString("N"),
            Kind = "caption",
            Origin = "CRONUS Core",
            SourceRepository = sourceRepository,
            SourcePath = sourcePath,
            HitCount = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        };
        ctx.TranslationMemory.Add(entry);
        await ctx.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<DateTime?> DeletedAtAsync(long entryId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.TranslationMemory.IgnoreQueryFilters()
            .Where(e => e.Id == entryId).Select(e => e.DeletedAt).FirstOrDefaultAsync();
    }

    // ---- remove_translation: the role gate --------------------------------

    [Fact]
    public async Task Remove_as_a_plain_User_is_refused_and_leaves_the_entry_alive()
    {
        await SeedUserAsync(9100, UserRole.User);
        var entryId = await SeedEntryAsync();

        await using var ctx = _db.NewContext();
        await FluentActions.Awaiting(() => NewTools(ctx).RemoveTranslationAsync(entryId))
            .Should().ThrowAsync<McpException>();

        (await DeletedAtAsync(entryId)).Should().BeNull("the refusal must not have removed anything");
    }

    [Theory]
    [InlineData(UserRole.Editor, false)]
    [InlineData(UserRole.Admin, false)]
    [InlineData(UserRole.User, true)] // SiteAdmin overrides the org role
    public async Task Remove_soft_deletes_for_editor_admin_and_site_admin(UserRole role, bool isSiteAdmin)
    {
        await SeedUserAsync(9101, role, isSiteAdmin);
        var entryId = await SeedEntryAsync();

        await using var ctx = _db.NewContext();
        var result = await NewTools(ctx).RemoveTranslationAsync(entryId);

        result.EntryId.Should().Be(entryId);
        result.Removed.Should().BeTrue();
        (await DeletedAtAsync(entryId)).Should().NotBeNull("remove is a soft-delete, recoverable from the web page");
    }

    [Fact]
    public async Task Remove_without_a_user_in_scope_is_refused()
    {
        var entryId = await SeedEntryAsync();
        _db.OrgContext.CurrentUserId = null;

        await using var ctx = _db.NewContext();
        await FluentActions.Awaiting(() => NewTools(ctx).RemoveTranslationAsync(entryId))
            .Should().ThrowAsync<McpException>();

        (await DeletedAtAsync(entryId)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_of_another_orgs_entry_surfaces_as_not_found()
    {
        await SeedUserAsync(9102, UserRole.Admin);
        var otherOrgEntry = await SeedEntryAsync(TestDb.OtherOrgId);

        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).RemoveTranslationAsync(otherOrgEntry);

        // Not a distinct refusal: an id in another tenant must be indistinguishable
        // from an id that never existed (the pattern DeliveryToolsTests documents).
        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
        (await DeletedAtAsync(otherOrgEntry)).Should().BeNull();
    }

    // ---- vote_translation: direction parsing and scoping -------------------

    [Theory]
    [InlineData("up", 1, 1)]
    [InlineData("UP", 1, 1)]
    [InlineData(" down ", -1, -1)]
    [InlineData("clear", 0, 0)]
    [InlineData("none", 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData(null, 0, 0)]
    public async Task Vote_accepts_up_down_and_clear(string? direction, int expectedScore, int expectedMyVote)
    {
        await SeedUserAsync(9103, UserRole.User);
        var entryId = await SeedEntryAsync();

        await using var ctx = _db.NewContext();
        var result = await NewTools(ctx).VoteTranslationAsync(entryId, direction!);

        result.EntryId.Should().Be(entryId);
        result.Score.Should().Be(expectedScore);
        result.MyVote.Should().Be(expectedMyVote);
    }

    /// <summary>
    /// Pins today's behaviour, not a preference: <c>vote_translation</c> has no
    /// role gate, so a plain User can write a vote. See the class remark.
    /// </summary>
    [Fact]
    public async Task Vote_as_a_plain_User_is_allowed_today()
    {
        await SeedUserAsync(9104, UserRole.User);
        var entryId = await SeedEntryAsync();

        await using var ctx = _db.NewContext();
        var result = await NewTools(ctx).VoteTranslationAsync(entryId, "up");

        result.MyVote.Should().Be(1);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("+1")]
    [InlineData("yes")]
    public async Task Vote_rejects_a_direction_it_does_not_understand(string direction)
    {
        await SeedUserAsync(9105, UserRole.Editor);
        var entryId = await SeedEntryAsync();

        await using var ctx = _db.NewContext();
        await FluentActions.Awaiting(() => NewTools(ctx).VoteTranslationAsync(entryId, direction))
            .Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task A_hit_learned_from_a_repository_names_the_file_it_came_from()
    {
        // The agent-facing half of #631: an entry the ingest learned carries the
        // repository and path, so an agent can go and read the translation in
        // context rather than trusting the free-text origin.
        await SeedUserAsync(9107, UserRole.Editor);
        await SeedEntryAsync(sourceRepository: "cronus-dk/customer-app",
            sourcePath: "PaymentImport/Translations/PaymentImport.da-DK.xlf");

        await using var ctx = _db.NewContext();
        var hits = await NewTools(ctx).SearchTranslationMemoryAsync("Posting Date");

        hits.Should().ContainSingle();
        hits[0].SourceRepository.Should().Be("cronus-dk/customer-app");
        hits[0].SourcePath.Should().Be("PaymentImport/Translations/PaymentImport.da-DK.xlf");
    }

    [Fact]
    public async Task A_hit_that_came_from_an_upload_names_no_repository()
    {
        await SeedUserAsync(9108, UserRole.Editor);
        await SeedEntryAsync();

        await using var ctx = _db.NewContext();
        var hits = await NewTools(ctx).SearchTranslationMemoryAsync("Posting Date");

        hits[0].SourceRepository.Should().BeNull();
        hits[0].SourcePath.Should().BeNull();
    }

    // ---- search_translation_memory: removed entries are the Editor's view (#1547)

    private async Task<long> SeedRemovedEntryAsync()
    {
        var entryId = await SeedEntryAsync();
        await using var ctx = _db.NewContext();
        await ctx.TranslationMemory.Where(e => e.Id == entryId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow));
        return entryId;
    }

    [Fact]
    public async Task A_plain_User_asking_for_removed_entries_does_not_get_them()
    {
        await SeedUserAsync(9109, UserRole.User);
        await SeedRemovedEntryAsync();

        await using var ctx = _db.NewContext();
        var hits = await NewTools(ctx).SearchTranslationMemoryAsync("Posting Date", includeRemoved: true);

        hits.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UserRole.Editor, false)]
    [InlineData(UserRole.Admin, false)]
    [InlineData(UserRole.User, true)] // SiteAdmin overrides the org role
    public async Task An_editor_asking_for_removed_entries_gets_them(UserRole role, bool isSiteAdmin)
    {
        await SeedUserAsync(9110, role, isSiteAdmin);
        var entryId = await SeedRemovedEntryAsync();

        await using var ctx = _db.NewContext();
        var hits = await NewTools(ctx).SearchTranslationMemoryAsync("Posting Date", includeRemoved: true);

        hits.Should().ContainSingle().Which.EntryId.Should().Be(entryId);
        hits[0].Removed.Should().BeTrue();
    }

    [Fact]
    public async Task Vote_on_another_orgs_entry_surfaces_as_not_found()
    {
        await SeedUserAsync(9106, UserRole.Editor);
        var otherOrgEntry = await SeedEntryAsync(TestDb.OtherOrgId);

        await using var ctx = _db.NewContext();
        var act = () => NewTools(ctx).VoteTranslationAsync(otherOrgEntry, "up");

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not found");
    }
    [Fact]
    public async Task Machine_translate_without_a_provider_set_up_points_at_the_settings()
    {
        await using var ctx = _db.NewContext();
        var tools = NewMachineTranslationTools(ctx, new ThrowingProvider());

        var act = () => tools.MachineTranslateAsync("Hello", "da-DK");

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task Machine_translate_reports_a_provider_failure_as_a_failure_not_as_missing_setup()
    {
        // A used-up quota or a refused language used to read as "not configured"
        // and sent the admin to a settings page that was already right (#1344).
        await using var ctx = _db.NewContext();
        var settings = _db.NewMachineTranslationSettingsService(ctx);
        await settings.SaveMachineTranslationAsync(new MtSettingsInput("deepl", "k:fx", false, MtTrigger.OnDemand));
        var tools = NewMachineTranslationTools(ctx, new ThrowingProvider());

        var act = () => tools.MachineTranslateAsync("Hello", "da-DK");

        var ex = (await act.Should().ThrowAsync<McpException>()).Which;
        ex.Message.Should().Contain("refused or failed");
        ex.Message.Should().NotContain("not configured");
    }

    private TranslatorTools NewMachineTranslationTools(AppDbContext ctx, IMachineTranslationProvider provider) =>
        new(new TranslationMemoryService(ctx, _db.OrgContext, NullLogger<TranslationMemoryService>.Instance),
            new MachineTranslationService(
                _db.NewMachineTranslationSettingsService(ctx),
                new SingleProviderFactory(provider),
                NullLogger<MachineTranslationService>.Instance),
            ctx,
            _db.OrgContext);

    private sealed class SingleProviderFactory(IMachineTranslationProvider provider) : IMachineTranslationProviderFactory
    {
        public IMachineTranslationProvider Create(ResolvedMtSettings settings) => provider;
    }

    private sealed class ThrowingProvider : IMachineTranslationProvider
    {
        public Task<MtTestResult> TestConnectionAsync(CancellationToken ct) => Task.FromResult(new MtTestResult(false, "x"));
        public Task<MtTranslation> TranslateAsync(string sourceText, string? sourceLanguage, string targetLanguage, string? context, CancellationToken ct)
            => throw new HttpRequestException("Quota exceeded");
    }
}
