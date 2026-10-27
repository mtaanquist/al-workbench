using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Keeps the tenant-isolation fence reviewable.
///
/// <para><c>IgnoreQueryFilters()</c> is the only way past the EF query filter
/// that scopes every read to the acting organisation, so CLAUDE.md requires a
/// maintainer's confirmation before a new one is added. That rule used to lean
/// on a hand-written list of "the sanctioned call sites" in the docs; by the
/// time issue #705 was filed the list named eight while the tree held closer to
/// two hundred, so a reviewer had no way to tell a reviewed bypass from a new
/// one.</para>
///
/// <para>These tests replace the list with two mechanical checks: a baseline
/// count per file and member, so adding, moving or removing a bypass shows up
/// as a deliberate diff on this file, and a comment check, so every call site
/// names the sanctioned category it belongs to (see the fence paragraph in
/// CLAUDE.md and <c>.design/auth-and-audit.md</c>). The count used to be per
/// file only, which let a bypass move to another method, or one be deleted and
/// a copied one added in the same file, without touching the baseline (#1426).
/// Raw SQL, the other way past the filter, has its own baseline in
/// <see cref="RawSqlBaselineTests"/>.</para>
/// </summary>
public sealed class IgnoreQueryFiltersBaselineTests
{
    private const string Marker = "IgnoreQueryFilters(";

