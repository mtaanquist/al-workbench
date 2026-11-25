namespace ALDevToolbox.Services.Cal;

/// <summary>A resolved C/AL object type a receiver points at, by (kind, id).</summary>
public readonly record struct CalTypeRef(string Kind, int Id);

/// <summary>
/// The variable / Rec scope a body executes in. Built by
/// <see cref="ALDevToolbox.Services.ObjectExplorer.Import.CalImportService"/> from the
/// owner object's globals plus the procedure's parameters and locals.
/// </summary>
public sealed class CalExtractScope
{
    /// <summary>Variable name → the object type it's declared as (Record/Codeunit/Page/…), case-insensitive.</summary>
    public Dictionary<string, CalTypeRef> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The object owning the body — bare <c>Proc(...)</c> calls resolve here.</summary>
    public string? OwnerKind { get; init; }
    public int? OwnerId { get; init; }

    /// <summary>What <c>Rec</c> / <c>xRec</c> / an implicit bare <c>"Field"</c> binds to (a table). Null for codeunits.</summary>
    public CalTypeRef? Rec { get; init; }
}

/// <summary>
/// One extracted call-site reference. Normally identified by
/// <paramref name="TargetId"/>, with the target name filled in by the import's
/// id→name post-pass. Static object receivers (<c>CODEUNIT::"Sales-Post"</c>)
/// are the inverse: the source names the object but not its id, so they carry
/// <paramref name="TargetName"/> and a null <paramref name="TargetId"/>, and the
/// import resolves the id from the name instead.
/// </summary>
public sealed record CalRef(
    int Line, int Column,
    string TargetKind, int? TargetId,
    string? MemberName, string? MemberKind,
    string ReferenceKind,
    string? TargetName = null);

public sealed record CalExtractionResult(
    IReadOnlyList<CalRef> References,
    int UnresolvedReceivers,
    IReadOnlyList<CalRef> SystemReferences);

