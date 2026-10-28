using System.Reflection;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The MCP half of the project-visibility fence. <c>ObjectExplorerTools</c> is a
/// separate call path from the web services covered by
/// <c>ObjectExplorer/ReleaseVisibilityTests</c>: it applies the fence itself, in
/// <c>ResolveReleaseAsync</c> (every release-keyed tool) and
/// <c>EnsureSymbolVisibleAsync</c> (the two tools that accept a caller-supplied
/// <c>symbolId</c>). A service-layer test stays green no matter what happens
/// here, so the matrix is repeated at this boundary. See
/// <c>.design/teams-and-visibility.md</c> and issue #669.
///
/// <para>The matrix is the same two columns <c>ReleaseVisibilityTests</c> uses:
/// a non-member of the assigned team is refused; a member is answered. Anything
/// that behaves the same in both columns is either ungated or over-gated.</para>
/// </summary>
public sealed class ObjectExplorerToolsVisibilityTests : IDisposable
{
    private readonly TestDb _db = new();

    private const int OwnerUserId = 9600;
    private const int MemberUserId = 9601;
    private const int OutsiderUserId = 9602;

    public void Dispose() => _db.Dispose();

    // ── Fixture ─────────────────────────────────────────────────────────

    /// <summary>What one seeded scenario hands the tests: the hidden release, a
    /// visible one to pair it against, and real names discovered from the import.</summary>
    private sealed record Fixture(
        int HiddenReleaseId,
        string HiddenReleaseLabel,
        string OpenReleaseLabel,
        string ModuleName,
        string ObjectName,
        string ObjectKind,
        string ProcedureName,
        long ProcedureSymbolId,
        long CallingSymbolId);

    private async Task<Fixture> SeedAsync()
    {
        await SeedUsersAsync();
        ActAs(OwnerUserId);

        const string hiddenLabel = "CRONUS Denmark on BC 26.0";
        const string openLabel = "BC 26.0";
        var hiddenReleaseId = await ImportReleaseAsync("Microsoft_DK_Core.app", hiddenLabel);
        await ImportReleaseAsync("Microsoft_OIOUBL.app", openLabel);

        var projectId = await SeedPrivateProjectAsync("CRONUS Denmark");
        await LinkByBuildAsync(projectId, hiddenReleaseId);

        await using var ctx = _db.NewContext();
        var moduleName = await ctx.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == hiddenReleaseId)
            .Select(m => m.Name)
            .FirstAsync();

