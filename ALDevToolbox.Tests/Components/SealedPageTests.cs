using System.Security.Claims;
using System.Text.Json;
using SealedPage = ALDevToolbox.Components.Pages.Sealed;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Sealed;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/sealed</c>, the sender's half (.design/sealed.md): creating a link stores what
/// the browser sealed with the expiry picked, an expiry off the list is refused next to
/// its field, only a link the sender can still stop offers Revoke, and Revoke asks
/// first (#1438). The browser's crypto is stood in for by <see cref="SealingBrowser"/>.
/// </summary>
public sealed class SealedPageTests : IAsyncDisposable
{
    private const int AlexUserId = 9350;
    private const int SamUserId = 9351;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public SealedPageTests()
    {
        using (var seed = _db.NewContext())
        {
            seed.Users.AddRange(NewUser(AlexUserId, "alex@cronus.test"), NewUser(SamUserId, "sam@cronus.test"));
            seed.SaveChanges();
        }
        _db.OrgContext.CurrentUserId = AlexUserId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("alex@cronus.test");
        auth.SetRoles("User");
        auth.SetClaims(new Claim(HttpOrganizationContext.UserIdClaim, AlexUserId.ToString()));

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<SealedLinkService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddSingleton<IJSRuntime>(new SealingBrowser());
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    /// <summary>
    /// What wwwroot/sealed.js answers, with random bytes of the right sizes for the
    /// ciphertext and the checks: the server only ever sees opaque bytes, so nothing
    /// here depends on real encryption. The page's result type is private, so the
    /// answer is handed over as JSON, as the real interop does.
    /// </summary>
    private sealed class SealingBrowser : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? answer = identifier switch
            {
                "aldtSealed.seal" => new
                {
                    handle = "h1",
                    ciphertext = B64(64),
                    iv = B64(12),
                    linkCheck = B64(32),
                },
                "aldtSealed.finish" => true,
                _ => null,
            };
            return ValueTask.FromResult(answer is null
                ? default!
                : JsonSerializer.Deserialize<TValue>(JsonSerializer.Serialize(answer), JsonSerializerOptions.Web)!);
        }

        private static string B64(int bytes) =>
            System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));
    }

    private static User NewUser(int id, string email) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, PasswordHash = "x", DisplayName = email.Split('@')[0],
        Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };

    private async Task<int> SeedLinkAsync(int createdBy, string label, bool revoked = false)
    {
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        var link = new SealedLink
        {
            OrganizationId = TestDb.DefaultOrgId, CreatedByUserId = createdBy,
            Token = Guid.NewGuid().ToString("N")[..22], Label = label,
            Ciphertext = new byte[64], Iv = new byte[12], LinkCheckHash = new byte[32],
            CreatedAt = now.AddMinutes(-5), ExpiresAt = now.AddDays(1),
            ClosedAt = revoked ? now.AddMinutes(-1) : null,
            ClosedReason = revoked ? SealedLinkClosedReason.Revoked : null,
        };
        seed.SealedLinks.Add(link);
        await seed.SaveChangesAsync();
        return link.Id;
    }

    private List<SealedLink> StoredLinks()
    {
        using var ctx = _db.NewContext();
        return ctx.SealedLinks.AsNoTracking().OrderBy(l => l.Id).ToList();
    }

    private IRenderedComponent<SealedPage> RenderLoaded(string loadedSign)
    {
        var cut = _ctx.Render<SealedPage>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(loadedSign));
        return cut;
    }

    private static AngleSharp.Dom.IElement CreateButton(IRenderedComponent<SealedPage> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Create link");

    private static AngleSharp.Dom.IElement ExpirySelect(IRenderedComponent<SealedPage> cut) =>
        cut.FindAll("select").Single(s => s.QuerySelectorAll("option").Any(o => o.TextContent == "In 7 days"));

    [Fact]
    public void Creating_a_link_stores_it_with_the_expiry_picked_and_shows_it_once()
    {
        var cut = RenderLoaded("Links you send will appear here");
        ExpirySelect(cut).Change("168");

        var before = DateTime.UtcNow;
        CreateButton(cut).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Your link is ready"));
        var link = StoredLinks().Should().ContainSingle().Subject;
        link.CreatedByUserId.Should().Be(AlexUserId);
        link.ExpiresAt.Should().BeCloseTo(before.AddHours(168), TimeSpan.FromMinutes(1));
        cut.Markup.Should().Contain("expires in 7 days");
    }

    [Fact]
    public void An_expiry_that_is_not_one_of_the_choices_is_refused_next_to_its_field()
    {
        var cut = RenderLoaded("Links you send will appear here");
        // The select only offers the choices; a tampered circuit can still send another.
        ExpirySelect(cut).Change("5");

        CreateButton(cut).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pick when the link expires: in an hour, a day or a week."));
        cut.Markup.Should().NotContain("Your link is ready");
        StoredLinks().Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_senders_own_open_link_offers_revoke()
    {
        await SeedLinkAsync(AlexUserId, "Payroll API key");
        await SeedLinkAsync(AlexUserId, "Old SMTP password", revoked: true);
        await SeedLinkAsync(SamUserId, "Sam's secret");

        var cut = RenderLoaded("Payroll API key");

        var rows = cut.FindAll("tbody tr");
        rows.Should().HaveCount(2, "a plain user sees only their own links");
        cut.Markup.Should().NotContain("Sam's secret");
        cut.Markup.Should().NotContain("Show everyone's links");
        RevokeIn(rows.Single(r => r.TextContent.Contains("Payroll API key"))).Should().ContainSingle();
        RevokeIn(rows.Single(r => r.TextContent.Contains("Old SMTP password"))).Should().BeEmpty(
            "a link that has stopped working has nothing left to revoke");
    }

    [Fact]
    public async Task Dismissing_revoke_leaves_the_link_working_and_confirming_stops_it()
    {
        var id = await SeedLinkAsync(AlexUserId, "Payroll API key");
        var cut = RenderLoaded("Payroll API key");

        RevokeIn(cut.Find("tbody tr")).Single().Click();
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .Single(b => b.TextContent.Trim() == "Cancel").Click());
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions").Should().BeEmpty());
        StillTrue.For(() => StoredLinks().Single(l => l.Id == id).ClosedAt is null, "saying no must leave the link working");

        RevokeIn(cut.Find("tbody tr")).Single().Click();
        cut.WaitForAssertion(() => cut.FindAll(".confirm-dialog__actions .btn")
            .Single(b => b.TextContent.Trim() == "Revoke link").Click());

        cut.WaitForAssertion(() => cut.Find("tbody tr").TextContent.Should().Contain("Revoked"));
        var link = StoredLinks().Single(l => l.Id == id);
        link.ClosedReason.Should().Be(SealedLinkClosedReason.Revoked);
        link.Ciphertext.Should().BeNull();
    }

    private static IEnumerable<AngleSharp.Dom.IElement> RevokeIn(AngleSharp.Dom.IElement row) =>
        row.QuerySelectorAll("button").Where(b => b.TextContent.Trim() == "Revoke");
}