/// <summary>
/// Walks a C/AL procedure / trigger body and emits <c>method_call</c> and
/// <c>field_access</c> references to other objects, resolving receivers by id
/// through the supplied <see cref="CalExtractScope"/>. The statement-level
/// analogue of the AL <c>AlReferenceExtractor</c>'s member-chain core — it does
/// not re-parse object structure (the C/AL parser already did that), so it only
/// needs the body text + scope.
///
/// <para>Resolves the common, high-value shapes:</para>
/// <list type="bullet">
///   <item><c>Receiver.Method(...)</c> → method_call on the receiver's type
///     (unless Method is a runtime built-in, in which case it's skipped — and
///     if it's a field-name-taking built-in like <c>SETRANGE</c>, the first
///     argument is emitted as a field_access).</item>
///   <item><c>Receiver."Field"</c> / <c>Receiver.Field</c> → field_access.</item>
///   <item>bare <c>Proc(...)</c> → method_call on the owner object.</item>
///   <item>bare <c>"Field"</c> and bare field-name-taking built-ins
///     (<c>TESTFIELD("No.")</c>) → field_access on the implicit <c>Rec</c>.</item>
///   <item>static object receivers (<c>CODEUNIT::"Sales-Post"</c>,
///     <c>DATABASE::Customer</c>, <c>PAGE::"Customer List"</c>) →
///     property_object on the named object.</item>
/// </list>
/// Deeper chains (<c>a.b.c</c>) resolve only the first hop; option access
/// (<c>x::Value</c>) and unresolved receivers are skipped (and counted for the
/// import diagnostic).
/// </summary>
public static class CalReferenceExtractor
{
    public static CalExtractionResult Extract(string body, CalExtractScope scope)
    {
        var refs = new List<CalRef>();
        var sysRefs = new List<CalRef>();
        int unresolved = 0;
        var t = CalLexer.Tokenize(body);

        for (int k = 0; k < t.Count; k++)
        {
            var tok = t[k];
            if (tok.Kind != CalTokenKind.Identifier && tok.Kind != CalTokenKind.QuotedIdentifier)
                continue;
            // A token already consumed as a member (preceded by '.') isn't a head.
            if (k > 0 && t[k - 1].Kind == CalTokenKind.Dot) continue;
            // ── Static object receiver (CODEUNIT::"Sales-Post") ─────────
            // and option value qualifier (x::Value), which share the `::` shape.
            if (k + 1 < t.Count && t[k + 1].Kind == CalTokenKind.ColonColon)
            {
                if (tok.Kind == CalTokenKind.Identifier
                    && StaticReceiverKinds.TryGetValue(tok.Text, out var staticKind)
                    && TryEmitStaticObjectRef(t, k, staticKind, refs, sysRefs))
                {
                    k += 2; // consume `::` and the object name together
                    continue;
                }
                // A plain option value (x::Open) — the head enumerates an
                // option, not a reference.
                k++;
                continue;
            }
            if (tok.Kind == CalTokenKind.Identifier && CalBuiltinMethods.IsKeyword(tok.Text)) continue;

            var next = Peek(t, k + 1);

            // ── Receiver.Member ─────────────────────────────────────────
            if (next.Kind == CalTokenKind.Dot)
            {
                var member = Peek(t, k + 2);
                if (member.Kind is not (CalTokenKind.Identifier or CalTokenKind.QuotedIdentifier)) continue;

                var receiver = ResolveHead(tok, scope);
                if (receiver is null)
                {
                    if (!CalBuiltinMethods.IsStaticReceiver(tok.Text)) unresolved++;
                    k += 1; // step past the head; the dot/member get re-evaluated harmlessly
                    continue;
                }

                var afterMember = Peek(t, k + 3);
                if (afterMember.Kind == CalTokenKind.LParen)
                {
                    if (CalBuiltinMethods.IsReceiverMethod(member.Text))
                    {
                        // Built-in like Cust.INSERT() — a system reference (#279).
                        sysRefs.Add(new CalRef(member.Line, member.Column,
                            receiver.Value.Kind, receiver.Value.Id, member.Text, "system", "method_call"));
                        if (CalBuiltinMethods.IsFieldNameTaking(member.Text))
                            EmitFieldArg(t, k + 3, receiver.Value, refs);
                    }
                    else
                    {
                        refs.Add(new CalRef(member.Line, member.Column,
                            receiver.Value.Kind, receiver.Value.Id, member.Text, "procedure", "method_call"));
                    }
                }
                else if (CalBuiltinMethods.IsReceiverMethod(member.Text))
                {
                    // Paren-less built-in: `Cust.INSERT;` / `Rec.MODIFY;` are
                    // calls, and classic C/AL writes them without parentheses
                    // more often than not. They used to be dropped entirely —
                    // now they land in the system-reference table like their
                    // parenthesised twin (issue #712).
                    sysRefs.Add(new CalRef(member.Line, member.Column,
                        receiver.Value.Kind, receiver.Value.Id, member.Text, "system", "method_call"));
                }
                else
                {
                    // Receiver.Field (read) — or a paren-less call to a
                    // procedure declared on the receiver, which reads the same
                    // in C/AL. The import's post-pass reclassifies the row to
                    // method_call when the name matches a procedure on the
                    // target object (issue #712); it can't be decided here
                    // because the walker sees one object at a time.
                    refs.Add(new CalRef(member.Line, member.Column,
                        receiver.Value.Kind, receiver.Value.Id, member.Text, "field", "field_access"));
                }
                k += 2; // advance to the member; loop ++ moves past it
                continue;
            }

            // ── bare Name(...) ──────────────────────────────────────────
            if (next.Kind == CalTokenKind.LParen && tok.Kind == CalTokenKind.Identifier)
            {
                if (CalBuiltinMethods.IsBareFunction(tok.Text) || CalBuiltinMethods.IsStaticReceiver(tok.Text))
                    continue;
                if (scope.Variables.ContainsKey(tok.Text)) continue; // array index / var, not a call

                if (CalBuiltinMethods.IsFieldNameTaking(tok.Text) && scope.Rec is not null)
                {
                    // Implicit Rec built-in like TESTFIELD("X") — system ref (#279)
                    // plus the field arg as a normal field_access.
                    sysRefs.Add(new CalRef(tok.Line, tok.Column,
                        scope.Rec.Value.Kind, scope.Rec.Value.Id, tok.Text, "system", "method_call"));
                    EmitFieldArg(t, k + 1, scope.Rec.Value, refs); // implicit Rec.TESTFIELD("X")
                    continue;
                }
                if (CalBuiltinMethods.IsReceiverMethod(tok.Text))
                {
                    // Implicit Rec built-in like INSERT / MODIFY — system ref on Rec (#279).
                    if (scope.Rec is not null)
                        sysRefs.Add(new CalRef(tok.Line, tok.Column,
                            scope.Rec.Value.Kind, scope.Rec.Value.Id, tok.Text, "system", "method_call"));
                    continue; // implicit Rec built-in
                }

                // A procedure on the owner object.
                if (scope.OwnerKind is not null && scope.OwnerId is not null)
                    refs.Add(new CalRef(tok.Line, tok.Column,
                        scope.OwnerKind, scope.OwnerId.Value, tok.Text, "procedure", "method_call"));
                continue;
            }

            // ── bare "Field" (implicit Rec field) ───────────────────────
            if (tok.Kind == CalTokenKind.QuotedIdentifier && scope.Rec is not null)
            {
                refs.Add(new CalRef(tok.Line, tok.Column,
                    scope.Rec.Value.Kind, scope.Rec.Value.Id, tok.Text, "field", "field_access"));
            }
        }

        return new CalExtractionResult(refs, unresolved, sysRefs);
    }