        // A body-bearing procedure whose name is unique on its object, so the
        // name-resolving form of get_procedure_source has an unambiguous target.
        var bodied = await ctx.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == hiddenReleaseId
                        && s.EndLine != null
                        && (s.Kind == "procedure" || s.Kind == "trigger"
                            || s.Kind == "internal_procedure" || s.Kind == "local_procedure"))
            .Select(s => new { s.Id, s.Name, s.ObjectId, ObjectName = s.Object!.Name, ObjectKind = s.Object!.Kind })
            .ToListAsync();
        var unique = bodied
            .GroupBy(s => (s.ObjectId, s.Name))
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .First();

        // A symbol that emits outgoing references, for list_procedure_calls.
        var callingSymbolId = await ctx.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == hiddenReleaseId && r.SourceSymbolId != null)
            .Select(r => r.SourceSymbolId!.Value)
            .FirstAsync();

        return new Fixture(
            hiddenReleaseId, hiddenLabel, openLabel, moduleName,
            unique.ObjectName, unique.ObjectKind, unique.Name, unique.Id, callingSymbolId);
    }

    // ── The matrix ──────────────────────────────────────────────────────

    /// <summary>
    /// Every release-keyed tool in <c>ObjectExplorerTools</c>, invoked with the
    /// hidden release as its release argument. A name may carry a parenthesised
    /// variant ("(by symbolId)"); the part before it is the MCP tool name, and
    /// <see cref="Every_tool_that_takes_a_release_is_in_the_matrix"/> holds the
    /// list to the class, so a new release-keyed tool cannot be left out of it.
    /// </summary>
    private static IEnumerable<(string Name, Func<ObjectExplorerTools, string, Fixture, Task> Invoke)> ReleaseKeyedTools()
    {
        yield return ("compare_releases", (t, key, f) => t.CompareReleasesAsync(key, f.OpenReleaseLabel));
        yield return ("compare_releases (hidden on the right)", (t, key, f) => t.CompareReleasesAsync(f.OpenReleaseLabel, key));
        yield return ("compare_release_files", (t, key, f) => t.CompareReleaseFilesAsync(key, f.OpenReleaseLabel));
        yield return ("compare_release_files (hidden on the right)", (t, key, f) => t.CompareReleaseFilesAsync(f.OpenReleaseLabel, key));
        yield return ("search_objects", (t, key, f) => t.SearchObjectsAsync(key, namePattern: ""));
        yield return ("search_procedures", (t, key, f) => t.SearchProceduresAsync(key, namePattern: ""));
        yield return ("search_content", (t, key, f) => t.SearchContentAsync(key, query: "procedure"));
        yield return ("find_references", (t, key, f) => t.FindReferencesAsync(key, f.ObjectName, f.ObjectKind));
        yield return ("find_system_references", (t, key, f) => t.FindSystemReferencesAsync(key, f.ObjectName, f.ObjectKind));
        yield return ("get_object_outline", (t, key, f) => t.GetObjectOutlineAsync(key, f.ObjectName, f.ObjectKind));
        yield return ("get_procedure_source (by name)", (t, key, f) =>
            t.GetProcedureSourceAsync(key, objectName: f.ObjectName, objectKind: f.ObjectKind, procedureName: f.ProcedureName));
        yield return ("get_procedure_source (by symbolId)", (t, key, f) =>
            t.GetProcedureSourceAsync(key, symbolId: f.ProcedureSymbolId));
        yield return ("list_procedure_calls (by name)", (t, key, f) =>
            t.ListProcedureCallsAsync(key, objectName: f.ObjectName, objectKind: f.ObjectKind, procedureName: f.ProcedureName));
        yield return ("list_procedure_calls (by symbolId)", (t, key, f) =>
            t.ListProcedureCallsAsync(key, symbolId: f.CallingSymbolId));
        yield return ("list_translation_languages", (t, key, f) => t.ListTranslationLanguagesAsync(key));
        yield return ("search_translations", (t, key, f) => t.SearchTranslationsAsync(key, query: "e"));
        yield return ("list_release_modules", (t, key, f) => t.ListReleaseModulesAsync(key));
        yield return ("download_symbol_reference", (t, key, f) => t.DownloadSymbolReferenceAsync(key, f.ModuleName));
    }

    /// <summary>
    /// Tools that take no release, so there is nothing to fence. Each one needs a
    /// reason; the test below also refuses an entry that does take a release.
    /// </summary>
    private static readonly HashSet<string> ToolsWithoutARelease = new(StringComparer.Ordinal)
    {
        // Lists the releases the caller may see; the list itself is fenced by the service.
        "list_releases",
    };

    private static List<(string Name, MethodInfo Method)> RegisteredTools() =>
        typeof(ObjectExplorerTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => (Attribute: m.GetCustomAttribute<McpServerToolAttribute>(), Method: m))
            .Where(t => t.Attribute is not null)
            .Select(t => (t.Attribute!.Name ?? t.Method.Name, t.Method))
            .ToList();

    private static string UnknownSymbol(long symbolId) =>
        $"Symbol id {symbolId} doesn't exist. Call get_object_outline to see the current ids for an object.";

    private static bool TakesARelease(MethodInfo method) =>
        method.GetParameters().Any(p => p.Name!.Contains("release", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Every_tool_that_takes_a_release_is_in_the_matrix()
    {
        var registered = RegisteredTools();
        registered.Should().NotBeEmpty("the reflection must find the tools, or this test proves nothing");
        var inMatrix = ReleaseKeyedTools().Select(t => t.Name.Split(" (")[0]).ToHashSet(StringComparer.Ordinal);

        registered.Where(t => TakesARelease(t.Method)).Select(t => t.Name)
            .Should().BeEquivalentTo(inMatrix, "a release-keyed tool missing here is never checked against the fence");
        registered.Select(t => t.Name).Except(inMatrix)
            .Should().BeEquivalentTo(ToolsWithoutARelease, "every other tool is listed with the reason it takes no release");
        registered.Where(t => ToolsWithoutARelease.Contains(t.Name)).Should().OnlyContain(t => !TakesARelease(t.Method));
    }

    [Fact]
    public async Task Every_release_keyed_tool_refuses_a_private_projects_release_for_a_non_member()
    {
        var fixture = await SeedAsync();
        ActAs(OutsiderUserId);

        // By label and by numeric id: an agent holding either form gets the
        // same deliberately indistinguishable refusal.
        foreach (var key in new[] { fixture.HiddenReleaseLabel, fixture.HiddenReleaseId.ToString() })
        {
            foreach (var (name, invoke) in ReleaseKeyedTools())
            {
                await using var ctx = _db.NewContext();
                var tools = NewTools(ctx);
                // Word for word what a release that does not exist gets, so the refusal
                // gives away nothing about the hidden one.
                // The symbolId forms never resolve the release: the symbol check refuses
                // them, with the wording an unknown symbol id gets.
                var unknown = name switch
                {
                    "get_procedure_source (by symbolId)" => UnknownSymbol(fixture.ProcedureSymbolId),
                    "list_procedure_calls (by symbolId)" => UnknownSymbol(fixture.CallingSymbolId),
                    _ => int.TryParse(key, out var id)
                        ? $"Release {id} does not exist."
                        : $"Release '{key}' was not found. Call list_releases to see available labels.",
                };
                (await ((Func<Task>)(() => invoke(tools, key, fixture)))
                    .Should().ThrowAsync<McpException>($"{name} must not answer a non-member (release key '{key}')"))
                    .Which.Message.Should().Be(unknown, name);
            }
        }
    }

    /// <summary>
    /// The <c>symbolId</c> path skips release resolution entirely — a small
    /// sequential integer the caller supplies. <c>EnsureSymbolVisibleAsync</c>
    /// is the only thing standing between a guessed id and a Private project's
    /// AL source, so it gets its own test rather than only riding along above.
    /// </summary>
    [Fact]
    public async Task A_guessed_symbolId_from_a_private_projects_release_is_refused_to_a_non_member()
    {
        var fixture = await SeedAsync();
        ActAs(OutsiderUserId);

        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        // The release argument names a release the outsider CAN see, so only
        // the symbol check can refuse this.
        var source = await ((Func<Task>)(() =>
            tools.GetProcedureSourceAsync(fixture.OpenReleaseLabel, symbolId: fixture.ProcedureSymbolId)))
            .Should().ThrowAsync<McpException>();
        source.Which.Message.Should().Contain("doesn't exist");

        var calls = await ((Func<Task>)(() =>
            tools.ListProcedureCallsAsync(fixture.OpenReleaseLabel, symbolId: fixture.CallingSymbolId)))
            .Should().ThrowAsync<McpException>();
        calls.Which.Message.Should().Contain("doesn't exist");
    }

    [Fact]
    public async Task Every_release_keyed_tool_answers_a_member_of_the_assigned_team()
    {
        var fixture = await SeedAsync();
        ActAs(MemberUserId);

        foreach (var key in new[] { fixture.HiddenReleaseLabel, fixture.HiddenReleaseId.ToString() })
        {
            foreach (var (name, invoke) in ReleaseKeyedTools())
            {
                await using var ctx = _db.NewContext();
                var tools = NewTools(ctx);
                await ((Func<Task>)(() => invoke(tools, key, fixture)))
                    .Should().NotThrowAsync($"{name} must answer a member of the assigned team (release key '{key}')");
            }
        }
    }

    [Fact]
    public async Task A_symbolId_from_a_private_projects_release_is_served_to_a_member()
    {
        var fixture = await SeedAsync();
        ActAs(MemberUserId);

        await using var ctx = _db.NewContext();
        var tools = NewTools(ctx);

        (await tools.GetProcedureSourceAsync(fixture.OpenReleaseLabel, symbolId: fixture.ProcedureSymbolId))
            .SymbolId.Should().Be(fixture.ProcedureSymbolId);
        (await tools.ListProcedureCallsAsync(fixture.OpenReleaseLabel, symbolId: fixture.CallingSymbolId))
            .Should().NotBeEmpty();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private void ActAs(int? userId, bool siteAdmin = false)
    {
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = siteAdmin;
    }

    private async Task SeedUsersAsync()
    {
        await using var ctx = _db.NewContext();
        foreach (var (id, email, role) in new[]
        {
            (OwnerUserId, "owner@example.com", UserRole.Editor),
            (MemberUserId, "mel@example.com", UserRole.User),
            (OutsiderUserId, "nils@example.com", UserRole.User),
        })
        {
            ctx.Users.Add(new User
            {
                Id = id,
                OrganizationId = TestDb.DefaultOrgId,
                Email = email,
                PasswordHash = "x",
                DisplayName = email,
                Role = role,
                Status = UserStatus.Active,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
    }

    /// <summary>A Private project owned by the owner, with one assigned team the member is on.</summary>
    private async Task<int> SeedPrivateProjectAsync(string name)
    {
        int projectId, teamId;
        await using (var ctx = _db.NewContext())
        {
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = name,
                CreatedByUserId = OwnerUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            var team = new Team
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = "Team " + name,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.Teams.Add(team);
            await ctx.SaveChangesAsync();
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId,
                TeamId = team.Id,
                UserId = MemberUserId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
            projectId = project.Id;
            teamId = team.Id;
        }

        ActAs(OwnerUserId);
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(
                ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            var projects = new ProjectService(
                ctx, _db.OrgContext, access, discovery, _db.NewOfflineGitHubAccessService(ctx), _db.NewRepositoryReadProbe(ctx), NullLogger<ProjectService>.Instance);
            await projects.SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        }

        return projectId;
    }

    /// <summary>Links a release to a project the way a pipeline build does.</summary>
    private async Task LinkByBuildAsync(int projectId, int releaseId)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = "Production",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();

        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipeline.Id,
            ReleaseId = releaseId,
            Status = ProjectBuildStatus.Ready,
            StartedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    /// <summary>
    /// Imports a real fixture app so the symbols, references and file rows the
    /// tools read are the ones the importer actually produces.
    /// </summary>
    private async Task<int> ImportReleaseAsync(string appFileName, string label)
    {
        await using var ctx = _db.NewContext();
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(
                ctx, _db.OrgContext,
                new ALDevToolbox.Services.Translation.TranslationMemoryService(
                    ctx, _db.OrgContext,
                    NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
                _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, appFileName));
        var summary = await importer.ImportReleaseAsync(new ReleaseImportRequest(
            Label: label,
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload(appFileName, stream, SourceZipStream: null) },
            StoreSymbolReference: true));
        return summary.ReleaseId;
    }

    private ObjectExplorerTools NewTools(AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var references = new ReferenceQueryService(ctx, access, _db.OrgContext, NullLogger<ReferenceQueryService>.Instance);
        var explorer = new ObjectExplorerService(ctx, references, access, NullLogger<ObjectExplorerService>.Instance);
        var search = new ObjectSearchService(ctx, access);
        var translations = new TranslationQueryService(ctx);
        var comparison = new ReleaseComparisonService(ctx, access, NullLogger<ReleaseComparisonService>.Instance);
        return new ObjectExplorerTools(explorer, search, references, translations, comparison);
    }
}
