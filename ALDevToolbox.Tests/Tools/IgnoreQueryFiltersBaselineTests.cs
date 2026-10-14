using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

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
/// <para>These tests replace the list with two mechanical checks: a per-file
/// baseline count, so adding, moving or removing a bypass shows up as a
/// deliberate diff on this file, and a comment check, so every call site names
/// the sanctioned category it belongs to (see the fence paragraph in CLAUDE.md
/// and <c>.design/auth-and-audit.md</c>).</para>
/// </summary>
public sealed class IgnoreQueryFiltersBaselineTests
{
    private const string Marker = "IgnoreQueryFilters(";

    /// <summary>
    /// Call sites per scanned file, relative to the repository root with forward
    /// slashes. Generated from the tree, not aspirational: bump an entry (or add
    /// a file) only together with the justification comment the second test
    /// demands, and only with the maintainer's sign-off.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> Baseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ALDevToolbox/Components/Pages/SignupDetails.razor"] = 1,
        ["ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminAuditDiffPage.razor"] = 2,
        ["ALDevToolbox/Endpoints/AccountAuthEndpoints.cs"] = 2,
        ["ALDevToolbox/Endpoints/AccountEndpoints.cs"] = 2,
        ["ALDevToolbox/Endpoints/AccountMfaEndpoints.cs"] = 2,
        ["ALDevToolbox/Endpoints/AdminUserEndpoints.cs"] = 4,
        ["ALDevToolbox/Endpoints/CookieSessionRevalidation.cs"] = 1,
        ["ALDevToolbox/Endpoints/OAuthEndpoints.cs"] = 1,
        ["ALDevToolbox/Endpoints/StartupTasks.cs"] = 4,
        ["ALDevToolbox/Endpoints/StrongAuthGate.cs"] = 3,
        ["ALDevToolbox/Services/Account/AccountService.cs"] = 5,
        ["ALDevToolbox/Services/Account/AuthService.cs"] = 4,
        ["ALDevToolbox/Services/Account/EmailMfaService.cs"] = 4,
        ["ALDevToolbox/Services/Account/EntraSignInService.cs"] = 13,
        ["ALDevToolbox/Services/Account/InviteService.cs"] = 5,
        ["ALDevToolbox/Services/Account/PasskeyService.cs"] = 10,
        ["ALDevToolbox/Services/Account/PasswordResetService.cs"] = 4,
        ["ALDevToolbox/Services/Account/PendingSignupService.cs"] = 1,
        ["ALDevToolbox/Services/Account/PersonalAccessTokenService.cs"] = 4,
        ["ALDevToolbox/Services/Account/RecoveryCodeService.cs"] = 3,
        ["ALDevToolbox/Services/Account/SiteAdminService.cs"] = 5,
        ["ALDevToolbox/Services/Account/TotpService.cs"] = 8,
        ["ALDevToolbox/Services/Account/UserAdministrationService.cs"] = 14,
        ["ALDevToolbox/Services/Backups/PerTenantBackupService.cs"] = 1,
        ["ALDevToolbox/Services/GitHub/GitHubConnectionService.cs"] = 1,
        ["ALDevToolbox/Services/OAuth/OAuthClaimsTransformer.cs"] = 1,
        ["ALDevToolbox/Services/OAuth/OAuthClientAdminService.cs"] = 5,
        ["ALDevToolbox/Services/ObjectExplorer/Import/PersistedImportJobs.cs"] = 8,
        ["ALDevToolbox/Services/ObjectExplorer/Import/ReleaseAutoImportScheduler.cs"] = 1,
        ["ALDevToolbox/Services/Organizations/OrganizationAdminService.cs"] = 1,
        ["ALDevToolbox/Services/Organizations/OrganizationConfigService.cs"] = 3,
        ["ALDevToolbox/Services/Organizations/PlatformOrganizationFileSeeder.cs"] = 1,
        ["ALDevToolbox/Services/SingleTenant/SingleTenantSeeder.cs"] = 2,
        ["ALDevToolbox/Services/Templates/FolderTreeHydrator.cs"] = 4,
        ["ALDevToolbox/Services/Templates/TemplateImportService.cs"] = 7,
    };

    [Fact]
    public void Call_site_counts_match_the_reviewed_baseline()
    {
        var actual = ScanRepository().ToDictionary(f => f.Path, f => f.Sites.Count, StringComparer.Ordinal);

        var added = actual.Keys.Where(p => !Baseline.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var removed = Baseline.Keys.Where(p => !actual.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var changed = actual.Keys
            .Where(p => Baseline.TryGetValue(p, out var b) && b != actual[p])
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => $"{p}: baseline {Baseline[p]}, found {actual[p]}")
            .ToList();

        var problems = new List<string>();
        if (added.Count > 0)
        {
            problems.Add(
                "New file(s) with IgnoreQueryFilters() calls: " + string.Join(", ", added) +
                ". Crossing the tenant fence in a new file needs the maintainer's confirmation (CLAUDE.md). " +
                "If it is confirmed, give each call site a one-line justification comment naming its sanctioned " +
                "category and the predicate that pins it, then add the file to the baseline in this test.");
        }
        if (removed.Count > 0)
        {
            problems.Add(
                "Baseline names file(s) that no longer call IgnoreQueryFilters(): " + string.Join(", ", removed) +
                ". Good news, but the baseline has to stay honest — drop those entries from this test.");
        }
        foreach (var line in changed)
        {
            var parts = line.Split(": baseline ");
            var path = parts[0];
            problems.Add(actual[path] > Baseline[path]
                ? line + ". A new bypass of the tenant query filter needs the maintainer's confirmation " +
                  "(CLAUDE.md). If it is confirmed, add a one-line justification comment naming its sanctioned " +
                  "category and the predicate that pins it, then bump this file's baseline deliberately."
                : line + ". A bypass was removed — lower this file's baseline so it stays honest.");
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

    private static readonly System.Text.RegularExpressions.Regex CategoryName =
        new(@"\bcategory\s+[1-6]\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private sealed record Site(int Line, bool HasComment, bool NamesCategory);

    private sealed record ScannedFile(string Path, IReadOnlyList<Site> Sites);

    private static List<ScannedFile> ScanRepository()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "ALDevToolbox");
        var results = new List<ScannedFile>();
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(p => !IsExcluded(root, p))
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var sites = ScanFile(File.ReadAllLines(file));
            if (sites.Count == 0) continue;
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            results.Add(new ScannedFile(relative, sites));
        }
        return results;
    }

    /// <summary>
    /// Generated code is out of scope: EF migrations write their own filtered
    /// queries, and build output is not source.
    /// </summary>
    private static bool IsExcluded(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var segments = relative.Split('/');
        return segments.Contains("obj") || segments.Contains("bin") || segments.Contains("Migrations");
    }

    /// <summary>
    /// Strips comments the way a reader does — <c>//</c> to end of line, plus
    /// <c>/* */</c> and Razor's <c>@* *@</c> blocks — then reports each line of
    /// remaining code that calls <c>IgnoreQueryFilters(</c>, and whether a
    /// comment sits on that line or on one of the three lines above it, and
    /// whether that comment names a fence category.
    /// </summary>
    private static List<Site> ScanFile(IReadOnlyList<string> raw)
    {
        var code = new string[raw.Count];
        var commented = new bool[raw.Count];
        var commentText = new string[raw.Count];
        var inBlock = false;

        for (var i = 0; i < raw.Count; i++)
        {
            var line = raw[i];
            var buffer = new System.Text.StringBuilder(line.Length);
            var comment = new System.Text.StringBuilder();
            var hasComment = false;
            var j = 0;
            while (j < line.Length)
            {
                if (inBlock)
                {
                    hasComment = true;
                    if (Starts(line, j, "*/") || Starts(line, j, "*@")) { inBlock = false; j += 2; }
                    else { comment.Append(line[j]); j++; }
                }
                else if (Starts(line, j, "//"))
                {
                    hasComment = true;
                    comment.Append(line, j, line.Length - j);
                    break;
                }
                else if (Starts(line, j, "/*") || Starts(line, j, "@*"))
                {
                    hasComment = true;
                    inBlock = true;
                    j += 2;
                }
                else
                {
                    buffer.Append(line[j]);
                    j++;
                }
            }
            code[i] = buffer.ToString();
            commented[i] = hasComment;
            commentText[i] = comment.ToString();
        }

        var sites = new List<Site>();
        for (var i = 0; i < code.Length; i++)
        {
            var occurrences = CountOccurrences(code[i], Marker);
            if (occurrences == 0) continue;

            // A comment on the call's own line counts; so does a pure comment
            // line within the three lines above it (the shape CLAUDE.md asks for).
            // The text checked for a category is that line's comment plus the
            // whole run of pure comment lines the nearest one belongs to, so a
            // justification that wraps, or sits under a longer remark, still
            // reads as one comment.
            var documented = commented[i];
            var text = new System.Text.StringBuilder(commented[i] ? commentText[i] : string.Empty);
            var nearest = -1;
            for (var k = i - 1; k >= Math.Max(0, i - 3); k--)
            {
                if (commented[k] && code[k].Trim().Length == 0) { nearest = k; break; }
            }
            if (nearest >= 0)
            {
                documented = true;
                for (var k = nearest; k >= 0 && commented[k] && code[k].Trim().Length == 0; k--)
                {
                    text.Insert(0, commentText[k] + " ");
                }
            }
            var namesCategory = documented && CategoryName.IsMatch(text.ToString());
            for (var n = 0; n < occurrences; n++)
            {
                sites.Add(new Site(i + 1, documented, namesCategory));
            }
        }
        return sites;
    }

    private static bool Starts(string line, int index, string token) =>
        index + token.Length <= line.Length && string.CompareOrdinal(line, index, token, 0, token.Length) == 0;

    private static int CountOccurrences(string line, string token)
    {
        var count = 0;
        var index = line.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = line.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string RepoRoot()
    {
        var dir = ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
