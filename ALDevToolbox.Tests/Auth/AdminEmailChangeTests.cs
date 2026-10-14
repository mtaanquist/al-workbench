using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// An admin-made email change has to reach the person it moves: the current
/// address gets a single-use link that stops the change, the change can't be
/// confirmed until a day after it was asked for, and the swap itself counts as
/// a credential change, so it ends the person's sessions and tokens the way a
/// password reset does. The emails themselves are pinned in
/// <c>AdminEmailChangeNoticeTests</c>.
/// </summary>
public sealed class AdminEmailChangeTests : IDisposable
{
    private const int OrgId = TestDb.DefaultOrgId;
    private static readonly TimeSpan Delay = UserAdministrationService.EmailChangeConfirmDelay;

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Request_returns_what_the_notice_to_the_current_address_needs()
    {
        var (adminId, memberId) = await SeedAsync();
        var requestedAt = Now;

        var request = await RequestAsync(memberId, adminId, "Member@Elsewhere.example ");

        request.CurrentEmail.Should().Be("member@cronus.example");
        request.NewEmail.Should().Be("member@elsewhere.example");
        request.DisplayName.Should().Be("Alex Hansen");
        request.RequestedByName.Should().Be("Sam Berg");
        request.RequestedByEmail.Should().Be("admin@cronus.example");
        request.CancelToken.Should().NotBe(request.ConfirmToken);
        request.ConfirmableFrom.Should().Be(requestedAt + Delay);
        request.ExpiresAt.Should().Be(requestedAt + UserAdministrationService.EmailChangeTokenLifetime);

        await using var read = _db.NewContext();
        var cancel = await read.PasswordResetTokens
            .SingleAsync(t => t.UserId == memberId && t.Purpose == TokenPurpose.EmailChangeCancel);
        cancel.TokenHash.Should().NotBe(request.CancelToken, "only the hash is stored");
        cancel.ExpiresAt.Should().Be(requestedAt + UserAdministrationService.EmailChangeTokenLifetime);
    }

    [Fact]
    public async Task Confirming_is_refused_until_a_day_after_the_request_without_using_up_the_link()
    {
        var (adminId, memberId) = await SeedAsync();
        var requestedAt = Now;
        var request = await RequestAsync(memberId, adminId);

        _clock.Advance(Delay - TimeSpan.FromMinutes(1));
        var early = await ConfirmAsync(request.ConfirmToken);
        early.Outcome.Should().Be(EmailChangeConfirmOutcome.NotYet);
        early.ConfirmableFrom.Should().Be(requestedAt + Delay);
        (await ReadUserAsync(memberId)).Email.Should().Be("member@cronus.example");

        _clock.Advance(TimeSpan.FromMinutes(1));
        (await ConfirmAsync(request.ConfirmToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Confirmed);
        (await ReadUserAsync(memberId)).Email.Should().Be("member@elsewhere.example");
    }

    [Fact]
    public async Task Confirming_stops_working_when_the_link_expires()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        _clock.Advance(UserAdministrationService.EmailChangeTokenLifetime);

        (await ConfirmAsync(request.ConfirmToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Invalid);
        (await ReadUserAsync(memberId)).Email.Should().Be("member@cronus.example");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(30)]
    [InlineData(71)]
    public async Task Cancel_link_works_for_as_long_as_the_change_is_pending(int hoursLater)
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        _clock.Advance(TimeSpan.FromHours(hoursLater));

        (await CancelAsync(request.CancelToken)).Should().Be(memberId);
        (await ConfirmAsync(request.ConfirmToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Invalid);

        var user = await ReadUserAsync(memberId);
        user.Email.Should().Be("member@cronus.example");
        user.PendingEmail.Should().BeNull();
        user.PendingEmailAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_link_is_used_up_by_one_cancel()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);

        (await CancelAsync(request.CancelToken)).Should().Be(memberId);

        await using (var read = _db.NewContext())
        {
            (await read.PasswordResetTokens
                    .SingleAsync(t => t.UserId == memberId && t.Purpose == TokenPurpose.EmailChangeCancel))
                .ConsumedAt.Should().Be(Now);
        }
        (await CancelAsync(request.CancelToken)).Should().BeNull();
    }

