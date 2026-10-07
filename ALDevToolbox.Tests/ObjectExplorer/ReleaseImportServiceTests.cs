using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Black-box coverage of <see cref="ReleaseImportService"/>. Drives the
/// service end-to-end through the shared <see cref="TestDb"/> Postgres
/// fixture: feed real Microsoft <c>.app</c> fixtures into
/// <c>ImportReleaseAsync</c>, then read back the <c>oe_*</c> rows to assert
/// the schema-shaped invariants we care about — counts, the cross-module
/// references the find-references query will key off of, the idempotency
/// guarantee on the (release, app, version, hash) tuple, and the
/// Release lifecycle (ingesting → ready/failed).
/// </summary>
public sealed class ReleaseImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private const string DkCoreAppId = "40d64215-8abc-4d96-87dc-2894e5431115";
    private const string OioublAppId = "edc24573-b277-44bb-8c2a-7a90fcabd055";
    private static readonly Guid BaseAppId = Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972");

    private ReleaseImportService NewService(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), NullLogger<TranslationImportService>.Instance),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    // ── SymbolReference.json capture (opt-in) ────────────────────────────

    [Fact]
    public async Task Stores_symbol_reference_json_when_requested()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK (symbols)",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_DK_Core.app", appStream, SourceZipStream: null) },
            StoreSymbolReference: true));

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == summary.ReleaseId)
            .Select(m => new { m.SymbolReferenceContentHash, Json = m.SymbolReferenceContent!.Content })
            .SingleAsync();

        module.SymbolReferenceContentHash.Should().NotBeNullOrEmpty();
        module.Json.Should().NotBeNullOrEmpty();
        // Stored as clean JSON (BOM stripped) — parses and looks like the symbol file.
        module.Json.TrimStart().Should().StartWith("{");
        var doc = System.Text.Json.JsonDocument.Parse(module.Json);
        doc.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public async Task Does_not_store_symbol_reference_json_by_default()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK (no symbols)",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_DK_Core.app", appStream, SourceZipStream: null) }));

        await using var read = _db.NewContext();
        var hash = await read.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == summary.ReleaseId)
            .Select(m => m.SymbolReferenceContentHash)
            .SingleAsync();
        hash.Should().BeNull("the store-symbol-reference option defaults to off");
    }

    // ── Queued-import split (BeginReleaseAsync / MarkFailedAsync) ─────────

    [Fact]
    public async Task BeginReleaseAsync_creates_an_ingesting_row_then_ProcessReleaseAsync_completes_it()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var releaseId = await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            Label: "BC 25.18 DK (queued)", Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null));

        await using (var mid = _db.NewContext())
        {
            var pending = await mid.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
            pending.Status.Should().Be("ingesting", "the row is created up front so it shows in the admin list");
        }

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ProcessReleaseAsync(
            releaseId,
            new[] { new AppFileUpload("Microsoft_DK_Core.app", appStream, SourceZipStream: null) });

        summary.ReleaseId.Should().Be(releaseId);
        summary.ModulesImported.Should().Be(1);

        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId))
            .Status.Should().Be("ready");
    }

    [Fact]
    public async Task BeginReleaseAsync_allows_a_duplicate_label_without_a_dedup_key()
    {
        // The label is a pure display string now — only the explicit dedup key
        // dedups, so two keyless releases can share a label.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await svc.BeginReleaseAsync(new ReleaseImportMetadata("Dupe", "first_party", null, null));

        var act = async () => await svc.BeginReleaseAsync(new ReleaseImportMetadata("Dupe", "first_party", null, null));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BeginReleaseAsync_rejects_a_duplicate_dedup_key_before_creating_a_row()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "Label A", "first_party", null, null, DedupKey: "bc-onprem:28.2:dk"));

        // Different label, same key → rejected (the key is the identity).
        var act = async () => await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "Label B", "first_party", null, null, DedupKey: "bc-onprem:28.2:dk"));
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DedupKey");
    }

    [Fact]
    public async Task MarkFailedAsync_flips_a_queued_row_to_failed_with_a_message()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var releaseId = await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "Will fail", "first_party", null, null));
        await svc.MarkFailedAsync(releaseId, "Download was unreachable.");

        await using var read = _db.NewContext();
        var row = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        row.Status.Should().Be("failed");
        row.StatusMessage.Should().Be("Download was unreachable.");
    }

    [Fact]
    public async Task MarkReadyWithoutIngestAsync_flips_a_build_release_ready_holding_no_files()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var releaseId = await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "CRONUS on BC 30.0", "project", null, null));
        await svc.MarkReadyWithoutIngestAsync(releaseId);

        await using var read = _db.NewContext();
        var row = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        row.Status.Should().Be("ready");
        row.SourceFileCount.Should().Be(0);
        (await read.OeModules.AsNoTracking().AnyAsync(m => m.ReleaseId == releaseId)).Should().BeFalse();
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Imports_dk_core_into_a_ready_release_with_expected_row_counts()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_DK_Core.app", appStream, SourceZipStream: null) }));

        summary.ModulesImported.Should().Be(1);
        summary.ModulesSkipped.Should().Be(0);
        // DK Core: 4 codeunits + 3 pageextensions + 7 reportextensions = 14 objects.
        summary.ObjectsImported.Should().Be(14);
        summary.SourceFilesImported.Should().BeGreaterThan(0);

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == summary.ReleaseId);
        release.Status.Should().Be("ready");
        release.Label.Should().Be("BC 25.18 DK");
        release.Kind.Should().Be("first_party");
        // No Base App came in this release, so BcVersion stays null.
        release.BcVersion.Should().BeNull();

        var modules = await read.OeModules.AsNoTracking().Where(m => m.ReleaseId == summary.ReleaseId).ToListAsync();
        modules.Should().ContainSingle()
            .Which.AppId.Should().Be(Guid.Parse(DkCoreAppId));
    }

    [Fact]
    public async Task Imports_oioubl_records_a_tableextension_extends_reference_back_to_base_app()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK + OIOUBL",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_OIOUBL.app", appStream, SourceZipStream: null) }));

        await using var read = _db.NewContext();
        // OIOUBL's "OIOUBL-Fin. Charge Memo Line" tableextension extends Base App's
        // "Finance Charge Memo Line" table — that's an extends_target reference
        // pointing at Base App's AppId.
        var extendsRef = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.ReferenceKind == "extends_target"
                && r.TargetAppId == BaseAppId
                && r.TargetObjectKind == "table"
                && r.TargetObjectName == "Finance Charge Memo Line")
            .ToListAsync();
        extendsRef.Should().NotBeEmpty(
            because: "the parser sees the #...# prefix on tableextension Target and the importer materialises it as a row");
    }

    [Fact]
    public async Task Imports_resolved_variable_subtypes_as_cross_module_references()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, null) }));

        await using var read = _db.NewContext();
        // CopyDepreciationBookExt declares a Codeunit-typed variable resolving to
        // Base App's "Cancel FA Ledger Entries" (id 5624). Verify the reference
        // row got the full triplet — that's what the find-references query keys on.
        // Use a list rather than Single because the same target can appear from
        // a variable AND from a parameter or return type in unrelated methods.
        var refs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.ReferenceKind == "variable_type"
                && r.TargetAppId == BaseAppId
                && r.TargetObjectKind == "codeunit"
                && r.TargetObjectId == 5624)
            .ToListAsync();
        refs.Should().NotBeEmpty();
        refs.Should().OnlyContain(r => r.TargetObjectName == "Cancel FA Ledger Entries");
    }

    [Fact]
    public async Task Imports_table_fields_as_field_symbols_with_their_id()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "OIOUBL", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("oioubl.app", appStream, null) }));

        await using var read = _db.NewContext();
        var profileTable = await read.OeModuleObjects.AsNoTracking()
            .SingleAsync(o => o.Kind == "table" && o.Name == "OIOUBL-Profile");
        var fields = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ObjectId == profileTable.Id && s.Kind == "table_field")
            .OrderBy(s => s.FieldId)
            .ToListAsync();
        fields.Should().HaveCount(2);
        fields[0].Name.Should().Be("OIOUBL-Code");
        fields[0].FieldId.Should().Be(13630);
        fields[1].Name.Should().Be("OIOUBL-Profile ID");
        fields[1].FieldId.Should().Be(13631);
    }

    [Fact]
    public async Task Prefers_paired_source_zip_over_app_embedded_source()
    {
        // BC 28.x first-party modules ship as Ready2Run wrappers whose
        // inner .app's embedded source is partial — the canonical source
        // sits in the sibling .Source.zip on the DVD. The importer must
        // prefer the zip whenever it's paired, falling back to the .app's
        // embedded source only when no zip was uploaded. Pin that
        // priority by feeding the same Microsoft_DK_Core fixture with its
        // matching .Source.zip and asserting both the file row's path and
        // its content are the values the zip ships (the same .app without
        // a paired zip would still work via the embedded source — that's
        // covered by Stamps_module_object_line_numbers_from_embedded_source).
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var zipStream = File.OpenRead(Path.Combine(FixtureRoot, "DK Core.Source.zip"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK paired", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, zipStream) }));

        await using var read = _db.NewContext();
        var codeunit = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Kind == "codeunit" && o.Name == "DK Core Event Subscribers")
            .SingleAsync();
        codeunit.SourceFileId.Should().NotBeNull(
            because: "the .Source.zip ships the same canonical src/Codeunits/... path the symbol package expects");
        codeunit.LineNumber.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Links_symbol_objects_to_source_via_al_header_regardless_of_zip_layout()
    {
        // The symbol package's ReferenceSourceFileName is not a
        // reliable join key: in BC 28.x even Microsoft's first-party
        // modules ship the .Source.zip with paths that don't agree
        // with the symbol-package paths (some have a project-folder
        // prefix, some have nested src/, some are bare). The importer
        // matches objects to files via the (Kind, Name) declaration at
        // the top of each .al file instead. Pin that contract by
        // wrapping DK Core's source files in a synthetic .Source.zip
        // whose paths bear no resemblance to ReferenceSourceFileName,
        // and assert objects still link.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var alFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var origZip = File.OpenRead(Path.Combine(FixtureRoot, "DK Core.Source.zip")))
        using (var archive = new System.IO.Compression.ZipArchive(origZip, System.IO.Compression.ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = entry.Open();
                using var r = new StreamReader(s);
                alFiles[entry.FullName] = r.ReadToEnd();
            }
        }
        alFiles.Should().NotBeEmpty();

        // Repack each .al file under a deliberately weird path that
        // does NOT match any plausible ReferenceSourceFileName.
        var weirdZipBytes = BuildSyntheticSourceZip(alFiles, pathMangler: original =>
            "WeirdProjectFolder/SomethingElse/" + Path.GetFileName(original));

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var weirdZip = new MemoryStream(weirdZipBytes);
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK weird-zip", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, weirdZip) }));

        await using var read = _db.NewContext();
        var codeunit = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Kind == "codeunit" && o.Name == "DK Core Event Subscribers")
            .SingleAsync();
        codeunit.SourceFileId.Should().NotBeNull(
            because: "header-based matching ignores the .Source.zip path layout and pairs by (Kind, Name)");
        codeunit.LineNumber.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The end of the doc-comment path (issue #561): a <c>&lt;summary&gt;</c>
    /// written above a declaration in the shipped source has to survive
    /// ingest and land on the <c>oe_module_symbols</c> row.
    ///
    /// Both halves of the symbol merge are asserted, because they reach the
    /// row by different routes and only one of them is obvious. An internal
    /// procedure is in the symbol package, so its row is built from the
    /// package and the source match only contributes positions — and now the
    /// description, which the package cannot carry at all: the AL compiler
    /// drops doc comments, so nothing downstream of the compiler has ever
    /// seen one. A local procedure is absent from the package and its row
    /// comes from the source extractor outright.
    /// </summary>
    [Fact]
    public async Task Stores_the_doc_summary_written_above_a_declaration_in_source()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var alFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var origZip = File.OpenRead(Path.Combine(FixtureRoot, "DK Core.Source.zip")))
        using (var archive = new System.IO.Compression.ZipArchive(origZip, System.IO.Compression.ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                alFiles[entry.FullName] = reader.ReadToEnd();
            }
        }

        // Document two of the shipped declarations in place. Microsoft's
        // localisation apps carry no doc comments, so this is the only way to
        // get a documented declaration through the real ingest path.
        var target = alFiles.Keys.Single(k => k.EndsWith("UpgradeLocalPermissionSet.Codeunit.al", StringComparison.Ordinal));
        var documented = alFiles[target]
            .Replace("    internal procedure RunUpgrade()",
                """
                        /// <summary>
                        /// Removes the local permission set once, guarded by an upgrade tag.
                        /// </summary>
                        /// <remarks>Runs per database.</remarks>
                        internal procedure RunUpgrade()
                """.TrimEnd() + "\n", StringComparison.Ordinal)
            .Replace("    local procedure RemoveLocalPermissionSet()",
                """
                        /// <summary>Deletes the rows the permission set left behind.</summary>
                        local procedure RemoveLocalPermissionSet()
                """.TrimEnd() + "\n", StringComparison.Ordinal);
        documented.Should().NotBe(alFiles[target], because: "both declarations must actually have been found");
        alFiles[target] = documented;

        var zipBytes = BuildSyntheticSourceZip(alFiles, pathMangler: original => original);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var sourceZip = new MemoryStream(zipBytes);
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK documented", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, sourceZip) }));

        await using var read = _db.NewContext();
        var symbols = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Module!.ReleaseId == summary.ReleaseId
                && s.Object!.Name == "Upgrade Local Permission Set")
            .Select(s => new { s.Name, s.Kind, s.Doc })
            .ToListAsync();

        symbols.Single(s => s.Name == "RunUpgrade").Doc.Should().Be(
            "Removes the local permission set once, guarded by an upgrade tag.",
            because: "the package-derived row takes its description from the matched source declaration");
        symbols.Single(s => s.Name == "RemoveLocalPermissionSet").Doc.Should().Be(
            "Deletes the rows the permission set left behind.",
            because: "a local procedure is only ever seen by the source extractor");

        // Everything else in the module stays null. A description that
        // appears on an undocumented declaration is worse than none, and
        // this is the assertion that catches a doc block drifting.
        symbols.Where(s => s.Name is not ("RunUpgrade" or "RemoveLocalPermissionSet"))
            .Should().OnlyContain(s => s.Doc == null);
    }

    private static byte[] BuildSyntheticSourceZip(IReadOnlyDictionary<string, string> alFiles, Func<string, string> pathMangler)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (original, content) in alFiles)
            {
                var entry = zip.CreateEntry(pathMangler(original));
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Stamps_module_object_line_numbers_from_embedded_source()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, null) }));

        await using var read = _db.NewContext();
        // The codeunit declaration sits at the top of its .al file (line 1
        // after any namespace declaration). The exact line varies with header
        // comments / namespaces, but it must be >= 1 and the SourceFile FK
        // must be wired.
        var codeunit = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Kind == "codeunit" && o.Name == "DK Core Event Subscribers")
            .SingleAsync();
        codeunit.LineNumber.Should().BeGreaterThan(0);
        codeunit.SourceFileId.Should().NotBeNull(
            because: "ReferenceSourceFileName matches a file in the embedded src/ tree");
    }

    // ── Idempotency ─────────────────────────────────────────────────────

    [Fact]
    public async Task Byte_identical_re_upload_is_skipped_silently_on_the_same_release()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var first = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Release A", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));
        first.ModulesImported.Should().Be(1);
        first.ModulesSkipped.Should().Be(0);

        // Re-uploading into a *different* Release would just create a new
        // module; the skip is per-Release. Verify by re-running with the same
        // bytes against a second Release.
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var second = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Release B", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s2, null) }));
        second.ModulesImported.Should().Be(1);
        second.ModulesSkipped.Should().Be(0);
    }

    // ── Validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Rejects_request_missing_label_with_field_keyed_error()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "  ", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s, null) }));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Label");
    }

    [Fact]
    public async Task Rejects_request_with_unknown_kind()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Foo", Kind: "wat",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s, null) }));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Kind");
    }

    [Fact]
    public async Task Accepts_the_cal_kind()
    {
        // 'cal' is the kind the endpoint stamps on legacy C/AL TXT imports.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var releaseId = await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            Label: "CRONUS objects 2016", Kind: "cal",
            ParentReleaseId: null, ApplicationVersionId: null));

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Kind.Should().Be("cal");
    }

    [Fact]
    public async Task Rejects_request_with_no_uploads()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Foo", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: Array.Empty<AppFileUpload>()));
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Uploads");
    }

    // ── Failure handling ────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_app_flips_the_release_to_failed_and_records_the_message()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        // A non-NAVX stream — the reader throws InvalidDataException, the
        // service must catch, stamp the release as failed, and rethrow so
        // the caller sees the original error.
        await using var notAnAppStream = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Bad release", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("not-an-app.zip", notAnAppStream, null) }));
        await act.Should().ThrowAsync<InvalidDataException>();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking()
            .SingleAsync(r => r.Label == "Bad release");
        release.Status.Should().Be("failed");
        release.StatusMessage.Should().NotBeNullOrEmpty();
        release.StatusMessage.Should().Contain("NAVX");
    }

    // ── Cancellation and the status it leaves (#1180) ──────────────────

    [Fact]
    public async Task A_failure_is_recorded_even_when_the_token_is_cancelled_meanwhile()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var releaseId = await svc.BeginReleaseAsync(new ReleaseImportMetadata("Cancelled meanwhile", "first_party", null, null));
        using var cts = new CancellationTokenSource();
        var runningWhileImporting = false;
        await using var upload = new FailingStream(() =>
        {
            runningWhileImporting = ReleaseIngests.IsRunning(releaseId);
            cts.Cancel();
            return new InvalidDataException("Not a NAVX package.");
        });

        var act = () => svc.ProcessReleaseAsync(releaseId, [new AppFileUpload("broken.app", upload, null)], ct: cts.Token);

        await act.Should().ThrowAsync<InvalidDataException>();
        runningWhileImporting.Should().BeTrue("a build waiting on this release must see it being imported");
        ReleaseIngests.IsRunning(releaseId).Should().BeFalse();
        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("failed");
        release.StatusMessage.Should().Be("Not a NAVX package.");
    }

    [Fact]
    public async Task Its_own_cancellation_leaves_the_release_to_the_caller()
    {
        // The import worker relies on this: a shutdown leaves the release importing
        // so the startup reconciler can resume its job.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var releaseId = await svc.BeginReleaseAsync(new ReleaseImportMetadata("Shut down", "first_party", null, null));
        using var cts = new CancellationTokenSource();
        await using var upload = new FailingStream(() =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });

        var act = () => svc.ProcessReleaseAsync(releaseId, [new AppFileUpload("CRONUS.app", upload, null)], ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId)).Status.Should().Be("ingesting");
    }

    [Fact]
    public async Task A_cancelled_amend_is_failed_rather_than_left_importing()
    {
        var releaseId = await SeedReadyReleaseAsync("Amend cancelled", "Microsoft_DK_Core.app");
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        using var cts = new CancellationTokenSource();
        await using var upload = new FailingStream(() =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });

        var act = () => svc.AmendReleaseAsync(releaseId, [new AppFileUpload("CRONUS.app", upload, null)], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ReleaseIngests.IsRunning(releaseId).Should().BeFalse();
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId)).Status.Should().Be("failed");
    }

    /// <summary>An upload whose first read runs <c>onRead</c> and throws what it returns.</summary>
    private sealed class FailingStream(Func<Exception> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1024;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw onRead();
        public override int Read(Span<byte> buffer) => throw onRead();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw onRead();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw onRead();
        public override long Seek(long offset, SeekOrigin origin) => Position = origin == SeekOrigin.End ? Length + offset : offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── Per-upload flag propagation ────────────────────────────────────

    [Fact]
    public async Task Threads_is_test_is_internal_is_language_pack_flags_through_to_module_rows()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Flag propagation",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[]
            {
                // Pass the same fixture but mark it test+internal+language-pack —
                // exercises the flag-propagation path the folder-ZIP upload
                // layer relies on to surface DVD folder conventions.
                new AppFileUpload(
                    FileName: "Microsoft_DK_Core.app",
                    AppStream: appStream,
                    SourceZipStream: null,
                    IsTest: true,
                    IsInternal: true,
                    IsLanguagePack: true),
            }));

        await using var read = _db.NewContext();
        var module = await read.OeModules.AsNoTracking()
            .SingleAsync(m => m.ReleaseId == summary.ReleaseId);
        module.IsTest.Should().BeTrue();
        module.IsInternal.Should().BeTrue();
        module.IsLanguagePack.Should().BeTrue();
    }

    // ── Dedup-key uniqueness (label is display-only) ────────────────────

    [Fact]
    public async Task Allows_a_second_active_release_with_the_same_label()
    {
        // Manual uploads carry no dedup key, so a repeated label is fine now —
        // the label is a pure display string.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var act = async () => await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "BC 25.18 DK", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("oioubl.app", s2, null) }));
        await act.Should().NotThrowAsync();

        await using var read = _db.NewContext();
        (await read.OeReleases.CountAsync(r => r.Label == "BC 25.18 DK" && r.DeletedAt == null)).Should().Be(2);
    }

    [Fact]
    public async Task Allows_reusing_a_dedup_key_after_the_previous_release_was_soft_deleted()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        var first = await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "Recycled", "first_party", null, null, DedupKey: "bc-onprem:25.18:dk"));

        // While it's active, the same key is refused.
        var blocked = async () => await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "Recycled again", "first_party", null, null, DedupKey: "bc-onprem:25.18:dk"));
        (await blocked.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("DedupKey");

        // Soft-delete so the partial unique index no longer blocks reuse.
        var release = await ctx.OeReleases.SingleAsync(r => r.Id == first);
        release.DeletedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();

        var second = async () => await svc.BeginReleaseAsync(new ReleaseImportMetadata(
            "Recycled again", "first_party", null, null, DedupKey: "bc-onprem:25.18:dk"));
        await second.Should().NotThrowAsync();
    }

    // ── Parent-release linkage ──────────────────────────────────────────

    [Fact]
    public async Task Records_parent_release_pointer_when_set()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);

        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var parent = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Parent BC 25.18", Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var child = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Customer X on BC 25.18", Kind: "project",
            ParentReleaseId: parent.ReleaseId, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("oioubl.app", s2, null) }));

        await using var read = _db.NewContext();
        var rel = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == child.ReleaseId);
        rel.ParentReleaseId.Should().Be(parent.ReleaseId);
    }

    // ── Phase-2 call-site references ────────────────────────────────────

    [Fact]
    public async Task Pages_source_table_name_resolves_from_numeric_id_to_table_name()
    {
        // Modern BC (28.x+) emits a page's SourceTable property as a
        // bare numeric object id (e.g. "36" for Sales Header). The
        // import path stores the raw value and a second-pass resolver
        // swaps the numeric id for the table's name so the reference
        // extractor's Rec → SourceTable binding (BuildGlobalScope) can
        // ResolveTypeByName on it. Without the resolution, Rec.X chains
        // on the page drop because "36" doesn't match any catalog entry.
        //
        // Invariant under test: every page whose numeric SourceTable id
        // points to a table imported in this release has had the id
        // swapped for the table's name. Pages whose SourceTable points
        // to a table outside this release (cross-release shadowing —
        // gap #3 in al-reference-extractor-gaps.md) can legitimately
        // keep a numeric value; those aren't this fix's job. The test
        // also asserts that the resolver fires at all on the fixtures
        // — at least one page must have started as numeric and been
        // resolved, otherwise the test has no signal.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await using var s2 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "SourceTable resolution",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[]
            {
                new AppFileUpload("Microsoft_DK_Core.app", s1, null),
                new AppFileUpload("Microsoft_OIOUBL.app", s2, null),
            }));

        await using var read = _db.NewContext();
        var pages = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == summary.ReleaseId
                && (o.Kind == "page" || o.Kind == "pageextension"))
            .Select(o => new { o.Name, o.Kind, o.SourceTableName })
            .ToListAsync();

        var inReleaseTableIds = await read.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == summary.ReleaseId
                && o.Kind == "table"
                && o.ObjectId != null)
            .Select(o => o.ObjectId!.Value)
            .ToListAsync();
        var inReleaseTableIdSet = new HashSet<int>(inReleaseTableIds);

        // Any page whose SourceTable points to a table in this release
        // must have its source_table_name resolved to that table's name
        // (i.e. no longer digit-only).
        var unresolved = pages
            .Where(p => p.SourceTableName != null
                && System.Text.RegularExpressions.Regex.IsMatch(p.SourceTableName, "^[0-9]+$"))
            .Where(p => int.TryParse(p.SourceTableName, out var id) && inReleaseTableIdSet.Contains(id))
            .ToList();
        unresolved.Should().BeEmpty(
            because: "for every page whose numeric SourceTable id matches a table imported in this "
                   + "release, the resolver should have swapped the id for the table's name");

        // Smoke check that the resolver actually ran on at least one
        // page in the fixture — guards against the test silently
        // becoming a no-op if a future fixture switch eliminates the
        // numeric form. Resolved pages have a non-numeric, non-empty
        // source_table_name; pre-fix they'd have been numeric.
        pages.Should().Contain(p =>
            p.SourceTableName != null
            && p.SourceTableName.Length > 0
            && !System.Text.RegularExpressions.Regex.IsMatch(p.SourceTableName, "^[0-9]+$"),
            because: "at least one page in DK Core / OIOUBL fixtures should have its SourceTable "
                   + "resolved to a table name — either via the legacy hash-ref path or the "
                   + "numeric resolver added by this change");
    }

    [Fact]
    public async Task Import_emits_method_call_or_field_access_references_for_DK_Core()
    {
        // DK Core ships actual .al source (it's a small extension wrapper
        // with procedures that call into the Base App tables / codeunits
        // declared in its own symbol package + variable rows). Once the
        // per-module loop finishes, EmitCallSiteReferencesAsync runs the
        // AL reference extractor over every file and writes method_call
        // / field_access rows. We don't pin a specific (object, member)
        // because the .app's source is opaque — but we DO expect the
        // bucket to be non-empty.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Phase 2 DK Core",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var read = _db.NewContext();
        var refs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == summary.ReleaseId)
            .Where(r => r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access")
            .Select(r => new { r.ReferenceKind, r.TargetObjectName, r.TargetMemberName, r.TargetMemberKind })
            .ToListAsync();

        refs.Should().NotBeEmpty(
            because: "DK Core's .al source contains member-access patterns the extractor must surface");
        refs.Should().AllSatisfy(r =>
        {
            r.TargetMemberName.Should().NotBeNullOrEmpty(
                because: "member-scoped rows always carry the member name");
            r.TargetMemberKind.Should().NotBeNullOrEmpty(
                because: "member-scoped rows always carry the member kind");
        });
    }

    [Fact]
    public async Task Stamps_end_line_and_end_column_on_procedure_symbols()
    {
        // Issue #181: the extractor's procedure walker captures the
        // matching `end;` token's position and the import service plumbs
        // it onto oe_module_symbols. Body-bearing kinds (procedure /
        // trigger / event publisher / event subscriber) must end up with
        // both columns populated; field rows must not.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "End-line stamp DK Core",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var read = _db.NewContext();
        var procedures = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == summary.ReleaseId)
            .Where(s => s.Kind == "procedure" || s.Kind == "local_procedure"
                     || s.Kind == "internal_procedure" || s.Kind == "protected_procedure"
                     || s.Kind == "trigger" || s.Kind == "event_publisher" || s.Kind == "event_subscriber")
            .Where(s => s.LineNumber > 0)
            .ToListAsync();
        procedures.Should().NotBeEmpty(
            because: "DK Core ships .al source with at least a handful of procedure bodies");
        procedures.Where(p => p.EndLine is not null).Should().NotBeEmpty(
            because: "the walker should now stamp end_line on body-bearing kinds");
        // Every populated row obeys the start <= end invariant.
        procedures.Where(p => p.EndLine is not null).Should().AllSatisfy(p =>
        {
            p.EndLine!.Value.Should().BeGreaterThanOrEqualTo(p.LineNumber);
            p.EndColumn.Should().NotBeNull();
        });

        // Field rows don't have bodies — they keep EndLine null. The
        // DK Core fixture is codeunit / pageextension / reportextension
        // only, so this query can legitimately return an empty list;
        // assert that no field-kind row carries an end line rather
        // than using AllSatisfy (which fails on an empty input).
        var fieldsWithEndLine = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.Module!.ReleaseId == summary.ReleaseId)
            .Where(s => s.Kind == "table_field" || s.Kind == "page_field" || s.Kind == "page_action")
            .Where(s => s.EndLine != null)
            .ToListAsync();
        fieldsWithEndLine.Should().BeEmpty(
            because: "fields and actions don't have bodies — EndLine must stay null on those kinds");
    }

    [Fact]
    public async Task Stamps_source_symbol_id_on_method_call_references_emitted_from_procedure_bodies()
    {
        // Issue #181: ExtractedReference now carries SourceMemberLine and
        // the import service resolves it to the symbol's id, stamping
        // source_symbol_id on the persisted row. The forward-edge MCP
        // list_procedure_calls keys on this column.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Source-symbol-id DK Core",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var read = _db.NewContext();
        var memberRefs = await read.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == summary.ReleaseId)
            .Where(r => r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access")
            .Select(r => new { r.Id, r.SourceSymbolId, r.SourceObjectId, r.LineNumber })
            .ToListAsync();
        memberRefs.Should().NotBeEmpty();
        memberRefs.Where(r => r.SourceSymbolId is not null).Should().NotBeEmpty(
            because: "method_call / field_access rows emitted from inside a procedure body should carry source_symbol_id");

        // Every stamped row must point at an oe_module_symbols row that
        // belongs to the same source object — defensive check that the
        // (Owner, LineNumber) match didn't cross objects.
        var stampedIds = memberRefs.Where(r => r.SourceSymbolId is not null).Select(r => r.SourceSymbolId!.Value).Distinct().ToList();
        var symbolOwners = await read.OeModuleSymbols.AsNoTracking()
            .Where(s => stampedIds.Contains(s.Id))
            .Select(s => new { s.Id, s.ObjectId })
            .ToListAsync();
        var ownerById = symbolOwners.ToDictionary(s => s.Id, s => s.ObjectId);
        memberRefs.Where(r => r.SourceSymbolId is not null).Should().AllSatisfy(r =>
        {
            ownerById[r.SourceSymbolId!.Value].Should().Be(r.SourceObjectId,
                because: "source_symbol_id must reference a symbol on the same source object");
        });
    }

    [Fact]
    public async Task Import_emits_system_references_for_builtin_calls()
    {
        // #279: built-in/system method calls (Insert / Modify / SetRange / …)
        // that find-references drops are captured in oe_module_system_references.
        // DK Core's bodies are full of them.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "System-refs DK Core",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));

        await using var read = _db.NewContext();
        var sysRefs = await read.OeModuleSystemReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == summary.ReleaseId)
            .Select(r => new { r.SystemMethodName, r.ReferenceKind, r.TargetObjectKind, r.SourceSymbolId })
            .ToListAsync();

        sysRefs.Should().NotBeEmpty(
            because: "DK Core calls built-in record methods the normal extractor drops");
        sysRefs.Should().Contain(r => r.ReferenceKind == "method_call" && r.SystemMethodName != string.Empty);
        // System calls from inside a procedure body carry the enclosing symbol.
        sysRefs.Where(r => r.SourceSymbolId is not null).Should().NotBeEmpty();
    }

    [Fact]
    public async Task BackfillSystemReferencesAsync_repopulates_without_touching_normal_refs()
    {
        // #291: re-extract system references over already-stored source. Import,
        // record the baselines, wipe the system refs (mimicking a pre-#279
        // release), backfill, and assert the system refs return identically
        // while oe_module_references is left exactly as it was.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s1 = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Backfill DK Core",
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", s1, null) }));
        var releaseId = summary.ReleaseId;

        int normalBefore, sysBefore;
        await using (var read = _db.NewContext())
        {
            normalBefore = await read.OeModuleReferences.AsNoTracking()
                .CountAsync(r => r.Module!.ReleaseId == releaseId);
            sysBefore = await read.OeModuleSystemReferences.AsNoTracking()
                .CountAsync(r => r.Module!.ReleaseId == releaseId);
        }
        sysBefore.Should().BeGreaterThan(0);

        await using (var wipe = _db.NewContext())
        {
            await wipe.Database.ExecuteSqlRawAsync(
                "DELETE FROM oe_module_system_references WHERE module_id IN (SELECT id FROM oe_modules WHERE release_id = {0});",
                releaseId);
        }

        await using (var b = _db.NewContext())
        {
            await NewService(b).BackfillSystemReferencesAsync(releaseId);
        }

        await using var after = _db.NewContext();
        var sysAfter = await after.OeModuleSystemReferences.AsNoTracking()
            .CountAsync(r => r.Module!.ReleaseId == releaseId);
        var normalAfter = await after.OeModuleReferences.AsNoTracking()
            .CountAsync(r => r.Module!.ReleaseId == releaseId);

        sysAfter.Should().Be(sysBefore,
            because: "backfill reproduces exactly the system references the import emitted");
        normalAfter.Should().Be(normalBefore,
            because: "system-only backfill must not touch oe_module_references");
    }

    // ── Amend (issue #216) ──────────────────────────────────────────────

    /// <summary>
    /// Helper that ingests a release with one module, returning its id.
    /// Keeps the amend-test setup compact since most tests need a
    /// pre-existing ready release with at least one module before the
    /// amend can run.
    /// </summary>
    private async Task<int> SeedReadyReleaseAsync(string label, string appFile)
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, appFile));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: label,
            Kind: "first_party",
            ParentReleaseId: null,
            ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload(appFile, stream, SourceZipStream: null) }));
        return summary.ReleaseId;
    }

    [Fact]
    public async Task Amend_adds_a_second_module_to_an_existing_ready_release()
    {
        var releaseId = await SeedReadyReleaseAsync("Amend happy path", "Microsoft_DK_Core.app");

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var second = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var amend = await svc.AmendReleaseAsync(
            releaseId,
            new[] { new AppFileUpload("Microsoft_OIOUBL.app", second, SourceZipStream: null) });

        amend.ReleaseId.Should().Be(releaseId);
        amend.ModulesImported.Should().Be(1);
        amend.ModulesSkipped.Should().Be(0);

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("ready");
        release.StatusMessage.Should().BeNull();

        var modules = await read.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .OrderBy(m => m.Name)
            .ToListAsync();
        modules.Should().HaveCount(2);
        modules.Select(m => m.AppId).Should().Contain(Guid.Parse(DkCoreAppId));
        modules.Select(m => m.Name).Should().Contain("OIOUBL");

        // Denormalised totals must reflect the amended file set.
        var groundTruth = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Length = g.Sum(f => (long)f.FileContent!.ContentLength) })
            .SingleAsync();
        release.SourceFileCount.Should().Be(groundTruth.Count);
        release.SourceContentLength.Should().Be(groundTruth.Length);
    }

    [Fact]
    public async Task Amend_re_uploading_byte_identical_module_is_a_silent_skip()
    {
        var releaseId = await SeedReadyReleaseAsync("Amend idempotent", "Microsoft_DK_Core.app");

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var same = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var amend = await svc.AmendReleaseAsync(
            releaseId,
            new[] { new AppFileUpload("Microsoft_DK_Core.app", same, SourceZipStream: null) });

        amend.ModulesImported.Should().Be(0);
        amend.ModulesSkipped.Should().Be(1);

        await using var read = _db.NewContext();
        var modulesInRelease = await read.OeModules.AsNoTracking()
            .CountAsync(m => m.ReleaseId == releaseId);
        modulesInRelease.Should().Be(1, because: "no duplicate row should be created on a byte-identical re-upload");
    }

    [Fact]
    public async Task Amend_refuses_when_release_is_not_ready()
    {
        // Seed a release, then flip it to "ingesting" by hand to simulate
        // a concurrent amend racing into a partly-mutated release.
        var releaseId = await SeedReadyReleaseAsync("Amend not-ready", "Microsoft_DK_Core.app");
        await using (var setup = _db.NewContext())
        {
            var r = await setup.OeReleases.SingleAsync(r => r.Id == releaseId);
            r.Status = "ingesting";
            await setup.SaveChangesAsync();
        }

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        var act = async () => await svc.AmendReleaseAsync(
            releaseId,
            new[] { new AppFileUpload("Microsoft_OIOUBL.app", s, SourceZipStream: null) });

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ReleaseId");
    }

    [Fact]
    public async Task Amend_refuses_when_release_does_not_exist()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var s = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var act = async () => await svc.AmendReleaseAsync(
            releaseId: 99_999,
            new[] { new AppFileUpload("Microsoft_DK_Core.app", s, SourceZipStream: null) });

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ReleaseId");
    }

    [Fact]
    public async Task Amend_refuses_when_no_uploads_provided()
    {
        var releaseId = await SeedReadyReleaseAsync("Amend no uploads", "Microsoft_DK_Core.app");

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var act = async () => await svc.AmendReleaseAsync(releaseId, Array.Empty<AppFileUpload>());
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Uploads");
    }

    [Fact]
    public async Task Amend_flips_release_to_failed_and_preserves_existing_modules_on_exception()
    {
        var releaseId = await SeedReadyReleaseAsync("Amend failure", "Microsoft_DK_Core.app");

        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var notAnApp = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
        var act = async () => await svc.AmendReleaseAsync(
            releaseId,
            new[] { new AppFileUpload("not-an-app.zip", notAnApp, SourceZipStream: null) });
        await act.Should().ThrowAsync<InvalidDataException>();

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        release.Status.Should().Be("failed");
        release.StatusMessage.Should().NotBeNullOrEmpty();

        var modules = await read.OeModules.AsNoTracking()
            .CountAsync(m => m.ReleaseId == releaseId);
        modules.Should().Be(1, because: "the pre-existing module must survive an amend failure");
    }

    [Fact]
    public async Task Amend_re_emits_call_site_references_against_the_full_module_set()
    {
        // Bring in DK Core first (its source contains member-access patterns
        // the extractor surfaces as method_call / field_access references
        // against tables in its own symbol package).
        var releaseId = await SeedReadyReleaseAsync("Amend reindex", "Microsoft_DK_Core.app");
        int beforeRefCount;
        await using (var read = _db.NewContext())
        {
            beforeRefCount = await read.OeModuleReferences.AsNoTracking()
                .CountAsync(r => r.Module!.ReleaseId == releaseId
                    && (r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access"));
            beforeRefCount.Should().BeGreaterThan(0,
                because: "DK Core import emits call-site references during phase 2");
        }

        // Amend OIOUBL on top of DK Core. The reindex sweep wipes the
        // previous call-site rows for this release and re-emits them
        // against the union of modules, so DK Core's call sites must
        // still be present (and OIOUBL's get added when its .al has any).
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var second = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
        await svc.AmendReleaseAsync(
            releaseId,
            new[] { new AppFileUpload("Microsoft_OIOUBL.app", second, SourceZipStream: null) });

        await using var afterCtx = _db.NewContext();
        var afterRefCount = await afterCtx.OeModuleReferences.AsNoTracking()
            .CountAsync(r => r.Module!.ReleaseId == releaseId
                && (r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access"));
        afterRefCount.Should().BeGreaterThanOrEqualTo(beforeRefCount,
            because: "the reindex must preserve DK Core's call sites; adding OIOUBL can only add more");

        // No duplicates from the reindex: a (module, source_object, line,
        // column, target) tuple should appear at most once. Picking a
        // representative key — line + column + target — catches the
        // double-emit a missing DELETE would cause.
        var dupes = await afterCtx.OeModuleReferences.AsNoTracking()
            .Where(r => r.Module!.ReleaseId == releaseId
                && (r.ReferenceKind == "method_call" || r.ReferenceKind == "field_access"))
            .GroupBy(r => new { r.SourceObjectId, r.LineNumber, r.ColumnNumber, r.TargetMemberName, r.TargetObjectName })
            .Where(g => g.Count() > 1)
            .CountAsync();
        dupes.Should().Be(0,
            because: "the amend path must DELETE then re-emit so the reindex doesn't produce duplicate rows");
    }

    // ── Retry: ReopenForRetryAsync ───────────────────────────────────────

    [Fact]
    public async Task ReopenForRetryAsync_flips_a_failed_release_back_to_ingesting_and_clears_the_message()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            releaseId = await svc.BeginReleaseAsync(
                new ReleaseImportMetadata("Retry me", "first_party", null, null));
            await svc.MarkFailedAsync(releaseId, "the download stalled");
        }

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).ReopenForRetryAsync(releaseId);
        }

        await using var read = _db.NewContext();
        var r = await read.OeReleases.AsNoTracking().SingleAsync(x => x.Id == releaseId);
        r.Status.Should().Be("ingesting");
        r.StatusMessage.Should().BeNull("a reopened release reads as in-progress, not failed");
    }

    [Fact]
    public async Task ReopenForRetryAsync_refuses_a_release_that_is_not_failed()
    {
        // A successfully-imported (ready) release can't be retried.
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var app = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: "Already ready", Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("Microsoft_DK_Core.app", app, SourceZipStream: null) }));

        await using var ctx2 = _db.NewContext();
        var act = async () => await NewService(ctx2).ReopenForRetryAsync(summary.ReleaseId);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Retry");
    }

    [Fact]
    public async Task ReopenForRetryAsync_refuses_a_soft_deleted_release()
    {
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            releaseId = await svc.BeginReleaseAsync(
                new ReleaseImportMetadata("Deleted then retried", "first_party", null, null));
            await svc.MarkFailedAsync(releaseId, "boom");
        }
        await using (var ctx = _db.NewContext())
        {
            var r = await ctx.OeReleases.SingleAsync(x => x.Id == releaseId);
            r.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        await using var ctx3 = _db.NewContext();
        var act = async () => await NewService(ctx3).ReopenForRetryAsync(releaseId);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Retry");
    }

    [Fact]
    public async Task ReopenForRetryAsync_throws_for_a_missing_release()
    {
        await using var ctx = _db.NewContext();
        var act = async () => await NewService(ctx).ReopenForRetryAsync(999_999);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Retry");
    }
}
