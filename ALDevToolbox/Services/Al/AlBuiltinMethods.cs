using System;
using System.Collections.Generic;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Names of AL methods + system fields the runtime adds to every value
/// of a given kind. The reference extractor consults this list AFTER
/// failing to resolve a member through the catalog (oe_module_symbols)
/// so calls like <c>Cust.Insert(true)</c>, <c>Cust.SetRange(...)</c>,
/// <c>JObject.Add(...)</c>, <c>SomeText.Contains(...)</c> are quietly
/// skipped instead of inflating the unresolved counter.
///
/// Coverage is the everyday-AL subset. Two canonical references for
/// future updates:
/// <list type="bullet">
///   <item>Microsoft's "Methods (Auto)" library reference at
///     <see href="https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/methods-auto/library"/>
///     — authoritative for every receiver-typed method (Record /
///     Codeunit / Page / Text / Json / …).</item>
///   <item>The AL VS Code extension's highlight grammar at
///     <see href="https://github.com/microsoft/AL/blob/master/highlightjs_al/src/al.js"/>
///     — flat lists for type identifiers (<c>BUILTIN_TYPES_KEYWORDS</c>),
///     statement / operator keywords (<c>NORMAL_KEYWORDS</c>,
///     <c>OPERATOR_KEYWORDS</c>, <c>LITERAL_KEYWORDS</c>) and DSL
///     keywords inside object bodies (<c>METADATA_KEYWORDS</c>).
///     Cross-referenced when bulk-updating the sets below.</item>
/// </list>
/// Extend this list when a real-world import logs an unresolved
/// receiver that's actually a built-in we missed.
///
/// <para><b>EXTENDING WHEN MICROSOFT ADDS NEW METHODS / TYPES:</b></para>
/// <list type="bullet">
///   <item><b>New method on a Record</b> (`Customer.NewMethod()`)
///     → add to <see cref="RecordMethods"/>.</item>
///   <item><b>New system field on Record</b> (`Rec.SystemSomething`)
///     → add to <see cref="RecordSystemFields"/>.</item>
///   <item><b>New method on Codeunit / Page / Report / Xmlport /
///     Query receivers</b> → add to the matching `XxxMethods` set
///     below; the kind-dispatch in <see cref="IsBuiltin"/> already
///     covers every AL object kind.</item>
///   <item><b>New method on Text / List / Dictionary / Json</b>
///     → add to <see cref="TextMethods"/> / <see cref="CollectionMethods"/>
///     / <see cref="JsonMethods"/>.</item>
///   <item><b>New method on an Enum receiver</b>
///     (`MyEnum.Names`, `MyEnum.FromInteger(...)`) → add to
///     <see cref="EnumMethods"/>.</item>
///   <item><b>Method exposed on multiple receivers</b>
///     (`.AsInteger()`, `.HasValue()`, `.Trim()`) → add to
///     <see cref="CommonMethods"/>; checked regardless of receiver.</item>
///   <item><b>New AL system function callable with no receiver</b>
///     (`Message(...)`, `StrSubstNo(...)`, the AL `[Attribute]`-style
///     keywords) → add to <see cref="BareCallableFunctions"/>.</item>
///   <item><b>New AL statement / operator keyword that lexes as an
///     identifier</b> (`if`, `not`, `xor`) → add to
///     <see cref="StatementKeywords"/>.</item>
///   <item><b>New AL declarative-DSL keyword inside an object body</b>
///     (`area`, `group`, `field`, `value`, page/table layout
///     constructs) → add to <see cref="ObjectDslKeywords"/>.</item>
///   <item><b>New AL built-in static API receiver</b>
///     (`CODEUNIT.Run(...)`, `Session.X(...)`, `XmlDocument.Create()`)
///     → add to <see cref="BuiltinStaticReceivers"/>.</item>
///   <item><b>New AL runtime type that variables can be declared
///     as</b> (`Dialog`, `RecordRef`, `HttpClient`, new scalar like
///     `Text` / `Decimal`) → add to <see cref="KnownSystemTypes"/>
///     so chains through variables of that type silence cleanly.</item>
/// </list>
///
/// <para>BC platform virtual-table ids and names live in a separate
/// list at <c>ReleaseImportAllowLists.PlatformVirtualTables</c> — the
/// chain-walker also has a <c>IsPlatformVirtualTableId</c> range
/// check (2000000000..2000000999) in <c>AlReferenceExtractor.cs</c>
/// as a safety net.</para>
///
/// Why a hand-curated allow-list instead of querying Microsoft's
/// system-symbols package: the symbol package isn't shipped in the
/// .app files our import pipeline ingests, and even when it would
/// be available the list churns slowly enough that an explicit set
/// (with the MS doc URL above the table for follow-up) reads more
/// honestly than a "we couldn't resolve" log we'd have to remember
/// to triage.
/// </summary>
public static class AlBuiltinMethods
{
    /// <summary>
    /// Methods callable on a Record value — every table / tableextension
    /// receiver gets them by default. From the BC docs' "Record Methods"
    /// page; covers the everyday CRUD + filter + field-reflection set.
    /// </summary>
    public static readonly HashSet<string> RecordMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // CRUD + cursor.
        "Insert", "Modify", "Delete", "DeleteAll", "ModifyAll", "Rename", "Reset",
        "Get", "GetBySystemId", "Find", "FindFirst", "FindLast", "FindSet", "Next",
        // Filtering / sorting.
        "SetRange", "SetFilter", "GetFilter", "GetFilters", "ClearMarks",
        "SetCurrentKey", "CurrentKey", "CurrentKeyIndex",
        "SetView", "GetView", "SetPosition", "GetPosition",
        "SetAutoCalcFields", "CalcFields", "CalcSums",
        "SetAscending", "GetAscending", "Ascending",
        "SetSecurityFilterOnRespectiveTables",
        "CopyFilter", "CopyFilters", "FilterGroup", "HasFilter",
        "Mark", "MarkedOnly", "Marking",
        "Ascending", "IsTemporary",
        // Field reflection / introspection.
        "FieldCaption", "FieldName", "FieldNo", "FieldExists", "FieldActive",
        "TableCaption", "TableName",
        "TestField", "Validate", "ValidateAll",
        "FieldError",
        // Count / state.
        "IsEmpty", "Count", "CountApprox",
        "GetRangeMin", "GetRangeMax",
        // Copy / transfer.
        "Copy", "TransferFields", "Init", "InitRecord",
        "SetRecFilter", "SetTable",
        "ChangeCompany", "CurrentCompany",
        // Locking / async.
        "LockTable", "ReadIsolation", "ReadPermission", "WritePermission",
        // Misc.
        "AddLoadFields", "AddLink", "GetLink", "RemoveLink",
        // Record-link family + transaction consistency. `HasLinks` /
        // `DeleteLinks` / `CopyLinks` round out the AddLink/GetLink set;
        // `Consistent([Boolean])` flags a multi-table posting batch as
        // (in)consistent. All are AL-runtime Record methods, never in
        // the catalog.
        "HasLinks", "DeleteLinks", "CopyLinks", "Consistent",
        "Number", "RecordLevelLocking",
        "RecordId", "GetView", "SetView",
        "Caption", "CaptionClass",
        // BC 18+ — partial-record loading.
        "SetLoadFields", "LoadFields",
        // BC 26+ — `SetBaseLoadFields` extends `SetLoadFields` with
        // the base set the caller wants loaded regardless of the
        // filter narrowing. Same family of partial-record APIs.
        "SetBaseLoadFields",
        // Security filter mode property — `Rec.SecurityFiltering(SecurityFilter::Filtered)`.
        // Also valid as a bare implicit-Rec call from inside a table
        // body; the bare-self-call resolver picks it up through this
        // entry too.
        "SecurityFiltering",
        // Note: AssistEdit / Lookup / Drilldown are intentionally NOT
        // listed here. Microsoft's Base App declares user procedures
        // with those names (e.g. Sales Header.AssistEdit is a real
        // public procedure that opens a number-series picker, not the
        // field-level built-in). Adding them as built-ins would silently
        // mask resolver misses instead of bumping the unresolved
        // counter — defeating the diagnostic signal we use to spot
        // catalog gaps.
    };

    /// <summary>
    /// System-added fields exposed on every Record. These don't appear
    /// in <c>oe_module_symbols</c> but a <c>Cust."SystemId"</c> access
    /// shouldn't count as unresolved.
    /// </summary>
    /// <summary>
    /// Subset of <see cref="RecordMethods"/> that takes one or more
    /// field-name arguments (as bare or quoted identifiers, e.g.
    /// <c>Rec.SetRange("No.", '...')</c> or
    /// <c>Item.FieldNo("Qty. on Assembly Order")</c>). When the chain
    /// walker sees one of these called on a record receiver, it sets
    /// <see cref="AlExtractionState.CurrentFieldReceiver"/> for the
    /// duration of the parens so bare identifiers inside resolve as
    /// field accesses on that receiver — otherwise they'd fall
    /// through to no-emit (no chain head, no Rec in scope for a
    /// codeunit, etc.) and Find references on a tableextension-
    /// declared field wouldn't pick them up.
    ///
    /// Some entries here (<c>CalcFields</c>) take MULTIPLE field
    /// names; the field-receiver context applies to the whole arg
    /// list, not just the first arg. False positives only fire if a
    /// non-field arg happens to lex as an identifier matching a
    /// field name on the receiver — vanishingly rare in practice and
    /// always silenced by the catalog lookup miss when it doesn't.
    /// </summary>
    public static readonly HashSet<string> FieldNameTakingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Validate", "ValidateAll",
        "ModifyAll",
        "SetRange", "SetFilter",
        "FieldNo", "FieldName", "FieldCaption",
        "FieldExists", "FieldActive", "FieldError",
        "TestField",
        "CalcFields", "CalcSums",
        "SetCurrentKey", "SetAscending",
        "GetFilter", "GetFilters", "GetRangeMin", "GetRangeMax",
        "AddLoadFields", "SetLoadFields",
    };

    public static readonly HashSet<string> RecordSystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemId",
        "SystemCreatedAt",
        "SystemCreatedBy",
        "SystemModifiedAt",
        "SystemModifiedBy",
        "SystemRowVersion",
    };

    /// <summary>
    /// Methods callable on a Codeunit instance — primarily the Run
    /// family. Codeunit-declared methods that the user defined come
    /// from <c>oe_module_symbols</c>; this list is the runtime layer.
    /// </summary>
    public static readonly HashSet<string> CodeunitMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run", "RunModal", "RunWithCheck",
    };

    /// <summary>
    /// Methods callable on a Page instance — `SomePage.RunModal()`,
    /// `SomePage.SetRecord(Rec)`, `SomePage.GetRecord(Rec)`. The
    /// page-instance methods are AL-runtime, not user-declared, so
    /// they never appear in oe_module_symbols.
    /// </summary>
    public static readonly HashSet<string> PageMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run", "RunModal", "SetRecord", "GetRecord",
        "SetTableView", "GetTableView", "SetSelectionFilter",
        "Editable", "Update", "Close", "SaveRecord",
        "Caption",
        // Lookup-mode dispatch (set by callers before invoking RunModal
        // to make the page act as a lookup picker).
        "LookupMode", "SetLookupMode", "GetLookupMode",
    };

    /// <summary>
    /// Methods callable on a Report instance — `SomeReport.RunModal()`,
    /// `SomeReport.SaveAs(...)`.
    /// </summary>
    public static readonly HashSet<string> ReportMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run", "RunModal", "SaveAs", "SaveAsPdf", "SaveAsExcel",
        "SaveAsHtml", "SaveAsWord", "SaveAsXml",
        "SetTableView", "GetTableView",
        "UseRequestPage", "UseSystemPrinter",
        // `MyReport.Execute()` — BC 26+ replacement for the Run/RunModal
        // pair when the caller wants the rendered output without a UI.
        "Execute",
        // `MyReport.RunRequestPage()` — runs only the request page
        // (returns the user's filter selections as XML) without
        // executing the report body. Base App uses it from
        // RegisteredWhseMovements / RegisteredWhsePicks / etc. pages
        // to gather filters before passing them to a deletion routine.
        "RunRequestPage",
    };

    /// <summary>
    /// Methods callable on an Xmlport instance — `SomeXmlport.Import()`,
    /// `SomeXmlport.SetSource(...)`.
    /// </summary>
    public static readonly HashSet<string> XmlportMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run", "RunModal", "Import", "Export",
        "SetSource", "SetDestination", "GetSource",
        "SetTableView", "GetTableView",
        // `MyXmlport.ImportFile()` — runtime helper that pops up the
        // platform's import dialog. Used by `CAL Test Management` for
        // each of CAL Test Coverage Map / CAL Export Test Result /
        // CAL Import Enabled Codeunit.
        "ImportFile",
        // Property accessors on xmlport instances — `FIELDDELIMITER`,
        // `FIELDSEPARATOR`, `TEXTENCODING`, `USEREQUESTPAGE` etc. Used
        // by base-app code that builds an xmlport at runtime to set
        // these from a config table before .Import / .Export.
        "FIELDDELIMITER", "FIELDSEPARATOR", "TEXTENCODING",
        "USEREQUESTPAGE", "USEEXTERNALSCHEMA", "FILENAME",
        "FormatFieldsForBC14_X",
    };

    /// <summary>
    /// Methods callable on a Query instance — `SomeQuery.Open()`,
    /// `SomeQuery.Read()`.
    /// </summary>
    public static readonly HashSet<string> QueryMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open", "Read", "Close",
        "SetRange", "SetFilter",
        "TopNumberOfRows", "SaveAsCsv", "SaveAsXml",
    };

    /// <summary>
    /// Methods exposed on a Text receiver — <c>someText.Contains(...)</c>.
    /// AL accepts these as instance methods on Text variables.
    /// </summary>
    public static readonly HashSet<string> TextMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Contains", "StartsWith", "EndsWith",
        "IndexOf", "IndexOfAny", "LastIndexOf",
        "Substring", "Split", "Replace",
        "ToLower", "ToUpper",
        "Trim", "TrimEnd", "TrimStart",
        "PadLeft", "PadRight",
        "Length",
    };

    /// <summary>
    /// Methods on the AL JSON value family (JsonObject / JsonArray /
    /// JsonToken / JsonValue). Common enough to land here even though
    /// these aren't AL "object kinds" — variables typed as JsonObject
    /// etc. still pass through the extractor.
    /// </summary>
    public static readonly HashSet<string> JsonMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // JsonObject.
        "Add", "Get", "Contains", "Remove", "Replace", "Keys", "Values",
        // JsonArray.
        "AddFirst", "AddLast", "AsArray",
        // JsonToken / JsonValue.
        "AsValue", "AsObject", "AsToken",
        "AsBoolean", "AsBigInteger", "AsDecimal", "AsDuration", "AsInteger",
        "AsText", "AsCode", "AsDate", "AsTime", "AsDateTime", "AsGuid",
        "IsArray", "IsObject", "IsValue", "IsNull",
        "ReadFrom", "WriteTo",
        "SelectToken", "SelectValue",
        // Shared.
        "Count",
    };

    /// <summary>
    /// Methods on the List / Dictionary collection generics. Users
    /// declare <c>List of [Text]</c>, <c>Dictionary of [Code[20],
    /// Integer]</c> — the receiver type doesn't resolve to an AL
    /// object, but the method calls still pass through the chain.
    /// </summary>
    public static readonly HashSet<string> CollectionMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // List.
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveRange",
        "Get", "GetRange", "Set",
        "IndexOf", "LastIndexOf",
        "Contains",
        "Reverse", "Count", "ToArray",
        // Dictionary.
        "ContainsKey", "Keys", "Values",
        // Shared.
        "Clear",
    };

    /// <summary>
    /// Methods exposed on an Enum / EnumExtension receiver — variables
    /// typed as <c>Enum "Feature To Update"</c> get these in addition
    /// to the per-enum values. They're not declared on the enum object
    /// itself, so the catalog can't resolve them; the chain walker
    /// consults this list when the receiver kind is <c>enum</c> /
    /// <c>enumextension</c>.
    ///
    /// <para>Canonical reference: the BC "Enum Methods" / "Enum Data
    /// Type" docs (Names, Ordinals, GetValueAt, FromInteger). The
    /// <c>AsInteger</c> overload lives in <see cref="CommonMethods"/>
    /// because Option / Variant / record-bound enum fields share it.</para>
    /// </summary>
    public static readonly HashSet<string> EnumMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reflection over the enum's value set. Names / Ordinals return
        // List of [Text] / List of [Integer] respectively; downstream
        // .IndexOf / .Get / .Contains land in CollectionMethods.
        "Names", "Ordinals",
        // Lookup by ordinal — returns the enum value.
        "GetValueAt", "FromInteger",
        // Per-value caption (BC 23+).
        "Caption",
    };

    /// <summary>
    /// The subset of <see cref="EnumMethods"/> invoked on the enum <em>type</em>
    /// itself (reflection over the value set) rather than on an enum value.
    /// These are unambiguously enum-static, so the chain walker treats them as
    /// built-ins even when a type-literal head resolved to a <em>same-named</em>
    /// table / interface instead of the enum. BC ships colliding names — e.g.
    /// <c>enum "Ext. File Storage Connector"</c> beside a same-named table, and
    /// <c>enum "Email Connector"</c> beside <c>interface "Email Connector"</c> —
    /// so <c>"X".Ordinals()</c> otherwise strands as a <c>chain-step</c>
    /// unresolved against the wrong object kind.
    /// </summary>
    public static readonly HashSet<string> EnumStaticMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Names", "Ordinals", "GetValueAt", "FromInteger",
    };

    /// <summary>True when <paramref name="memberName"/> is an enum reflection static (<see cref="EnumStaticMethods"/>).</summary>
    public static bool IsEnumStaticMethod(string memberName) => EnumStaticMethods.Contains(memberName);

    /// <summary>
    /// Catch-all: a name we treat as built-in regardless of receiver
    /// kind. Add sparingly — these get filtered everywhere they
    /// appear in a chain.
    /// </summary>
    public static readonly HashSet<string> CommonMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Lots of types expose a Length() / Count() — Text, List,
        // JsonArray, etc.
        "Length",
        "Count",
        // Type-conversion methods on Option / Enum / Variant fields.
        // The chain walker doesn't track field types through to enums,
        // so receiver-typed dispatch can't see these; treating them as
        // common-builtin silences `.AsInteger()` on any record-bound
        // field that happens to be an Option/Enum.
        "AsInteger", "AsBoolean", "AsText", "AsCode", "AsDecimal",
        "AsDateTime", "AsDate", "AsTime", "AsDuration", "AsGuid",
        "AsBigInteger",
        // Inverse conversion — `MyRec."Action"::FromInteger(N)` /
        // `MyEnum.FromInteger(N)` builds an enum value from an int.
        // Same rationale as AsInteger — chain walker doesn't track
        // enum types on field receivers.
        "FromInteger",
        // Variant / InStream introspection.
        "HasValue", "IsValue", "IsArray", "IsObject", "IsNull",
        // Text-shape methods exposed on multiple receivers.
        "Trim", "TrimStart", "TrimEnd", "Unwrap",
        "Split", "Replace", "Substring",
        // DateTime decomposition. `CurrentDateTime().Date()` and
        // `.Time()` extract the components — also exposed on Variant
        // (after `.HasValue()` checks) and DateFormula. Bare-call
        // form silences only after own-member resolution misses, so
        // a user procedure named `Date` on the owner still wins.
        "Date", "Time",
        // Primitive-to-text conversion. Exposed on every scalar type
        // (Integer / DateTime / Decimal / Guid / Code / Boolean): the
        // canonical `MyInteger.ToText()` / `SystemCreatedAt.ToText()`
        // shape in Base App's report-layout dialogs. Same rationale as
        // `AsInteger` above — we don't track scalar receiver types
        // through the chain, so receiver-typed dispatch can't see it.
        "ToText",
        // Stream / blob primitives (TempBlob, File, InStream, OutStream).
        "CreateInStream", "CreateOutStream",
        "ReadText", "WriteText", "Read", "Write", "EOS",
    };

    /// <summary>
    /// AL system functions callable without a receiver: <c>Message('Hello')</c>,
    /// <c>Error('Boom')</c>, <c>StrSubstNo(...)</c> etc. The bare-self-call
    /// resolver consults this list first so it doesn't try to look these up
    /// as procedures on the file's owner object — a same-named user procedure
    /// would shadow the system function in AL, but in practice Microsoft's
    /// code doesn't, and treating the system function as the dominant case
    /// stays quiet across the imported corpus.
    ///
    /// Coverage is the high-frequency subset of
    /// <see href="https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/developer/methods-auto/library"/>'s
    /// "Essential functions". Extend when a real import logs a false-positive
    /// unresolved self-call for a name that's actually a system function.
    /// </summary>
    public static readonly HashSet<string> BareCallableFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Diagnostic / flow.
        "Message", "Error", "Confirm", "Exit", "Quit", "Sleep",
        "GetLastErrorText", "GetLastErrorCode", "GetLastErrorObject",
        "GetLastErrorCallStack", "ClearLastError", "AssertError",
        // Collected-errors API (BC 22+ try-collect pattern). Added
        // alongside the existing error helpers so a bare
        // `HasCollectedErrors()` / `GetCollectedErrors()` etc. doesn't
        // fire as a self-method.
        "ClearCollectedErrors", "GetCollectedErrors",
        "HasCollectedErrors", "IsCollectingErrors",
        // Dialog system functions surfaced as bare. StrMenu is the
        // common one — pops a Choose-an-option modal.
        "StrMenu", "HideSubsequentDialogs", "LogInternalError",
        // Strings.
        "Format", "Evaluate", "StrLen", "StrSubstNo", "StrPos", "StrCheckSum",
        "CopyStr", "DelChr", "DelStr", "InsStr", "MaxStrLen", "IncStr",
        "LowerCase", "UpperCase", "ConvertStr", "SelectStr",
        "PadStr",
        // Secret-text variant of StrSubstNo (BC 22+).
        "SecretStrSubstNo",
        // SecretText instance methods (Unwrap is also in CommonMethods).
        "IsEmpty",
        // Numerics.
        "Abs", "Power", "Sqrt", "Round", "Random", "RandomRange", "Randomize",
        // Date / time.
        "Today", "Time", "CurrentDateTime", "WorkDate", "CreateDateTime",
        "Date2DMY", "Date2DWY", "DMY2Date", "DWY2Date", "CalcDate",
        "ClosingDate", "NormalDate",
        "DT2Date", "DT2Time", "DaTi2Variant", "Variant2Date",
        // Identity / environment.
        "CreateGuid", "CompanyName", "UserId", "UserSecurityId",
        "GuiAllowed", "IsServiceTier", "ApplicationLanguage",
        "GlobalLanguage", "WindowsLanguage",
        "TenantId", "SID", "ServiceInstanceId", "SessionId",
        "ApplicationIdentifier", "ApplicationPath", "TemporaryPath",
        "SerialNumber",
        // Encryption.
        "Encrypt", "Decrypt", "EncryptionEnabled", "EncryptionKeyExists",
        "CreateEncryptionKey", "DeleteEncryptionKey",
        "ExportEncryptionKey", "ImportEncryptionKey",
        // Database admin / connections.
        "AlterKey", "ChangeUserPassword", "CheckLicenseFile",
        "CopyCompany", "CurrentTransactionType",
        "DataFileInformation", "ExportData", "ImportData",
        "GetDefaultTableConnection", "HasTableConnection",
        "RegisterTableConnection", "SetDefaultTableConnection",
        "UnregisterTableConnection",
        "IsInWriteTransaction", "LastUsedRowVersion", "MinimumActiveRowVersion",
        "LockTimeout", "LockTimeoutDuration",
        "SelectLatestVersion", "SetUserPassword",
        // Session control / telemetry.
        "ApplicationArea", "CurrentClientType", "CurrentExecutionMode",
        "DefaultClientType", "EnableVerboseTelemetry",
        "GetCurrentModuleExecutionContext", "GetExecutionContext",
        "GetModuleExecutionContext", "IsSessionActive",
        "LogAuditMessage", "LogMessage", "LogSecurityAudit",
        "SendTraceTag", "SetDocumentServiceToken",
        // Array / arg helpers.
        "ArrayLen", "CompressArray", "CopyArray", "CopyStream",
        "CanLoadType", "CaptionClassTranslate",
        // Code-coverage runtime API.
        "CodeCoverageInclude", "CodeCoverageLoad", "CodeCoverageLog",
        "CodeCoverageRefresh",
        // Object import / export.
        "ExportObjects", "ImportObjects",
        // URL helpers.
        "GetDocumentUrl", "GetDotNetType", "GetUrl",
        "ImportStreamWithUrlAccess",
        // File system functions. The bare-callable check now runs
        // AFTER own-member catalog resolution in
        // <see cref="AlProcedureWalker.TryConsumeBareSelfCall"/>, so a
        // user procedure named the same as one of these (e.g. a
        // <c>procedure Exists()</c> on Persistent Blob Impl. or
        // Guided Experience Impl.) wins through the catalog and only
        // bare calls without a matching own-member silence here.
        // That's the AL-correct resolution order — user procedures
        // shadow same-named system functions.
        //
        // Coverage: `Exists` is the deprecated AL file-existence
        // global (`if Exists(FileName) then ...`) used across
        // PositivePayExportMgt, Attachment.Table, ExcelBuffer.Table
        // and similar legacy file-system code. Other distinctive
        // file globals (CreateTempFile / Download / Upload / etc.)
        // were already silent. Common-name receiver methods (Open /
        // Close / Read / Write / Erase / Copy / Create / Rename /
        // Len / Name / Pos / Seek / Trunc / TextMode / WriteMode /
        // View) stay out — they're handled by the per-receiver
        // method sets (CommonMethods etc.) on actual chain calls,
        // and a bare call to one of those names would more often
        // be a mis-parsed chain than a deprecated system function.
        "Exists", "Erase",
        "CreateTempFile", "Download", "DownloadFromStream",
        "Upload", "UploadIntoStream",
        "GetStamp", "SetStamp", "IsPathTemporary",
        "ViewFromStream",
        // Misc.
        "TypeNameOf", "Database",
        // Variable lifecycle.
        "Clear", "ClearAll",
        // Numeric / type predicates that take an arg with no receiver.
        "IsNull", "IsNullGuid",
        // Cast / format helpers.
        "Increment", "Decrement",
        // Transaction control — top-level system function.
        "Commit",
        // Event subscription binding.
        "BindSubscription", "UnbindSubscription",
        // Navigation / UI.
        "Hyperlink",
        // Date / time helpers also exposed as bare functions.
        "RoundDateTime", "Time2Variant", "Variant2Time",
        // Background session control.
        "StartSession", "StopSession",
        // AL property-value constructors. Appear inside property values
        // like `TableRelation = Customer."No." where(Blocked = const(false))`
        // or `SubPageLink = "No." = field("No.")` or
        // `SourceTableView = sorting("No.") order(ascending) where(Type = const(Item))`.
        // They look like calls but introduce filter / constant / sort /
        // field-binding expressions.
        "const", "filter", "where", "upperlimit", "sorting", "order",
        // Compiler attributes that lex as `[Identifier(...)]` and
        // surface as bare-call shapes inside square brackets. Treat
        // as no-op bare callables so they don't pollute the diagnostic.
        // EventSubscriber is intentionally NOT here — it has dedicated
        // extraction (TryConsumeEventSubscriber) that emits publisher
        // bindings.
        "Scope", "NonDebuggable", "Obsolete", "InherentEntitlements",
        "InherentPermissions", "IntegrationEvent", "BusinessEvent",
        "InternalEvent", "TryFunction", "ExternalBusinessEvent",
        "HandlerFunctions", "TransactionModel", "TestPermissions",
        "Test",
    };

    /// <summary>
    /// AL <b>declarative-DSL</b> keywords that introduce nested
    /// structure inside an object body but are NOT procedure calls.
    /// Pages, pageextensions, tableextensions, reports, xmlports,
    /// enums and permissionsets all use this `keyword(args) { ... }`
    /// syntax for their layout / fields / actions / values.
    ///
    /// Without explicit handling these surface as <c>bare-call</c>
    /// unresolveds — `area(content)` inside a page body looks
    /// identical to a procedure invocation to the lexer. The
    /// extractor skips them silently (no reference emitted, no
    /// counter bump) so the diagnostic samples can focus on real
    /// gaps.
    ///
    /// Coverage note: <c>field</c>, <c>trigger</c>, <c>group</c>,
    /// <c>value</c> overlap with names that legitimately exist as
    /// AL identifiers in other contexts (a procedure named <c>group</c>
    /// is rare but legal). The skip is unconditional here because
    /// the matching call sites are always in declarative position;
    /// false positives would need a procedure named after a DSL
    /// keyword AND invoked from a place the bare-call resolver runs
    /// — vanishingly rare in practice.
    /// </summary>
    public static readonly HashSet<string> ObjectDslKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pages / pageextensions: layout containers and content.
        "area", "group", "field", "repeater", "cuegroup", "part",
        "systempart", "usercontrol", "fixed", "grid", "label",
        "separator", "filter",
        // Pages / pageextensions: actions.
        "action", "actionref", "customaction", "actiongroup",
        "systemaction", "fileuploadaction",
        // Pageextensions: layout / action manipulators.
        "modify", "add", "addafter", "addbefore", "addlast", "addfirst",
        "movefirst", "movebefore", "moveafter", "movelast",
        "addchange",
        // Tables / tableextensions: structure.
        "key", "fieldgroup",
        // Reports: structure.
        "dataitem", "column", "requestpage", "dataset", "rendering",
        "layout",
        // XMLports: schema.
        "textelement", "tableelement", "fieldelement", "fieldattribute",
        "textattribute",
        // Enums.
        "value",
        // PermissionSets.
        "permissions", "tabledata", "includedpermissionsets",
        // Controladd-ins, queries, profiles — declarative children.
        "controladdin", "querytype", "elements", "filters", "orderby",
        "dataitemlink", "column",
        // Page / pageextension `view(...)` blocks — declarative
        // filter / sort presets on listpage actions. `descending`
        // appears in query `column` decoration as a sort direction
        // (looks like a function call to the lexer).
        "view", "views", "descending", "ascending",
        // BC 28 list-page analytics: pages declare an
        // `analysisviews { analysisview("Name") { ... } }` block
        // pointing at a JSON definition file. Same shape as
        // `views { view(...) { } }` — declarative, no user code,
        // both the singular and plural form land in bare-self-
        // call dispatch and need silencing.
        "analysisview", "analysisviews",
    };

    /// <summary>
    /// True for an AL declarative-DSL keyword that opens a nested
    /// block inside an object body. See <see cref="ObjectDslKeywords"/>
    /// for the rationale.
    /// </summary>
    public static bool IsObjectDslKeyword(string name) =>
        !string.IsNullOrEmpty(name) && ObjectDslKeywords.Contains(name);

    /// <summary>
    /// AL built-in static APIs callable with no instance — typically
    /// invoked as <c>Kind.Method(...)</c> from anywhere in code.
    /// <c>CODEUNIT.Run(Codeunit::"Foo")</c> and <c>PAGE.RunModal(...)</c>
    /// are the canonical examples; <c>NavApp.GetCurrentModuleInfo(...)</c>
    /// is the equivalent for app metadata. The extractor doesn't model
    /// the methods on these static APIs (no symbol-package entries
    /// for them), so chains through them resolve nothing — but they
    /// shouldn't pollute the diagnostic either.
    /// </summary>
    public static readonly HashSet<string> BuiltinStaticReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Static kind dispatchers. `CODEUNIT.Run(...)`, `PAGE.RunModal(...)`,
        // `REPORT.RunModal(...)`, `XMLPORT.Import(...)`, `QUERY.Open(...)`.
        "CODEUNIT", "PAGE", "REPORT", "XMLPORT", "QUERY", "ENUM",
        // Explicit system-namespace prefix. `SYSTEM.Clear(X)` is the AL
        // disambiguator for system functions when a user procedure of
        // the same name is in scope; same surface as the bare call.
        "SYSTEM",
        // BC application-name accessor. `ProductName.Full()` /
        // `.Marketing()` / `.Short()` return the configured app names.
        "ProductName",
        // App metadata / lifecycle.
        "NavApp",
        // Session-scoped runtime APIs.
        "Session", "TaskScheduler", "TestField",
        // Database identity (`DATABASE::Customer` is a typed-literal
        // expression — handled separately — but `DATABASE.X(...)` would
        // surface here if it appears).
        "DATABASE",
        // Current-object runtime keywords. CurrPage / currXMLport /
        // CurrReport / CurrQuery refer to the currently-running object
        // instance; their methods (Update, Close, Skip, ColumnNo, etc.)
        // aren't in any module's catalog. CurrQuery surfaces inside
        // query OnBeforeOpen / OnAfterGetRecord triggers as
        // `CurrQuery.ColumnNo("X")` / `CurrQuery.Read()` etc.
        "CurrPage", "currXMLport", "CurrReport", "CurrQuery",
        // XML / JSON / encoding primitives also exposed as static
        // factory receivers. `XmlDocument.ReadFrom(...)` /
        // `XmlDocument.Create()` / `XmlElement.Create(...)` create
        // instances; the type name itself is the receiver.
        "XmlDocument", "XmlDeclaration", "XmlElement", "XmlNode",
        "XmlAttribute", "XmlComment", "XmlText", "XmlCData",
        "XmlProcessingInstruction", "XmlNamespaceManager",
        "JsonObject", "JsonArray", "JsonValue", "JsonToken",
        // Cryptography / encoding helpers.
        "Base64Convert", "CryptographyManagement",
        // Scalar built-in types used as static factories / receivers,
        // not as variables: `Version.Create('1.0.0.0')`,
        // `ErrorInfo.Create('msg')`. They're also in KnownSystemTypes
        // (for the variable-typed case); listing them here silences the
        // `Kind.Method(...)` static-call shape too. `SecretText.Empty()`
        // (BC 28+ AI-prompt callsites) and the existing scalar set
        // share the static-receiver shape.
        "Version", "ErrorInfo", "SecretText", "Guid",
        // System types frequently used as static dispatchers without a
        // variable declaration. `Dialog.StrMenu(...)`, `Dialog.Open(...)`,
        // `Text.StrSubstNo(...)`, `Text.Format(...)` are the canonical
        // base-app shapes — the receiver names the type, not an in-scope
        // variable. Without these silences, every static call surfaces
        // as head-not-a-variable (the variable lookup misses and the
        // catalog has no "Dialog" / "Text" object).
        "Dialog", "Text",
        // AL runtime static APIs surfaced as `<Name>.<Method>(...)`:
        //   IsolatedStorage.Set / .Get / .SetEncrypted / .Delete — the
        //     per-app secret store used across BC's connectors and
        //     authenticators.
        //   NumberSequence.Insert / .Current / .Next — the platform's
        //     gap-less number-sequence service.
        //   MediaSet.FindOrphans — the System Application's media
        //     cleanup helper.
        // All three resolve at runtime through the AL kernel rather
        // than the catalog, so chain heads through them should
        // silence cleanly.
        "IsolatedStorage", "NumberSequence", "MediaSet",
        // Top-level AL namespaces. Fully-qualified type references
        // like `Microsoft.Service.Document."Service Line Type".FromInteger(Type)`
        // surface as a chain head starting with `Microsoft`. The
        // existing static-receiver walk advances through the dotted
        // namespace path, through the final quoted type name, and
        // past any method-call parens — silencing the whole chain.
        // Note: BC ships `Microsoft` as its top-level publisher
        // namespace; `System` is already in this list above as the
        // dispatcher shorthand for system functions, which doubles
        // as the .NET System namespace prefix.
        "Microsoft",
        // Report runtime — `RequestOptionsPage.Update(false)` from
        // inside a report's procedures forces the request page UI to
        // refresh. The receiver name is the static keyword, not a
        // variable.
        "RequestOptionsPage",
        // Upgrade-codeunit runtime APIs. `HybridDeployment.VerifyCanStartUpgrade()`
        // gates company-replicated-data upgrades; `UpgradeTag.HasUpgradeTag(...)` /
        // `.SetUpgradeTag(...)` / `.SetAllUpgradeTags(...)` is the bookmark
        // mechanism BC's upgrade codeunits use to skip already-run blocks.
        // Both surface as static receivers across every upgrade .al file.
        "HybridDeployment", "UpgradeTag",
        // Session-scoped diagnostics. `SessionInformation.ServerInstanceName()`,
        // `.TenantId`, `.UserId`, … are read from many places without a
        // variable declaration.
        "SessionInformation",
        // Legacy company-property accessor — `COMPANYPROPERTY.DISPLAYNAME()`,
        // `.PICTURE()`, etc. A system receiver, never a catalog object.
        "COMPANYPROPERTY",
    };

    /// <summary>
    /// True when <paramref name="name"/> is an AL built-in static API
    /// head — chains rooted here are AL-runtime, not user code, and
    /// shouldn't be diagnosed as unresolved variables.
    /// </summary>
    public static bool IsBuiltinStaticReceiver(string name) =>
        !string.IsNullOrEmpty(name) && BuiltinStaticReceivers.Contains(name);

    /// <summary>
    /// AL built-in / system types that won't ever resolve through
    /// the catalog — they're either runtime primitives
    /// (<c>Dialog</c>, <c>RecordRef</c>, <c>RecordId</c>,
    /// <c>FieldRef</c>, <c>Variant</c>), XML / JSON primitives
    /// (<c>XmlDocument</c>, <c>XmlElement</c>, <c>JsonObject</c>),
    /// I/O primitives (<c>InStream</c>, <c>OutStream</c>, <c>File</c>),
    /// HTTP (<c>HttpClient</c>, <c>HttpRequestMessage</c>), or app
    /// metadata (<c>ModuleInfo</c>). A variable typed as one of these
    /// is legitimate but its chain steps (e.g. <c>Dialog.Open(...)</c>)
    /// can't be resolved through our catalog, so silence the
    /// diagnostic.
    /// </summary>
    /// <para>Canonical reference: the AL extension's highlight-grammar
    /// keeps the full list of built-in type identifiers at
    /// <see href="https://github.com/microsoft/AL/blob/master/highlightjs_al/src/al.js"/>
    /// (search for <c>BUILTIN_TYPES_KEYWORDS</c>). When that list
    /// grows in a new BC release, mirror the new entries below.
    /// Note: AL object kind keywords (codeunit, page, table, …) live
    /// in <c>AlReferenceExtractor.IsAlObjectKeyword</c> instead — those
    /// belong to variables whose type names resolve through the
    /// catalog, not to be silenced here.</para>
    public static readonly HashSet<string> KnownSystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Scalar AL types — these surface as variable types when the
        // declared shape is `var X: Text[250]` etc. The chain walker
        // legitimately finds them in scope but the type doesn't
        // resolve through the catalog (they're language primitives,
        // not AL objects). Without silencing, every chain through a
        // Text / Integer / Decimal variable (e.g. `Result.TrimEnd()`,
        // `MyText.Split(' ')`) shows up as head-var-type-unresolved.
        // Length qualifiers like `[250]` are stripped before lookup so
        // `Text[250]` matches `"Text"` here.
        "Text", "Code", "Integer", "Decimal", "Boolean",
        "Date", "Time", "DateTime", "Char", "Byte",
        "BigInteger", "Real", "Option",
        // String-constant scalars.
        "Label", "TextConst", "Blob",
        // Runtime references and variants.
        "Dialog", "RecordRef", "RecordId", "FieldRef", "KeyRef",
        "Variant", "Guid", "DateFormula", "BigText",
        // Secret-handling primitives (BC 22+).
        "SecretText",
        // Web service action context (passed into API page actions).
        "WebServiceActionContext", "ActionContext",
        // Report / page contexts.
        "ReportAPIType", "ReportFormat",
        // XML / JSON primitives.
        "XmlDocument", "XmlElement", "XmlNode", "XmlNodeList",
        "XmlAttribute", "XmlAttributeCollection", "XmlComment",
        "XmlText", "XmlCData", "XmlDeclaration", "XmlDocumentType",
        "XmlNamespaceManager", "XmlNameTable", "XmlReadOptions",
        "XmlWriteOptions", "XmlProcessingInstruction",
        "JsonObject", "JsonArray", "JsonValue", "JsonToken",
        // I/O.
        "InStream", "OutStream", "File", "TempBlob",
        // Field-level media runtime types. `field(140; Image; Media)`
        // exposes `.ImportFile`, `.ExportFile`, `.HasValue`, etc. on
        // the field value. Without recognising the type name here,
        // `Image.ImportFile(...)` inside Customer.Table.al picked up
        // the System Application's `Codeunit "Image"` via the bare
        // catalog match and fired chain-step on every Media field.
        "Media", "MediaSet",
        // BC 26+ — `FileUpload` is the browser-native upload widget
        // surface. Variables typed `FileUpload` expose `.CurrentFile`,
        // `.SingleFile`, `.UploadIntoStream(...)` etc. via the runtime.
        "FileUpload",
        // HTTP.
        "HttpClient", "HttpRequestMessage", "HttpResponseMessage",
        "HttpHeaders", "HttpContent",
        // App metadata.
        "ModuleInfo", "ModuleDependencyInfo",
        // Data-upgrade infrastructure. `DataTransfer` powers the
        // upgrade-codeunit Field-set-Field shape (`SetSourceTable`,
        // `AddFieldValue`, `CopyRows`, …) introduced in BC 22+.
        "DataTransfer",
        // .NET interop.
        "DotNet", "DotNetAssembly", "DotNetTypeDeclaration",
        "Automation",
        // Generic collections (built-in generics).
        "List", "Dictionary",
        // Encoding / text / cryptography.
        "TextEncoding", "Encoding", "TextBuilder", "StringBuilder",
        "Base64Convert", "CryptographyManagement",
        // Notification primitives.
        "Notification", "NotificationScope",
        // Filter / view / record-level primitives.
        "FilterPageBuilder", "TableFilter", "SecurityFilter",
        "View", "Views", "AnalysisView", "AnalysisViews",
        // Enum-shaped runtime primitives — variables typed against
        // these legitimately surface as in-scope but can't resolve to
        // an AL object. List mirrors the AL extension's
        // BUILTIN_TYPES_KEYWORDS (see class doc-comment).
        "ClientType", "ConnectionType", "DataClassification",
        "DataScope", "DefaultLayout", "ErrorType",
        "ExecutionContext", "ExecutionMode", "FieldClass",
        "FieldType", "Joker", "ObjectType", "PageResult",
        "SecurityFiltering", "SessionSettings",
        "TableConnectionType", "TransactionModel", "TransactionType",
        "Verbosity", "WebServiceActionResultCode",
        // Test-scaffolding receivers.
        "TestAction", "TestField", "TestFilterField",
        "TestPermissions",
        // Page / chart parts.
        "ChartPart",
        // Misc primitives.
        "Version", "Duration", "ErrorInfo",
    };

    /// <summary>
    /// True when the declared type name is one of the AL runtime /
    /// system types the catalog never tracks — used to silence
    /// the <c>head-var-type-unresolved</c> diagnostic for variables
    /// typed against these primitives. Strips the optional
    /// <c>[N]</c> length qualifier (<c>Text[250]</c>, <c>Code[20]</c>)
    /// before checking so scalar declarations match the bare name in
    /// <see cref="KnownSystemTypes"/>.
    /// </summary>
    public static bool IsKnownSystemType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        // Symbol-package-derived types arrive as `Text[250]` (full
        // string including the length brackets) for scalar fields.
        // The KnownSystemTypes set holds the bare type name; strip
        // the bracket before matching so `Text[250]`, `Code[20]`,
        // `Char[10]` all silence cleanly.
        var bracket = typeName.IndexOf('[');
        var bare = bracket > 0 ? typeName.Substring(0, bracket) : typeName;
        return KnownSystemTypes.Contains(bare);
    }

    /// <summary>
    /// AL statement / operator keywords that lex as <see cref="AlTokenKind.Identifier"/>
    /// and can syntactically appear right before <c>(</c> without being a
    /// procedure call: <c>if (X = 5) then</c>, <c>not (Foo)</c>,
    /// <c>while (i &lt; n) do</c>. The bare-call detector filters these so
    /// it doesn't try to resolve them as self-procedures.
    /// </summary>
    public static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Control flow.
        "if", "then", "else", "elseif",
        "while", "do",
        "repeat", "until",
        "for", "foreach", "to", "downto", "in",
        "case", "of",
        "with",
        "begin", "end",
        "exit", "return", "break",
        // Assertion / error-trap operators.
        "asserterror",
        // Declarations (defensive — the scope walker skips most of these,
        // but parametrised attributes can land here too).
        "var", "procedure", "trigger", "event",
        // Procedure / variable modifiers.
        "local", "internal", "protected", "temporary",
        // Operators that take a parenthesised expression.
        "not", "and", "or", "xor", "mod", "div",
        // Literal-shaped keywords AL exposes.
        "true", "false",
    };

    /// <summary>
    /// True for an AL system function callable with no receiver.
    /// </summary>
    public static bool IsBareCallable(string name) =>
        !string.IsNullOrEmpty(name) && BareCallableFunctions.Contains(name);

    /// <summary>
    /// True for an AL statement / operator keyword that the bare-call
    /// detector must skip so <c>if (X) then</c> doesn't get treated as
    /// a call to <c>if</c>.
    /// </summary>
    public static bool IsStatementKeyword(string name) =>
        !string.IsNullOrEmpty(name) && StatementKeywords.Contains(name);

    /// <summary>
    /// True for the runtime-supplied FIELDS (as opposed to methods) a
    /// record exposes — <c>SystemId</c> and friends. Used to keep a
    /// paren-less built-in classified correctly: <c>Rec.Modify;</c> is a
    /// call, <c>Rec.SystemId</c> is a read. See issue #712.
    /// </summary>
    public static bool IsBuiltinField(string memberName) =>
        !string.IsNullOrEmpty(memberName) && RecordSystemFields.Contains(memberName);

    /// <summary>
    /// True when <paramref name="memberName"/> is a runtime built-in
    /// for the receiver's kind. The reference extractor uses this to
    /// distinguish "real unresolved" (typo / missing import / cross-
    /// release shadowing we don't yet handle) from "we caught a runtime
    /// method that was never going to be in the catalog."
    /// </summary>
    public static bool IsBuiltin(string ownerKind, string memberName)
    {
        if (string.IsNullOrEmpty(memberName)) return false;
        if (CommonMethods.Contains(memberName)) return true;

        // The catalog stores kinds lower-case (table, codeunit, …);
        // be defensive about casing on the caller side.
        return ownerKind?.ToLowerInvariant() switch
        {
            "table" or "tableextension" =>
                RecordMethods.Contains(memberName) || RecordSystemFields.Contains(memberName),
            "codeunit" =>
                CodeunitMethods.Contains(memberName),
            "page" or "pageextension" =>
                PageMethods.Contains(memberName),
            "report" or "reportextension" =>
                ReportMethods.Contains(memberName),
            "xmlport" =>
                XmlportMethods.Contains(memberName),
            "query" =>
                QueryMethods.Contains(memberName),
            "enum" or "enumextension" =>
                EnumMethods.Contains(memberName),
            _ => false,
        };
    }
}
