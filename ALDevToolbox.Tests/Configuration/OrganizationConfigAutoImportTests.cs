using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Configuration;

/// <summary>
/// The per-org automatic-release-import settings on
/// <see cref="OrganizationConfigService"/>: the country value accepts a
/// comma-separated list, is stored in a canonical trimmed/lower-cased/de-duped
/// form, and enabling without at least one valid code is rejected with a
/// field-keyed error. <see cref="OrganizationConfigService.ParseAutoImportCountries"/>
/// is the shared parser the scheduler relies on, so its shape is pinned here too.
/// </summary>
public sealed class OrganizationConfigAutoImportTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Save_canonicalises_a_comma_separated_list()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.SaveAutoImportAsync(enabled: true, country: " W1, dk ,NL,dk, de ");

        await using var read = _db.NewContext();
        var row = await read.OrganizationSettings.AsNoTracking().SingleAsync();
        row.AutoImportReleasesEnabled.Should().BeTrue();
        row.AutoImportCountry.Should().Be("w1,dk,nl,de",
            "codes are trimmed, lower-cased, and de-duplicated in first-seen order");
    }

    [Fact]
    public async Task Save_still_accepts_a_single_code()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.SaveAutoImportAsync(enabled: true, country: "dk");

        await using var read = _db.NewContext();
        (await read.OrganizationSettings.AsNoTracking().SingleAsync())
            .AutoImportCountry.Should().Be("dk");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(", ,")]
    public async Task Enabling_without_any_code_is_rejected(string? country)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var act = () => svc.SaveAutoImportAsync(enabled: true, country: country);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("AutoImportCountry");
    }

    [Theory]
    [InlineData("denmark")]
    [InlineData("dk,denmark")]
    [InlineData("d k")]
    public async Task Malformed_codes_are_rejected(string country)
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        var act = () => svc.SaveAutoImportAsync(enabled: true, country: country);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("AutoImportCountry");
    }

    [Fact]
    public async Task Disabling_keeps_the_stored_list_for_a_later_re_enable()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);
        await svc.SaveAutoImportAsync(enabled: true, country: "w1,dk");

        await svc.SaveAutoImportAsync(enabled: false, country: "w1,dk");

        await using var read = _db.NewContext();
        var row = await read.OrganizationSettings.AsNoTracking().SingleAsync();
        row.AutoImportReleasesEnabled.Should().BeFalse();
        row.AutoImportCountry.Should().Be("w1,dk");
    }

    [Fact]
    public async Task Previews_are_stored_only_while_the_sweep_itself_is_enabled()
    {
        await using var ctx = _db.NewContext();
        var svc = _db.NewOrganizationConfigService(ctx);

        await svc.SaveAutoImportAsync(enabled: true, country: "dk", includePreviews: true);
        await using (var read = _db.NewContext())
        {
            (await read.OrganizationSettings.AsNoTracking().SingleAsync()).AutoImportPreviewsEnabled.Should().BeTrue();
        }

        // Turning the sweep off takes previews with it: they ride on the daily
        // pass, so a stored "true" would mean nothing and mislead the form.
        await svc.SaveAutoImportAsync(enabled: false, country: "dk", includePreviews: true);
        await using (var read = _db.NewContext())
        {
            (await read.OrganizationSettings.AsNoTracking().SingleAsync()).AutoImportPreviewsEnabled.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("dk", new[] { "dk" })]
    [InlineData("w1,dk,nl", new[] { "w1", "dk", "nl" })]
    [InlineData(" W1 , dk ,, dk ", new[] { "w1", "dk" })]
    public void ParseAutoImportCountries_splits_trims_lowers_and_dedupes(string? value, string[] expected)
    {
        OrganizationConfigService.ParseAutoImportCountries(value).Should().Equal(expected);
    }
}
