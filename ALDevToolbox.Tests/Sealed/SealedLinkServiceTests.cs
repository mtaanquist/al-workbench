using System.Buffers.Text;
using System.Security.Cryptography;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.Sealed;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Sealed;

/// <summary>
/// Sealed links (.design/sealed.md). The browser's crypto is not under test here:
/// the service only ever sees opaque bytes, so random bytes stand in for the
/// ciphertext and the two checks, and what is pinned is what the server does with
/// them - how a view is spent, what refuses one, what closes a link, who may see
/// and revoke it, and that the recipient's half works with no organisation at all.
/// </summary>
public sealed class SealedLinkServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    // ---- create ------------------------------------------------------------

    [Fact]
    public async Task Create_stores_only_ciphertext_and_hashes_and_audits_it()
    {
        var alex = await SignInAsync("alex@cronus.example");
        var input = Input();

        var token = await CreateAsync(input);

        SealedLinkService.IsWellFormedToken(token).Should().BeTrue();
        await using var ctx = _db.NewContext();
        var link = await ctx.SealedLinks.SingleAsync();
        link.Token.Should().Be(token);
        link.CreatedByUserId.Should().Be(alex);
        link.Ciphertext.Should().Equal(Base64Url.DecodeFromChars(input.Ciphertext));
        link.LinkCheckHash.Should().Equal(SHA256.HashData(Base64Url.DecodeFromChars(input.LinkCheck)),
            "only a hash of the check is kept, never the check itself");
        link.ExpiresAt.Should().Be(Start.UtcDateTime.AddHours(24));
        link.MaxViews.Should().Be(1);
        var audit = await ctx.AuditLog.SingleAsync(a => a.EntityType == AuditEntityType.SealedLink);
        audit.Action.Should().Be(AuditAction.Created);
        audit.EntityName.Should().Be("Entra secret for CRONUS");
        audit.ChangedByUserId.Should().Be(alex);
    }

    [Theory]
    [InlineData(2, 1, null, "ExpiresInHours")]
    [InlineData(24, 0, null, "MaxViews")]
    [InlineData(24, 6, null, "MaxViews")]
    [InlineData(24, 1, "x", "Label")]
    public async Task Create_refuses_choices_outside_the_rules(int hours, int views, string? longLabel, string field)
    {
        await SignInAsync("alex@cronus.example");
        var label = longLabel is null ? null : new string('x', SealedLinkService.MaxLabelLength + 1);

        var act = () => CreateAsync(Input(hours: hours, views: views, label: label));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task Create_refuses_a_secret_that_is_not_sealed_or_too_large()
    {
        await SignInAsync("alex@cronus.example");

        var garbled = () => CreateAsync(Input() with { Ciphertext = "not base64url!" });
        var tooLarge = () => CreateAsync(Input() with
        {
            Ciphertext = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SealedLinkService.MaxSecretBytes + 17)),
        });
        var halfPassphrase = () => CreateAsync(Input() with { PassphraseSalt = B64(16), PassphraseCheck = null });

        (await garbled.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Secret");
        (await tooLarge.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Secret");
        (await halfPassphrase.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Passphrase");
    }

    // ---- reveal ------------------------------------------------------------

    [Fact]
    public async Task The_recipient_needs_no_organisation_and_a_one_view_link_closes_after_it()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);
        SignOut();

        var summary = await Service().GetForRecipientAsync(token);
        summary.Should().NotBeNull();
        summary!.OrganizationName.Should().NotBeNullOrWhiteSpace();
        summary.ViewsLeft.Should().Be(1);
        summary.PassphraseSalt.Should().BeNull();

        var result = await Service().RevealAsync(token, input.LinkCheck, null, "203.0.113.4");

        result.Status.Should().Be(SealedRevealStatus.Revealed);
        result.Ciphertext.Should().Equal(Base64Url.DecodeFromChars(input.Ciphertext));
        result.ViewsLeft.Should().Be(0);
        (await Service().RevealAsync(token, input.LinkCheck, null, "203.0.113.4")).Status.Should().Be(SealedRevealStatus.Gone);
        (await Service().GetForRecipientAsync(token)).Should().BeNull();

        var link = await ReadLinkAsync();
        link.Ciphertext.Should().BeNull("a used-up link keeps nothing to decrypt");
        link.ClosedReason.Should().Be(SealedLinkClosedReason.UsedUp);
        link.ViewCount.Should().Be(1);
        link.Events.Should().ContainSingle(e => e.Kind == SealedLinkEventKind.Opened && e.IpAddress == "203.0.113.4");
    }

    [Fact]
    public async Task The_creator_can_open_their_own_link_without_spending_a_view()
    {
        var alex = await SignInAsync("alex@cronus.example");
        var input = Input() with { PassphraseSalt = B64(16), PassphraseCheck = B64(32) };
        var token = await CreateAsync(input);

        (await Service().GetForRecipientAsync(token))!.OpenedByCreator.Should().BeTrue();
        var mine = await Service().RevealAsync(token, input.LinkCheck, input.PassphraseCheck, "203.0.113.4");
        var again = await Service().RevealAsync(token, input.LinkCheck, input.PassphraseCheck, "203.0.113.4");

        mine.Status.Should().Be(SealedRevealStatus.Revealed);
        mine.ByCreator.Should().BeTrue();
        mine.Ciphertext.Should().Equal(Base64Url.DecodeFromChars(input.Ciphertext));
        again.ViewsLeft.Should().Be(1, "the creator's looks spend nothing");
        var link = await ReadLinkAsync();
        link.ViewCount.Should().Be(0);
        link.Events.Should().BeEmpty("nothing to tell the sender about their own look");

        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status
            .Should().Be(SealedRevealStatus.WrongPassphrase, "the creator still needs the passphrase");
        (await ReadLinkAsync()).FailedPassphraseAttempts.Should().Be(1);

        SignOut();
        var summary = await Service().GetForRecipientAsync(token);
        summary!.OpenedByCreator.Should().BeFalse();
        var theirs = await Service().RevealAsync(token, input.LinkCheck, input.PassphraseCheck, null);
        theirs.ByCreator.Should().BeFalse();
        theirs.ViewsLeft.Should().Be(0);
        (await ReadLinkAsync()).CreatedByUserId.Should().Be(alex);
    }

    [Fact]
    public async Task A_creator_look_after_signing_out_is_refused_without_spending_the_view()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);
        SignOut();

        var result = await Service().RevealAsync(token, input.LinkCheck, null, null, asCreator: true);

        result.Status.Should().Be(SealedRevealStatus.NotCreator);
        result.Ciphertext.Should().BeNull();
        var link = await ReadLinkAsync();
        link.ViewCount.Should().Be(0, "the page promised the creator a free look, so nothing may be spent");
        link.ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_creator_gets_nothing_from_a_wrong_link_or_a_closed_one()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);

        (await Service().RevealAsync(token, B64(32), null, null, asCreator: true)).Status.Should().Be(SealedRevealStatus.Gone);
        await Service().RevokeAsync((await Service().ListAsync()).Single().Id);

        (await Service().GetForRecipientAsync(token)).Should().BeNull();
        (await Service().RevealAsync(token, input.LinkCheck, null, null, asCreator: true)).Status.Should().Be(SealedRevealStatus.Gone);
    }

    [Fact]
    public async Task Someone_else_signed_in_spends_a_view_like_any_recipient()
    {
        var alex = await SignInAsync("alex@cronus.example");
        var input = Input(views: 2);
        var token = await CreateAsync(input);

        await SignInAsync("admin@cronus.example", UserRole.Admin);
        (await Service().GetForRecipientAsync(token))!.OpenedByCreator.Should().BeFalse();
        var admin = await Service().RevealAsync(token, input.LinkCheck, null, null);

        admin.ByCreator.Should().BeFalse("only the creator looks for free, not an Admin");
        admin.ViewsLeft.Should().Be(1);

        // The creator's user id under another organisation is not the creator: both ids must match the row.
        _db.OrgContext.CurrentUserId = alex;
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        var elsewhere = await Service().RevealAsync(token, input.LinkCheck, null, null);
        elsewhere.ByCreator.Should().BeFalse();
        elsewhere.ViewsLeft.Should().Be(0);
    }

    [Fact]
    public async Task A_link_without_its_key_part_spends_nothing()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);
        SignOut();

        (await Service().RevealAsync(token, B64(32), null, null)).Status.Should().Be(SealedRevealStatus.Gone);
        (await Service().RevealAsync(token, null, null, null)).Status.Should().Be(SealedRevealStatus.Gone);
        (await Service().RevealAsync("AAAAAAAAAAAAAAAAAAAAAA", input.LinkCheck, null, null)).Status.Should().Be(SealedRevealStatus.Gone);

        (await ReadLinkAsync()).ViewCount.Should().Be(0);
        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status.Should().Be(SealedRevealStatus.Revealed);
    }

    [Fact]
    public async Task A_link_for_three_opens_works_three_times()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input(views: 3);
        var token = await CreateAsync(input);
        SignOut();

        var left = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var result = await Service().RevealAsync(token, input.LinkCheck, null, null);
            result.Status.Should().Be(SealedRevealStatus.Revealed);
            left.Add(result.ViewsLeft);
        }

        left.Should().Equal(2, 1, 0);
        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status.Should().Be(SealedRevealStatus.Gone);
        (await ReadLinkAsync()).ClosedReason.Should().Be(SealedLinkClosedReason.UsedUp);
    }

    [Fact]
    public async Task Two_reveals_at_once_cannot_both_take_the_last_view()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);
        SignOut();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await new SealedLinkService(ctx, _db.OrgContext, _clock, NullLogger<SealedLinkService>.Instance)
                .RevealAsync(token, input.LinkCheck, null, null);
        }));

        results.Count(r => r.Status == SealedRevealStatus.Revealed).Should().Be(1);
        (await ReadLinkAsync()).ViewCount.Should().Be(1);
    }

    [Fact]
    public async Task A_burst_of_guesses_at_once_still_gets_only_five_and_the_last_view_closes_the_link()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input() with { PassphraseSalt = B64(16), PassphraseCheck = B64(32) };
        var token = await CreateAsync(input);
        SignOut();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var ctx = _db.NewContext();
            return await new SealedLinkService(ctx, _db.OrgContext, _clock, NullLogger<SealedLinkService>.Instance)
                .RevealAsync(token, input.LinkCheck, B64(32), null);
        }));

        results.Count(r => r.Status is SealedRevealStatus.WrongPassphrase or SealedRevealStatus.LockedOut)
            .Should().Be(SealedLinkService.MaxPassphraseAttempts);
        var link = await ReadLinkAsync();
        link.FailedPassphraseAttempts.Should().Be(SealedLinkService.MaxPassphraseAttempts);
        link.ClosedReason.Should().Be(SealedLinkClosedReason.LockedOut);
        link.Ciphertext.Should().BeNull();
    }

    [Fact]
    public async Task A_wrong_passphrase_spends_no_view_and_five_lock_the_link()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input() with { PassphraseSalt = B64(16), PassphraseCheck = B64(32) };
        var token = await CreateAsync(input);
        SignOut();

        (await Service().GetForRecipientAsync(token))!.PassphraseSalt.Should().Be(input.PassphraseSalt);

        var first = await Service().RevealAsync(token, input.LinkCheck, B64(32), "203.0.113.9");
        first.Status.Should().Be(SealedRevealStatus.WrongPassphrase);
        first.AttemptsLeft.Should().Be(SealedLinkService.MaxPassphraseAttempts - 1);
        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status
            .Should().Be(SealedRevealStatus.WrongPassphrase, "no passphrase at all is a wrong one");
        (await ReadLinkAsync()).ViewCount.Should().Be(0);

        (await Service().RevealAsync(token, input.LinkCheck, input.PassphraseCheck, null)).Status
            .Should().Be(SealedRevealStatus.Revealed);
    }

    [Fact]
    public async Task The_fifth_wrong_passphrase_locks_the_link_for_good()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input(views: 2) with { PassphraseSalt = B64(16), PassphraseCheck = B64(32) };
        var token = await CreateAsync(input);
        SignOut();

        SealedRevealResult last = new(SealedRevealStatus.Gone);
        for (var i = 0; i < SealedLinkService.MaxPassphraseAttempts; i++)
        {
            last = await Service().RevealAsync(token, input.LinkCheck, B64(32), null);
        }

        last.Status.Should().Be(SealedRevealStatus.LockedOut);
        (await Service().RevealAsync(token, input.LinkCheck, input.PassphraseCheck, null)).Status
            .Should().Be(SealedRevealStatus.Gone, "the right passphrase is too late once the link has locked");
        var link = await ReadLinkAsync();
        link.ClosedReason.Should().Be(SealedLinkClosedReason.LockedOut);
        link.Ciphertext.Should().BeNull();
        link.Events.Should().HaveCount(SealedLinkService.MaxPassphraseAttempts)
            .And.OnlyContain(e => e.Kind == SealedLinkEventKind.WrongPassphrase);
    }

    [Fact]
    public async Task An_expired_link_refuses_before_the_sweep_has_run()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input(hours: 1);
        var token = await CreateAsync(input);
        SignOut();

        _clock.Advance(TimeSpan.FromHours(1));

        (await Service().GetForRecipientAsync(token)).Should().BeNull();
        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status.Should().Be(SealedRevealStatus.Gone);
    }

    // ---- list and revoke ---------------------------------------------------

    [Fact]
    public async Task People_see_their_own_links_and_an_admin_can_see_everyones()
    {
        await SignInAsync("alex@cronus.example");
        await CreateAsync(Input(label: "Alex's"));
        var sam = await SignInAsync("sam@cronus.example");
        await CreateAsync(Input(label: "Sam's"));

        (await Service().ListAsync()).Select(r => r.Label).Should().Equal("Sam's");
        var act = () => Service().ListAsync(everyone: true);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await SignInAsync("admin@cronus.example", UserRole.Admin);
        (await Service().CanSeeEveryoneAsync()).Should().BeTrue();
        (await Service().ListAsync(everyone: true)).Select(r => r.Label).Should().BeEquivalentTo(["Alex's", "Sam's"]);
        _ = sam;
    }

    [Fact]
    public async Task Another_organisations_links_never_appear()
    {
        await SignInAsync("alex@cronus.example", UserRole.Admin);
        await CreateAsync(Input(label: "CRONUS"));
        await SignInAsync("kim@fabrikam.example", UserRole.Admin, TestDb.OtherOrgId);
        await CreateAsync(Input(label: "Fabrikam"));

        (await Service().ListAsync(everyone: true)).Select(r => r.Label).Should().Equal("Fabrikam");
    }

    [Fact]
    public async Task Only_the_sender_or_an_admin_can_revoke_and_a_revoked_link_stops_working()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);
        var id = (await Service().ListAsync()).Single().Id;

        await SignInAsync("sam@cronus.example");
        var act = () => Service().RevokeAsync(id);
        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Link");

        await SignInAsync("admin@cronus.example", UserRole.Admin);
        await Service().RevokeAsync(id);

        var link = await ReadLinkAsync();
        link.ClosedReason.Should().Be(SealedLinkClosedReason.Revoked);
        link.Ciphertext.Should().BeNull();
        await using (var ctx = _db.NewContext())
        {
            (await ctx.AuditLog.CountAsync(a => a.EntityType == AuditEntityType.SealedLink && a.Action == AuditAction.Deleted))
                .Should().Be(1);
        }
        SignOut();
        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status.Should().Be(SealedRevealStatus.Gone);
    }

    [Fact]
    public async Task The_sender_can_extend_an_open_link_from_now_and_it_is_audited()
    {
        var alex = await SignInAsync("alex@cronus.example");
        var token = await CreateAsync(Input(hours: 24));
        var id = (await Service().ListAsync()).Single().Id;
        _clock.Advance(TimeSpan.FromHours(20));

        await Service().ExtendAsync(id, 72);

        var link = await ReadLinkAsync();
        link.ExpiresAt.Should().Be(Start.UtcDateTime.AddHours(20 + 72), "an extension counts from now, not from creation");
        link.ClosedAt.Should().BeNull();
        link.ViewCount.Should().Be(0);
        link.MaxViews.Should().Be(1, "extending gives more time, not more views");
        link.Ciphertext.Should().NotBeNull();
        await using var ctx = _db.NewContext();
        (await ctx.AuditLog.SingleAsync(a => a.EntityType == AuditEntityType.SealedLink && a.Action == AuditAction.Updated))
            .ChangedByUserId.Should().Be(alex);

        _clock.Advance(TimeSpan.FromHours(30));
        SignOut();
        (await Service().GetForRecipientAsync(token)).Should().NotBeNull("the link would have expired without the extension");
    }

    [Fact]
    public async Task Extending_refuses_a_closed_link_a_shorter_time_and_a_choice_outside_the_list()
    {
        await SignInAsync("alex@cronus.example");
        await CreateAsync(Input(hours: 168));
        var id = (await Service().ListAsync()).Single().Id;

        var shorter = () => Service().ExtendAsync(id, 72);
        var odd = () => Service().ExtendAsync(id, 1000);
        var same = () => Service().ExtendAsync(id, 168);
        (await shorter.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ExpiresInHours");
        (await odd.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ExpiresInHours");
        (await same.Should().ThrowAsync<PlanValidationException>()).Which.Errors
            .Should().ContainKey("ExpiresInHours", "a week from now on a fresh week-long link adds nothing");
        (await ReadLinkAsync()).ExpiresAt.Should().Be(Start.UtcDateTime.AddHours(168), "a refused extension changes nothing");

        _clock.Advance(TimeSpan.FromHours(169));
        var expired = () => Service().ExtendAsync(id, 168);
        (await expired.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Link");

        await CreateAsync(Input(hours: 24));
        var second = (await Service().ListAsync()).First().Id;
        await Service().RevokeAsync(second);
        var revoked = () => Service().ExtendAsync(second, 168);
        (await revoked.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Link");
    }

    [Fact]
    public async Task Only_the_sender_or_an_admin_can_extend()
    {
        await SignInAsync("alex@cronus.example");
        await CreateAsync(Input(hours: 1));
        var id = (await Service().ListAsync()).Single().Id;

        await SignInAsync("kim@cronus.example");
        var colleague = () => Service().ExtendAsync(id, 24);
        (await colleague.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Link");

        await SignInAsync("kim@fabrikam.example", UserRole.Admin, TestDb.OtherOrgId);
        var otherOrgAdmin = () => Service().ExtendAsync(id, 24);
        (await otherOrgAdmin.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Link");

        await SignInAsync("admin@cronus.example", UserRole.Admin);
        await Service().ExtendAsync(id, 24);
        (await ReadLinkAsync()).ExpiresAt.Should().Be(Start.UtcDateTime.AddHours(24));
    }

    [Fact]
    public void New_links_default_to_three_days()
    {
        SealedLinkService.DefaultExpiryHours.Should().Be(72);
        SealedLinkService.ExpiryChoices.Should().Contain(SealedLinkService.DefaultExpiryHours);
    }

    [Fact]
    public async Task Revoking_a_link_that_has_already_closed_keeps_why_it_closed()
    {
        await SignInAsync("alex@cronus.example");
        var input = Input();
        var token = await CreateAsync(input);
        var id = (await Service().ListAsync()).Single().Id;
        SignOut();
        (await Service().RevealAsync(token, input.LinkCheck, null, null)).Status.Should().Be(SealedRevealStatus.Revealed);

        await SignInAsync("alex@cronus.example");
        await Service().RevokeAsync(id);

        (await ReadLinkAsync()).ClosedReason.Should().Be(SealedLinkClosedReason.UsedUp);
        await using var ctx = _db.NewContext();
        (await ctx.AuditLog.CountAsync(a => a.EntityType == AuditEntityType.SealedLink && a.Action == AuditAction.Deleted))
            .Should().Be(0, "nothing was revoked");
    }

    // ---- sweep -------------------------------------------------------------

    [Fact]
    public async Task The_sweep_closes_expired_links_and_deletes_closed_ones_after_retention()
    {
        await SignInAsync("alex@cronus.example");
        await CreateAsync(Input(hours: 1));
        SignOut();
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        _clock.Advance(TimeSpan.FromHours(2));
        await SweepAsync();
        var link = await ReadLinkAsync();
        link.ClosedReason.Should().Be(SealedLinkClosedReason.Expired);
        link.Ciphertext.Should().BeNull();

        _clock.Advance(SealedLinkService.RetentionAfterClose);
        await SweepAsync();
        await using var ctx = _db.NewContext();
        (await ctx.SealedLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_sender_hears_once_about_each_open_and_once_about_a_lockout()
    {
        var alex = await SignInAsync("alex@cronus.example");
        var input = Input(views: 2, label: "Entra secret for CRONUS") with { PassphraseSalt = B64(16), PassphraseCheck = B64(32) };
        var token = await CreateAsync(input);
        SignOut();
        (await Service().RevealAsync(token, input.LinkCheck, input.PassphraseCheck, "203.0.113.4")).Status
            .Should().Be(SealedRevealStatus.Revealed);
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;

        (await SweepAsync()).Should().Be(1);
        (await SweepAsync()).Should().Be(0, "an open is told about once");

        SignOut();
        for (var i = 0; i < SealedLinkService.MaxPassphraseAttempts; i++)
        {
            await Service().RevealAsync(token, input.LinkCheck, B64(32), null);
        }
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        (await SweepAsync()).Should().Be(1);
        (await SweepAsync()).Should().Be(0);

        _email.Sent.Select(s => s.Subject).Should().Equal(
            "Sealed link opened: Entra secret for CRONUS",
            "Sealed link locked: Entra secret for CRONUS");
        _email.Sent[0].Html.Should().Contain("203.0.113.4");
        await using var ctx = _db.NewContext();
        var told = await ctx.UserNotifications.AsNoTracking()
            .Where(n => n.UserId == alex && n.Category == NotificationCategory.SealedLinks)
            .OrderBy(n => n.Id).Select(n => n.Detail).ToListAsync();
        told.Should().HaveCount(2);
        told[1].Should().Contain("shown 1 time", "the secret was seen before the link locked, so the sender must not be told nobody saw it");
    }

    // ---- helpers -----------------------------------------------------------

    private SealedLinkService Service() =>
        new(_db.NewContext(), _db.OrgContext, _clock, NullLogger<SealedLinkService>.Instance);

    private async Task<string> CreateAsync(SealedLinkInput input)
    {
        await using var ctx = _db.NewContext();
        return await new SealedLinkService(ctx, _db.OrgContext, _clock, NullLogger<SealedLinkService>.Instance)
            .CreateAsync(input);
    }

    private async Task<int> SweepAsync()
    {
        await using var ctx = _db.NewContext();
        var notifications = new NotificationService(
            ctx, _db.NewContextFactory(),
            new NotificationPreferenceService(ctx, _db.OrgContext, _clock, NullLogger<NotificationPreferenceService>.Instance),
            _email, new EmailRenderer(_services, NullLoggerFactory.Instance),
            new PublicOrigin("https://workbench.cronus.example"), _db.OrgContext, _clock, NullLogger<NotificationService>.Instance);
        return await new SealedLinkSweepService(ctx, notifications, _clock, NullLogger<SealedLinkSweepService>.Instance)
            .SweepAsync();
    }

    private async Task<SealedLink> ReadLinkAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.SealedLinks.IgnoreQueryFilters().AsNoTracking().Include(l => l.Events).SingleAsync();
    }

    private static SealedLinkInput Input(int hours = 24, int views = 1, string? label = "Entra secret for CRONUS") =>
        new(B64(64), B64(12), B64(32), null, null, hours, views, label);

    private static string B64(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    private async Task<int> SignInAsync(string email, UserRole role = UserRole.User, int orgId = TestDb.DefaultOrgId)
    {
        _db.OrgContext.CurrentOrganizationId = orgId;
        await using var ctx = _db.NewContext();
        var user = await ctx.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new User
            {
                OrganizationId = orgId, Email = email, DisplayName = email.Split('@')[0], PasswordHash = "x",
                Role = role, Status = UserStatus.Active, CreatedAt = Start.UtcDateTime,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = user.Id;
        return user.Id;
    }

    /// <summary>The recipient: no organisation, no user.</summary>
    private void SignOut()
    {
        _db.OrgContext.CurrentOrganizationId = null;
        _db.OrgContext.CurrentUserId = null;
    }
}
