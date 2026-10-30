using System;
using System.Collections.Generic;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.Al;


/// <summary>
/// Walks procedure and trigger bodies inside an AL source file: scope
/// frames (parameters + locals), member chains (<c>Receiver.Member…</c>
/// and the typed-literal <c>Kind::"Name".Member</c> form), bare
/// self-calls, label uses, implicit-Rec field access, and member
/// dispatch via the resolver.
///
/// The body grammar is owner-kind-agnostic — a procedure body's tokens
/// look the same whether the procedure lives on a Page, a Codeunit, or
/// a Table — so a single walker handles all cases. The companion
/// orchestrator (private <c>Walker</c> nested in
/// <see cref="AlReferenceExtractor"/>) dispatches object-scope DSL
/// constructs and calls into this walker once a body opens.
///
/// State is shared through <see cref="AlExtractionState"/>. The
/// orchestrator passes itself a <see cref="WalkBalancedParens"/>
/// callback (via <see cref="_dispatchOneToken"/>) so the inside of a
/// method-call's argument list re-enters the main dispatcher — keeps
/// nested references like <c>Rec.Validate("X", Customer."No.")</c>
/// emitting through the same code path the main loop uses.
/// </summary>
internal sealed class AlProcedureWalker
{
    private readonly AlExtractionState _state;
    private readonly Action _dispatchOneToken;

    public AlProcedureWalker(AlExtractionState state, Action dispatchOneToken)
    {
        _state = state;
        _dispatchOneToken = dispatchOneToken;
    }

    // ── Scope frames ────────────────────────────────────────────────

    /// <summary>
    /// Constructs and pushes the outermost (object-scope) frame onto
    /// the scope stack. Holds object-scope globals from
    /// <see cref="AlExtractContext.GlobalVars"/> plus the implicit
    /// <c>Rec</c> / <c>xRec</c> binding when the owner kind exposes
    /// one. Called once at the start of <c>Walker.Run</c>.
    /// </summary>
    public void BuildAndPushGlobalScope()
    {
        var frame = new ScopeFrame();
        foreach (var (name, type) in _state.Ctx.GlobalVars)
        {
            frame.Vars[name] = type;
        }
        // Rec / xRec — implicit receivers for record-bearing owners.
        // The exact binding depends on the owner kind:
        //   - table → Rec is the table itself.
        //   - tableextension → Rec is the BASE TABLE (the extension
        //     is conceptually merged into it at runtime). The
        //     importer threads ExtendsObjectName through as
        //     OwnerSourceTableName for tableextensions.
        //   - page / pageextension → Rec is the page's SourceTable
        //     (extensions inherit it from their base page).
        //   - codeunit with TableNo → Rec is the table named by
        //     TableNo. `codeunit "Gen. Jnl.-Post"` with
        //     `TableNo = "Gen. Journal Line"` runs against a
        //     journal-line record, with Rec bound to that table.
        //     Without TableNo, a codeunit has no implicit Rec.
        //   - report / reportextension / xmlport / query / requestpage —
        //     Rec is the owner itself for now; per-dataitem binding
        //     is a follow-up.
        // The importer denormalises SourceTable / TableNo /
        // ExtendsObjectName-of-tableextension onto
        // oe_module_objects.source_table_name; this method reads it
        // back through OwnerSourceTableName.
        var selfType = DetermineRecBinding();
        if (selfType is not null)
        {
            frame.Vars["rec"] = selfType;
            frame.Vars["xrec"] = selfType;
            // CurrFieldRef in field validate triggers, etc., is a
            // FieldRef — not a record — so we don't add it here.
        }
        _state.ScopeStack.Push(frame);
    }

    private ResolvedVariableType? DetermineRecBinding()
    {
        var k = _state.Ctx.OwnerKind?.ToLowerInvariant();

        // Codeunit: Rec only exists when TableNo binds it. Without
        // the binding, a codeunit has no Rec — most non-trigger
        // codeunits fall here, so returning null is correct.
        if (k == "codeunit")
        {
            if (string.IsNullOrEmpty(_state.Ctx.OwnerSourceTableName)) return null;
            return new ResolvedVariableType("Record", _state.Ctx.OwnerSourceTableName);
        }

        // Pages and tableextensions: Rec is the SourceTable / base.
        // Tableextension's SourceTable is set from ExtendsObjectName
        // at import time so the extension's Rec.<X> resolves through
        // the base table's catalog + extensions.
        if (k == "page" || k == "pageextension" || k == "tableextension")
        {
            if (string.IsNullOrEmpty(_state.Ctx.OwnerSourceTableName)) return null;
            return new ResolvedVariableType("Record", _state.Ctx.OwnerSourceTableName);
        }

        // Reports / xmlports / queries with a declared SourceTable
        // (request-page-level property — common on simple processing
        // reports like Whse. Change Unit of Measure where there's no
        // explicit dataitem block) bind Rec to that table. The
        // dataitem-based PrescanAliases still overrides this when
        // the report has a dataset { dataitem(X; Y) } block — both
        // paths converge on "Rec is the report's primary record".
        if ((k == "report" || k == "reportextension"
                || k == "xmlport" || k == "query"
                || k == "requestpage")
            && !string.IsNullOrEmpty(_state.Ctx.OwnerSourceTableName))
        {
            return new ResolvedVariableType("Record", _state.Ctx.OwnerSourceTableName);
        }

        // Other record-bearing kinds: Rec is the owner itself
        // (tables, and reports / xmlports / queries with no
        // SourceTable and no dataitem yet — the prescan can still
        // rebind Rec to the first dataitem source later in the
        // setup, before any procedure body runs through the main
        // dispatch).
        if (k == "table"
            || k == "report" || k == "reportextension"
            || k == "xmlport" || k == "query"
            || k == "requestpage")
        {
            return new ResolvedVariableType(_state.Ctx.OwnerKind!, _state.Ctx.OwnerName);
        }

        return null;
    }