    /// <summary>
    /// The C/AL static object receivers that introduce an object literal, mapped
    /// to the object kind they imply. <c>DATABASE::</c> and <c>TABLE::</c> both
    /// name a table (the former is how classic C/AL writes a table literal in
    /// code; the latter appears in RunObject-style expressions). The remaining
    /// <c>StaticReceivers</c> entries in <see cref="CalBuiltinMethods"/>
    /// (<c>CurrPage</c>, <c>CurrReport</c>, <c>OBJECTTYPE</c>, ...) are runtime
    /// objects, not object literals, so they stay out of this map.
    /// </summary>
    private static readonly Dictionary<string, string> StaticReceiverKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DATABASE"] = "table",
            ["TABLE"] = "table",
            ["CODEUNIT"] = "codeunit",
            ["PAGE"] = "page",
            ["REPORT"] = "report",
            ["XMLPORT"] = "xmlport",
            ["QUERY"] = "query",
            ["FORM"] = "form",
            ["DATAPORT"] = "dataport",
            ["MENUSUITE"] = "menusuite",
        };

    /// <summary>
    /// Emits the object reference behind <c>KIND::Name</c> at token
    /// <paramref name="k"/> (the keyword). Returns false when the token after
    /// the <c>::</c> isn't a name or an id, so the caller can fall back to the
    /// option-qualifier path.
    ///
    /// <para>Emitting as <c>property_object</c> matches the AL walker's
    /// treatment of the same literal (<c>Codeunit::"Sales-Post"</c>), so
    /// Find references and the source-viewer underline behave identically
    /// across the two ingest paths. Crucially the name is consumed here: left
    /// to the bare-head dispatcher it would be mistaken for an implicit
    /// <c>Rec</c> field and emit a field_access for a field that doesn't
    /// exist (issue #713).</para>
    /// </summary>
    private static bool TryEmitStaticObjectRef(
        List<CalToken> t, int k, string kind, List<CalRef> refs, List<CalRef> sysRefs)
    {
        var name = Peek(t, k + 2);
        if (name.Kind is CalTokenKind.Identifier or CalTokenKind.QuotedIdentifier)
        {
            // The id isn't in the source; the import resolves it from the name.
            refs.Add(new CalRef(name.Line, name.Column, kind, null, null, null,
                "property_object", name.Text));
            EmitStaticDispatch(t, k, kind, null, name.Text, sysRefs);
            return true;
        }
        if (name.Kind == CalTokenKind.Number && int.TryParse(name.Text, out var id))
        {
            // Numeric literal (CODEUNIT::80) — resolvable by id like every
            // other C/AL reference, so the name post-pass fills it in.
            refs.Add(new CalRef(name.Line, name.Column, kind, id, null, null, "property_object"));
            EmitStaticDispatch(t, k, kind, id, null, sysRefs);
            return true;
        }
        return false;
    }

    /// <summary>
    /// <c>PAGE.RUNMODAL(PAGE::"Customer List",Cust)</c>: when the literal at
    /// <paramref name="k"/> is the first argument of a method on the static
    /// dispatcher of the same kind, record the call as a system reference on
    /// the object it names, as the AL walker does for <c>Page.RunModal</c>.
    /// </summary>
    private static void EmitStaticDispatch(
        List<CalToken> t, int k, string kind, int? id, string? name, List<CalRef> sysRefs)
    {
        if (k < 4) return;
        var lparen = t[k - 1];
        var method = t[k - 2];
        var dot = t[k - 3];
        var dispatcher = t[k - 4];
        if (lparen.Kind != CalTokenKind.LParen || dot.Kind != CalTokenKind.Dot) return;
        if (method.Kind != CalTokenKind.Identifier || dispatcher.Kind != CalTokenKind.Identifier) return;
        if (!string.Equals(dispatcher.Text, t[k].Text, StringComparison.OrdinalIgnoreCase)) return;
        sysRefs.Add(new CalRef(method.Line, method.Column, kind, id, method.Text, "system", "method_call", name));
    }

    private static CalTypeRef? ResolveHead(CalToken head, CalExtractScope scope)
    {
        if (string.Equals(head.Text, "Rec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(head.Text, "xRec", StringComparison.OrdinalIgnoreCase))
            return scope.Rec;
        return scope.Variables.TryGetValue(head.Text, out var tr) ? tr : null;
    }

    /// <summary>Emits a field_access for the first argument after the <c>(</c> at <paramref name="lparenIndex"/>, when it names a field.</summary>
    private static void EmitFieldArg(List<CalToken> t, int lparenIndex, CalTypeRef receiver, List<CalRef> refs)
    {
        var arg = Peek(t, lparenIndex + 1);
        if (arg.Kind is CalTokenKind.QuotedIdentifier or CalTokenKind.Identifier)
        {
            // A bare-identifier first arg could be a local var rather than a field;
            // quoted names are unambiguous. Emit for both — the id-resolved target
            // name lets the query layer drop a miss harmlessly.
            refs.Add(new CalRef(arg.Line, arg.Column,
                receiver.Kind, receiver.Id, arg.Text, "field", "field_access"));
        }
    }

    private static CalToken Peek(List<CalToken> t, int i)
        => i >= 0 && i < t.Count ? t[i] : new CalToken(CalTokenKind.Other, "", 0, 0);
}