    [Fact]
    public async Task An_old_cancel_link_does_not_stop_a_later_change()
    {
        var (adminId, memberId) = await SeedAsync();
        var first = await RequestAsync(memberId, adminId, "first@elsewhere.example");
        (await CancelAsync(first.CancelToken)).Should().Be(memberId);

        await RequestAsync(memberId, adminId, "again@elsewhere.example");

        (await CancelAsync(first.CancelToken)).Should().BeNull();
        (await ReadUserAsync(memberId)).PendingEmail.Should().Be("again@elsewhere.example");
    }

    [Fact]
    public async Task Cancel_and_confirm_tokens_do_not_stand_in_for_each_other()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        _clock.Advance(Delay);

        (await ConfirmAsync(request.CancelToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Invalid);
        (await CancelAsync(request.ConfirmToken)).Should().BeNull();

        (await ReadUserAsync(memberId)).PendingEmail.Should().Be("member@elsewhere.example");
    }

    [Fact]
    public async Task A_new_request_burns_the_previous_cancel_link()
    {
        var (adminId, memberId) = await SeedAsync();
        var first = await RequestAsync(memberId, adminId, "first@elsewhere.example");
        await RequestAsync(memberId, adminId, "second@elsewhere.example");

        (await CancelAsync(first.CancelToken)).Should().BeNull();
        (await ReadUserAsync(memberId)).PendingEmail.Should().Be("second@elsewhere.example");
    }

    [Fact]
    public async Task Confirming_the_change_ends_sessions_and_revokes_tokens()
    {
        var (adminId, memberId) = await SeedAsync();
        await using (var seed = _db.NewContext())
        {
            seed.PersonalAccessTokens.Add(new PersonalAccessToken
            {
                UserId = memberId, OrganizationId = OrgId, Name = "laptop",
                TokenHash = "hash-1", TokenPrefix = "aldt_pat_1", CreatedAt = Now,
            });
            await seed.SaveChangesAsync();
        }
        var oauth = _db.OpenIddictTokens;
        var oauthToken = await oauth.CreateAsync(new OpenIddictTokenDescriptor
        {
            Subject = memberId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Type = OpenIddictConstants.TokenTypes.Bearer,
            Status = OpenIddictConstants.Statuses.Valid,
            CreationDate = _clock.GetUtcNow(),
        });
        var request = await RequestAsync(memberId, adminId);
        _clock.Advance(Delay + TimeSpan.FromMinutes(5));

        var change = await ConfirmAsync(request.ConfirmToken);

        change.Outcome.Should().Be(EmailChangeConfirmOutcome.Confirmed);
        change.PreviousEmail.Should().Be("member@cronus.example");
        change.User!.Email.Should().Be("member@elsewhere.example");

        var tokenId = await oauth.GetIdAsync(oauthToken);
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == memberId);
        user.CredentialsChangedAt.Should().Be(Now, "sessions that started before the swap are dropped on their next check");
        (await read.PersonalAccessTokens.IgnoreQueryFilters().SingleAsync(p => p.UserId == memberId)).RevokedAt
            .Should().Be(Now);
        (await read.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
                .SingleAsync(t => t.Id == tokenId)).Status
            .Should().Be(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task A_refused_confirmation_changes_no_credentials()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        await CancelAsync(request.CancelToken);
        _clock.Advance(Delay);

        (await ConfirmAsync(request.ConfirmToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Invalid);

        (await ReadUserAsync(memberId)).CredentialsChangedAt.Should().BeNull();
    }

    [Fact]
    public async Task Confirming_twice_swaps_once()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        _clock.Advance(Delay);

        (await ConfirmAsync(request.ConfirmToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Confirmed);
        (await ConfirmAsync(request.ConfirmToken)).Outcome.Should().Be(EmailChangeConfirmOutcome.Invalid);
    }

    [Fact]
    public async Task Two_confirmations_at_once_swap_once()
    {
        var (adminId, memberId) = await SeedAsync();
        for (var round = 0; round < 5; round++)
        {
            var request = await RequestAsync(memberId, adminId, $"round{round}@elsewhere.example");
            _clock.Advance(Delay);

            var results = await Task.WhenAll(
                Task.Run(() => ConfirmAsync(request.ConfirmToken)),
                Task.Run(() => ConfirmAsync(request.ConfirmToken)));

            results.Count(r => r.Outcome == EmailChangeConfirmOutcome.Confirmed).Should().Be(1, $"round {round}");
            (await ReadUserAsync(memberId)).Email.Should().Be($"round{round}@elsewhere.example");
        }
    }

    [Fact]
    public async Task A_cancel_and_a_confirmation_at_once_never_both_win()
    {
        var (adminId, memberId) = await SeedAsync();
        for (var round = 0; round < 6; round++)
        {
            var before = (await ReadUserAsync(memberId)).Email;
            var request = await RequestAsync(memberId, adminId, $"race{round}@elsewhere.example");
            _clock.Advance(Delay);

            var confirm = Task.Run(() => ConfirmAsync(request.ConfirmToken));
            var cancel = Task.Run(() => CancelAsync(request.CancelToken));
            await Task.WhenAll(confirm, cancel);

            var confirmed = confirm.Result.Outcome == EmailChangeConfirmOutcome.Confirmed;
            var stopped = cancel.Result is not null;
            (confirmed ^ stopped).Should().BeTrue($"exactly one of them takes effect (round {round})");
            var user = await ReadUserAsync(memberId);
            user.Email.Should().Be(confirmed ? $"race{round}@elsewhere.example" : before);
            user.PendingEmail.Should().BeNull();
        }
    }

    [Fact]
    public async Task Withdraw_drops_the_pending_change_and_both_links()
    {
        var (adminId, memberId) = await SeedAsync();
        await RequestAsync(memberId, adminId);

        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).WithdrawEmailChangeAsync(memberId, OrgId);
        }

