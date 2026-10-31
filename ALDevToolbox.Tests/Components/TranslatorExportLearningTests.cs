using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Translation;
using ALDevToolbox.Services.Translation.Providers;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using TranslatorPage = ALDevToolbox.Components.Pages.Translator;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// What the Translator asks the translation memory to learn when it exports
/// (#1540). The export endpoint learns with no repository attached, and a pair
/// with none is suggested to the whole organisation - so a file that came from
/// a repository, which may belong to a Private solution, must send an empty
/// learn list, both while it is open and after a reload restores it. A file
/// from anywhere else still teaches the memory its edits.
///
/// <para>The page is rendered for real with its JS module stood in for, so
/// these follow the wiring - the repository origin handed to the browser's
/// session record, the restore reading it back, and the list the export
/// posts - rather than only the helpers that decide.</para>
/// </summary>
public sealed class TranslatorExportLearningTests : IAsyncDisposable
{
    private const int UserId = 1540;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/private-app";
    private const string SourcePath = "App/Translations/App.g.xlf";
    private const string UnitId = "Table 1 - Field 2 - Property Caption";

    private const string GeneratedXliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file datatype="xml" source-language="en-US" target-language="en-US" original="App">
            <body>
              <group id="body">
                <trans-unit id="Table 1 - Field 2 - Property Caption" size-unit="char">
                  <source>Amount</source>
                </trans-unit>
              </group>
            </body>
          </file>
        </xliff>
        """;

    private static readonly string DanishXliff = GeneratedXliff
        .Replace("target-language=\"en-US\"", "target-language=\"da-DK\"", StringComparison.Ordinal);

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitJSModuleInterop _module;
    private readonly FakeGitHubApi _api;

    // The browser stamps each open with an id and hands it back (#1595); these
    // stand in for the ids it would return. A restore offers the stored
    // session's id and Continue moves it to a fresh one (#1609).
    private const string UploadOpenId = "open-upload";
    private const string StoredOpenId = "open-stored";
    private const string RestoredOpenId = "open-restored";
    private readonly JSRuntimeInvocationHandler<string> _export;
    private readonly JSRuntimeInvocationHandler<string?> _openIds;

    public TranslatorExportLearningTests()
    {
        _ctx.AddAuthorization().SetAuthorized("translator@cronus.example");
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _module = _ctx.JSInterop.SetupModule("./Components/Pages/Translator.razor.js");
        _module.Mode = JSRuntimeMode.Loose;
        _export = _module.Setup<string>("exportNow", _ => true);
        _export.SetResult("exported");
        // A new open; a repository save's rebase passes one more argument.
        _openIds = _module.Setup<string?>("putBaseStream", i => i.Arguments.Count <= 5);
        _openIds.SetResult(UploadOpenId);

        _api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/repos/{Repo}/git/trees/main", HttpStatusCode.OK,
                $$"""{"sha":"tree-sha","truncated":false,"tree":[{"path":"{{SourcePath}}","type":"blob","sha":"blob-sha"}]}""")
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(SourcePath, GeneratedXliff, "loaded-sha"))
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo));

        var services = _ctx.Services;
        services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        services.AddDisplayTimeZone(_db);
        services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton<IMemoryCache>(new MemoryCache(Options.Create(new MemoryCacheOptions())));
        _db.AddStorageServices(services);
        services.AddScoped<OrganizationConfigService>();
        services.AddDataProtection();
        services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<TranslationMemoryService>();
        services.AddScoped<TranslationSuggestionCoordinator>();
        services.AddScoped(sp => _db.NewMachineTranslationSettingsService(sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>()));
        services.AddSingleton<IMachineTranslationProviderFactory, NoMachineTranslation>();
        services.AddScoped<MachineTranslationService>();
        services.AddScoped(sp => _db.NewGitHubAppClient(sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), _api));
        services.AddScoped(sp => _db.NewGitHubAccessService(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), sp.GetRequiredService<GitHubAppClient>()));
        services.AddScoped(sp => _db.NewGitHubRepositoryService(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), sp.GetRequiredService<GitHubAppClient>(),
            sp.GetRequiredService<GitHubAccessService>()));
        services.AddScoped(sp => _db.NewGitHubTranslationService(
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), sp.GetRequiredService<GitHubAppClient>(),
            sp.GetRequiredService<GitHubAccessService>()));

        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "translator@cronus.example",
            DisplayName = "translator@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Renderer.DisposeAsync();
        _db.WaitForQueriesToSettle();
        await _ctx.DisposeAsync();
        _db.Dispose();
    }

    [Fact]
    public async Task Export_a_copy_of_a_file_opened_from_a_repository_sends_nothing_to_learn()
    {
        await ReadyAsync();
        var cut = _ctx.Render<TranslatorPage>();

        var picker = cut.FindComponent<RepositoryPicker>();
        await cut.InvokeAsync(() => picker.Instance.SelectedChanged.InvokeAsync(new GitHubRepositorySummary(
            Repo, OrgLogin, "private-app", "main", IsPrivate: true, Description: null,
            HtmlUrl: $"https://github.com/{Repo}", CloneUrl: $"https://github.com/{Repo}.git")));
        cut.WaitForElement(".trepo__file").Click();
        Type(cut, "Beloeb");

        // The browser's record is told where the file came from, so a reload knows.
        var stored = _module.Invocations["putBaseStream"].Single().Arguments[4];
        JsonSerializer.Serialize(stored).Should().Contain(Repo).And.Contain("App/Translations/App.");

        cut.FindAll("button").Single(b => b.TextContent.Contains("Export a copy")).Click();

        var export = ExportCall(cut);
        ((string)export.Arguments[2]!).Should().Contain("Beloeb", "the copy still carries the edit");
        export.Arguments[3].Should().Be("[]", "the endpoint would learn it with no repository attached");
    }

    [Fact]
    public void A_restored_session_of_a_repository_file_still_sends_nothing_to_learn()
    {
        var cut = Restore(new TranslatorPage.RepositoryOriginDto { Repository = Repo, Path = "App/Translations/App.da-DK.xlf" });

        ExportRestored(cut).Arguments[3].Should().Be("[]");
    }

    [Fact]
    public void A_restored_session_of_a_file_from_elsewhere_teaches_its_edits()
    {
        var cut = Restore(repo: null);

        ExportRestored(cut).Arguments[3].Should().Be(JsonSerializer.Serialize(new[] { UnitId }));
    }

    [Fact]
    public void An_uploaded_file_teaches_its_edits_and_records_no_repository()
    {
        var cut = _ctx.Render<TranslatorPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(DanishXliff, "App.da-DK.xlf"));
        cut.WaitForElement("textarea.tr-tgtarea");
        Type(cut, "Beloeb");

        _module.Invocations["putBaseStream"].Single().Arguments.Skip(4).FirstOrDefault()
            .Should().BeNull("a file from disk has no repository to remember");

        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();

        var export = ExportCall(cut);
        export.Arguments[3].Should().Be(JsonSerializer.Serialize(new[] { UnitId }));
    }

    // ── The tab's own record (#1595, #1574, #1609) ────────────────────────
    // The browser keeps one session record per open, under the open's id. The
    // tab passes it back on every export, save and overlay write, so it only
    // ever touches its own record, and the browser answers "missing" if that
    // record has gone.

    [Fact]
    public void An_upload_passes_the_id_its_open_was_given_to_export_and_to_the_overlay()
    {
        var cut = _ctx.Render<TranslatorPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(DanishXliff, "App.da-DK.xlf"));
        cut.WaitForElement("textarea.tr-tgtarea");
        Type(cut, "Beloeb");

        // The overlay is written on a debounce off the render loop, so this
        // polls rather than waiting for a render.
        SpinWait.SpinUntil(() => _module.Invocations["putEdits"].Count > 0, TimeSpan.FromSeconds(5))
            .Should().BeTrue("typing persists the overlay for a reload");
        _module.Invocations["putEdits"].Should().OnlyContain(i => (string?)i.Arguments[0] == UploadOpenId);

        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();

        ExportCall(cut).Arguments[0].Should().Be(UploadOpenId);
    }

    [Fact]
    public void An_export_refused_because_the_stored_copy_is_gone_says_so_and_keeps_the_edits_unsaved()
    {
        _export.SetResult("missing");
        var cut = _ctx.Render<TranslatorPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(DanishXliff, "App.da-DK.xlf"));
        cut.WaitForElement("textarea.tr-tgtarea");
        Type(cut, "Beloeb");

        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();

        ExportCall(cut);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(TranslatorPage.StoredCopyMissing("export", fromRepository: false)));
        cut.FindAll("button.btn--primary").Should().Contain(b => b.TextContent.Contains("Export .xlf (1)"),
            "nothing was exported, so the edit is still unsaved");
    }

    [Fact]
    public void A_restored_session_uses_the_fresh_id_continue_gave_it_and_a_refused_save_says_so()
    {
        _module.Setup<bool>("hasHandle", _ => true).SetResult(true);
        _module.Setup<string>("saveInPlace", _ => true).SetResult("missing");
        var cut = Restore(repo: null, primaryLabel: "Save (1)");

        _module.Invocations["loadMeta"].Single().Arguments.Should().Equal(StoredOpenId);
        _module.Invocations["loadOriginalStream"].Single().Arguments.Should().Equal(RestoredOpenId);
        _module.Invocations["hasHandle"].Single().Arguments.Should().Equal(RestoredOpenId);

        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Save")).Click();

        cut.WaitForAssertion(() => _module.Invocations["saveInPlace"].Should().ContainSingle());
        _module.Invocations["saveInPlace"].Single().Arguments[0].Should().Be(RestoredOpenId);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(TranslatorPage.StoredCopyMissing("save", fromRepository: false)));
        cut.FindAll("button.btn--primary").Should().Contain(b => b.TextContent.Contains("Save (1)"),
            "nothing was written, so the edit is still unsaved");
    }

    /// <summary>
    /// The browser half (#1595, #1609): each open's record is stored under its
    /// own id, every reader and writer looks only at the tab's own key, and the
    /// restore prompt never offers, discards or takes over a session a live tab
    /// holds. The module has no JS test runner here, so this pins those rules
    /// in its source.
    /// </summary>
    [Fact]
    public void The_browser_module_keeps_one_record_per_open_and_leaves_live_tabs_alone()
    {
        var js = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "Components", "Pages", "Translator.razor.js"));

        // One record per open, under its id; no single site-wide slot.
        js.Should().Contain("store.put(rec, rec.openId);")
            .And.Contain("return await openRecord({ openId, fileName, originalXml, sourceLang, targetLang, edits: {}, repo: repo || null })")
            .And.Contain("await openRecord({ openId, fileName: file.name, originalXml, sourceLang: null, targetLang: null, edits: {}, handle }, false);")
            .And.NotContain("store.put(rec, KEY)")
            .And.NotContain("getRec()");
        // Export and save read only the tab's own record.
        Regex.Matches(js, @"const rec = await getOwnRec\(openId\);\s+const refused = notOwned\(rec, openId\);\s+if \(refused\) return refused;")
            .Count.Should().Be(2);
        js.Should().Contain("""return openId ? "missing" : "empty";""")
            // Overlay and name writes read, compare and write in one transaction.
            .And.Contain("export async function putEdits(openId, editsJson, learnedJson)")
            .And.Contain("export async function putMeta(openId, fileName, targetLang)")
            .And.Contain("if (!rec || rec.openId !== openId || change(rec) === false)");
        // Each open is held by a Web Lock while its tab has it, taken before
        // the record is written; the prompt skips held records, and Discard
        // and Continue act only on a session nobody holds.
        js.Should().Contain("navigator.locks.request(LOCK_PREFIX + openId, () => new Promise(")
            // A lock that cannot be taken falls back to the heartbeat, and the
            // heartbeat goes when the page does, so a reload sees its session.
            .And.Contain("heartbeat(h);\n                granted();")
            .And.Contain("window.addEventListener(\"pagehide\", gone);")
            .And.Contain("navigator.locks.request(LOCK_PREFIX + openId, { ifAvailable: true },")
            .And.Contain("const free = all.filter((r) => r && r.openId && !live.has(r.openId));")
            .And.Contain("return (await whileUnheld(openId, async () => { await deleteKey(openId); return true; })) === true;")
            .And.Contain("const result = await whileUnheld(openId, async () => {\n            const rec = await getOwnRec(openId);");
        // Continue moves the session to a fresh id and drops the old key.
        js.Should().Contain("const moved = { ...rec, openId: newId };")
            .And.Contain("store.delete(openId);");
        // A record from before #1609, under the old single key, is moved once.
        js.Should().Contain("const LEGACY_KEY = \"current\";")
            .And.Contain("store.delete(LEGACY_KEY);");
        // Storage stays bounded: sessions past the age limit or beyond the
        // newest few are deleted, and a tab's next open replaces its last.
        js.Should().Contain("now - r.savedAt <= maxAgeMs")
            .And.Contain("offerable.slice(0, MAX_KEPT)")
            .And.Contain("if (previousId && previousId !== rec.openId) store.delete(previousId);");
    }

    /// <summary>
    /// #1624: a stored session keeps the base its file had when it was opened.
    /// After the same file is opened again and saved, an older session of it
    /// would, on Continue and Save, write that old base back over the file. So
    /// every open (a pick, a drop, an upload, a repository file, or Continue)
    /// deletes the older sessions of its file that no live tab holds, and the
    /// prompt only ever offers the newest session of each file - while a
    /// session a live tab holds is still never touched (#1609).
    /// </summary>
    [Fact]
    public void The_browser_module_keeps_only_the_newest_session_of_each_file()
    {
        var js = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "Components", "Pages", "Translator.razor.js"));

        // A file is known by its name - a da-DK and a de-DE translation of one
        // generated file are different files - and a repository file also by
        // repository and path. When both copies were picked from disk, the
        // handle decides, so two same-named files in different folders differ.
        js.Should().Contain("""const name = "\n" + (r.fileName || "");""")
            .And.Contain("""return "repo\n" + r.repo.repository + "\n" + r.repo.path + name;""")
            .And.Contain("""return "file" + name;""");
        var same = js[js.IndexOf("async function sameFile(a, b) {", StringComparison.Ordinal)..];
        same = same[..same.IndexOf("\n}\n", StringComparison.Ordinal)];
        same.Should().StartWith("async function sameFile(a, b) {\n    if (fileKey(a) !== fileKey(b)) return false;")
            .And.Contain("if (a.handle && b.handle) {")
            .And.Contain("return await a.handle.isSameEntry(b.handle);");

        // Each new open, and Continue, drops the older copies of its file once
        // the open is stored and held.
        var open = js[js.IndexOf("async function openRecord(rec, dropOlder = true) {", StringComparison.Ordinal)..];
        open = open[..open.IndexOf("\n}\n", StringComparison.Ordinal)];
        open.Should().Contain("if (ok && dropOlder) await dropOlderCopies(rec);");
        open.IndexOf("dropOlderCopies", StringComparison.Ordinal)
            .Should().BeGreaterThan(open.IndexOf("await startOpen(", StringComparison.Ordinal));
        var load = js[js.IndexOf("export async function loadMeta(openId) {", StringComparison.Ordinal)..];
        load = load[..load.IndexOf("\n}\n", StringComparison.Ordinal)];
        load.Should().Contain("if (!ok) return { refused: \"error\" };\n            await dropOlderCopies(moved);");

        // Only older copies no live tab holds are deleted, and only under their
        // lock: Continue on an older session leaves a newer one alone.
        var drop = js[js.IndexOf("async function dropOlderCopies(rec) {", StringComparison.Ordinal)..];
        drop = drop[..drop.IndexOf("\n}\n", StringComparison.Ordinal)];
        drop.Should().Contain("if (!r || !r.openId || r.openId === rec.openId || live.has(r.openId)) continue;")
            .And.Contain("if ((r.savedAt || 0) > (rec.savedAt || 0)) continue;")
            .And.Contain("if (await sameFile(r, rec)) await whileUnheld(r.openId, () => deleteKey(r.openId));")
            .And.NotContain("store.delete(");

        // The prompt offers a session only when no other record of its file,
        // held or not, is newer; the older ones fall outside what it keeps.
        js.Should().Contain("if ((o.savedAt || 0) > (r.savedAt || 0) && await sameFile(o, r)) return true;");
        var peek = js[js.IndexOf("export async function peek(maxAgeMs) {", StringComparison.Ordinal)..];
        peek = peek[..peek.IndexOf("\n}\n", StringComparison.Ordinal)];
        peek.Should().Contain("if (!(await superseded(r, all))) current.push(r);")
            .And.Contain("const offerable = current")
            .And.Contain("if (!keep.has(r.openId)) await whileUnheld(r.openId, () => deleteKey(r.openId));")
            .And.NotContain("superseded(r, free)", "a newer copy a live tab holds still makes an older free one out of date");
    }

    /// <summary>
    /// A file picked or dropped from disk is stored before anything parses it,
    /// so it drops the older sessions of its file only once the page has
    /// opened it: one that fails to open leaves every other session alone.
    /// </summary>
    [Fact]
    public void A_picked_file_drops_older_copies_of_its_file_only_once_it_opened()
    {
        var js = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "Components", "Pages", "Translator.razor.js"));

        js.Should().Contain("edits: {}, handle }, false);")
            .And.Contain("export async function opened(openId) {\n    const rec = await getOwnRec(openId);\n    if (rec) await dropOlderCopies(rec);");
    }

    /// <summary>
    /// A refused Continue says why: only a session another tab holds is "open
    /// in another tab"; one deleted since the prompt appeared, or one the
    /// browser could not read, says so instead (OEX5-4).
    /// </summary>
    [Fact]
    public void The_browser_says_why_it_refused_continue()
    {
        var js = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "Components", "Pages", "Translator.razor.js"));
        var load = js[js.IndexOf("export async function loadMeta(openId) {", StringComparison.Ordinal)..];
        load = load[..load.IndexOf("\n}\n", StringComparison.Ordinal)];
        load.Should().Contain("return result || { refused: \"held\" };")
            .And.Contain("return { refused: \"error\" };")
            .And.NotContain("return null;");
    }

    [Theory]
    [InlineData("gone", "That session is no longer stored in this browser, so there is nothing to pick up.")]
    [InlineData("error", "That session could not be read from this browser")]
    public void Continue_on_a_session_that_is_not_stored_any_more_does_not_blame_another_tab(string refused, string message)
    {
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta", _ => true)
            .SetResult(new TranslatorPage.SessionMeta { Refused = refused });
        var cut = RenderWithStoredSession();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(message));
        cut.Markup.Should().NotContain("open in another tab")
            .And.NotContain("Pick up where you left off?");
        _module.Invocations["loadOriginalStream"].Should().BeEmpty("there is no session to read");
        cut.FindAll("textarea.tr-tgtarea").Should().BeEmpty();
    }

    [Fact]
    public void Continue_on_a_session_another_tab_holds_still_says_so()
    {
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta", _ => true)
            .SetResult(new TranslatorPage.SessionMeta { Refused = "held" });
        var cut = RenderWithStoredSession();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("That file is open in another tab now, so its edits were kept."));
    }

    /// <summary>
    /// OEX5-3: a picked or dropped file is stored before anything parses it.
    /// One that does not open must not stay stored, or a later visit offers
    /// it on the restore prompt.
    /// </summary>
    [Theory]
    [InlineData("not xliff at all", "as XLIFF v1.2")]
    [InlineData("", "Could not read")]
    public async Task A_picked_file_that_does_not_open_is_not_kept_for_the_restore_prompt(string content, string error)
    {
        _module.Setup<bool>("clear", _ => true).SetResult(true);
        _module.Setup<IJSStreamReference>("loadOriginalStream", _ => true).SetResult(new TextStreamReference(content));
        var cut = _ctx.Render<TranslatorPage>();

        await cut.InvokeAsync(() => cut.Instance.OpenFromHandleAsync("broken.xlf", "open-picked"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(error));
        _module.Invocations["clear"].Should().ContainSingle()
            .Which.Arguments.Should().Equal("open-picked");
        _module.Invocations["opened"].Should().BeEmpty("a file that did not open replaces nothing");
        cut.FindAll("textarea.tr-tgtarea").Should().BeEmpty();
    }

    [Fact]
    public async Task A_picked_file_that_opens_is_kept_and_replaces_older_copies()
    {
        _module.Setup<IJSStreamReference>("loadOriginalStream", _ => true).SetResult(new TextStreamReference(DanishXliff));
        var cut = _ctx.Render<TranslatorPage>();

        await cut.InvokeAsync(() => cut.Instance.OpenFromHandleAsync("App.da-DK.xlf", "open-picked"));

        cut.WaitForElement("textarea.tr-tgtarea");
        _module.Invocations["clear"].Should().BeEmpty();
        _module.Invocations["opened"].Should().ContainSingle()
            .Which.Arguments.Should().Equal("open-picked");
    }

    [Fact]
    public void Continue_on_a_session_whose_stored_copy_is_empty_says_so()
    {
        _module.Setup<bool>("clear", _ => true).SetResult(true);
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta", _ => true).SetResult(new TranslatorPage.SessionMeta
        {
            OpenId = RestoredOpenId,
            FileName = "App.da-DK.xlf",
        });
        _module.Setup<IJSStreamReference>("loadOriginalStream", _ => true).SetResult(new TextStreamReference(""));
        var cut = RenderWithStoredSession();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Could not restore the saved session: the stored copy of"));
        _module.Invocations["clear"].Single().Arguments.Should().Equal(RestoredOpenId);
    }

    // ── The restore prompt (#1609) ────────────────────────────────────────

    [Fact]
    public void The_restore_prompt_asks_for_sessions_within_the_age_limit_and_names_the_one_it_offers()
    {
        var cut = RenderWithStoredSession();

        _module.Invocations["peek"].Single().Arguments.Should().Equal((long)TimeSpan.FromDays(7).TotalMilliseconds);
        cut.Markup.Should().Contain("App.da-DK.xlf");
    }

    /// <summary>
    /// The browser module outlives the component: after a resumed connection a
    /// fresh, empty page renders in the same tab while the module still holds
    /// the open the previous page had. Unless the page lets go of it before it
    /// looks, its own session is hidden from it as if another tab had it.
    /// </summary>
    [Fact]
    public void A_page_with_no_file_lets_go_of_any_open_the_tab_still_holds_before_looking_for_sessions()
    {
        RenderWithStoredSession();

        _module.Invocations.Select(i => i.Identifier).Where(id => id is "release" or "peek")
            .Should().Equal("release", "peek");
    }

    [Fact]
    public void Discard_names_the_session_the_prompt_offered()
    {
        _module.Setup<bool>("clear", _ => true).SetResult(true);
        var cut = RenderWithStoredSession();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Discard those edits").Click();

        cut.WaitForAssertion(() => _module.Invocations["clear"].Should().ContainSingle());
        _module.Invocations["clear"].Single().Arguments.Should().Equal(StoredOpenId);
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Pick up where you left off?"));
        cut.Markup.Should().NotContain("open in another tab");
    }

    [Fact]
    public void Discard_of_a_session_another_tab_has_picked_up_keeps_it_and_says_so()
    {
        // The browser refuses: a tab took the session over after the prompt appeared.
        _module.Setup<bool>("clear", _ => true).SetResult(false);
        var cut = RenderWithStoredSession();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Discard those edits").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("That file is open in another tab now, so its edits were kept."));
        cut.Markup.Should().NotContain("Pick up where you left off?");
    }

    [Fact]
    public void Continue_on_a_session_another_tab_has_picked_up_opens_nothing()
    {
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta", _ => true).SetResult(null);
        var cut = RenderWithStoredSession();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("That file is open in another tab now, so its edits were kept."));
        cut.Markup.Should().NotContain("Pick up where you left off?");
        _module.Invocations["loadMeta"].Single().Arguments.Should().Equal(StoredOpenId);
        _module.Invocations["loadOriginalStream"].Should().BeEmpty("there is no session to read");
        cut.FindAll("textarea.tr-tgtarea").Should().BeEmpty();
    }

    [Fact]
    public async Task A_restore_that_fails_after_continue_lets_go_of_the_session_so_it_can_be_offered_again()
    {
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta", _ => true).SetResult(new TranslatorPage.SessionMeta
        {
            OpenId = RestoredOpenId,
            FileName = "App.da-DK.xlf",
        });
        _module.Setup<IJSStreamReference>("loadOriginalStream", _ => true)
            .SetException(new JSException("The stored copy could not be read."));
        var cut = RenderWithStoredSession();
        var releasesBefore = _module.Invocations["release"].Count;

        // Awaited rather than the synchronous Click(): that only rethrows when the
        // handler has already faulted by the time it returns, and on a loaded runner
        // the renderer can still be finishing the first render, so the dispatch is
        // queued and Click() returned clean (seen on #1638's CI, shard a).
        var restore = () => cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await restore.Should().ThrowAsync<JSException>();
        _module.Invocations["release"].Should().HaveCount(releasesBefore + 1,
            "the browser moved the session to an id this tab would otherwise go on holding");
    }

    [Fact]
    public async Task Leaving_the_page_lets_go_of_the_open_so_another_tab_can_offer_it()
    {
        var cut = _ctx.Render<TranslatorPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(DanishXliff, "App.da-DK.xlf"));
        cut.WaitForElement("textarea.tr-tgtarea");

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());

        // Once on first render (nothing held yet) and once on leaving.
        _module.Invocations["release"].Should().HaveCount(2);
        _module.Invocations["clear"].Should().BeEmpty("leaving keeps the stored file for a restore");
    }

    [Fact]
    public async Task A_repository_file_whose_stored_copy_is_gone_points_at_save_to_repository()
    {
        await ReadyAsync();
        _export.SetResult("missing");
        var cut = _ctx.Render<TranslatorPage>();
        var picker = cut.FindComponent<RepositoryPicker>();
        await cut.InvokeAsync(() => picker.Instance.SelectedChanged.InvokeAsync(new GitHubRepositorySummary(
            Repo, OrgLogin, "private-app", "main", IsPrivate: true, Description: null,
            HtmlUrl: $"https://github.com/{Repo}", CloneUrl: $"https://github.com/{Repo}.git")));
        cut.WaitForElement(".trepo__file").Click();
        Type(cut, "Beloeb");

        cut.FindAll("button").Single(b => b.TextContent.Contains("Export a copy")).Click();

        ExportCall(cut);
        var message = TranslatorPage.StoredCopyMissing("export", fromRepository: true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(message));
        message.Should().Contain("Save to repository still works").And.NotContain("will be lost");
    }

    [Fact]
    public void An_export_with_nothing_to_send_keeps_the_edits_unsaved()
    {
        _export.SetResult("empty");
        var cut = _ctx.Render<TranslatorPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(DanishXliff, "App.da-DK.xlf"));
        cut.WaitForElement("textarea.tr-tgtarea");
        Type(cut, "Beloeb");

        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();

        ExportCall(cut);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nothing to export yet"));
        cut.FindAll("button.btn--primary").Should().Contain(b => b.TextContent.Contains("Export .xlf (1)"),
            "nothing was exported, so the edit is still unsaved");
    }

    [Fact]
    public async Task A_repository_save_rebases_only_its_own_record_and_drops_the_id_when_refused()
    {
        await ReadyAsync();
        // No translation branch or language file yet, so the save cuts one and opens a pull request.
        _api.On(HttpMethod.Get, $"/repos/{Repo}/contents/App/Translations/App.da-DK.xlf", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK,
                """{"ref":"refs/heads/main","object":{"sha":"base-sha"}}""")
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("base-sha"))
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.OK,
                """{"content":{"sha":"written-blob-sha"},"commit":{"sha":"new-commit-sha"}}""")
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                $$"""{"number":12,"html_url":"https://github.com/{{Repo}}/pull/12"}""");
        // The rebase carries the tab's id; the browser answers null when it
        // could not write the record.
        _module.Setup<string?>("putBaseStream", i => i.Arguments.Count > 5).SetResult(null);

        var cut = _ctx.Render<TranslatorPage>();
        var picker = cut.FindComponent<RepositoryPicker>();
        await cut.InvokeAsync(() => picker.Instance.SelectedChanged.InvokeAsync(new GitHubRepositorySummary(
            Repo, OrgLogin, "private-app", "main", IsPrivate: true, Description: null,
            HtmlUrl: $"https://github.com/{Repo}", CloneUrl: $"https://github.com/{Repo}.git")));
        cut.WaitForElement(".trepo__file").Click();
        Type(cut, "Beloeb");

        cut.FindAll("button").First(b => b.TextContent.Contains("Save to repository")).Click();
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Save and open a pull request")));
        cut.FindAll("button").First(b => b.TextContent.Contains("Save and open a pull request")).Click();

        cut.WaitForAssertion(() => _module.Invocations["putBaseStream"].Should().HaveCount(2), TimeSpan.FromSeconds(5));
        var rebase = _module.Invocations["putBaseStream"][1];
        rebase.Arguments.Should().HaveCount(6);
        rebase.Arguments[5].Should().Be(UploadOpenId, "the rebase names the open it may replace");

        // The tab holds no id any more, so its next export sends none.
        cut.FindAll("button").Single(b => b.TextContent.Contains("Export a copy")).Click();
        ExportCall(cut).Arguments[0].Should().BeNull();
    }

    /// <summary>Renders the page with a stored session for the restore prompt to offer.</summary>
    private IRenderedComponent<TranslatorPage> RenderWithStoredSession()
    {
        _module.Setup<TranslatorPage.SessionPeek?>("peek", _ => true).SetResult(new TranslatorPage.SessionPeek
        {
            OpenId = StoredOpenId,
            FileName = "App.da-DK.xlf",
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var cut = _ctx.Render<TranslatorPage>();
        cut.WaitForAssertion(() =>
            cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Continue"));
        return cut;
    }

    private IRenderedComponent<TranslatorPage> Restore(TranslatorPage.RepositoryOriginDto? repo, string primaryLabel = "Export .xlf (1)")
    {
        _module.Setup<TranslatorPage.SessionMeta?>("loadMeta", _ => true).SetResult(new TranslatorPage.SessionMeta
        {
            OpenId = RestoredOpenId,
            FileName = "App.da-DK.xlf",
            SourceLang = "en-US",
            TargetLang = "da-DK",
            Edits = new() { [UnitId] = new TranslatorPage.EditDto { T = "Beloeb", S = "translated" } },
            Repo = repo,
        });
        _module.Setup<IJSStreamReference>("loadOriginalStream", _ => true).SetResult(new TextStreamReference(DanishXliff));

        var cut = RenderWithStoredSession();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continue").Click();
        // The restored edit is already in the overlay, so the unsaved count shows on Export.
        cut.WaitForAssertion(() => cut.FindAll("button.btn--primary")
            .Should().Contain(b => b.TextContent.Contains(primaryLabel)));
        return cut;
    }

    private JSRuntimeInvocation ExportCall(IRenderedComponent<TranslatorPage> cut)
    {
        cut.WaitForAssertion(() => _module.Invocations["exportNow"].Should().ContainSingle());
        return _module.Invocations["exportNow"].Single();
    }

    private JSRuntimeInvocation ExportRestored(IRenderedComponent<TranslatorPage> cut)
    {
        cut.FindAll("button").First(b => b.ClassList.Contains("btn--primary") && b.TextContent.Contains("Export")).Click();
        var export = ExportCall(cut);
        ((string)export.Arguments[2]!).Should().Contain("Beloeb", "the restored edit is in the copy");
        return export;
    }

    private static void Type(IRenderedComponent<TranslatorPage> cut, string text) =>
        cut.WaitForElement("textarea.tr-tgtarea").Input(text);

    private async Task ReadyAsync()
    {
        using (var rsa = RSA.Create(2048))
        {
            await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
                AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        }
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var oauth = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, oauth)).LinkAsync("the-code");
        }
    }

    /// <summary>Machine translation is off for the org, so nothing ever builds a provider.</summary>
    private sealed class NoMachineTranslation : IMachineTranslationProviderFactory
    {
        public IMachineTranslationProvider Create(ResolvedMtSettings settings) =>
            throw new InvalidOperationException("Machine translation is off in these tests.");
    }

    /// <summary>The browser's stored original, as the restore streams it back.</summary>
    private sealed class TextStreamReference : IJSStreamReference
    {
        private readonly byte[] _bytes;
        public TextStreamReference(string text) => _bytes = Encoding.UTF8.GetBytes(text);
        public long Length => _bytes.Length;
        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
