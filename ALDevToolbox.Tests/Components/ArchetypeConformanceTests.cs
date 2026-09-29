using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Keeps the page frame in one place.
///
/// <para>Every page used to hand-write the same <c>page-head</c> from the design
/// handoff, and the structure lived in sixty copies that nothing compared - which
/// is how the Upgrades page shipped a filter bar with its Search button wrapped
/// under the box (#805). The archetype components in
/// <c>Components/Shared/Archetypes/</c> own that markup now; see "Page archetypes"
/// in PROJECT.md.</para>
///
/// <para>Two fences, and the milestone that built them is finished, so both lists
/// are exceptions now rather than a backlog.</para>
///
/// <para><b>Nobody copies the head.</b> A file that writes its own <c>page-head</c>
/// fails unless it is on <see cref="NotYetMigrated"/>, and a file on that list that
/// no longer writes one fails too, so the list stays honest.</para>
///
/// <para><b>Every page has a frame.</b> A routable page that draws anything must
/// compose an archetype - directly, or through a shared component that does (the
/// tabbed frames, the section headers) - or be on <see cref="OwnFrame"/> with the
/// reason it is not. That is what stops a new page being started from a copy of
/// an old one's markup. It is "at least one", not "exactly one": a tabbed page
/// inside <c>SettingsPage</c> legitimately composes the frame and, in its body,
/// <c>EmptyState</c> and <c>LoadingBlock</c>.</para>
/// </summary>
public sealed class ArchetypeConformanceTests
{
    /// <summary>
    /// A <c>class</c> attribute naming the <c>page-head</c> block itself (alone, with
    /// other classes, or with a modifier) - not its <c>page-head__*</c> parts, which
    /// the detail archetype legitimately reuses outside a page head.
    /// </summary>
    private static readonly Regex HandWrittenPageHead = new(
        """class="(?:[^"]*\s)?page-head(?:\s|"|--)""", RegexOptions.Compiled);

    /// <summary>
    /// Components that hand-write their page head, relative to the repository root.
    /// Each is deliberate and says why. Do not add to it without the same.
    /// </summary>
    private static readonly IReadOnlySet<string> NotYetMigrated = new HashSet<string>(StringComparer.Ordinal)
    {
        // The head swaps the title for an inline rename box and its Save / Cancel, which
        // PageHead's Title string cannot hold. One consumer, so it does not get a slot.
        "ALDevToolbox/Components/Pages/Teams/TeamDetail.razor",
    };

    [Fact]
    public void No_new_component_hand_writes_a_page_head()
    {
        var offenders = HandWriting().Where(p => !NotYetMigrated.Contains(p)).ToList();

        offenders.Should().BeEmpty(
            "a page composes <PageHead> from Components/Shared/Archetypes/ rather than copying the " +
            "page-head markup - see \"Page archetypes\" in PROJECT.md. Do not add to the baseline");
    }

    [Fact]
    public void Migrated_components_are_removed_from_the_baseline()
    {
        var handWriting = HandWriting().ToHashSet(StringComparer.Ordinal);
        var stale = NotYetMigrated.Where(p => !handWriting.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer hand-write a page-head (migrated, moved or deleted) - good news, " +
            "but the baseline has to stay honest, so drop their entries from this test");
    }

    /// <summary>The frames a page composes. Primitives (EmptyState, LoadingBlock, FilterBar) are not frames.</summary>
    private static readonly string[] Frames =
    [
        "PageHead", "ListPage", "DetailPage", "EditPage", "GeneratorPage", "LauncherPage",
        "DocsPage", "ErrorPage",
        // The sign-in family's frame. It predates the archetypes folder and is the
        // handoff's auth card, one component for all eight pages.
        "AuthCard",
    ];