        (await ReadUserAsync(memberId)).PendingEmail.Should().BeNull();
        await using var read = _db.NewContext();
        (await read.PasswordResetTokens.Where(t => t.UserId == memberId).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(t => t.ConsumedAt != null);
    }

    [Fact]
    public async Task Withdraw_refuses_a_user_from_another_organisation()
    {
        var (_, memberId) = await SeedAsync();

        await using var ctx = _db.NewContext();
        Func<Task> act = () => NewUserAdmin(ctx).WithdrawEmailChangeAsync(memberId, TestDb.OtherOrgId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("UserId");
    }

    private UserAdministrationService NewUserAdmin(Data.AppDbContext ctx) =>
        _db.NewUserAdministrationService(ctx, _clock);

    private async Task<EmailChangeRequest> RequestAsync(int memberId, int adminId, string newEmail = "member@elsewhere.example")
    {
        await using var ctx = _db.NewContext();
        return await NewUserAdmin(ctx).RequestEmailChangeAsync(memberId, newEmail, OrgId, adminId);
    }

    private async Task<EmailChangeConfirmResult> ConfirmAsync(string token)
    {
        await using var ctx = _db.NewContext();
        return await NewUserAdmin(ctx).ConfirmEmailChangeAsync(token);
    }

    private async Task<int?> CancelAsync(string token)
    {
        await using var ctx = _db.NewContext();
        return await NewUserAdmin(ctx).CancelEmailChangeAsync(token);
    }

    private async Task<(int AdminId, int MemberId)> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        var admin = NewUser("admin@cronus.example", "Sam Berg", UserRole.Admin);
        var member = NewUser("member@cronus.example", "Alex Hansen", UserRole.User);
        ctx.Users.AddRange(admin, member);
        await ctx.SaveChangesAsync();
        _db.OrgContext.CurrentOrganizationId = OrgId;
        _db.OrgContext.CurrentUserId = admin.Id;
        _db.OrgContext.IsSiteAdmin = false;
        return (admin.Id, member.Id);
    }

    private User NewUser(string email, string name, UserRole role) => new()
    {
        OrganizationId = OrgId,
        Email = email,
        DisplayName = name,
        PasswordHash = "placeholder",
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = Now,
    };

    private async Task<User> ReadUserAsync(int id)
    {
        await using var read = _db.NewContext();
        return await read.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }
}