    /// <summary>
    /// Call sites per scanned file (relative to the repository root, forward
    /// slashes) and per enclosing member: the method, property or minimal-API
    /// route the call sits in (<see cref="FenceSourceScan.EnclosingMember"/>).
    /// Generated from the tree, not aspirational: change an entry only together
    /// with the justification comment the other tests demand, and only with the
    /// maintainer's sign-off. A member renamed with its bypass untouched is the
    /// one change that moves an entry here without a review question behind it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Dictionary<string, int>> Baseline =
        new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
    {
        ["ALDevToolbox/Components/Pages/SignupDetails.razor"] = new()
        {
            ["OnInitializedAsync"] = 1,
        },
        ["ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminAuditDiffPage.razor"] = new()
        {
            ["OnInitializedAsync"] = 2,
        },
        ["ALDevToolbox/Endpoints/AccountAuthEndpoints.cs"] = new()
        {
            ["MapPost /auth/forgot-password"] = 1,
            ["MapPost /auth/login/magic"] = 1,
        },
        ["ALDevToolbox/Endpoints/AccountEndpoints.cs"] = new()
        {
            ["LoadUserAndVerifyPasswordAsync"] = 1,
            ["NotifyAdminsOfPendingSignupAsync"] = 1,
        },
        ["ALDevToolbox/Endpoints/AccountMfaEndpoints.cs"] = new()
        {
            ["MapPost /auth/account/2fa/email/begin"] = 1,
            ["MapPost /auth/login/challenge/email/issue"] = 1,
        },
        ["ALDevToolbox/Endpoints/AdminUserEndpoints.cs"] = new()
        {
            ["MapPost /admin/users/create"] = 1,
            ["MapPost /admin/users/invite"] = 1,
        },
        ["ALDevToolbox/Endpoints/CookieSessionRevalidation.cs"] = new()
        {
            ["ValidateAsync"] = 1,
        },
        ["ALDevToolbox/Endpoints/OAuthEndpoints.cs"] = new()
        {
            ["UpsertConsentAsync"] = 1,
        },
        ["ALDevToolbox/Endpoints/StartupTasks.cs"] = new()
        {
            ["EnsureBootstrapAdminAsync"] = 1,
            ["RunAsync"] = 3,
        },
        ["ALDevToolbox/Endpoints/StrongAuthGate.cs"] = new()
        {
            ["UseStrongAuthGate"] = 3,
        },
        ["ALDevToolbox/Services/Account/AccountService.cs"] = new()
        {
            ["CommitCredentialChangeAsync"] = 1,
            ["CompleteVerifiedSignupAsync"] = 2,
            ["ResolveOrganizationByEmailDomainAsync"] = 1,
            ["SignupAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/AuthService.cs"] = new()
        {
            ["CompleteMfaAsync"] = 1,
            ["IsMicrosoftOnlyOrganizationAsync"] = 1,
            ["TryLoginAsync"] = 1,
            ["TryReserveMfaAttemptAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/EmailMfaService.cs"] = new()
        {
            ["DisableAsync"] = 1,
            ["EnableAsync"] = 1,
            ["IssueChallengeAsync"] = 1,
            ["VerifyAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/EntraSignInService.cs"] = new()
        {
            ["CompleteCoreAsync"] = 5,
            ["FindLinkAsync"] = 1,
            ["GetClientSecretAsync"] = 1,
            ["GetLoginSurfaceAsync"] = 1,
            ["IsSignInAvailableAsync"] = 1,
            ["LinkAsync"] = 1,
            ["ResolveChallengeAsync"] = 2,
            ["ResolveChallengeForOrganizationAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/InviteService.cs"] = new()
        {
            ["AcceptAsync"] = 1,
            ["CreateAsync"] = 2,
            ["FindValidByTokenAsync"] = 1,
            ["RevokeAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/PasskeyService.cs"] = new()
        {
            ["BeginLoginCoreAsync"] = 2,
            ["BeginRegistrationAsync"] = 2,
            ["CompleteLoginAsync"] = 2,
            ["CompleteRegistrationAsync"] = 1,
            ["DeleteAsync"] = 1,
            ["ListForUserAsync"] = 1,
            ["RenameAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/PasswordResetService.cs"] = new()
        {
            ["ConsumeMagicLoginTokenAsync"] = 1,
            ["ConsumePasswordResetTokenAsync"] = 1,
            ["CreateMagicLoginTokenAsync"] = 1,
            ["CreatePasswordResetTokenAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/PendingSignupService.cs"] = new()
        {
            ["StartAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/PersonalAccessTokenService.cs"] = new()
        {
            ["ListAllAsync"] = 1,
            ["RevokeAsync"] = 1,
            ["ValidateAsync"] = 2,
        },
        ["ALDevToolbox/Services/Account/RecoveryCodeService.cs"] = new()
        {
            ["ConsumeAsync"] = 1,
            ["RegenerateAsync"] = 1,
            ["RemainingAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/SiteAdminService.cs"] = new()
        {
            ["DemoteAsync"] = 1,
            ["GetMembershipsAsync"] = 1,
            ["LoadUserAsync"] = 1,
            ["SearchAuditAsync"] = 1,
            ["SearchUsersAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/TotpService.cs"] = new()
        {
            ["BeginEnrollmentAsync"] = 2,
            ["ConfirmEnrollmentAsync"] = 2,
            ["DisableAsync"] = 3,
            ["VerifyAsync"] = 1,
        },
        ["ALDevToolbox/Services/Account/UserAdministrationService.cs"] = new()
        {
            ["ApproveSignupAsync"] = 1,
            ["BurnEmailChangeTokensAsync"] = 1,
            ["ConfirmEmailChangeAsync"] = 1,
            ["CountActiveAdminsAsync"] = 1,
            ["EmailChangeTokens"] = 1,
            ["LoadSignupRequestAsync"] = 1,
            ["LoadUserAsync"] = 1,
            ["LookupDisplayNameAsync"] = 1,
            ["RejectSignupAsync"] = 1,
            ["RequestEmailChangeAsync"] = 1,
            ["ResetMfaAsync"] = 4,
        },
        ["ALDevToolbox/Services/Backups/PerTenantBackupService.cs"] = new()
        {
            ["ListAsync"] = 1,
        },
        ["ALDevToolbox/Services/GitHub/GitHubConnectionService.cs"] = new()
        {
            ["ConnectAsync"] = 1,
        },
        ["ALDevToolbox/Services/OAuth/OAuthClaimsTransformer.cs"] = new()
        {
            ["TransformAsync"] = 1,
        },
        ["ALDevToolbox/Services/OAuth/OAuthClientAdminService.cs"] = new()
        {
            ["DeleteClientAsync"] = 1,
            ["ListAllAsync"] = 1,
            ["ListForOrganizationAsync"] = 1,
            ["ListForUserAsync"] = 1,
            ["RevokeConsentAsync"] = 1,
        },
        ["ALDevToolbox/Services/ObjectExplorer/Import/PersistedImportJobs.cs"] = new()
        {
            ["MarkCompletedAsync"] = 1,
            ["MarkFailedAsync"] = 1,
            ["MarkRunningAsync"] = 1,
            ["ReconcileOnStartupAsync"] = 2,
            ["SnapshotAsync"] = 3,
        },
        ["ALDevToolbox/Services/ObjectExplorer/Import/ReleaseAutoImportScheduler.cs"] = new()
        {
            ["ResolveTargetsAsync"] = 1,
        },
        ["ALDevToolbox/Services/Organizations/OrganizationAdminService.cs"] = new()
        {
            ["AddEmailDomainAsync"] = 1,
        },
        ["ALDevToolbox/Services/Organizations/OrganizationConfigService.cs"] = new()
        {
            ["GetForAsync"] = 3,
        },
        ["ALDevToolbox/Services/Organizations/PlatformOrganizationFileSeeder.cs"] = new()
        {
            ["EnsureForOrganizationAsync"] = 1,
        },
        ["ALDevToolbox/Services/Sealed/SealedLinkService.cs"] = new()
        {
            ["GetForRecipientAsync"] = 1,
            ["RecordWrongPassphraseAsync"] = 2,
            ["RevealAsync"] = 3,
        },
        ["ALDevToolbox/Services/SingleTenant/SingleTenantSeeder.cs"] = new()
        {
            ["SeedAsync"] = 2,
        },
        ["ALDevToolbox/Services/Templates/FolderTreeHydrator.cs"] = new()
        {
            ["HydrateExtensionFolderTreeAsync"] = 2,
            ["HydrateModuleExtensionFolderTreeAsync"] = 2,
        },
        ["ALDevToolbox/Services/Templates/TemplateImportService.cs"] = new()
        {
            ["EnsureApplicationVersionAsync"] = 2,
            ["EnsureModuleAsync"] = 1,
            ["ImportTemplateAsync"] = 2,
            ["ListSystemTemplatesAsync"] = 2,
        },
    };

    [Fact]
    public void Call_site_counts_match_the_reviewed_baseline()
    {
        var actual = ScanRepository().ToDictionary(
            f => f.Path,
            f => f.Sites.GroupBy(s => s.Member).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var path in actual.Keys.Union(Baseline.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var found = actual.GetValueOrDefault(path) ?? new Dictionary<string, int>();
            var reviewed = Baseline.GetValueOrDefault(path) ?? new Dictionary<string, int>();
            foreach (var member in found.Keys.Union(reviewed.Keys).OrderBy(m => m, StringComparer.Ordinal))
            {
                var now = found.GetValueOrDefault(member);
                var before = reviewed.GetValueOrDefault(member);
                if (now == before) continue;
                var where = $"{path} in {member}: baseline {before}, found {now}";
                problems.Add(now > before
                    ? where + ". A new or moved bypass of the tenant query filter needs the maintainer's " +
                      "confirmation (CLAUDE.md). If it is confirmed, give it a one-line justification comment " +
                      "naming its sanctioned category and the predicate that pins it, then update this entry " +
                      "in the baseline deliberately."
                    : where + ". A bypass was removed or moved - update this entry so the baseline stays honest.");
            }
        }

        problems.Should().BeEmpty(
            "the IgnoreQueryFilters() baseline is the tenant-isolation fence's review surface");
    }

    [Fact]
    public void Every_call_site_carries_a_justification_comment()
    {
        var undocumented = ScanRepository()
            .SelectMany(f => f.Sites.Where(s => !s.HasComment).Select(s => $"{f.Path}:{s.Line}"))
            .ToList();

        undocumented.Should().BeEmpty(
            "every IgnoreQueryFilters() call site must carry a one-line comment (on the same line, or within " +
            "the three lines above it) naming its sanctioned category and the predicate that pins the read — " +
            "see the fence paragraph in CLAUDE.md");
    }

    /// <summary>
    /// Any comment used to satisfy the check above, so a site sitting under
    /// an unrelated remark ("Use IgnoreQueryFilters so the lookup hits the
    /// unique index") passed without saying why crossing the fence is safe
    /// (#1336). The comment now has to name the category by number, which is
    /// what lets a reviewer check the site against that category's invariant.
    /// </summary>
    [Fact]
    public void Every_call_site_comment_names_its_fence_category()
    {
        var unnamed = ScanRepository()
            .SelectMany(f => f.Sites.Where(s => s.HasComment && !s.NamesCategory).Select(s => $"{f.Path}:{s.Line}"))
            .ToList();

        unnamed.Should().BeEmpty(
            "every IgnoreQueryFilters() justification comment must name its sanctioned category " +
            "(\"Fence category N (...)\", N from 1 to 6, see the fence paragraph in CLAUDE.md) and the predicate " +
            "that pins the read");
    }

    /// <summary>
    /// The member key is what makes a moved bypass visible, so it has to name
    /// real members. Pins the scanner on shapes the tree holds today: a tuple
    /// return type, a minimal-API handler lambda, and an expression-bodied
    /// property.
    /// </summary>
    [Fact]
    public void Call_sites_are_keyed_by_the_member_they_sit_in()
    {
        var keys = ScanRepository()
            .SelectMany(f => f.Sites.Select(s => $"{f.Path} in {s.Member}"))
            .ToHashSet(StringComparer.Ordinal);

        keys.Should().Contain("ALDevToolbox/Services/Account/AccountService.cs in SignupAsync");
        keys.Should().Contain("ALDevToolbox/Endpoints/AccountAuthEndpoints.cs in MapPost /auth/forgot-password");
        keys.Should().Contain("ALDevToolbox/Services/Account/UserAdministrationService.cs in EmailChangeTokens");
        keys.Should().NotContain(k => k.EndsWith(" in (file scope)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The case the per-file count missed (#1426): one bypass deleted and a
    /// commented copy added in another method of the same file. The file's
    /// total stays the same; the per-member counts do not.
    /// </summary>
    [Fact]
    public void A_bypass_moved_to_another_member_changes_the_baseline_key()
    {
        string[] before =
        [
            "public sealed class Service",
            "{",
            "    public async Task<User?> FindAsync(int userId, CancellationToken ct)",
            "    {",
            "        // Fence category 4 (explicitly scoped user-id lookup): pinned to u.Id == userId.",
            "        return await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);",
            "    }",
            "",
            "    private async Task<List<User>> ListAsync(string email, CancellationToken ct)",
            "    {",
            "        return await _db.Users.Where(u => u.Email == email).ToListAsync(ct);",
            "    }",
            "}",
        ];
        var after = before.ToArray();
        after[4] = "";
        after[5] = "        return await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);";
        after[10] = "        // Fence category 4 (explicitly scoped user-id lookup): pinned to u.Id == userId.\n" +
                    "        return await _db.Users.IgnoreQueryFilters().Where(u => u.Email == email).ToListAsync(ct);";
        after = string.Join("\n", after).Split('\n');

        static Dictionary<string, int> Keys(string[] source) =>
            ScanSource(source).GroupBy(s => s.Member).ToDictionary(g => g.Key, g => g.Count());

        Keys(before).Should().Equal(new Dictionary<string, int> { ["FindAsync"] = 1 });
        Keys(after).Should().Equal(new Dictionary<string, int> { ["ListAsync"] = 1 });
    }

    private static readonly Regex CategoryName = new(@"\bcategory\s+[1-6]\b", RegexOptions.IgnoreCase);

    private sealed record Site(int Line, string Member, bool HasComment, bool NamesCategory);

    private sealed record ScannedFile(string Path, IReadOnlyList<Site> Sites);

    /// <summary>
    /// Every line of code (comments stripped, so prose about the fence never
    /// counts) that calls <c>IgnoreQueryFilters(</c>, with the member it sits
    /// in and whether a justifying comment names a fence category.
    /// </summary>
    private static List<ScannedFile> ScanRepository()
    {
        var results = new List<ScannedFile>();
        foreach (var (relative, full) in FenceSourceScan.AppSourceFiles())
        {
            var sites = ScanSource(File.ReadAllLines(full));
            if (sites.Count > 0) results.Add(new ScannedFile(relative, sites));
        }
        return results;
    }

    private static List<Site> ScanSource(IReadOnlyList<string> raw)
    {
        var lines = FenceSourceScan.Read(raw);
        var sites = new List<Site>();
        for (var i = 0; i < lines.Length; i++)
        {
            var occurrences = FenceSourceScan.CountOccurrences(lines[i].Code, Marker);
            if (occurrences == 0) continue;
            var comment = FenceSourceScan.JustifyingComment(lines, i);
            var member = FenceSourceScan.EnclosingMember(lines, i);
            var namesCategory = comment is not null && CategoryName.IsMatch(comment);
            for (var n = 0; n < occurrences; n++)
            {
                sites.Add(new Site(i + 1, member, comment is not null, namesCategory));
            }
        }
        return sites;
    }
}