    /// <summary>
    /// Routable pages that draw their own frame, and why. Pages that draw nothing at
    /// all (the redirects) are not listed: there is no frame to have.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OwnFrame = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ALDevToolbox/Components/Pages/Diff.razor"] =
            "power tool: a one-of-a-kind layout inside the handoff's .pw frame, whose pw__head is that frame's own head",
        ["ALDevToolbox/Components/Pages/ObjectExplorer/OeCompareFile.razor"] =
            "power tool: the .cmp compare layout, one consumer",
        ["ALDevToolbox/Components/Pages/RecipeDetail.razor"] =
            "its head carries the description and tag links between the title and the actions, which DetailPage's head has no place for; the scripted move dropped them once (#839)",
        ["ALDevToolbox/Components/Pages/Teams/TeamDetail.razor"] =
            "its head swaps the title for an inline rename box; also on the hand-written head list above",
    };

    [Fact]
    public void Every_routable_page_that_draws_anything_composes_a_frame()
    {
        var root = RepoRoot();
        var components = Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'),
                p => Markup(File.ReadAllText(p)),
                StringComparer.Ordinal);

        // A shared component that composes a frame carries it to the pages that use
        // it (SettingsPage, TabbedPage, the section headers), however deep.
        var carriers = new HashSet<string>(Frames, StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (path, markup) in components)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!carriers.Contains(name) && !IsRoutable(markup) && Composes(markup, carriers))
                {
                    grew = carriers.Add(name);
                }
            }
        }

        var pages = components
            .Where(c => c.Key.StartsWith("ALDevToolbox/Components/Pages/", StringComparison.Ordinal) && IsRoutable(c.Value))
            .ToList();
        pages.Should().HaveCountGreaterThan(100, "the scan should be finding the app's pages");

        var frameless = pages
            .Where(c => DrawsSomething(c.Value) && !Composes(c.Value, carriers))
            .Select(c => c.Key)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        frameless.Except(OwnFrame.Keys).Should().BeEmpty(
            "a page composes one of the frames in Components/Shared/Archetypes/ rather than starting from " +
            "a copy of another page's markup - see \"Page archetypes\" in PROJECT.md and the rule in CLAUDE.md");
        OwnFrame.Keys.Except(frameless).Should().BeEmpty(
            "these pages compose a frame now (or are gone), so their exception has to go too");
    }

    /// <summary>
    /// The pages that still pass their trail as a <c>Crumbs</c> fragment, each because a
    /// list cannot say it. A <c>Trail</c> attribute is evaluated on every render of the
    /// frame, a fragment only when it is drawn - so a step that reads the loaded record
    /// has to stay a fragment, or the page throws while it is still loading.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FragmentCrumbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ALDevToolbox/Components/Pages/Admin/AuditDiffPage.razor"] = "the last step mixes text and a value",
        ["ALDevToolbox/Components/Pages/SiteAdmin/SiteAdminAuditDiffPage.razor"] = "the last step mixes text and a value",
        ["ALDevToolbox/Components/Pages/ObjectExplorer/OeModuleDetail.razor"] = "the release step is only there when the page came from a release",
        ["ALDevToolbox/Components/Pages/ObjectExplorer/OeObjectDetail.razor"] = "the release step is only there when the page came from a release",
        ["ALDevToolbox/Components/Pages/ObjectExplorer/OeReleaseDetail.razor"] = "names the loaded release",
        ["ALDevToolbox/Components/Pages/Environments/EnvironmentDetail.razor"] = "names the loaded environment and its solution",
        ["ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor"] = "names the loaded pipeline and its solution",
        ["ALDevToolbox/Components/Pages/Pipelines/ReleasePipelineDetail.razor"] = "names the loaded release pipeline",
        ["ALDevToolbox/Components/Pages/Projects/ProjectDetail.razor"] = "names the loaded solution",
        ["ALDevToolbox/Components/Pages/Upgrades/UpgradeDetail.razor"] = "names the loaded upgrade",
    };

    [Fact]
    public void A_crumb_trail_is_a_list_unless_the_page_has_a_reason()
    {
        var root = RepoRoot();
        var fragments = Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(p => Markup(File.ReadAllText(p)).Contains("<Crumbs>", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        fragments.Should().BeEquivalentTo(FragmentCrumbs.Keys,
            "a trail of plain links is written as Trail='@([new(\"Admin\", \"/admin\"), new(\"Modules\")])' so the chevrons "
            + "and the unlinked last step are drawn one way; keep the Crumbs fragment, and list the page here with its reason, "
            + "only when a step depends on data that is still loading or is conditional");
    }

    [Fact]
    public void No_component_hand_writes_an_alert()
    {
        var root = RepoRoot();
        var alert = new Regex(@"class=""(?:[^""]*\s)?alert(?:\s|--|"")", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(Path.Combine("Shared", "Alert.razor"), StringComparison.Ordinal))
            .Where(p => alert.IsMatch(Markup(File.ReadAllText(p))))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "an alert is <Alert Tone=\"AlertTone.Danger\">...</Alert>: the tone picks the icon and the role, which "
            + "pages used to pick by hand and picked differently (ten errors carried the warning triangle)");
    }

    [Fact]
    public void No_component_hand_writes_a_status_pill()
    {
        var root = RepoRoot();
        var pill = new Regex(@"class=""status-pill[\s""]", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(Path.Combine("Shared", "StatusPill.razor"), StringComparison.Ordinal))
            .Where(p => pill.IsMatch(Markup(File.ReadAllText(p))))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a status pill is <StatusPill Tone=\"success\">...</StatusPill>, which always carries the dot the live and running tones pulse");
    }

    [Fact]
    public void No_component_hand_writes_a_field_error()
    {
        var root = RepoRoot();
        var line = new Regex(@"class=""(?:[^""]*\s)?field-error(?:\s|"")", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(Path.Combine("Shared", "FieldError.razor"), StringComparison.Ordinal))
            .Where(p => line.IsMatch(Markup(File.ReadAllText(p))))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "an inline error is <FieldError Field=\"name\" Errors=\"_errors\" /> for a keyed validation message or "
            + "<FieldError Message=\"...\" /> for anything else: the glyph and the alert role are decided once there, "
            + "where pages used to pick two different glyphs and forget the role");
    }

    private static bool IsRoutable(string markup) => Regex.IsMatch(markup, @"^@page\s", RegexOptions.Multiline);

    private static bool Composes(string markup, IEnumerable<string> names) =>
        names.Any(n => Regex.IsMatch(markup, $@"<{n}[\s/>]"));

    /// <summary>An HTML element of its own - what a redirect page does not have.</summary>
    private static bool DrawsSomething(string markup) => Regex.IsMatch(markup, @"<[a-z][a-z0-9]*[\s>]");

    /// <summary>The file above its <c>@code</c> block, comments out.</summary>
    private static string Markup(string razor)
    {
        var code = razor.IndexOf("\n@code", StringComparison.Ordinal);
        var markup = code < 0 ? razor : razor[..code];
        return Regex.Replace(markup, @"@\*.*?\*@", "", RegexOptions.Singleline);
    }

    private static List<string> HandWriting()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(p => (Full: p, Relative: Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/')))
            // The archetype components are the one place the markup is meant to live.
            .Where(f => !f.Relative.StartsWith("ALDevToolbox/Components/Shared/Archetypes/", StringComparison.Ordinal))
            .Where(f => HandWrittenPageHead.IsMatch(File.ReadAllText(f.Full)))
            .Select(f => f.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
