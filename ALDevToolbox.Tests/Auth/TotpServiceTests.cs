using System.Buffers.Binary;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// TOTP enrolment, confirmation, verification, and disable. Uses an in-process
/// IDataProtectionProvider so secret encryption round-trips inside the test.
/// </summary>
public sealed class TotpServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private RecoveryCodeService NewRecovery(AppDbContext ctx) => new(ctx, _clock);
    private TotpService NewTotp(AppDbContext ctx) =>
        new(ctx, _db.DataProtectionProvider, _clock, NewRecovery(ctx));

    private async Task<int> SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = 7000,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "totp@example.com",
            PasswordHash = "x",
            DisplayName = "TOTP User",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await ctx.SaveChangesAsync();
        return 7000;
    }

    [Fact]
    public async Task Begin_enrollment_persists_encrypted_secret_but_does_not_enable_totp()
    {
        var userId = await SeedUserAsync();
        await using var ctx = _db.NewContext();
        var totp = NewTotp(ctx);
        var enrollment = await totp.BeginEnrollmentAsync(userId);

        enrollment.SecretBase32.Should().NotBeNullOrEmpty();
        enrollment.OtpAuthUri.Should().StartWith("otpauth://totp/");
        AssertScannableQrPng(enrollment.QrPng);

        await using var read = _db.NewContext();
        var row = await read.UserTotpSecrets.IgnoreQueryFilters().SingleAsync(s => s.UserId == userId);
        row.ConfirmedAt.Should().BeNull();
        row.SecretEncrypted.Should().NotBe(enrollment.SecretBase32, "secret is encrypted at rest");
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.TotpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_with_valid_code_enables_totp_and_returns_recovery_codes()
    {
        var userId = await SeedUserAsync();
        TotpEnrollment enrollment;
        await using (var ctx = _db.NewContext())
        {
            enrollment = await NewTotp(ctx).BeginEnrollmentAsync(userId);
        }

        var validCode = new Totp(Base32Encoding.ToBytes(enrollment.SecretBase32))
            .ComputeTotp(_clock.GetUtcNow().UtcDateTime);

        IReadOnlyList<string> codes;
        await using (var ctx = _db.NewContext())
        {
            codes = await NewTotp(ctx).ConfirmEnrollmentAsync(userId, validCode);
        }

        codes.Should().HaveCount(10);
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.TotpEnabled.Should().BeTrue();
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == userId))
            .Should().Be(10);
    }

    [Fact]
    public async Task Confirm_with_wrong_code_does_not_enable_totp()
    {
        var userId = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            await NewTotp(ctx).BeginEnrollmentAsync(userId);
        }
        await using var ctx2 = _db.NewContext();
        var act = () => NewTotp(ctx2).ConfirmEnrollmentAsync(userId, "000000");
        await act.Should().ThrowAsync<PlanValidationException>();
        await using var read = _db.NewContext();
        var user = await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.TotpEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_code_works_once_and_the_next_step_works_again()
    {
        // #1336: a code someone else saw or relayed must not work a second
        // time inside its ~90-second window.
        var userId = await SeedUserAsync();
        var secret = await EnrolAsync(userId);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var code = CodeAt(secret, _clock.GetUtcNow());

        await using (var ctx = _db.NewContext())
        {
            (await NewTotp(ctx).VerifyAsync(userId, code)).Should().BeTrue();
        }
        await using (var ctx = _db.NewContext())
        {
            (await NewTotp(ctx).VerifyAsync(userId, code)).Should().BeFalse("the same code was already used");
        }

        _clock.Advance(TimeSpan.FromSeconds(30));
        await using (var ctx = _db.NewContext())
        {
            (await NewTotp(ctx).VerifyAsync(userId, CodeAt(secret, _clock.GetUtcNow()))).Should().BeTrue();
        }
    }

    [Fact]
    public async Task An_older_code_still_in_the_window_is_refused_after_a_newer_one_was_used()
    {
        var userId = await SeedUserAsync();
        var secret = await EnrolAsync(userId);
        _clock.Advance(TimeSpan.FromSeconds(60));
        var older = CodeAt(secret, _clock.GetUtcNow().AddSeconds(-30));
        var current = CodeAt(secret, _clock.GetUtcNow());

        await using (var ctx = _db.NewContext())
        {
            (await NewTotp(ctx).VerifyAsync(userId, current)).Should().BeTrue();
        }
        await using (var ctx = _db.NewContext())
        {
            (await NewTotp(ctx).VerifyAsync(userId, older)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task The_code_that_confirmed_setup_cannot_be_used_to_sign_in()
    {
        var userId = await SeedUserAsync();
        var secret = await EnrolAsync(userId);

        await using var ctx = _db.NewContext();
        (await NewTotp(ctx).VerifyAsync(userId, CodeAt(secret, _clock.GetUtcNow()))).Should().BeFalse();
    }

    private async Task<string> EnrolAsync(int userId)
    {
        await using var ctx = _db.NewContext();
        var enrollment = await NewTotp(ctx).BeginEnrollmentAsync(userId);
        await NewTotp(ctx).ConfirmEnrollmentAsync(userId, CodeAt(enrollment.SecretBase32, _clock.GetUtcNow()));
        return enrollment.SecretBase32;
    }

    private static string CodeAt(string secret, DateTimeOffset at) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.UtcDateTime);

    [Fact]
    public async Task Disable_clears_secret_recovery_codes_and_flag()
    {
        var userId = await SeedUserAsync();
        TotpEnrollment enrollment;
        await using (var ctx = _db.NewContext())
        {
            enrollment = await NewTotp(ctx).BeginEnrollmentAsync(userId);
            var code = new Totp(Base32Encoding.ToBytes(enrollment.SecretBase32))
                .ComputeTotp(_clock.GetUtcNow().UtcDateTime);
            await NewTotp(ctx).ConfirmEnrollmentAsync(userId, code);
        }
        await using (var ctx = _db.NewContext())
        {
            await NewTotp(ctx).DisableAsync(userId);
        }
        await using var read = _db.NewContext();
        (await read.UserTotpSecrets.IgnoreQueryFilters().CountAsync(s => s.UserId == userId)).Should().Be(0);
        (await read.UserRecoveryCodes.IgnoreQueryFilters().CountAsync(c => c.UserId == userId)).Should().Be(0);
        (await read.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).TotpEnabled.Should().BeFalse();
    }

    /// <summary>
    /// Checks the enrolment PNG is a real image sized the way TotpService asks for
    /// it. The dimensions carry the two settings a QR reader actually cares about:
    /// the 6px-per-module scale, and the 4-module quiet zone the spec requires
    /// (a symbol rendered with no quiet zone still decodes in some readers and
    /// fails in others, which is the kind of break a length assertion misses).
    /// </summary>
    private static void AssertScannableQrPng(byte[] png)
    {
        png.Should().StartWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, "output is a PNG");

        // IHDR width/height are big-endian uint32s at offsets 16 and 20.
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));

        const int scale = 6, quietZoneModules = 4;
        width.Should().Be(height, "a QR symbol is square");
        (width % scale).Should().Be(0u, "each module renders as {0} pixels", scale);

        // Smallest possible symbol is version 1 at 21 modules, plus the quiet
        // zone on both sides. Anything under that lost the border.
        var minimum = (uint)((21 + quietZoneModules * 2) * scale);
        width.Should().BeGreaterThanOrEqualTo(minimum,
            "the symbol must carry a {0}-module quiet zone on each side", quietZoneModules);
    }
}
