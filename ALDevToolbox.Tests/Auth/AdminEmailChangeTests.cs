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
/// address gets a single-use link that stops the change, and the swap itself
/// counts as a credential change, so it ends the person's sessions and tokens
/// the way a password reset does. The emails themselves are pinned in
/// <c>AdminEmailChangeNoticeTests</c>.
/// </summary>
public sealed class AdminEmailChangeTests : IDisposable
{
    private const int OrgId = TestDb.DefaultOrgId;

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Request_returns_what_the_notice_to_the_current_address_needs()
    {
        var (adminId, memberId) = await SeedAsync();

        var request = await RequestAsync(memberId, adminId, "Member@Elsewhere.example ");

        request.CurrentEmail.Should().Be("member@cronus.example");
        request.NewEmail.Should().Be("member@elsewhere.example");
        request.DisplayName.Should().Be("Alex Hansen");
        request.RequestedByName.Should().Be("Sam Berg");
        request.RequestedByEmail.Should().Be("admin@cronus.example");
        request.CancelToken.Should().NotBe(request.ConfirmToken);

        await using var read = _db.NewContext();
        var cancel = await read.PasswordResetTokens
            .SingleAsync(t => t.UserId == memberId && t.Purpose == TokenPurpose.EmailChangeCancel);
        cancel.TokenHash.Should().NotBe(request.CancelToken, "only the hash is stored");
        cancel.ExpiresAt.Should().Be(Now + UserAdministrationService.EmailChangeTokenLifetime);
    }

    [Fact]
    public async Task Cancel_link_clears_the_pending_change_so_it_cannot_be_confirmed()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);

        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).CancelEmailChangeAsync(request.CancelToken)).Should().BeTrue();
        }
        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).ConfirmEmailChangeAsync(request.ConfirmToken)).Should().BeNull();
        }

        var user = await ReadUserAsync(memberId);
        user.Email.Should().Be("member@cronus.example");
        user.PendingEmail.Should().BeNull();
        user.PendingEmailAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_link_works_once()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).CancelEmailChangeAsync(request.CancelToken)).Should().BeTrue();
        }

        // A second change asked for after the first was stopped is not
        // stopped by the first change's link.
        var second = await RequestAsync(memberId, adminId, "again@elsewhere.example");
        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).CancelEmailChangeAsync(request.CancelToken)).Should().BeFalse();
        }

        (await ReadUserAsync(memberId)).PendingEmail.Should().Be("again@elsewhere.example");
        second.CancelToken.Should().NotBe(request.CancelToken);
    }

    [Fact]
    public async Task Cancel_link_expires_with_the_change()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);
        _clock.Advance(UserAdministrationService.EmailChangeTokenLifetime + TimeSpan.FromMinutes(1));

        await using var ctx = _db.NewContext();
        (await NewUserAdmin(ctx).CancelEmailChangeAsync(request.CancelToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_and_confirm_tokens_do_not_stand_in_for_each_other()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);

        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).ConfirmEmailChangeAsync(request.CancelToken)).Should().BeNull();
            (await NewUserAdmin(ctx).CancelEmailChangeAsync(request.ConfirmToken)).Should().BeFalse();
        }

        (await ReadUserAsync(memberId)).PendingEmail.Should().Be("member@elsewhere.example");
    }

    [Fact]
    public async Task A_new_request_burns_the_previous_cancel_link()
    {
        var (adminId, memberId) = await SeedAsync();
        var first = await RequestAsync(memberId, adminId, "first@elsewhere.example");
        await RequestAsync(memberId, adminId, "second@elsewhere.example");

        await using var ctx = _db.NewContext();
        (await NewUserAdmin(ctx).CancelEmailChangeAsync(first.CancelToken)).Should().BeFalse();
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
        _clock.Advance(TimeSpan.FromMinutes(5));

        ConfirmedEmailChange? change;
        await using (var ctx = _db.NewContext())
        {
            change = await NewUserAdmin(ctx).ConfirmEmailChangeAsync(request.ConfirmToken);
        }

        change.Should().NotBeNull();
        change!.PreviousEmail.Should().Be("member@cronus.example");
        change.User.Email.Should().Be("member@elsewhere.example");

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
        await using (var ctx = _db.NewContext())
        {
            await NewUserAdmin(ctx).CancelEmailChangeAsync(request.CancelToken);
        }

        await using (var ctx = _db.NewContext())
        {
            (await NewUserAdmin(ctx).ConfirmEmailChangeAsync(request.ConfirmToken)).Should().BeNull();
        }

        (await ReadUserAsync(memberId)).CredentialsChangedAt.Should().BeNull();
    }

    [Fact]
    public async Task Withdraw_drops_the_pending_change_and_both_links()
    {
        var (adminId, memberId) = await SeedAsync();
        var request = await RequestAsync(memberId, adminId);

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