    public static bool IsScopeOpener(AlToken tok)
    {
        if (tok.Kind != AlTokenKind.Identifier) return false;
        var v = tok.Value;
        return string.Equals(v, "procedure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "trigger", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts a fresh scope frame for a `procedure` or `trigger`
    /// declaration. Parses the parameter list and optional var block,
    /// then positions the cursor immediately after the opening
    /// `begin` so the main loop walks the body with the new frame
    /// active.
    /// </summary>
    public void StartProcedureScope()
    {
        // Capture the keyword kind before advancing — `procedure` or
        // `trigger`. The symbol extractor may later refine `procedure`
        // to `local_procedure` / `internal_procedure` / etc. via the
        // visibility prefix that sits BEFORE this keyword; reference-
        // emission only needs the broad bucket since the import service
        // matches scopes back to symbol rows by line number, not kind.
        string symbolKind = "procedure";
        if (_state.Pos < _state.Tokens.Count
            && _state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
            && string.Equals(_state.Tokens[_state.Pos].Value, "trigger", StringComparison.OrdinalIgnoreCase))
        {
            symbolKind = "trigger";
        }

        // Skip past `procedure` / `trigger` keyword, then any whitespace
        // before the name. (Scope modifiers like local/internal/protected sit
        // BEFORE `procedure`, so they're already behind us here.)
        _state.Pos++;
        _state.SkipWhitespaceTokens();

        // Procedure / trigger name — for triggers like `OnValidate`
        // this is just an identifier; for procedures it's the
        // procedure name. Capture name + line for #181 scope tracking
        // so reference emitters can stamp the owning member onto each
        // ExtractedReference and the import service can resolve
        // source_symbol_id without a line-range scan.
        string? symbolName = null;
        int symbolStartLine = 0;
        if (_state.Pos < _state.Tokens.Count
            && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
        {
            symbolName = _state.Tokens[_state.Pos].Value;
            symbolStartLine = _state.Tokens[_state.Pos].Line;
            _state.Pos++;
        }

        // ControlAddin / DotNet event-receiver triggers use the form
        // `trigger Receiver::EventName(params)` — the `Receiver` is
        // the in-scope variable bound to the addin, `EventName` is
        // the event being subscribed to. Without consuming the
        // `::EventName` suffix the parameter list parser sees `::`
        // as the next token and bails before reading
        // `(location: DotNet Location)` — every parameter is lost
        // from scope, and every `location.Foo` chain in the body
        // fires head-not-a-variable (resolved to a same-named
        // table by the catalog fallback). Captured shape examples:
        //   trigger LocationProvider::LocationChanged(location: DotNet Location)
        //   trigger EventReceiver::OnEventCheckEvent(sender: Variant; e: DotNet …)
        if (_state.Pos < _state.Tokens.Count
            && _state.Tokens[_state.Pos].Kind == AlTokenKind.DoubleColon
            && _state.Pos + 1 < _state.Tokens.Count
            && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
        {
            _state.Pos += 2; // past `::` and the event name
        }

        var frame = new ScopeFrame
        {
            SymbolKind = symbolKind,
            SymbolName = symbolName,
            SymbolStartLine = symbolStartLine,
        };

        // Optional parameter list: `(name: Type; name: Type)`.
        _state.SkipWhitespaceTokens();
        if (_state.At("(")) ParseParameterList(frame);

        // Optional return-type clause. Two shapes:
        //   `: ReturnType` (anonymous)
        //   `ReturnName: ReturnType` (named — the AL "named return value"
        //   idiom, where `ReturnName` becomes a writable local inside
        //   the body, e.g. `procedure GetX() X: Integer begin X := 42 end;`).
        // The named form introduces a variable that base-app idioms
        // frequently reference inside the body — without registering
        // it, every `X := ...` and `OnAfter(X, ...)` fires
        // head-not-a-variable. Anonymous return: no variable added,
        // just skip the type tokens.
        ParseOptionalReturnClause(frame);

        // Interface procedure declarations have no body — they end at
        // `;` after the (optional) return clause. Stop scanning here
        // so the orchestrator picks up the next `procedure` token in
        // the interface. Without this short-circuit the search for
        // `var`/`begin` walks past every subsequent declaration in
        // the interface and the second / third / Nth procedure's
        // parameter types never get their property_object references
        // emitted.
        if (string.Equals(_state.Ctx.OwnerKind, "interface", StringComparison.OrdinalIgnoreCase))
        {
            if (_state.At(";")) _state.Pos++;
            return;
        }

        // Optional local var block: `var name: Type; ...` between
        // procedure head and `begin`. Skip balanced `(...)` spans so
        // a `var` MODIFIER inside a sibling signature's parameter list
        // (typical with `#if X / proc(...,NewArg) / #else / proc(...) /
        // #endif` shapes — SalesPost.UpdateShippingNo is the canonical
        // case) isn't mis-read as the start of THIS procedure's var
        // block. Without the paren-skip, ParseVarBlock would consume
        // the sibling's params as locals and the real var block (with
        // `NoSeries: Codeunit "No. Series"` etc.) would be discarded
        // by the `;`/`begin` recovery — every reference to those locals
        // in the body then fires head-not-a-variable.
        while (_state.Pos < _state.Tokens.Count
               && !(_state.IsIdentifierTok(_state.Pos, "begin") || _state.IsIdentifierTok(_state.Pos, "var")))
        {
            if (_state.At("(")) { _state.SkipBalancedParens(); continue; }
            _state.Pos++;
        }
        if (_state.IsIdentifierTok(_state.Pos, "var"))
        {
            _state.Pos++;
            ParseVarBlock(frame);
        }

        // Skip to and past `begin`. Same paren-skip rationale.
        while (_state.Pos < _state.Tokens.Count && !_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            if (_state.At("(")) { _state.SkipBalancedParens(); continue; }
            _state.Pos++;
        }
        if (_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            _state.Pos++;
            frame.BeginDepth = 1;
        }

        _state.ScopeStack.Push(frame);
    }

    private void ParseParameterList(ScopeFrame frame)
    {
        // Expect `(`. Walk until matching `)`. Each parameter is
        // [var] name [, name]* : Type [; ...].
        if (!_state.At("(")) return;
        _state.Pos++; // consume (

        while (_state.Pos < _state.Tokens.Count && !_state.At(")"))
        {
            // Attribute on a parameter (rare but legal — same shape
            // as var-block attributes). Skip before reading the name
            // so the attribute's identifier doesn't claim the
            // parameter slot.
            if (_state.At("["))
            {
                _state.SkipAttribute();
                continue;
            }

            // Optional `var` modifier on a parameter — pass by ref.
            if (_state.IsIdentifierTok(_state.Pos, "var")) _state.Pos++;

            // One or more comma-separated parameter names sharing a type.
            var names = new List<string>();
            while (_state.Pos < _state.Tokens.Count
                   && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                       || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                names.Add(_state.Tokens[_state.Pos].Value);
                _state.Pos++;
                if (_state.At(",")) { _state.Pos++; continue; }
                break;
            }

            // Expect `:` then Type.
            if (!_state.At(":")) { SkipToNextParam(); continue; }
            _state.Pos++;

            var type = ReadTypeReference();
            foreach (var n in names) frame.Vars[n.ToLowerInvariant()] = type;

            // Step past `;` if present.
            if (_state.At(";")) _state.Pos++;
        }
        if (_state.At(")")) _state.Pos++;
    }

    private void SkipToNextParam()
    {
        while (_state.Pos < _state.Tokens.Count && !_state.At(";") && !_state.At(")")) _state.Pos++;
        if (_state.At(";")) _state.Pos++;
    }

    /// <summary>
    /// Parses the optional return clause sitting between a procedure's
    /// parameter list and its <c>var</c> / <c>begin</c>. Two shapes:
    /// <list type="bullet">
    ///   <item><c>) ReturnName: ReturnType</c> — the AL named-return
    ///     idiom; <c>ReturnName</c> becomes an in-scope local of the
    ///     declared type for the body. Base-app procedures like
    ///     <c>GetTableValuePair(FieldNo) TableValuePair: Dictionary</c>
    ///     reference the named return inside the body and fire
    ///     head-not-a-variable without this registration.</item>
    ///   <item><c>) : ReturnType</c> — anonymous return; no variable
    ///     introduced. The token-skip below covers both by checking
    ///     for the optional name before the colon.</item>
    /// </list>
    /// On entry the cursor sits just past the closing <c>)</c> of the
    /// parameter list (or wherever the parameter parser left it). On
    /// exit the cursor sits at the first <c>var</c> / <c>begin</c>
    /// token of the procedure head — or unchanged when no return
    /// clause is present.
    /// </summary>
    private void ParseOptionalReturnClause(ScopeFrame frame)
    {
        _state.SkipWhitespaceTokens();
        if (_state.Pos >= _state.Tokens.Count) return;

        // Named return: identifier then `:`. Quoted identifiers are
        // legal as names; bare identifiers must not collide with the
        // `var` / `begin` keywords (those mark the next sub-clause).
        int probe = _state.Pos;
        if ((_state.Tokens[probe].Kind == AlTokenKind.Identifier
             || _state.Tokens[probe].Kind == AlTokenKind.QuotedIdentifier)
            && !_state.IsIdentifierTok(probe, "var")
            && !_state.IsIdentifierTok(probe, "begin")
            && probe + 1 < _state.Tokens.Count
            && _state.Tokens[probe + 1].Kind == AlTokenKind.Punct
            && _state.Tokens[probe + 1].Value == ":")
        {
            var returnName = _state.Tokens[probe].Value;
            _state.Pos = probe + 2; // past name and `:`
            var type = ReadTypeReference();
            frame.Vars[returnName.ToLowerInvariant()] = type;
            return;
        }

        // Anonymous return: `: Type`.
        if (_state.At(":"))
        {
            _state.Pos++;
            ReadTypeReference();
        }
    }

    private void ParseVarBlock(ScopeFrame frame)
    {
        // Inside `var ... begin`. Each declaration is
        // `name[, name]*: Type;`. Stop at `begin`.
        while (_state.Pos < _state.Tokens.Count && !_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            // Attributes on the variable declaration (the AL
            // [SecurityFiltering(SecurityFilter::Filtered)] shape).
            // Skip the entire [...] before parsing the next name —
            // otherwise the attribute's identifier gets mis-read as
            // the variable name and the actual declaration gets
            // swallowed by the SkipToSemicolonOrBegin recovery.
            if (_state.At("["))
            {
                _state.SkipAttribute();
                continue;
            }

            if (_state.Tokens[_state.Pos].Kind != AlTokenKind.Identifier
                && _state.Tokens[_state.Pos].Kind != AlTokenKind.QuotedIdentifier)
            {
                _state.Pos++;
                continue;
            }

            var names = new List<string>();
            while (_state.Pos < _state.Tokens.Count
                   && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                       || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                names.Add(_state.Tokens[_state.Pos].Value);
                _state.Pos++;
                if (_state.At(",")) { _state.Pos++; continue; }
                break;
            }

            if (!_state.At(":")) { SkipToSemicolonOrBegin(); continue; }
            _state.Pos++;

            var type = ReadTypeReference();
            foreach (var n in names) frame.Vars[n.ToLowerInvariant()] = type;

            if (_state.At(";")) _state.Pos++;
        }
    }

    private void SkipToSemicolonOrBegin()
    {
        while (_state.Pos < _state.Tokens.Count
               && !_state.At(";")
               && !_state.IsIdentifierTok(_state.Pos, "begin"))
        {
            _state.Pos++;
        }
        if (_state.At(";")) _state.Pos++;
    }

    /// <summary>
    /// Reads a type reference like <c>Codeunit "Sales-Post"</c>,
    /// <c>Record Customer</c>, <c>Page "Customer Card"</c>,
    /// <c>Boolean</c>, <c>Code[20]</c>. Returns the (keyword,
    /// type name) pair the rest of the extractor cares about.
    /// Bare system types like <c>HttpClient</c> come back with a
    /// null Keyword — those won't resolve to AL objects later.
    ///
    /// When the type has an AL object keyword and the name
    /// resolves through the catalog, emits a <c>property_object</c>
    /// reference at the name token so var/parameter type names
    /// (e.g. <c>GLSetup: Record "General Ledger Setup"</c>) show
    /// up as clickable / underlinable like other resolved spans.
    /// The keyword is passed as a kind hint so collisions like
    /// table-and-page-of-the-same-name resolve deterministically.
    /// </summary>
    private ResolvedVariableType ReadTypeReference()
    {
        string? keyword = null;
        string typeName = string.Empty;
        int typeNameLine = 0;
        int typeNameColumn = 0;
        bool sawTypeName = false;

        if (_state.Pos >= _state.Tokens.Count) return new ResolvedVariableType(null, "");

        // First token is either an AL object keyword (Record /
        // Codeunit / Page / …) followed by the type name, or the
        // type identifier itself.
        var first = _state.Tokens[_state.Pos];
        if (first.Kind == AlTokenKind.Identifier && AlExtractionState.IsAlObjectKeyword(first.Value))
        {
            keyword = first.Value;
            _state.Pos++;
            if (_state.Pos < _state.Tokens.Count)
            {
                var t = _state.Tokens[_state.Pos];
                // Numeric-id form: `Record 380;`, `var X: Codeunit 1060;`.
                // Older AL — and still-shipping modules like Payment Links
                // to PayPal — declare record / codeunit variables by id
                // rather than quoted name. Resolve via the catalog's
                // (kind, id) index and substitute the canonical name so
                // downstream ResolveTypeByName lookups succeed.
                if (t.Kind == AlTokenKind.Number && int.TryParse(t.Value, out var objectId))
                {
                    var resolved = _state.Ctx.Resolver.ResolveTypeByObjectId(objectId, keyword);
                    if (resolved is not null)
                    {
                        typeName = resolved.Name;
                        typeNameLine = t.Line;
                        typeNameColumn = t.Column;
                        sawTypeName = true;
                    }
                    _state.Pos++;
                }
                else if (t.Kind == AlTokenKind.Identifier || t.Kind == AlTokenKind.QuotedIdentifier)
                {
                    typeName = t.Value;
                    typeNameLine = t.Line;
                    typeNameColumn = t.Column;
                    sawTypeName = true;
                    _state.Pos++;

                    // Fully-qualified namespaced type reference:
                    // `Record Microsoft.Manufacturing.StandardCost."Standard Cost Worksheet"`.
                    // Walk the dotted prefix and use the LAST segment
                    // as the actual type name — segments before it
                    // are the namespace path. Modern BC ships these
                    // increasingly often (every namespace-disambiguated
                    // cross-app reference uses this shape).
                    while (_state.Pos + 1 < _state.Tokens.Count
                           && _state.At(".")
                           && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                               || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
                    {
                        _state.Pos++; // past .
                        var segment = _state.Tokens[_state.Pos];
                        typeName = segment.Value;
                        typeNameLine = segment.Line;
                        typeNameColumn = segment.Column;
                        _state.Pos++;
                    }
                }
            }
        }
        else if (first.Kind == AlTokenKind.Identifier || first.Kind == AlTokenKind.QuotedIdentifier)
        {
            typeName = first.Value;
            _state.Pos++;
        }

        // `array[N, M, …] of <ElementType>` — fold the element type
        // up so the variable's recorded type is its element, not the
        // literal string "array". Without this, source-parsed locals
        // like `Contact: array[2] of Contact` get TypeName="array",
        // ResolveHeadType then misses on the catalog lookup, and
        // every `Contact[i].Method()` chain surfaces as
        // head-var-type-unresolved despite `Contact` being a real
        // table. AL grammar guarantees one element-type after the
        // `of`; nested arrays (`array of array of X`) aren't a thing
        // in BC. The bracket-skip on line 867 below catches the size
        // brackets, and the `of` branch reads the element type.
        if (string.Equals(typeName, "array", StringComparison.OrdinalIgnoreCase))
        {
            // Skip the size-bracket span(s) — `array[2]` /
            // `array[2, 3]`. The next `[` -> `]` walker below also
            // handles this, but we need to consume it here so we
            // can spot the `of` keyword that follows.
            while (_state.Pos < _state.Tokens.Count && _state.At("["))
            {
                int depth = 1;
                _state.Pos++;
                while (_state.Pos < _state.Tokens.Count && depth > 0)
                {
                    if (_state.At("[")) depth++;
                    else if (_state.At("]")) depth--;
                    _state.Pos++;
                }
            }
            if (_state.IsIdentifierTok(_state.Pos, "of"))
            {
                _state.Pos++;
                // Recursively read the element type. Keyword + name
                // come back from the inner read; we adopt them
                // verbatim so chains through the array variable
                // resolve against the element type's catalog entry.
                var inner = ReadTypeReference();
                return inner;
            }
        }

        // Only emit when we have an explicit AL keyword. Bare
        // identifier types (Integer, Boolean, custom variables in
        // scope) aren't navigable AL objects and would either
        // mis-resolve or pollute the underline.
        if (keyword is not null && sawTypeName && !string.IsNullOrEmpty(typeName))
        {
            var target = _state.Ctx.Resolver.ResolveTypeByName(typeName, keyword);
            if (target is not null)
            {
                _state.EmitResolved(typeNameLine, typeNameColumn, target, null, null, "property_object");
            }
        }

        // Length qualifier on a scalar (Code[20], Text[100]) —
        // we don't care, just skip.
        while (_state.Pos < _state.Tokens.Count && _state.At("["))
        {
            while (_state.Pos < _state.Tokens.Count && !_state.At("]")) _state.Pos++;
            if (_state.At("]")) _state.Pos++;
        }

        // Generic type parameters: `of [Type, Type, …]` on List /
        // Dictionary / built-in generic shapes. The contents don't
        // resolve to AL objects so the extractor never reads them,
        // but if we don't consume the `of [...]` tail here, the
        // var-block parser walks back into it and treats `of` as
        // the next variable name — which silently drops the
        // variable declared AFTER the generic-typed one. Bracket
        // depth tracking so `[Code[20], Integer]` nests cleanly.
        if (_state.IsIdentifierTok(_state.Pos, "of"))
        {
            _state.Pos++; // of
            if (_state.At("["))
            {
                int depth = 0;
                do
                {
                    if (_state.At("[")) depth++;
                    else if (_state.At("]")) depth--;
                    _state.Pos++;
                } while (_state.Pos < _state.Tokens.Count && depth > 0);
            }
        }

        return new ResolvedVariableType(keyword, typeName);
    }

    // ── Begin/case/end depth tracking ───────────────────────────────

    /// <summary>
    /// While inside a procedure / trigger body (<c>ScopeStack.Count &gt; 1</c>),
    /// the orchestrator delegates begin/case/end handling here so the
    /// procedure walker owns the depth counter and the matching pop.
    /// Returns true when the token was consumed.
    /// </summary>
    /// <summary>
    /// Returns the frame that should accumulate <c>begin</c> / <c>end</c>
    /// depth changes. Normally the top frame, but single-statement
    /// <c>with X do</c> frames are skipped — they have no begin/end
    /// anchor of their own (they're popped by their pre-scanned
    /// <see cref="ScopeFrame.EndTokenIndex"/>), so any nested
    /// <c>BEGIN</c>/<c>END</c> inside the with's single statement
    /// belongs to the procedure frame below.
    /// </summary>
    private ScopeFrame BlockDepthTrackingFrame()
    {
        foreach (var frame in _state.ScopeStack)
        {
            if (!frame.IsSingleStmtWith) return frame;
        }
        return _state.ScopeStack.Peek();
    }

    public bool TryHandleBlockDepth(AlToken tok)
    {
        if (_state.ScopeStack.Count <= 1 || tok.Kind != AlTokenKind.Identifier) return false;
        if (string.Equals(tok.Value, "begin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tok.Value, "case", StringComparison.OrdinalIgnoreCase))
        {
            BlockDepthTrackingFrame().BeginDepth++;
            _state.Pos++;
            return true;
        }
        if (string.Equals(tok.Value, "end", StringComparison.OrdinalIgnoreCase))
        {
            var frame = BlockDepthTrackingFrame();
            frame.BeginDepth--;
            if (frame.BeginDepth <= 0)
            {
                // Body just closed — capture the (start, end) span before
                // we lose the frame. The end token's line+column become
                // `end_line` / `end_column` on oe_module_symbols via
                // ReleaseImportService. SymbolName is only null on the
                // outermost object-scope frame, which TryHandleBlockDepth
                // never reaches (guarded by ScopeStack.Count > 1 above).
                // A `with X do begin … end` frame doesn't represent a
                // procedure / trigger; it's only here to shadow Rec for
                // the block's duration. Skip the symbol-scope emit and
                // invalidate the cached RecType so subsequent code reads
                // the outer Rec again.
                if (frame.IsWithFrame)
                {
                    _state.RecTypeResolved = false;
                    _state.RecTypeCache = null;
                }
                else if (frame.SymbolName is not null && frame.SymbolKind is not null)
                {
                    // `tok` is the `end` keyword; its Column is the column
                    // of `e`. EndColumn points PAST the last character so
                    // it lines up with how oe_module_symbols.column_end is
                    // recorded for declarations.
                    var endColumn = tok.Column + tok.Value.Length;
                    _state.SymbolScopes.Add(new ExtractedSymbolScope(
                        frame.SymbolKind,
                        frame.SymbolName,
                        frame.SymbolStartLine,
                        tok.Line,
                        endColumn));
                }
                _state.ScopeStack.Pop();
            }
            _state.Pos++;
            return true;
        }
        return false;
    }

    // ── Member-chain extraction ─────────────────────────────────────

    /// <summary>
    /// Reads a chain of <c>head.member.member…</c> starting at the
    /// current token, resolves the type at each step, and emits one
    /// reference per <c>.member</c> we manage to resolve. The cursor
    /// advances past the entire chain when we return.
    /// </summary>
    public void TryConsumeMemberChain()
    {
        // We arrive with Tokens[Pos] = head identifier; [Pos+1] is
        // either `.` (the canonical chain entry) or `[` (an array
        // index whose [Pos+N+1] is the chain's `.`). The orchestrator
        // peeks past balanced brackets when dispatching; we mirror
        // that here so the head's array index doesn't trip the
        // `WalkMemberChain` loop's `At(".")` check.
        var head = _state.Tokens[_state.Pos];
        _state.Pos++;
        SkipBalancedBracketSpans();

        // AL built-in static APIs like `CODEUNIT.Run(...)`,
        // `PAGE.RunModal(...)`, `NavApp.GetCurrentModuleInfo(...)`.
        // These are runtime dispatchers, not user variables or
        // catalog types — silently skip the chain (no reference
        // to emit, no diagnostic bump) but walk inside the args
        // via the main dispatch path so typed-literals like
        // `Codeunit::"Foo"` inside `CODEUNIT.Run(Codeunit::"Foo")`
        // still surface as references.
        if (AlBuiltinMethods.IsBuiltinStaticReceiver(head.Value))
        {
            while (_state.Pos < _state.Tokens.Count)
            {
                if (_state.At("."))
                {
                    _state.Pos++; // .
                    AlToken? memberTok = null;
                    if (_state.Pos < _state.Tokens.Count
                        && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                            || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
                    {
                        memberTok = _state.Tokens[_state.Pos];
                        _state.Pos++; // member
                    }
                    if (_state.Pos < _state.Tokens.Count && _state.At("("))
                    {
                        if (memberTok is { } member) EmitStaticDispatchSystemReference(head, member);
                        WalkBalancedParens();
                    }
                    continue;
                }
                // Enum-value continuation:
                // `Microsoft.Manufacturing.Document."Production Order Status"::Planned.AsInteger()`.
                // After the dotted namespace path resolves to a quoted
                // type name, an enum-value `::Identifier` may follow,
                // optionally chained with `.AsInteger()` / `.AsBoolean()`.
                // The catalog doesn't track enum values; treat the
                // whole `::Value.method(...)` tail as a silent skip.
                if (_state.Pos + 1 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos].Kind == AlTokenKind.DoubleColon
                    && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                        || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
                {
                    _state.Pos += 2; // :: value
                    continue;
                }
                break;
            }
            return;
        }

        // Resolve the head to a type (a starting receiver). Two shapes:
        //   1. head matches a variable in any enclosing scope frame.
        //   2. head matches an object name in the type catalog (the
        //      `Customer.Insert(true)` pattern).
        // Anything else: this chain doesn't yield references.
        var receiverType = ResolveHeadType(head, out var declaredAsVar);
        if (receiverType is null)
        {
            // Variable's declared type is a known AL runtime / system
            // type (Dialog, RecordRef, XmlDocument, ModuleInfo, …) —
            // or it's declared with the `DotNet` keyword which never
            // resolves through the AL catalog by design — or it's a
            // BC platform virtual table by numeric id (the runtime
            // reserves 2000000000..2000000999 for these). Silence the
            // diagnostic so it doesn't crowd out real unresolveds.
            if (declaredAsVar is not null
                && (string.Equals(declaredAsVar.Keyword, "DotNet", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(declaredAsVar.TypeName)
                        && (AlBuiltinMethods.IsKnownSystemType(declaredAsVar.TypeName)
                            || AlExtractionState.IsPlatformVirtualTableId(declaredAsVar.TypeName)))))
            {
                AdvancePastChain();
                return;
            }

            // Implicit-Rec field as chain head: bare quoted (or bare)
            // identifier inside a table / page body that doesn't match
            // a scope variable or catalog type but DOES match a field
            // on Rec. Common shape on tables:
            //   `"Document Type".AsInteger()`
            // resolves head as Rec."Document Type" (enum field), then
            // the chain walker advances the receiver to the enum and
            // walks `.AsInteger()` as an enum built-in. Without this
            // fallback every implicit-Rec field used at chain-head
            // position fires head-not-a-variable.
            if (TryConsumeImplicitRecFieldChainHead(head))
            {
                return;
            }

            _state.Unresolved++;
            // Two sub-cases for the diagnostic samples: did the var
            // lookup miss entirely, or did it find a var whose
            // declared type didn't resolve through the resolver?
            // The latter points at visibility / catalog issues for
            // a known-named type — much more actionable than "name
            // isn't in scope".
            if (declaredAsVar is not null)
            {
                _state.CaptureUnresolved("head-var-type-unresolved", head, null,
                    receiverNameOverride: $"{declaredAsVar.Keyword ?? "?"} {declaredAsVar.TypeName}");
            }
            else
            {
                _state.CaptureUnresolved("head-not-a-variable", head, null);
            }
            AdvancePastChain();
            return;
        }

        WalkMemberChain(receiverType);
    }

    /// <summary>
    /// <c>Page.RunModal(Page::"Customer List", Customer)</c>,
    /// <c>Report.SaveAsPdf(Report::"Statement", Path)</c>,
    /// <c>Codeunit.Run(Codeunit::"Sales-Post", SalesHeader)</c>: a static
    /// dispatcher whose first argument names the object it acts on. Recorded
    /// as a system reference on that object, the same row the instance form
    /// (<c>CustomerList.RunModal()</c>) produces, so "Find built-in calls" on
    /// a page or report finds both. Leaves the cursor where it was: the
    /// arguments are walked afterwards as usual.
    /// </summary>
    private void EmitStaticDispatchSystemReference(AlToken head, AlToken memberTok)
    {
        if (!AlKindKeywords.IsStaticDispatcher(head.Value)) return;
        var t = _state.Tokens;
        var p = _state.Pos; // at `(`
        if (p + 3 >= t.Count
            || t[p + 1].Kind != AlTokenKind.Identifier
            || t[p + 2].Kind != AlTokenKind.DoubleColon
            || (t[p + 3].Kind != AlTokenKind.Identifier && t[p + 3].Kind != AlTokenKind.QuotedIdentifier))
        {
            return;
        }
        var kindTok = t[p + 1];
        if (!string.Equals(kindTok.Value, head.Value, StringComparison.OrdinalIgnoreCase)) return;

        // Namespaced literal: the last dotted segment is the name.
        var nameTok = t[p + 3];
        var q = p + 4;
        while (q + 1 < t.Count && t[q].Kind == AlTokenKind.Punct && t[q].Value == "."
               && (t[q + 1].Kind == AlTokenKind.Identifier || t[q + 1].Kind == AlTokenKind.QuotedIdentifier))
        {
            nameTok = t[q + 1];
            q += 2;
        }

        var target = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value, kindTok.Value);
        if (target is null) return;
        _state.EmitSystemReference(new ExtractedSystemReference(
            Line: memberTok.Line,
            Column: memberTok.Column,
            TargetAppId: target.AppId,
            TargetObjectKind: target.Kind,
            TargetObjectId: target.ObjectId,
            TargetObjectName: target.Name,
            SystemMethodName: memberTok.Value,
            ReferenceKind: "method_call"));
    }

    /// <summary>
    /// Reads a typed-literal head <c>Kind::"Name"</c> followed by
    /// <c>.member.member…</c>. The dominant BC pattern this catches:
    /// <c>Codeunit::"Sales-Post".Run(SalesHeader)</c>,
    /// <c>Page::"Customer Card".RunModal()</c>,
    /// <c>Report::"Customer - List".Run()</c>.
    /// </summary>
    public void TryConsumeTypedLiteralChain()
    {
        // We arrive with Tokens[Pos] = Kind identifier,
        // [Pos+1] = `::`, [Pos+2] = Name (quoted or bare).
        var kindTok = _state.Tokens[_state.Pos];
        _state.Pos++; // Kind
        _state.Pos++; // ::
        var nameTok = _state.Tokens[_state.Pos];
        _state.Pos++; // Name

        // Fully-qualified namespaced typed literal:
        //   `Database::Microsoft.Assembly.Document."Assembly Header"`
        // Take the LAST segment as the actual name; segments before
        // it are the namespace path. Same treatment ReadTypeReference
        // applies to var-block `Record Foo.Bar."Baz"` declarations.
        //
        // Critical lookahead: STOP when the next segment is followed
        // by `(` — that's a method call on the resolved type (e.g.
        // `Codeunit::"Sales-Post".Run(SalesHeader)`), not a namespace
        // continuation, and it has to be left to WalkMemberChain.
        // Without the guard the loop would greedily eat the `.Run`
        // chain step and mis-resolve "Run" as the type name.
        while (_state.Pos + 1 < _state.Tokens.Count
               && _state.At(".")
               && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                   || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier)
               && !(_state.Pos + 2 < _state.Tokens.Count
                    && _state.Tokens[_state.Pos + 2].Kind == AlTokenKind.Punct
                    && _state.Tokens[_state.Pos + 2].Value == "("))
        {
            _state.Pos++; // .
            nameTok = _state.Tokens[_state.Pos];
            _state.Pos++;
        }

        // Pass the kind keyword as a hint so name collisions across
        // object kinds disambiguate cleanly — `Codeunit::"Foo"`
        // should never resolve to a Table or Page named "Foo".
        var receiverType = _state.Ctx.Resolver.ResolveTypeByName(nameTok.Value, kindTok.Value);
        if (receiverType is null)
        {
            // `Kind::Value` where `Kind` isn't a canonical AL object
            // keyword is an enum value reference (e.g. `Verbosity::Error`,
            // `DataClassification::SystemMetadata`, `Step::Done`).
            // The catalog doesn't track enum values, so we can't
            // resolve the `Value` half — but the chain isn't broken,
            // it's just a value reference we don't model. Silence
            // the diagnostic when the kind name isn't one of the
            // recognised AL object keywords.
            if (!AlExtractionState.IsAlObjectKeyword(kindTok.Value)
                && !string.Equals(kindTok.Value, "DATABASE", StringComparison.OrdinalIgnoreCase))
            {
                AdvancePastChain();
                return;
            }
            _state.Unresolved++;
            _state.CaptureUnresolved("typed-literal-name", nameTok, null, kindTok.Value);
            AdvancePastChain();
            return;
        }

        // Emit a property_object reference at the typed-literal's
        // name token. Gives Find references / Go-to-definition / the
        // underline on `Codeunit::"Sales-Post"`, `Page::"Customer Card"`,
        // and `Database::Microsoft.Assembly.Document."Assembly Header"`
        // — same shape RunObject / SourceTable property values already
        // emit. Fires regardless of whether the typed literal is
        // standalone (`Database::"Foo"` as a parameter) or precedes
        // a chain (`Codeunit::"Sales-Post".Run(...)`); the
        // method_call on the chained `.Run` emits separately via
        // WalkMemberChain below.
        _state.EmitResolved(nameTok.Line, nameTok.Column, receiverType, null, null, "property_object");

        // Three-part enum-value literal: `Enum::"Assembly Document Type"::Quote`
        // or `…::"Blanket Order"`. Once the enum type resolves above, a
        // SECOND `::` introduces the value. The catalog doesn't track enum
        // values, so consume the `:: Value` segment without emitting — the
        // chain tail (`.AsInteger()`, `.AsText()`, …) then walks from the
        // enum receiver as a built-in. Without this the value token surfaces
        // as a fresh chain head and fires head-not-a-variable.
        if ((string.Equals(receiverType.Kind, "enum", StringComparison.OrdinalIgnoreCase)
                || string.Equals(receiverType.Kind, "enumextension", StringComparison.OrdinalIgnoreCase))
            && _state.Pos + 1 < _state.Tokens.Count
            && _state.Tokens[_state.Pos].Kind == AlTokenKind.DoubleColon
            && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
        {
            _state.Pos += 2; // :: Value
        }

        // Walk `.member.member…` from the typed receiver, if any.
        WalkMemberChain(receiverType);
    }

    /// <summary>
    /// Recognises the parenthesised <c>as</c>-cast chain shape:
    /// <code>(IDocumentSender as ISentDocumentActions).GetApprovalStatus(...)</code>.
    /// This is the canonical interface-cast pattern in BC 26 (E-Document
    /// Core's Sent Document Approval / Cancellation / Mark Fetched /
    /// Get Response Runner all use it on the codeunit's own
    /// implementation surface). Without recognising it, the dispatch
    /// path walked past the cast and saw <c>GetApprovalStatus(</c> at
    /// the end — bare-self-call on the owner codeunit, which doesn't
    /// declare it. Emit the chain step against the post-<c>as</c>
    /// interface type so the GetApprovalStatus reference lands on the
    /// interface's declaration row.
    ///
    /// Returns true when the pattern matched (cursor advances through
    /// the cast and the trailing member chain); false otherwise (cursor
    /// untouched).
    /// </summary>
    public bool TryConsumeAsCastChain()
    {
        // Pattern: `(` Identifier `as` (Identifier|QuotedIdentifier) `)` `.` Member
        if (_state.Pos + 6 >= _state.Tokens.Count) return false;
        var t = _state.Tokens;
        int p = _state.Pos;
        if (t[p].Kind != AlTokenKind.Punct || t[p].Value != "(") return false;
        if (t[p + 1].Kind != AlTokenKind.Identifier
            && t[p + 1].Kind != AlTokenKind.QuotedIdentifier) return false;
        if (t[p + 2].Kind != AlTokenKind.Identifier
            || !string.Equals(t[p + 2].Value, "as", StringComparison.OrdinalIgnoreCase)) return false;
        if (t[p + 3].Kind != AlTokenKind.Identifier
            && t[p + 3].Kind != AlTokenKind.QuotedIdentifier) return false;
        if (t[p + 4].Kind != AlTokenKind.Punct || t[p + 4].Value != ")") return false;
        if (t[p + 5].Kind != AlTokenKind.Punct || t[p + 5].Value != ".") return false;

        var typeName = t[p + 3].Value;
        var receiverType = _state.Ctx.Resolver.ResolveTypeByName(typeName);
        // Advance cursor to the `.` so WalkMemberChain reads it as the
        // next chain step. The cast head is consumed regardless of
        // whether the type resolved — letting the dispatch fall through
        // would just re-emit `GetApprovalStatus(...)` as a bare-self-call.
        _state.Pos = p + 5;
        WalkMemberChain(receiverType);
        return true;
    }

    /// <summary>
    /// Walks the <c>.member.member…</c> tail of a chain after the
    /// head has been resolved to a receiver type. Emits one
    /// reference per step we resolve and advances the receiver to
    /// the member's return / declared type so chained access
    /// continues. Skips over method-call argument lists so they
    /// don't get misread as chain continuations.
    /// </summary>
    private void WalkMemberChain(AlTypeRef? receiverType)
    {
        while (_state.Pos < _state.Tokens.Count)
        {
            // `Var.Field::EnumValue` typed-literal continuation. The
            // catalog doesn't track enum / option values, so we
            // silently skip the value identifier. The receiver stays
            // pinned to the field's enum/option return type (or to
            // the null receiver inherited from a scalar-Option field)
            // so subsequent `.AsInteger()` / `.AsBoolean()` continues
            // through the EnumMethods / CommonMethods builtin set.
            //
            // Without this, every base-app case label of the shape
            //   SalesHeader."Document Type"::Quote:
            // and every chain like
            //   CFForecastEntry."Source Type"::Receivables.AsInteger()
            // fired head-not-a-variable on the value identifier when
            // the main dispatch picked it up as a fresh chain head.
            if (_state.Pos + 1 < _state.Tokens.Count
                && _state.Tokens[_state.Pos].Kind == AlTokenKind.DoubleColon
                && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                    || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
            {
                _state.Pos += 2;
                continue;
            }

            if (!_state.At(".")) break;

            // Receiver dropped to null upstream (scalar-Option field
            // with no resolvable type). Walk past trailing
            // `.Member(args)` chains so the main loop doesn't re-enter
            // on the dotted continuation and bump unresolved.
            if (receiverType is null)
            {
                AdvancePastChain();
                return;
            }

            _state.Pos++; // .

            if (_state.Pos >= _state.Tokens.Count) break;
            var memberTok = _state.Tokens[_state.Pos];
            if (memberTok.Kind != AlTokenKind.Identifier
                && memberTok.Kind != AlTokenKind.QuotedIdentifier)
            {
                break;
            }
            _state.Pos++;

            // Followed by ( → method_call. Without parens the syntax
            // can't tell a call from a field read — AL allows
            // `Rec.Modify;` — so the resolved member's kind decides
            // below (issue #712).
            var followedByParen = _state.Pos < _state.Tokens.Count && _state.At("(");

            // Resolve member on receiver type.
            var member = _state.Ctx.Resolver.ResolveMember(receiverType, memberTok.Value);
            if (member is null)
            {
                // Differentiate "real unresolved" from "AL runtime
                // built-in that was never going to be in the
                // catalog" (Insert / Get / Find / SetRange etc.).
                // Built-ins skip silently and terminate the chain;
                // genuine unresolved targets bump the diagnostic
                // counter so operators can size the residual gap.
                if (AlBuiltinMethods.IsBuiltin(receiverType.Kind, memberTok.Value))
                {
                    // Built-in like Rec.Insert()/Rec.Validate(...): not a normal
                    // reference, but record it as a SYSTEM reference (separate
                    // table) so "Find System References" can surface it without
                    // cluttering Find references. The receiver type is already
                    // resolved, so this row is fully qualified. See issue #279.
                    _state.EmitSystemReference(new ExtractedSystemReference(
                        Line: memberTok.Line,
                        Column: memberTok.Column,
                        TargetAppId: receiverType.AppId,
                        TargetObjectKind: receiverType.Kind,
                        TargetObjectId: receiverType.ObjectId,
                        TargetObjectName: receiverType.Name,
                        SystemMethodName: memberTok.Value,
                        // Paren-less built-ins are still calls (`Rec.Modify;`);
                        // only the runtime's system FIELDS (SystemId,
                        // SystemModifiedAt, …) are reads. See issue #712.
                        ReferenceKind: followedByParen || !AlBuiltinMethods.IsBuiltinField(memberTok.Value)
                            ? "method_call"
                            : "field_access"));
                    // The args may still contain references that need to
                    // surface — walk inside the parens via the main dispatch.
                    if (followedByParen) WalkArgsForBuiltin(receiverType, memberTok.Value);
                    return;
                }
                // Enum reflection statics (Names / Ordinals / GetValueAt /
                // FromInteger) are invoked on the enum *type*. When the
                // type-literal head name also matches a table/interface — BC
                // ships `enum "Ext. File Storage Connector"` beside a same-named
                // table, and `enum "Email Connector"` beside `interface "Email
                // Connector"` — the head resolves to the non-enum object and
                // these enum-only statics miss. They're built-ins, not real
                // references, so silence (and walk any args for inner refs)
                // rather than counting a chain-step unresolved against the wrong
                // object kind. See AlBuiltinMethods.EnumStaticMethods.
                if (AlBuiltinMethods.IsEnumStaticMethod(memberTok.Value))
                {
                    if (followedByParen) WalkBalancedParens();
                    return;
                }
                // Platform virtual tables (Record Field, Record Company,
                // Record File, Record Media, Record User, …) have
                // schemas that aren't fully modelled. Two ways to spot
                // them: the synthesised sentinel (AppId == Guid.Empty)
                // when we couldn't link them to a real module, and the
                // object-id range 2000000000–2000000999 when they
                // resolved through the System app's real symbol package
                // (Microsoft does ship some metadata for them, but it's
                // never a complete method list — static-style helpers
                // like File.ViewFromStream, Media.FindOrphans, plus
                // every BC's-current-month-added field, end up missing).
                // Silence the chain-step miss in either case; the
                // trade-off (no underline / Find-references support
                // on those specific members) was already implicit.
                if (receiverType.AppId == Guid.Empty
                    || (receiverType.ObjectId is int id
                        && id >= 2000000000 && id <= 2000000999))
                {
                    if (followedByParen) WalkBalancedParens();
                    return;
                }
                // Owner-globals / labels fallback: when the receiver IS
                // the owner (the canonical shape is `this.MyGlobal` or
                // `Rec.MyLabel` on tables), the chain step may name a
                // global var or label that lives in Ctx.GlobalVars /
                // object-scope labels — never in oe_module_members,
                // which only holds procedures / triggers / fields.
                //
                // Canonical samples:
                //   `this.LogiqEDocumentManagement.Send(...)` — global
                //     codeunit var on the owner codeunit; chain
                //     continues on the resolved codeunit type so
                //     `.Send(args)` then resolves normally.
                //   `Visible = TopBannerVisible and (CurrentStep = N)` —
                //     page globals referenced via implicit `this.`.
                //   `Error(PilotBaseUrlTok)` — table label in a
                //     `Rec.<Label>` chain.
                //
                // When the global's declared type resolves through the
                // catalog (Codeunit / Record / Page / …), the chain
                // continues against that type. When the type is a
                // scalar / Label / unresolvable, the chain ends here.
                // Either way, the unresolved sample is silenced.
                if (TryAdvanceToOwnerGlobal(receiverType, memberTok.Value, out var globalNextType))
                {
                    receiverType = globalNextType;
                    continue;
                }
                // Extension → base-object fallback. When the receiver
                // is the current owner AND the owner is a
                // reportextension / pageextension, AL routes member
                // calls to the base object's procedures too. The
                // canonical shape:
                //   `this.GetLocation("Location Code")`
                // inside `reportextension "Mfg. Get Outbound Source
                // Docs" extends "Get Outbound Source Docs"`, where
                // `GetLocation` is declared on the base report. The
                // bare-self-call resolver already has this branch
                // (line 2020 area); the chain walker needs it too for
                // explicit-this dispatch.
                if (TryAdvanceToBaseObject(receiverType, memberTok, followedByParen, out var baseNextType))
                {
                    receiverType = baseNextType;
                    continue;
                }
                _state.Unresolved++;
                _state.CaptureUnresolved("chain-step", memberTok, receiverType);
                AdvancePastChain();
                return;
            }

            // Members added by a tableextension/pageextension live
            // on the extension object, not the base. The resolver
            // signals that via member.DeclaringType; we stamp the
            // reference's target at the declaration site so Find
            // references on the extension's declaration row picks
            // this call up.
            var targetOwner = member.DeclaringType ?? receiverType;
            var refKind = followedByParen || AlKindKeywords.IsCallableMemberKind(member.Kind)
                ? "method_call"
                : "field_access";
            _state.EmitResolved(memberTok.Line, memberTok.Column, targetOwner, member.Name, member.Kind, refKind);

            // For chained access, advance receiverType to the
            // member's result type (return type for a procedure,
            // declared type for a field). Stop the chain when the
            // member yields a non-record/non-object type or when
            // we can't resolve the next type.
            var preAdvanceReceiver = receiverType;
            receiverType = AdvanceReceiverByMember(member);

            // Walk the method call's argument list so references
            // embedded in the args (`Rec.Validate("X", Y.Z)` —
            // both the bare quoted "X" and the chained Y.Z) emit
            // their refs. Pos lands past the matching `)` on
            // return; the outer while loop's At(".") check then
            // handles any chain continuation that follows.
            if (followedByParen) WalkArgsForBuiltin(preAdvanceReceiver, memberTok.Value);
        }
    }

    /// <summary>
    /// Walks a method call's argument list, with bonus context: when
    /// the called method takes field-name arguments (Validate /
    /// SetRange / FieldNo / CalcFields / …), set
    /// <see cref="AlExtractionState.CurrentFieldReceiver"/> to the
    /// receiver for the duration of the parens so the bare-arg
    /// dispatch hook can resolve identifiers as field accesses on
    /// it. Properly nests: a nested call inside (like
    /// <c>OuterRec.Validate(InnerRec.FieldNo("X"), 0)</c>) saves and
    /// restores the slot so the inner call sees its own receiver,
    /// not the outer's.
    /// </summary>
    private void WalkArgsForBuiltin(AlTypeRef receiver, string methodName)
    {
        var saved = _state.CurrentFieldReceiver;
        _state.CurrentFieldReceiver =
            AlBuiltinMethods.FieldNameTakingMethods.Contains(methodName)
                ? receiver
                : null;
        try
        {
            WalkBalancedParens();
        }
        finally
        {
            _state.CurrentFieldReceiver = saved;
        }
    }

    /// <summary>
    /// Walks tokens inside a balanced <c>( … )</c> range, dispatching
    /// each through the orchestrator's main loop so references
    /// embedded in method arguments emit naturally. Called by
    /// <see cref="WalkMemberChain"/> after emitting a method-call
    /// reference, replacing the previous <c>SkipBalancedParens</c>
    /// that swallowed every arg-side reference (so
    /// <c>Rec.Validate("Sell-to Customer No.", Customer."No.")</c>
    /// was losing both arg references).
    ///
    /// Assumes <see cref="AlExtractionState.Pos"/> is at the opening
    /// <c>(</c>. On return, <see cref="AlExtractionState.Pos"/> is past
    /// the matching <c>)</c>.
    /// </summary>
    public void WalkBalancedParens()
    {
        if (!_state.At("(")) return;

        // Re-entrancy guard: each dispatched arg token can recurse back into
        // this method (chain handling → WalkArgsForBuiltin → WalkBalancedParens).
        // Past the depth cap, stop recursing and skip the rest of this paren
        // range non-recursively so a pathological deeply-nested file can't
        // overflow the stack. See issue #363.
        if (_state.WalkDepth >= AlExtractionState.MaxWalkDepth)
        {
            _state.SkipBalancedParens();
            return;
        }

        _state.WalkDepth++;
        try
        {
            _state.Pos++; // past `(`
            int depth = 1;
            while (_state.Pos < _state.Tokens.Count)
            {
                if (_state.At("("))
                {
                    depth++;
                    _state.Pos++;
                    continue;
                }
                if (_state.At(")"))
                {
                    depth--;
                    _state.Pos++;
                    if (depth == 0) return;
                    continue;
                }
                _dispatchOneToken();
            }
        }
        finally
        {
            _state.WalkDepth--;
        }
    }

    /// <summary>
    /// Consumes a <c>with X do begin … end;</c> statement, pushing a scope
    /// frame that shadows Rec to X's type for the body's duration. AL's
    /// <c>with</c> is deprecated and the compiler warns about it, but
    /// Microsoft's regional / banking modules still ship it (the AMC
    /// Banking codeunits build payment records via
    /// <c>with PaymentExportData do begin … SetCustomerAsRecipient(...) … end;</c>);
    /// without this handler every bare identifier inside surfaces as
    /// bare-call unresolved.
    ///
    /// <para>Only the <c>do begin … end</c> shape is rebound — single-
    /// statement <c>with X do Foo();</c> has no begin/end pair to anchor
    /// the pop, so the cursor advances past <c>with X do</c> without
    /// pushing a frame and the body walks with the outer Rec. Microsoft's
    /// corpus uses the multi-statement shape exclusively.</para>
    ///
    /// <para>Returns true when at least <c>with X do</c> was consumed so
    /// the dispatcher doesn't fall through to identifier-handling on the
    /// <c>with</c> keyword. False (with the cursor restored) when the
    /// shape doesn't match — e.g. a stray <c>with</c> followed by
    /// something we can't resolve as a record-typed expression.</para>
    /// </summary>
    public bool TryConsumeWithStatement()
    {
        var savedPos = _state.Pos;
        _state.Pos++; // past `with`
        _state.SkipWhitespaceTokens();
        if (_state.Pos >= _state.Tokens.Count)
        {
            _state.Pos = savedPos;
            return false;
        }

        // Read the first identifier of the with-expression. AL supports
        // chained `with X, Y do …` but Microsoft's corpus uses single
        // identifiers; we resolve the first one and skip past any commas.
        var xTok = _state.Tokens[_state.Pos];
        if (xTok.Kind != AlTokenKind.Identifier && xTok.Kind != AlTokenKind.QuotedIdentifier)
        {
            _state.Pos = savedPos;
            return false;
        }
        var xType = ResolveHeadType(xTok, out _);
        _state.Pos++; // past X identifier

        // Skip optional namespace/member chain on the with-expression
        // (e.g. `with Rec."Document Type" do` — rare). We don't model it
        // for Rec rebinding; the AMC pattern is a plain variable.
        while (_state.Pos + 1 < _state.Tokens.Count
               && _state.At(",")
               && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                   || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
        {
            _state.Pos += 2;
        }
        _state.SkipWhitespaceTokens();

        if (!_state.IsIdentifierTok(_state.Pos, "do"))
        {
            _state.Pos = savedPos;
            return false;
        }
        _state.Pos++; // past `do`
        _state.SkipWhitespaceTokens();

        // Two shapes downstream:
        //   `with X do begin … end;` — multi-statement, anchored by
        //     begin/end so TryHandleBlockDepth pops the frame on the
        //     matching end.
        //   `with X do <single statement>;` — no begin/end; pop the
        //     frame when the cursor reaches the matching `;` (found
        //     here by a forward scan tracking parens / brackets /
        //     begin-end depth). Canonical BC pattern:
        //     `WITH GenJournalLine DO IF X THEN
        //        InsertPaymentFileError(...);` — without the rebind,
        //     the bare call fires unresolved.
        var multiStmt = _state.IsIdentifierTok(_state.Pos, "begin");

        if (xType is null) return true;

        // Map the resolved type's catalog kind back to the AL keyword
        // ResolveTypeByName expects on next lookup (table → Record).
        var keyword = string.Equals(xType.Kind, "table", StringComparison.OrdinalIgnoreCase)
            ? "Record"
            : xType.Kind;
        var withFrame = new ScopeFrame
        {
            IsWithFrame = true,
        };
        withFrame.Vars["rec"] = new ResolvedVariableType(keyword, xType.Name);
        withFrame.Vars["xrec"] = new ResolvedVariableType(keyword, xType.Name);

        if (!multiStmt)
        {
            withFrame.IsSingleStmtWith = true;
            withFrame.EndTokenIndex = ScanForSingleStatementEnd(_state.Pos);
            // Guard: if the scan didn't find a terminator (malformed
            // source / truncation), don't push — falls back to the
            // pre-fix behaviour of "no rebind, take the unresolved".
            if (withFrame.EndTokenIndex <= _state.Pos) return true;
        }

        _state.ScopeStack.Push(withFrame);
        // Invalidate the cached RecType so subsequent bare-call /
        // implicit-Rec lookups read the with-frame's binding.
        _state.RecTypeResolved = false;
        _state.RecTypeCache = null;

        // Leave the cursor at `begin` (multi-stmt) or the first token of
        // the single statement — the main loop dispatches normally; the
        // orchestrator pops single-stmt frames when Pos >= EndTokenIndex.
        return true;
    }

    /// <summary>
    /// Scans forward from <paramref name="startPos"/> through the AL
    /// tokens to find the <c>;</c> that terminates a single statement,
    /// tracking <c>(</c> / <c>)</c>, <c>[</c> / <c>]</c>, and
    /// <c>begin</c> / <c>end</c> (and <c>case</c> / <c>end</c>) depths
    /// so nested constructs don't terminate early. Returns the position
    /// ONE PAST the closing <c>;</c>, or <paramref name="startPos"/> if
    /// no terminator was found before the token stream ends.
    /// </summary>
    private int ScanForSingleStatementEnd(int startPos)
    {
        int depth = 0;
        for (int i = startPos; i < _state.Tokens.Count; i++)
        {
            var t = _state.Tokens[i];
            if (t.Kind == AlTokenKind.Punct)
            {
                if (t.Value == "(" || t.Value == "[") { depth++; continue; }
                if (t.Value == ")" || t.Value == "]") { if (depth > 0) depth--; continue; }
                if (t.Value == ";" && depth == 0) return i + 1;
            }
            else if (t.Kind == AlTokenKind.Identifier)
            {
                if (string.Equals(t.Value, "begin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.Value, "case", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
                else if (string.Equals(t.Value, "end", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth > 0) depth--;
                }
            }
        }
        return startPos;
    }

    /// <summary>
    /// Bare self-procedure call detector: <c>DoStuff(...)</c> with no
    /// receiver. The dispatch order matches AL's actual resolution
    /// semantics — a user procedure on the owner shadows a same-named
    /// system function — so we try own-member resolution FIRST and
    /// only fall through to the AL-system-function silence list when
    /// the catalog has no candidate. Earlier revisions of this method
    /// checked the system-function list first to save a catalog
    /// lookup, but that silenced legitimate same-named user
    /// procedures (a codeunit's own <c>Message(s)</c> would lose its
    /// method_call reference). The catalog hit is cheap; the AL-
    /// correct order is the right one.
    ///
    /// Filter steps that DO stay at the top:
    /// <list type="bullet">
    ///   <item>AL statement / operator keywords (<c>if</c>, <c>not</c>, …) —
    ///     these legitimately precede <c>(</c> without being calls.</item>
    ///   <item>Object-DSL keywords (<c>area(content)</c>,
    ///     <c>field("X"; …)</c>) — declarative syntax, never user
    ///     procedure names.</item>
    ///   <item>Implicit-Rec <see cref="AlBuiltinMethods.RecordMethods"/>
    ///     shorthand on Rec-bearing owners — bare <c>Insert()</c> on a
    ///     page / tableextension. The risk (a codeunit-with-TableNo
    ///     defining its own <c>Insert</c> proc) is theoretical; if a
    ///     real case shows up we'd reorder this too.</item>
    ///   <item>In-scope variables — AL doesn't allow calling a variable
    ///     as a function, but if the name is in scope we treat it as
    ///     referenced and advance past.</item>
    /// </list>
    ///
    /// Returns true when the token was handled (cursor advanced),
    /// false when the caller should fall through to the default
    /// "advance one token" path.
    /// </summary>
    public bool TryConsumeBareSelfCall()
    {
        var head = _state.Tokens[_state.Pos];
        var name = head.Value;

        if (AlBuiltinMethods.IsStatementKeyword(name))
        {
            // Statement / operator keyword — let the default advance
            // walk past it. Returning false makes the main loop do
            // exactly one Pos++ next iteration; the `(` after it is
            // re-entered for whatever sits inside the parens.
            return false;
        }
        if (AlBuiltinMethods.IsObjectDslKeyword(name))
        {
            // Page / pageextension / tableextension / report /
            // xmlport / enum / permissionset declarative keyword —
            // `area(content)`, `field("X"; Rec."Y")`, `value(N; "Z")`,
            // `modify(SomeField) { … }`. These look identical to
            // procedure calls to the lexer but introduce nested
            // declarative structure; the arg list still walks
            // through the main loop so the inner expressions (a
            // page-field's `Rec."Y"` reference, for example)
            // continue to emit.
            return false;
        }

        // Implicit-Rec method call. On a page / pageextension /
        // tableextension / codeunit-with-TableNo body, a bare
        // `Insert();` is shorthand for `Rec.Insert();` and
        // `SetCurrentKey(X);` is `Rec.SetCurrentKey(X);`. The
        // RecordMethods list is the authoritative set of Record
        // built-ins, so the check is just "owner has Rec AND name
        // is a RecordMethod" → silent skip. Without this, every
        // implicit-Rec call in BC pages surfaces as a bare-call
        // unresolved.
        if (RecType() is not null && AlBuiltinMethods.RecordMethods.Contains(name))
        {
            return false;
        }

        // In-scope variable? AL doesn't allow calling a variable as
        // a function — if the name is in scope we treat it as
        // referenced and just advance past.
        var key = name.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.ContainsKey(key))
            {
                return false;
            }
        }

        // Try to resolve as a member on the file's owner object.
        var ownerType = OwnerType();
        if (ownerType is null)
        {
            // No owner-side catalog scope to consult. Return false
            // unconditionally so the orchestrator's main loop does
            // its standard Pos++ — returning true here without
            // advancing the cursor sends the loop back into the
            // same token forever. The system-function silence list
            // doesn't apply yet because we haven't tried the
            // catalog; with no owner context we can't say either way,
            // so fall through to the default advance.
            return false;
        }

        var member = _state.Ctx.Resolver.ResolveMember(ownerType, name);
        if (member is null)
        {
            // Implicit-Rec procedure call. A bare `SavePassword()` on a
            // page or `BlockDynamicTracking()` in a tableextension is
            // shorthand for `Rec.<Proc>()`, where Rec is the page's
            // SourceTable / the extension's base table — NOT the owner
            // object. The owner lookup above only sees the page's /
            // extension's own procedures, so a base-table (or
            // sibling-extension) procedure called bare lands here.
            // Resolve against Rec's type — ResolveMember walks base →
            // visible extensions — and emit the call at the declaring
            // object so Find references on it picks the call up. Only
            // worthwhile when Rec differs from the owner: for a plain
            // table owner Rec IS the owner and the lookup above covered
            // it; for a codeunit with no TableNo RecType() is null.
            var recType = RecType();
            if (recType is not null && !recType.Equals(ownerType))
            {
                var recMember = _state.Ctx.Resolver.ResolveMember(recType, name);
                if (recMember is not null)
                {
                    var recTarget = recMember.DeclaringType ?? recType;
                    _state.EmitResolved(head.Line, head.Column, recTarget, recMember.Name, recMember.Kind, "method_call");
                    _state.Pos++;
                    return true;
                }
            }

            // Extension → base-object fallback. A pageextension's bare
            // `NoOfRecords(TableID)` can resolve to a procedure on the
            // BASE PAGE (e.g. `Navigate.NoOfRecords` on the `Navigate Ext.`
            // pageextension) — distinct from Rec, which is the page's
            // source TABLE. Same shape for reportextension → base report.
            // Tableextensions don't need this branch: the base table IS
            // the Rec type, so the recType fallback above already walked
            // base → visible extensions.
            var baseType = BaseObjectType();
            if (baseType is not null
                && !baseType.Equals(ownerType)
                && (recType is null || !baseType.Equals(recType)))
            {
                var baseMember = _state.Ctx.Resolver.ResolveMember(baseType, name);
                if (baseMember is not null)
                {
                    var baseTarget = baseMember.DeclaringType ?? baseType;
                    _state.EmitResolved(head.Line, head.Column, baseTarget, baseMember.Name, baseMember.Kind, "method_call");
                    _state.Pos++;
                    return true;
                }
            }

            // Dataitem-source fallback for report / query / xmlport
            // owners: a bare call inside a column / fieldelement
            // property expression (the canonical shape is
            //   AutoFormatExpression = GetCurrencyCodeFromBank();
            // inside a report's `column(N; …) { … }`) targets a
            // procedure on the current dataitem's SOURCE TABLE, not on
            // the report itself. The per-kind structure extractor
            // pushes the active dataitem source onto
            // AlExtractionState.DataItemStack; we walk it innermost-
            // to-outermost so a bare call in a NESTED dataitem trigger
            // (e.g. `EmptyLine()` inside an `Integer`-loop dataitem
            // under a `"Gen. Journal Line"` parent) still resolves
            // against the parent's source table when the inner one
            // doesn't expose the method.
            foreach (var dataItemSource in _state.ActiveDataItemSources())
            {
                if (dataItemSource.Equals(ownerType)) continue;
                if (recType is not null && dataItemSource.Equals(recType)) continue;
                if (baseType is not null && dataItemSource.Equals(baseType)) continue;
                var diMember = _state.Ctx.Resolver.ResolveMember(dataItemSource, name);
                if (diMember is null) continue;
                var diTarget = diMember.DeclaringType ?? dataItemSource;
                _state.EmitResolved(head.Line, head.Column, diTarget, diMember.Name, diMember.Kind, "method_call");
                _state.Pos++;
                return true;
            }

            // All catalog lookups missed. Now check whether the name
            // matches an AL system function (`Message`, `Error`,
            // `Format`, `Exists`, …) or a runtime built-in method
            // exposed without a receiver. If so, silence; else this
            // is a genuinely unresolved bare call.
            if (MatchesBareCallableSilence(name))
            {
                return false;
            }

            // Could be a bare AL system function we don't have on our
            // list yet, or a same-named procedure on a related extension
            // we don't track from this angle. Counted but not emitted.
            _state.Unresolved++;
            _state.CaptureUnresolved("bare-call", head, ownerType);
            return false;
        }

        // Tableextension-declared self-members fall through the
        // resolver's DeclaringType path same as receiver chains.
        var targetOwner = member.DeclaringType ?? ownerType;
        _state.EmitResolved(head.Line, head.Column, targetOwner, member.Name, member.Kind, "method_call");

        // Continue the chain when the call is followed by `.member`
        // and the called procedure's return type resolves to a real
        // catalog object — `Get(field).AddFilterValueV2(...)` shape.
        // Without this the bare-self-call emitted Get's reference but
        // left `.AddFilterValueV2(...)` for the main loop to re-
        // dispatch as a fresh bare-self-call against the owner, which
        // missed (AddFilterValueV2 lives on the codeunit Get returns).
        //
        // Walking the args via WalkBalancedParens preserves the
        // "inner `Cust.X()` still resolves" guarantee — same dispatcher
        // the main loop would have used.
        _state.Pos++;
        if (_state.Pos < _state.Tokens.Count && _state.At("("))
        {
            var returnType = AdvanceReceiverByMember(member);
            WalkBalancedParens();
            if (returnType is not null
                && _state.Pos < _state.Tokens.Count && _state.At("."))
            {
                WalkMemberChain(returnType);
            }
        }
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is on one of the
    /// AL-system-function or built-in-method silence lists — the
    /// bare-callable functions (<see cref="AlBuiltinMethods.BareCallableFunctions"/>),
    /// the per-receiver method sets (<see cref="AlBuiltinMethods.RecordMethods"/>,
    /// <see cref="AlBuiltinMethods.CodeunitMethods"/>, etc.), or the
    /// common-method set. Called from
    /// <see cref="TryConsumeBareSelfCall"/> AFTER own-member catalog
    /// resolution has missed; this ordering matches AL's actual
    /// resolution semantics (user procedures shadow same-named
    /// system functions) and avoids the false-positive risk the
    /// reverse ordering carried.
    ///
    /// The bare-callable check covers system functions like
    /// `Message` / `Error` / `Format` / `Exists` (the deprecated
    /// AL file-system check, distinct from a user `procedure Exists`
    /// which the catalog lookup above would have already matched).
    /// The per-receiver lists catch mis-parsed chain calls (the
    /// chain head got lost upstream, so `SomeRec.Insert(...)` arrived
    /// here as bare `Insert(...)`) and text/variant bare uses
    /// (`Trim`, `Unwrap`, `HasValue`, `AsInteger`).
    /// </summary>
    private static bool MatchesBareCallableSilence(string name) =>
        AlBuiltinMethods.IsBareCallable(name)
        || AlBuiltinMethods.RecordMethods.Contains(name)
        || AlBuiltinMethods.RecordSystemFields.Contains(name)
        || AlBuiltinMethods.CodeunitMethods.Contains(name)
        || AlBuiltinMethods.PageMethods.Contains(name)
        || AlBuiltinMethods.ReportMethods.Contains(name)
        || AlBuiltinMethods.XmlportMethods.Contains(name)
        || AlBuiltinMethods.QueryMethods.Contains(name)
        || AlBuiltinMethods.CommonMethods.Contains(name)
        || AlBuiltinMethods.TextMethods.Contains(name)
        || AlBuiltinMethods.CollectionMethods.Contains(name)
        || AlBuiltinMethods.JsonMethods.Contains(name);

    /// <summary>
    /// Lazily resolves the file owner's <see cref="AlTypeRef"/> via
    /// the resolver. Cached because bare-self-call detection can fire
    /// many times per file and the resolver lookup walks a dictionary
    /// each call.
    /// </summary>
    public AlTypeRef? OwnerType()
    {
        if (_state.OwnerTypeResolved) return _state.OwnerTypeCache;
        _state.OwnerTypeResolved = true;
        if (string.IsNullOrEmpty(_state.Ctx.OwnerName)) return null;
        // Pass the owner's catalog kind as the hint so bare self-calls
        // on a pageextension named the same as its base page (or any
        // similarly-named extension over its base) land on the
        // extension's own members, not on the base object's. Without
        // the hint, the resolver's non-extension preference would
        // pick the base, which is the wrong receiver for self-calls.
        _state.OwnerTypeCache = _state.Ctx.Resolver.ResolveTypeByName(_state.Ctx.OwnerName, _state.Ctx.OwnerKind);
        return _state.OwnerTypeCache;
    }

    /// <summary>
    /// Lazily resolves Rec's <see cref="AlTypeRef"/> — the receiver
    /// type implicit-field-access uses. For tables / tableextensions
    /// this is the owner type; for pages / pageextensions it's the
    /// SourceTable. <see cref="BuildAndPushGlobalScope"/> already
    /// encoded the choice in the bottom scope frame's <c>rec</c>
    /// entry; we read it back and resolve once.
    /// </summary>
    /// <summary>
    /// Resolves the base object an extension targets — the base page for a
    /// pageextension, the base report for a reportextension, etc. — via
    /// <see cref="AlExtractContext.OwnerExtendsName"/>. Returns null for
    /// non-extension owners or when the base name isn't set. Distinct from
    /// <see cref="RecType"/>: a pageextension's Rec is the source TABLE,
    /// while the base is the PAGE itself, and procedures declared on the
    /// page live there (not on Rec). Cached for the same reason as
    /// <see cref="OwnerType"/> — bare-self-calls can fire many times per
    /// file and the resolver lookup walks a dictionary each call.
    /// </summary>
    public AlTypeRef? BaseObjectType()
    {
        if (_state.BaseObjectTypeResolved) return _state.BaseObjectTypeCache;
        _state.BaseObjectTypeResolved = true;
        if (string.IsNullOrEmpty(_state.Ctx.OwnerExtendsName)) return null;
        var baseKind = _state.Ctx.OwnerKind?.ToLowerInvariant() switch
        {
            "pageextension" => "page",
            "tableextension" => "table",
            "reportextension" => "report",
            "enumextension" => "enum",
            "permissionsetextension" => "permissionset",
            _ => null,
        };
        if (baseKind is null) return null;
        _state.BaseObjectTypeCache = _state.Ctx.Resolver.ResolveTypeByName(_state.Ctx.OwnerExtendsName, baseKind);
        return _state.BaseObjectTypeCache;
    }

    public AlTypeRef? RecType()
    {
        if (_state.RecTypeResolved) return _state.RecTypeCache;
        _state.RecTypeResolved = true;
        // Active dataitem source wins: inside a report/query/xmlport
        // dataitem body, AL binds Rec to THAT dataitem's source table,
        // shadowing any object-level Rec binding. The structure
        // extractor pushes the source on the stack on dataitem entry
        // and pops on its `}`, so a sibling dataitem's body sees its
        // own source — not the previous sibling's. This is what keeps
        // bare-self-call dispatch from leaking across siblings (see
        // Sibling_dataitem_does_not_leak_into_next_dataitem_scope).
        // Pre-cache via RecTypeCache so subsequent calls in the same
        // block don't re-query the resolver per token.
        var di = _state.CurrentDataItemSource;
        if (di is not null)
        {
            _state.RecTypeCache = di;
            return di;
        }
        // Walk INNERMOST-first: in normal code only the object-scope frame
        // binds `rec`, so this lands the same place as before; but a
        // `with X do begin` pushes a frame whose `rec` shadows the object's
        // for the duration of the block, and innermost-first lets that
        // override take effect.
        ResolvedVariableType? declared = null;
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.TryGetValue("rec", out var found))
            {
                declared = found;
                break;
            }
        }
        if (declared is null) return null;
        if (string.IsNullOrEmpty(declared.TypeName)) return null;
        // Pass the keyword: Rec on a page is `Record` → must be a
        // table (not a tableextension someone named the same way).
        _state.RecTypeCache = _state.Ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
        return _state.RecTypeCache;
    }

    // ── Label declarations + uses ───────────────────────────────────

    private static readonly HashSet<string> ObjectSectionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "procedure", "trigger", "fields", "actions", "keys",
        "layout", "dataset", "schema", "elements", "controls",
        "requestpage", "labels",
    };

    /// <summary>
    /// Object-scope <c>var</c> block scanner. Walks each
    /// <c>Name[, Name]*: Type[ "X"];</c> declaration and adds the
    /// variable to the bottom (object-scope) frame so bare uses in
    /// any procedure / trigger body in the file resolve through the
    /// scope-walk that fields and locals already use.
    ///
    /// Why source-side scanning when the symbol package already
    /// supplies <see cref="AlExtractContext.GlobalVars"/>? Globals
    /// declared inside a feature-flag <c>#if X / #endif</c> block
    /// (BC pattern: <c>#if not CLEAN26 ... var UpgradeTagDefinitions:
    /// Codeunit "Upgrade Tag Definitions"; ... #endif</c>) drop out
    /// of the symbol package whenever the flag isn't the live one
    /// the compiler chose. Without source-side fallback, every
    /// procedure-body reference to those vars fires head-not-a-
    /// variable. Bottom-frame writes are gap-fills only — symbol-
    /// package entries from <see cref="BuildAndPushGlobalScope"/>
    /// stay authoritative when both list the same name.
    ///
    /// Terminates at the object's closing <c>}</c> or the next
    /// section keyword (<c>procedure</c>, <c>trigger</c>,
    /// <c>fields</c>, …). The method name is kept (was
    /// label-only originally) so existing callers still link.
    /// </summary>
    public void ScanObjectScopeLabels()
    {
        _state.Pos++; // var keyword

        // Find the bottom (object-scope) frame to mutate.
        ScopeFrame? bottom = null;
        foreach (var f in _state.ScopeStack) bottom = f;
        if (bottom is null) return;

        int braceDepth = 0;
        while (_state.Pos < _state.Tokens.Count)
        {
            var tok = _state.Tokens[_state.Pos];

            if (tok.Kind == AlTokenKind.Punct)
            {
                if (tok.Value == "{") { braceDepth++; _state.Pos++; continue; }
                if (tok.Value == "}")
                {
                    if (braceDepth == 0) return;
                    braceDepth--;
                    _state.Pos++;
                    continue;
                }
                if (tok.Value == "[")
                {
                    // Attribute on a declaration — `[NonDebuggable]`,
                    // `[SecurityFiltering(...)]`, etc. Skip the
                    // bracketed span so the attribute's inner
                    // identifier isn't mis-read as a variable name.
                    _state.SkipAttribute();
                    continue;
                }
            }
            if (braceDepth == 0
                && tok.Kind == AlTokenKind.Identifier
                && ObjectSectionKeywords.Contains(tok.Value))
            {
                return;
            }

            // `Name[, Name]*: Type[ "X"];` — capture every declaration
            // shape so codeunit / record / page / interface / scalar /
            // array globals all land in the bottom frame. Quoted-
            // identifier names are legal in AL at object scope too.
            if (tok.Kind != AlTokenKind.Identifier && tok.Kind != AlTokenKind.QuotedIdentifier)
            {
                _state.Pos++;
                continue;
            }

            var names = new List<string>();
            int nameStart = _state.Pos;
            while (_state.Pos < _state.Tokens.Count
                   && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                       || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
            {
                names.Add(_state.Tokens[_state.Pos].Value);
                _state.Pos++;
                if (_state.At(",")) { _state.Pos++; continue; }
                break;
            }
            if (!_state.At(":"))
            {
                // Not a declaration. Advance past the name(s) we
                // just consumed; loop continues. (Avoids re-reading
                // the same identifier and looping forever.)
                if (_state.Pos == nameStart) _state.Pos++;
                continue;
            }
            _state.Pos++; // past `:`

            var type = ReadTypeReference();
            foreach (var n in names)
            {
                var key = n.ToLowerInvariant();
                if (!bottom.Vars.ContainsKey(key)) bottom.Vars[key] = type;
            }

            if (_state.At(";")) _state.Pos++;
        }
    }

    /// <summary>
    /// Emits a <c>label_use</c> reference when the bare identifier
    /// resolves to an in-scope variable typed <c>Label</c>. Target
    /// is the file's owner object + the label's name + kind
    /// <c>"label"</c>, so the existing member-scoped query (and
    /// import-time symbol-id stamping) lights up the Find references
    /// path identical to a procedure call.
    /// </summary>
    public bool TryConsumeLabelUse()
    {
        var tok = _state.Tokens[_state.Pos];
        var key = tok.Value.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (!frame.Vars.TryGetValue(key, out var declared)) continue;
            if (!string.Equals(declared.TypeName, "Label", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var owner = OwnerType();
            if (owner is null) return false;
            _state.EmitResolved(tok.Line, tok.Column, owner, tok.Value, "label", "label_use");
            _state.Pos++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Implicit-Rec field access: a bare identifier or quoted
    /// identifier inside a body, with no qualifier. When the owner
    /// kind exposes Rec and the identifier matches a field on Rec's
    /// type, emit a <c>field_access</c> reference targeting that
    /// type. Followed-by-paren / followed-by-dot / followed-by-
    /// double-colon shapes are filtered by the caller (those go
    /// through the chain / typed-literal / bare-call paths).
    ///
    /// Filters (in order):
    ///   1. Rec must be in scope (RecType != null).
    ///   2. The identifier must not already match an in-scope variable
    ///      (parameter / local / global). AL resolves variables
    ///      first; we mirror that.
    ///   3. Bare identifiers (not quoted): drop statement keywords
    ///      (<c>if</c>, <c>then</c>, <c>not</c>, …), AL system
    ///      functions (<c>Message</c>, <c>Today</c>, …), and AL
    ///      literal-shaped keywords (<c>true</c>, <c>false</c>, …)
    ///      via <see cref="AlBuiltinMethods.IsStatementKeyword"/> /
    ///      <see cref="AlBuiltinMethods.IsBareCallable"/>.
    ///      Quoted identifiers bypass these — they're almost always
    ///      field names in AL bodies.
    ///   4. The identifier must resolve to a member on Rec's type
    ///      with kind = <c>field</c>. A method match doesn't emit
    ///      (those go through the bare-self-call path when
    ///      followed by parens, or are silently dropped here).
    ///
    /// Known limitation: inside a <c>with X do …</c> block the
    /// implicit receiver is X, not Rec. Gap #5 in
    /// <c>al-reference-extractor-gaps.md</c>; this handler would
    /// emit on Rec's type instead of X's. False positive only fires
    /// when both X and Rec have a same-named field — usually
    /// resolves to a silent drop.
    ///
    /// Returns true when the token was handled (cursor advanced),
    /// false when the caller should fall through to the default
    /// "advance one token" path.
    /// </summary>
    public bool TryConsumeImplicitFieldAccess()
    {
        var head = _state.Tokens[_state.Pos];
        var name = head.Value;

        // 1. Rec must exist.
        var rec = RecType();
        if (rec is null) return false;

        // 2. Skip when name is an in-scope variable.
        var key = name.ToLowerInvariant();
        if (_state.IsVarInScope(key)) return false;

        // 3. For bare identifiers, additional keyword / system-function
        //    filters. Quoted identifiers bypass — they're field names.
        if (head.Kind == AlTokenKind.Identifier)
        {
            if (AlBuiltinMethods.IsStatementKeyword(name)) return false;
            if (AlBuiltinMethods.IsBareCallable(name)) return false;
        }

        // 4. Resolve as a field on Rec's type. Methods don't qualify
        //    here — those need parens to be a call site, and bare
        //    field-shaped accesses are what we're after.
        var member = _state.Ctx.Resolver.ResolveMember(rec, name);
        if (member is null) return false;
        if (!AlExtractionState.IsFieldKind(member.Kind))
        {
            return false;
        }

        var targetOwner = member.DeclaringType ?? rec;
        _state.EmitResolved(head.Line, head.Column, targetOwner, member.Name, member.Kind, "field_access");
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Bare identifier in a procedure body that resolves to an
    /// object-scope global variable (not a local, not a Label, not
    /// Rec / xRec). Emits a <c>variable_use</c> reference targeted at
    /// the file's owner with <c>TargetMemberName = &lt;var&gt;</c>;
    /// the import-side stamps the actual <c>TargetVariableId</c> from
    /// <c>oe_module_variables</c>. Right-click "Find references" on
    /// a global then returns these rows in a single seek (filtered
    /// index on <c>target_variable_id</c>).
    ///
    /// Filters (in order):
    ///   1. Must be inside a procedure / trigger body
    ///      (<c>ScopeStack.Count &gt; 1</c>).
    ///   2. Name must resolve in the GLOBAL frame and NOT in any
    ///      inner (procedure-local / parameter) frame — locals
    ///      shadow globals in AL, same as every block-scoped
    ///      language.
    ///   3. Skip the special <c>rec</c> / <c>xrec</c> entries that
    ///      <see cref="BuildAndPushGlobalScope"/> seeds — they're
    ///      implicit-receiver shortcuts, not user-declared globals.
    ///   4. Skip Label-typed entries; <see cref="TryConsumeLabelUse"/>
    ///      runs ahead of this and handles them as <c>label_use</c>.
    ///
    /// Returns true when the token was consumed (cursor advanced),
    /// false to fall through. See
    /// <c>.design/al-reference-extractor-refactor.md</c> step 6.
    /// </summary>
    public bool TryConsumeGlobalVariableUse()
    {
        if (_state.ScopeStack.Count <= 1) return false;

        var tok = _state.Tokens[_state.Pos];
        var name = tok.Value;
        var key = name.ToLowerInvariant();

        // rec / xrec are special — not navigable globals.
        if (string.Equals(key, "rec", StringComparison.Ordinal)
            || string.Equals(key, "xrec", StringComparison.Ordinal))
        {
            return false;
        }

        // Walk innermost-first. The global frame is the LAST one
        // iterated. Hits in non-global frames mean the name is a
        // local / parameter — silently consume.
        ScopeFrame? matchFrame = null;
        ResolvedVariableType? matchType = null;
        ScopeFrame? globalFrame = null;
        foreach (var frame in _state.ScopeStack)
        {
            globalFrame = frame; // last-iterated wins
            if (matchFrame is null && frame.Vars.TryGetValue(key, out var declared))
            {
                matchFrame = frame;
                matchType = declared;
            }
        }
        if (matchFrame is null) return false;
        if (!ReferenceEquals(matchFrame, globalFrame)) return false;

        // Labels run through TryConsumeLabelUse before we get here,
        // but a bare Label identifier whose label-use path bailed
        // (e.g. no owner type) could still land here. Defer the
        // emission to the label path.
        if (matchType is not null
            && string.Equals(matchType.TypeName, "Label", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var owner = OwnerType();
        if (owner is null) return false;

        _state.EmitResolved(tok.Line, tok.Column, owner, name, "global_variable", "variable_use");
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Resolves a bare identifier (typically quoted) as a field
    /// access on the current field-receiver context — set by
    /// <see cref="WalkArgsForBuiltin"/> while walking the arg list of
    /// a record built-in that takes field names (Validate / SetRange
    /// / FieldNo / CalcFields / …). Without this hook, inside
    /// <c>Item.FieldNo("Qty. on Assembly Order")</c> the bare quoted
    /// id <c>"Qty. on Assembly Order"</c> would fall through every
    /// dispatch (no chain head, no Rec for a codeunit owner, no
    /// matching scope variable) and no field_access reference would
    /// emit — Find references on the field's declaration row (often
    /// on a tableextension like <c>Asm. Item</c>) would miss the
    /// call. Returns true when consumed; false to fall through.
    ///
    /// Skips when the identifier matches a local / parameter /
    /// global variable in scope — AL's resolution prefers variable
    /// values over field-name string-coercion in those positions.
    /// </summary>
    public bool TryResolveFieldReceiverContext(AlToken tok)
    {
        var receiver = _state.CurrentFieldReceiver;
        if (receiver is null) return false;

        var name = tok.Value;
        var key = name.ToLowerInvariant();
        if (_state.IsVarInScope(key)) return false;

        var member = _state.Ctx.Resolver.ResolveMember(receiver, name);
        if (member is null) return false;
        if (!AlExtractionState.IsFieldKind(member.Kind)) return false;

        var targetOwner = member.DeclaringType ?? receiver;
        _state.EmitResolved(tok.Line, tok.Column, targetOwner, member.Name, member.Kind, "field_access");
        _state.Pos++;
        return true;
    }

    /// <summary>
    /// Handles a chain whose head turned out not to be a variable or
    /// a catalog type, but matches a field on the implicit Rec
    /// receiver. Emits a <c>field_access</c> at the head, advances
    /// the receiver to the field's return type (an enum for
    /// <c>"Document Type"</c>, a record for FK-typed fields, etc.),
    /// and continues the chain via
    /// <see cref="WalkMemberChain"/>. Returns true when the head
    /// resolved as a Rec field — the caller is done; false to let
    /// the existing unresolved diagnostic fire.
    /// </summary>
    private bool TryConsumeImplicitRecFieldChainHead(AlToken head)
    {
        // First try Rec (the owner's bound table). Then fall back to
        // the current dataitem source for report / query / xmlport
        // owners. AL binds Rec to the report itself (not the dataitem
        // source) at object scope, so inside an `OnAfterGetRecord`
        // trigger of `dataitem(X; "Sales Header")` a bare quoted
        // field like `"Document Type"` resolves on `"Sales Header"`,
        // not on the report. The bare-self-call resolver already had
        // this fallback (PR #254); mirror it here for bare field
        // chain heads — `"Account Type".AsInteger()`,
        // `"Attachment File".HasValue()`, `Type.AsInteger()` all
        // surface inside dataitem triggers and fired
        // head-not-a-variable before this fix.
        var rec = RecType();
        AlMember? member = null;
        AlTypeRef? receiver = null;
        if (rec is not null)
        {
            member = _state.Ctx.Resolver.ResolveMember(rec, head.Value);
            if (member is not null && AlExtractionState.IsFieldKind(member.Kind))
            {
                receiver = rec;
            }
            else
            {
                member = null;
            }
        }
        if (member is null)
        {
            // Walk innermost-to-outermost across active dataitem
            // sources. Same rationale as the bare-self-call fallback:
            // a nested dataitem trigger that reads an implicit-Rec
            // field defined on the parent dataitem's source table
            // still gets that field resolved.
            foreach (var di in _state.ActiveDataItemSources())
            {
                if (rec is not null && di.Equals(rec)) continue;
                var diMember = _state.Ctx.Resolver.ResolveMember(di, head.Value);
                if (diMember is not null && AlExtractionState.IsFieldKind(diMember.Kind))
                {
                    member = diMember;
                    receiver = di;
                    break;
                }
            }
        }
        if (member is null || receiver is null) return false;

        var targetOwner = member.DeclaringType ?? receiver;
        _state.EmitResolved(head.Line, head.Column, targetOwner, member.Name, member.Kind, "field_access");

        WalkMemberChain(AdvanceReceiverByMember(member));
        return true;
    }

    /// <summary>
    /// Resolves the head identifier of a member chain. Returns the
    /// receiver type on success; null when neither the variable
    /// lookup nor the catalog lookup found a type.
    /// <paramref name="declaredAsVar"/> reports the in-scope declaration
    /// for diagnostic purposes when the catalog lookup of a declared
    /// variable's type fails — letting the caller distinguish
    /// "head wasn't a variable at all" from "head was a variable
    /// but its declared type doesn't resolve" (which usually means a
    /// visibility / dependency-graph issue worth surfacing).
    /// </summary>
    private AlTypeRef? ResolveHeadType(AlToken head, out ResolvedVariableType? declaredAsVar)
    {
        declaredAsVar = null;

        // AL `this` keyword (BC 24+) — refers to the current object
        // instance. `this.Member` from inside a codeunit / report /
        // page / table dispatches against the OwnerType. Without
        // this short-circuit, every `this.Foo()` chain in the
        // System Application / Base App files that adopted the new
        // syntax fires head-not-a-variable.
        if (string.Equals(head.Value, "this", StringComparison.OrdinalIgnoreCase))
        {
            return OwnerType();
        }

        // Step 1: walk the scope stack innermost-first.
        var name = head.Value.ToLowerInvariant();
        foreach (var frame in _state.ScopeStack)
        {
            if (frame.Vars.TryGetValue(name, out var declared))
            {
                declaredAsVar = declared;
                // Variables typed with an AL object keyword
                // (Record Customer, Codeunit "Sales-Post") resolve
                // to a type in the catalog. Variables typed with a
                // bare identifier (HttpClient, JsonObject) don't.
                // Pass the keyword through as a kind hint — `Record`
                // means "table", `Codeunit` means "codeunit", etc.
                // Disambiguates name collisions across kinds.
                if (string.IsNullOrEmpty(declared.TypeName)) return null;
                // A bare built-in type name (File, View, Version, …) names
                // the AL runtime type, NOT a same-named catalog table — AL
                // requires the `Record` keyword for table-typed variables.
                // Without this guard `var ExportFile: File` resolves to the
                // System "File" table and every `ExportFile.WriteMode := true`
                // / `.Create(...)` fires chain-step. Returning null routes
                // through the KnownSystemType branch in the caller (the
                // diagnostic still sees declaredAsVar for context).
                if (string.IsNullOrEmpty(declared.Keyword)
                    && AlBuiltinMethods.IsKnownSystemType(declared.TypeName))
                {
                    return null;
                }
                // DotNet variables (`var File: DotNet File;`) name a .NET
                // type whose members live outside the AL catalog. The
                // keyword carries the DotNet hint, but the type name
                // sometimes collides with an AL platform table — `File`
                // also names the virtual id=2000000022 table, and
                // `Convert` collides with nothing but would still false-
                // resolve if anyone added that table later. Short-circuit
                // before the catalog lookup; the caller's keyword=="DotNet"
                // branch silences the chain without emitting unresolved.
                if (string.Equals(declared.Keyword, "DotNet", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return _state.Ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
            }
        }

        // Step 2: Rec-field collision check. Inside a Rec-bearing
        // owner (table / page / extension), a bare head that also
        // names a Rec field shadows any catalog name match — AL
        // resolves the field, not a same-named codeunit. Canonical
        // sample: <c>Image.ImportFile(...)</c> on
        // <c>Customer.Table.al</c> where <c>Image</c> is a Media-typed
        // field on Customer AND a Codeunit "Image" exists in System
        // Application. Without this short-circuit the codeunit wins
        // the catalog lookup and the .ImportFile chain step fires
        // chain-step unresolved on a codeunit that doesn't expose it.
        // Returning null with declaredAsVar populated routes through
        // the known-system-type silence path on Media / Text /
        // Decimal / etc.
        var rec = RecType();
        if (rec is not null)
        {
            var recField = _state.Ctx.Resolver.ResolveMember(rec, head.Value);
            if (recField is not null && AlExtractionState.IsFieldKind(recField.Kind))
            {
                // Stash the field's runtime type so the silence path
                // can recognise it. Most table-field types are
                // scalars (Code/Text/Decimal/etc.) — runtime types
                // like Media / MediaSet / Blob land here too. The
                // chain walker treats these as terminal.
                declaredAsVar = new ResolvedVariableType(
                    null, recField.ReturnTypeName ?? string.Empty);
                return null;
            }
        }

        // Step 3: head IS a type name (Customer.Insert pattern).
        return _state.Ctx.Resolver.ResolveTypeByName(head.Value);
    }

    private AlTypeRef? AdvanceReceiverByMember(AlMember member)
    {
        // Fields with a Record-keyword type continue the chain;
        // scalar types (Code, Text, Decimal, etc.) terminate it.
        // Procedures with a known return type continue the chain;
        // procedures with no return / scalar return terminate.
        if (string.IsNullOrEmpty(member.ReturnTypeName)) return null;
        // Pass the return-type keyword so the resolver picks the
        // correct kind when the name collides across kinds. Canonical
        // sample: `EDocument.GetEDocumentService()` returns
        // <c>Record "E-Document Service"</c>, and BC ships BOTH a
        // table AND a page named "E-Document Service". Without the
        // hint the resolver could land on the page and every
        // subsequent chain step (.GetImportProcessVersion, etc.)
        // misses against the wrong receiver.
        return _state.Ctx.Resolver.ResolveTypeByName(
            member.ReturnTypeName, member.ReturnTypeKeyword);
    }

    /// <summary>
    /// When a chain step's member lookup misses on the owner type,
    /// fall back to the owner's global vars / labels. Globals live in
    /// <see cref="AlExtractContext.GlobalVars"/> (mirrored into the
    /// bottom scope frame by <see cref="BuildAndPushGlobalScope"/>);
    /// labels added by <see cref="ScanObjectScopeLabels"/> land there
    /// too. Neither shape is in <c>oe_module_members</c>, so the
    /// resolver's lookup misses even though the reference is legal AL
    /// — `this.MyGlobal` / `Rec.MyLabel` from inside the owner.
    /// <para/>
    /// Returns true when the name matched a global / label, with
    /// <paramref name="nextReceiver"/> set to the resolved type when
    /// the global's declared type lives in the catalog
    /// (Record / Codeunit / Page / …) so the chain can continue, or
    /// null when the type is scalar / Label / unresolvable (in which
    /// case the chain ends here). Returns false when the receiver
    /// isn't the owner, or the name doesn't match any global.
    /// </summary>
    private bool TryAdvanceToOwnerGlobal(AlTypeRef receiverType, string memberName, out AlTypeRef? nextReceiver)
    {
        nextReceiver = null;
        var owner = OwnerType();
        if (owner is null || !receiverType.Equals(owner)) return false;

        // Bottom scope frame holds Ctx.GlobalVars plus any labels
        // scanned by ScanObjectScopeLabels — both are valid `this.X`
        // / `Rec.X` targets. Stack iterates top→bottom, so the last
        // entry is the bottom.
        ScopeFrame? bottom = null;
        foreach (var frame in _state.ScopeStack) bottom = frame;
        if (bottom is null) return false;

        var key = memberName.ToLowerInvariant();
        if (!bottom.Vars.TryGetValue(key, out var declared)) return false;

        // The global matched; try to resolve its declared type so the
        // chain can continue. Scalars / Labels (empty TypeName) leave
        // nextReceiver null — the chain walker's next iteration will
        // see receiverType=null and AdvancePastChain.
        if (!string.IsNullOrEmpty(declared.TypeName))
        {
            nextReceiver = _state.Ctx.Resolver.ResolveTypeByName(declared.TypeName, declared.Keyword);
        }
        return true;
    }

    /// <summary>
    /// When a chain step's member lookup misses on the owner type
    /// AND the owner is a reportextension / pageextension, walk to
    /// the base object and re-try the lookup. AL merges extension and
    /// base scopes at dispatch time, so `this.GetLocation(...)` on a
    /// reportextension's body can call a procedure declared on the
    /// base report. The bare-self-call path already handles this; the
    /// chain walker mirrors it here for explicit-`this` dispatch.
    /// Emits the reference at the base object's declaration when the
    /// lookup succeeds. Returns true when consumed (cursor stays put;
    /// caller continues the chain on the advanced receiver), false
    /// when the receiver isn't the owner, the owner isn't an
    /// extension kind, or the base doesn't expose the member.
    /// </summary>
    private bool TryAdvanceToBaseObject(
        AlTypeRef receiverType, AlToken memberTok, bool followedByParen,
        out AlTypeRef? nextReceiver)
    {
        nextReceiver = null;
        var owner = OwnerType();
        if (owner is null || !receiverType.Equals(owner)) return false;
        var k = _state.Ctx.OwnerKind?.ToLowerInvariant();
        if (k != "reportextension" && k != "pageextension") return false;

        var baseType = BaseObjectType();
        if (baseType is null) return false;
        var member = _state.Ctx.Resolver.ResolveMember(baseType, memberTok.Value);
        if (member is null) return false;

        var targetOwner = member.DeclaringType ?? baseType;
        var refKind = followedByParen || AlKindKeywords.IsCallableMemberKind(member.Kind)
            ? "method_call"
            : "field_access";
        _state.EmitResolved(memberTok.Line, memberTok.Column, targetOwner, member.Name, member.Kind, refKind);
        nextReceiver = AdvanceReceiverByMember(member);
        return true;
    }

    /// <summary>
    /// Skips zero or more balanced <c>[…]</c> bracket spans
    /// (multi-dimensional array indexing, e.g. <c>Var[2]</c> or
    /// <c>Matrix[2][3]</c>). Called after the chain head's identifier
    /// so the WalkMemberChain loop sees the trailing <c>.</c> directly.
    /// Receiver type stays pinned to the variable's declared type —
    /// AL arrays carry the element type as the variable's declared
    /// type already (the <c>array[N] of</c> modifier is peeled off
    /// by ReadTypeReference), so no further adjustment is needed.
    /// </summary>
    private void SkipBalancedBracketSpans()
    {
        while (_state.Pos < _state.Tokens.Count && _state.At("["))
        {
            int depth = 1;
            _state.Pos++;
            while (_state.Pos < _state.Tokens.Count && depth > 0)
            {
                if (_state.At("[")) depth++;
                else if (_state.At("]")) depth--;
                _state.Pos++;
            }
        }
    }

    private void AdvancePastChain()
    {
        // We've already advanced past the head. Walk through any
        // `.member` follow-ons, `::EnumValue` typed-literal steps,
        // and method-call argument lists so the main loop doesn't
        // re-enter and double-count. Without the `::Value` branch,
        // a dead-receiver chain like
        //   Var."Document Type"::Quote.AsInteger()
        // (where Var resolved but the field's type didn't) would
        // bail after `."Document Type"` and the main dispatch would
        // pick `Quote` up as a fresh chain head.
        while (_state.Pos < _state.Tokens.Count)
        {
            if (_state.At("."))
            {
                _state.Pos++; // .
                if (_state.Pos < _state.Tokens.Count
                    && (_state.Tokens[_state.Pos].Kind == AlTokenKind.Identifier
                        || _state.Tokens[_state.Pos].Kind == AlTokenKind.QuotedIdentifier))
                {
                    _state.Pos++;
                }
                if (_state.Pos < _state.Tokens.Count && _state.At("(")) _state.SkipBalancedParens();
                continue;
            }
            if (_state.Pos + 1 < _state.Tokens.Count
                && _state.Tokens[_state.Pos].Kind == AlTokenKind.DoubleColon
                && (_state.Tokens[_state.Pos + 1].Kind == AlTokenKind.Identifier
                    || _state.Tokens[_state.Pos + 1].Kind == AlTokenKind.QuotedIdentifier))
            {
                _state.Pos += 2;
                continue;
            }
            break;
        }
    }

    /// <summary>
    /// Emits a <c>field_access</c> reference at <paramref name="tok"/>
    /// when its value resolves to a field on <paramref name="receiver"/>.
    /// Silent no-op otherwise so callers can scan permissively. Used
    /// by the orchestrator's object-scope property handlers
    /// (DataCaptionFields, CalcFormula, SourceTableView's where/sorting
    /// clauses) which all emit field references against a known
    /// receiver type.
    /// </summary>
    public void EmitFieldAccessIfResolves(AlToken tok, AlTypeRef receiver)
    {
        var member = _state.Ctx.Resolver.ResolveMember(receiver, tok.Value);
        if (member is null) return;
        if (!AlExtractionState.IsFieldKind(member.Kind)) return;
        var targetOwner = member.DeclaringType ?? receiver;
        _state.EmitResolved(tok.Line, tok.Column, targetOwner, member.Name, member.Kind, "field_access");
    }
}
