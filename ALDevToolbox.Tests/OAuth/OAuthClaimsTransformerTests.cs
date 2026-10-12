using System.Security.Claims;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// <see cref="OAuthClaimsTransformer"/> is the only thing binding an OAuth
/// access token to a tenant: it turns a token's <c>sub</c> + <c>org</c> pair
/// into the org / user / role claims every downstream EF query filter trusts.
/// Its refusals are silent by design (the principal comes back unchanged), so
/// a dropped or inverted check looks exactly like a working one at the call
/// site — hence this class pins each refusal directly. See issue #667.
/// </summary>
public sealed class OAuthClaimsTransformerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<User> SeedUserAsync(
        int organizationId = TestDb.DefaultOrgId,
        UserStatus status = UserStatus.Active,
        UserRole role = UserRole.User,
        bool isSiteAdmin = false,
        string email = "alice@cronus.test",
        DateTime? credentialsChangedAt = null)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            DisplayName = "Alice",
            PasswordHash = "ignored",
            Role = role,
            Status = status,
            IsSiteAdmin = isSiteAdmin,
            CreatedAt = DateTime.UtcNow,
            CredentialsChangedAt = credentialsChangedAt,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    /// <summary>A bare OpenIddict-shaped principal: whatever claims the caller passes, nothing else.</summary>
    private static ClaimsPrincipal TokenPrincipal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "AuthenticationTypes.Federation"));

    private async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        await using var ctx = _db.NewContext();
        return await new OAuthClaimsTransformer(ctx).TransformAsync(principal);
    }

    [Fact]
    public async Task Valid_subject_and_org_stamp_the_bridge_claims()
    {
        var user = await SeedUserAsync(role: UserRole.Editor);

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().Be(user.Id.ToString());
        result.FindFirstValue(HttpOrganizationContext.OrganizationIdClaim).Should().Be(TestDb.DefaultOrgId.ToString());
        result.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(user.Id.ToString());
        result.FindFirstValue(ClaimTypes.Email).Should().Be("alice@cronus.test");
        result.FindFirstValue(ClaimTypes.Name).Should().Be("Alice");
        result.FindFirstValue(ClaimTypes.Role).Should().Be("Editor");
        result.FindFirstValue(HttpOrganizationContext.SiteAdminClaim).Should().BeNull();
    }

    [Fact]
    public async Task Org_claim_naming_a_different_tenant_leaves_the_principal_unchanged()
    {
        // The load-bearing check: the user lives in the Default org, the token
        // says Other. If this ever stops refusing, every downstream query filter
        // faithfully serves the wrong tenant's data.
        var user = await SeedUserAsync(organizationId: TestDb.DefaultOrgId);

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.OtherOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
        result.FindFirstValue(HttpOrganizationContext.OrganizationIdClaim).Should().BeNull();
        result.FindFirstValue(ClaimTypes.Role).Should().BeNull();
    }

    [Fact]
    public async Task Disabled_user_is_not_stamped()
    {
        var user = await SeedUserAsync(status: UserStatus.Disabled);

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
    }

    /// <summary>
    /// Access tokens are checked locally, not against the token table, so
    /// revoking them on a password change would leave them usable until they
    /// expire. The credential-change stamp is what cuts them off.
    /// </summary>
    [Fact]
    public async Task Token_issued_before_the_password_changed_is_not_stamped()
    {
        var changedAt = new DateTime(2026, 6, 1, 9, 0, 0, 500, DateTimeKind.Utc);
        var user = await SeedUserAsync(credentialsChangedAt: changedAt);

        var principal = TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString()));
        principal.SetCreationDate(new DateTimeOffset(changedAt).AddMinutes(-10));

        var result = await TransformAsync(principal);

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
    }

    [Fact]
    public async Task Token_with_no_creation_date_is_not_stamped_once_the_password_has_changed()
    {
        var user = await SeedUserAsync(credentialsChangedAt: new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc));

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]     // same second, stored without fractions
    [InlineData(60)]
    public async Task Token_issued_after_the_password_changed_is_stamped(int secondsAfter)
    {
        var changedAt = new DateTime(2026, 6, 1, 9, 0, 0, 500, DateTimeKind.Utc);
        var user = await SeedUserAsync(credentialsChangedAt: changedAt);

        var principal = TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString()));
        principal.SetCreationDate(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero).AddSeconds(secondsAfter));

        var result = await TransformAsync(principal);

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().Be(user.Id.ToString());
    }

    /// <summary>
    /// The cut-off reads the creation date off the validated principal, so it
    /// only works while the token format carries that date across. This sends
    /// the principal through the same Data Protection formatters the server
    /// writes access tokens with and the validation side reads them back with,
    /// so a format change that drops the date fails here rather than silently
    /// refusing (or admitting) every token.
    /// </summary>
    [Theory]
    [InlineData(-600, false)]  // issued ten minutes before the change
    [InlineData(0.1, true)]    // same second, after the change
    [InlineData(60, true)]     // a minute after
    public async Task Access_token_round_tripped_through_the_token_format_is_judged_by_its_creation_date(
        double secondsFromChange, bool expectStamped)
    {
        var changedAt = new DateTime(2026, 6, 1, 9, 0, 0, 500, DateTimeKind.Utc);
        var user = await SeedUserAsync(credentialsChangedAt: changedAt);

        var issued = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, user.Id.ToString()),
                new Claim(OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString()),
            ],
            authenticationType: "OpenIddict.Server.AspNetCore"));
        issued.SetTokenType(OpenIddictConstants.TokenTypeIdentifiers.AccessToken);
        issued.SetCreationDate(new DateTimeOffset(changedAt).AddSeconds(secondsFromChange));
        issued.SetExpirationDate(new DateTimeOffset(changedAt).AddHours(1));

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            new OpenIddict.Server.DataProtection.OpenIddictServerDataProtectionFormatter().WriteToken(writer, issued);
        }
        buffer.Position = 0;
        ClaimsPrincipal validated;
        using (var reader = new BinaryReader(buffer))
        {
            validated = new OpenIddict.Validation.DataProtection.OpenIddictValidationDataProtectionFormatter().ReadToken(reader)!;
        }
        validated.GetCreationDate().Should().NotBeNull("the token format must carry the creation date");

        var result = await TransformAsync(validated);

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim)
            .Should().Be(expectStamped ? user.Id.ToString() : null);
    }

    [Fact]
    public async Task Unknown_subject_is_not_stamped()
    {
        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, "999999"),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
    }

    [Fact]
    public async Task Principal_already_carrying_the_user_claim_is_left_alone()
    {
        // Cookie and PAT principals arrive here already stamped; re-running must
        // not re-derive (or duplicate) their claims from the token fields.
        var user = await SeedUserAsync(role: UserRole.Admin);
        var principal = TokenPrincipal(
            (HttpOrganizationContext.UserIdClaim, "4242"),
            (HttpOrganizationContext.OrganizationIdClaim, TestDb.OtherOrgId.ToString()),
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString()));

        var result = await TransformAsync(principal);

        result.FindAll(HttpOrganizationContext.UserIdClaim).Should().ContainSingle()
            .Which.Value.Should().Be("4242");
        result.FindFirstValue(HttpOrganizationContext.OrganizationIdClaim).Should().Be(TestDb.OtherOrgId.ToString());
        result.FindFirstValue(ClaimTypes.Role).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("not-a-number", "1")]
    [InlineData("1", null)]
    [InlineData("1", "not-a-number")]
    public async Task Missing_or_non_numeric_subject_or_org_is_a_no_op(string? subject, string? org)
    {
        await SeedUserAsync();
        var claims = new List<(string, string)>();
        if (subject is not null) claims.Add((OpenIddictConstants.Claims.Subject, subject));
        if (org is not null) claims.Add((OAuthClaimsTransformer.OrgClaim, org));

        var result = await TransformAsync(TokenPrincipal(claims.ToArray()));

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
    }

    [Fact]
    public async Task Unauthenticated_principal_is_a_no_op()
    {
        var user = await SeedUserAsync();
        // No authentication type => IsAuthenticated is false.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            new Claim(OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString()),
        ]));

        var result = await TransformAsync(principal);

        result.FindFirstValue(HttpOrganizationContext.UserIdClaim).Should().BeNull();
    }

    [Fact]
    public async Task Site_admin_gets_both_the_claim_and_the_role()
    {
        var user = await SeedUserAsync(role: UserRole.Admin, isSiteAdmin: true);

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.SiteAdminClaim).Should().Be("true");
        result.IsInRole(HttpOrganizationContext.SiteAdminRole).Should().BeTrue();
        result.IsInRole("Admin").Should().BeTrue();
    }

    [Fact]
    public async Task System_org_member_gets_the_system_org_claim()
    {
        // The migration stamps the Default org as the singleton system org.
        var user = await SeedUserAsync(organizationId: TestDb.DefaultOrgId);

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.DefaultOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.SystemOrgClaim).Should().Be("true");
    }

    [Fact]
    public async Task Regular_org_member_does_not_get_the_system_org_claim()
    {
        var user = await SeedUserAsync(organizationId: TestDb.OtherOrgId, email: "bob@cronus.test");

        var result = await TransformAsync(TokenPrincipal(
            (OpenIddictConstants.Claims.Subject, user.Id.ToString()),
            (OAuthClaimsTransformer.OrgClaim, TestDb.OtherOrgId.ToString())));

        result.FindFirstValue(HttpOrganizationContext.OrganizationIdClaim).Should().Be(TestDb.OtherOrgId.ToString());
        result.FindFirstValue(HttpOrganizationContext.SystemOrgClaim).Should().BeNull();
    }
}
