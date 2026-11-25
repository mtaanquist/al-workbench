// CodeMirror 6 companion shared by every page that embeds a syntax-aware
// text editor (TOML for /admin/templates, JSON for /admin/configuration/workspace).
//
// The CodeMirror packages are vendored under wwwroot/lib/codemirror/ and imported
// by relative path, so the app has no runtime CDN dependency and works on an
// air-gapped host. They are pre-bundled single files (no bundler in this repo);
// wwwroot/lib/codemirror/README.md records the exact versions and the commands
// that produced them, which is also how you bump them.
//
// The shared packages (@codemirror/state, view, language, @lezer/common,
// @lezer/highlight) are each vendored once and imported by every other file, so
// CodeMirror's extension system sees a single canonical instance. Duplicating one
// of them breaks the instanceof checks with "Unrecognized extension value in
// extension set".
//
// The exported functions are ID-keyed so Blazor's IJSObjectReference can
// stay a plain integer rather than wrapping the EditorView itself: simpler
// lifecycle, no DotNet.createJSObjectReference plumbing.

import { EditorView, lineNumbers, highlightActiveLineGutter, highlightSpecialChars,
    drawSelection, dropCursor, rectangularSelection, crosshairCursor,
    highlightActiveLine, keymap, Decoration, WidgetType, showPanel, gutter, GutterMarker,
    placeholder }
    from "./lib/codemirror/view.js";
import { EditorState, Compartment, RangeSetBuilder, StateField, StateEffect }
    from "./lib/codemirror/state.js";
import { defaultKeymap, history, historyKeymap, indentWithTab }
    from "./lib/codemirror/commands.js";
import { syntaxHighlighting, HighlightStyle, indentOnInput, bracketMatching,
    foldGutter, foldKeymap, StreamLanguage }
    from "./lib/codemirror/language.js";
import { search, searchKeymap, highlightSelectionMatches, openSearchPanel }
    from "./lib/codemirror/search.js";
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap }
    from "./lib/codemirror/autocomplete.js";
import { lintKeymap, lintGutter, setDiagnostics }
    from "./lib/codemirror/lint.js";
import { toml }
    from "./lib/codemirror/legacy-modes-toml.js";
import { json as jsonMode }
    from "./lib/codemirror/lang-json.js";
import { tags }
    from "./lib/codemirror/lezer-highlight.js";

// Lightweight AL StreamParser. Not a full AL grammar — recognises keywords,
// strings, double-quoted identifiers (AL allows spaces inside `"..."`), comments
// (line + block), and numeric literals. Categorisation cross-checked against
// Microsoft's AL TextMate grammar (github.com/microsoft/AL/blob/master/grammar/
// alsyntax.tmlanguage) so built-in types render distinctly from control
// keywords, the same way the AL VS Code extension shows them.
const AL_KEYWORDS = new Set([
    // Control flow.
    "begin", "end", "if", "then", "else", "do", "while", "repeat", "until",
    "for", "to", "downto", "foreach", "in", "case", "of", "exit", "break",
    // Declaration & scope.
    "procedure", "trigger", "local", "internal", "protected", "var",
    "with", "namespace", "using", "interface", "implements", "extends",
    "implements", "raises", "obsolete", "subscribers", "subscriber",
    "temporary", "rec", "xrec", "currfieldno", "currpage", "currreport",
    // Object-type keywords (typed-reference introducers — also in
    // AL_OBJECT_KEYWORDS below so the following identifier is coloured).
    "codeunit", "table", "tableextension", "page", "pageextension",
    "pagecustomization", "report", "reportextension", "xmlport", "query",
    "enum", "enumextension", "permissionset", "permissionsetextension",
    "profile", "controladdin", "dotnet",
    // Operator keywords.
    "and", "or", "not", "xor", "div", "mod",
    // Boolean / null literals.
    "true", "false",
    // Metadata / property keywords (from the MS grammar). Not exhaustive,
    // but covers what's surprising-when-uncoloured in real BaseApp code.
    "where", "ascending", "descending", "filter", "const", "average",
    "count", "exist", "field", "min", "max", "sum",
    "add", "addfirst", "addlast", "addbefore", "addafter",
    "modify", "movebefore", "moveafter", "customizes",
    "action", "actions", "fields", "keys", "schema", "values",
    "elements", "textelement", "tableelement", "fieldattribute",
    "textattribute", "requestpage",
]);

// Built-in AL types. Distinct from AL_KEYWORDS so they get `typeName`
// styling — matches the AL VS Code extension's `keyword.other.builtintypes.al`
// scope. Drawn from the MS grammar and the current AL methods reference.
const AL_BUILTIN_TYPES = new Set([
    // Primitives.
    "boolean", "byte", "char", "code", "date", "dateformula", "datetime",
    "decimal", "duration", "guid", "integer", "biginteger", "label", "option",
    "text", "time", "variant",
    // Streams / files.
    "instream", "outstream", "file",
    // Reference types and containers.
    "array", "list", "dictionary", "blob", "media", "mediaset",
    "recordid", "recordref", "fieldref", "keyref",
    // Modern (HTTP / JSON / XML).
    "httpclient", "httpcontent", "httpheaders", "httprequestmessage",
    "httpresponsemessage",
    "jsonarray", "jsonobject", "jsontoken", "jsonvalue",
    "xmlattribute", "xmlattributecollection", "xmlcdata", "xmlcomment",
    "xmldeclaration", "xmldocument", "xmldocumenttype", "xmlelement",
    "xmlnamespacemanager", "xmlnamespacescope", "xmlnametable", "xmlnode",
    "xmlnodelist", "xmlprocessinginstruction", "xmlreadoptions", "xmltext",
    "xmlwriteoptions",
    // UI / runtime.
    "action", "dialog", "errorinfo", "filterpagebuilder", "notification",
    "page", "report", "session", "sessionsettings", "textbuilder",
    "textconst", "textencoding", "verbosity", "version", "testpage",
    "clienttype", "tableconnectiontype",
]);

// Keywords whose following identifier is an *object name* — colour the
// next bare identifier (after any object-ID number) the same way we colour
// quoted AL identifiers, so `table 5721 Purchasing` reads the same as
// `table 36 "Sales Header"`.
const AL_OBJECT_KEYWORDS = new Set([
    "codeunit", "table", "tableextension", "page", "pageextension",
    "pagecustomization", "report", "reportextension", "xmlport", "query",
    "enum", "enumextension", "permissionset", "permissionsetextension",
    "profile", "controladdin", "record",
    "requestpage", "testpage", "testpart", "testrequestpage", "interface",
    "extends", "tabledata",
]);

// Keywords whose following identifier is the *name of a procedure or
// trigger being declared* — gives the name a distinct colour (CodeMirror
// maps `def` to a definition tag, typically bold/coloured).
const AL_DEFINITION_KEYWORDS = new Set([
    "procedure", "trigger",
]);

const alParser = {
    startState() {
        return {
            inBlockComment: false,
            expectObjectName: false,
            expectDefinitionName: false,
        };
    },
    token(stream, state) {
        if (state.inBlockComment) {
            while (!stream.eol()) {
                if (stream.match("*/")) {
                    state.inBlockComment = false;
                    return "comment";
                }
                stream.next();
            }
            return "comment";
        }
        if (stream.eatSpace()) return null;
        if (stream.match("//")) {
            stream.skipToEnd();
            return "comment";
        }
        if (stream.match("/*")) {
            state.inBlockComment = true;
            return "comment";
        }
        // Double-quoted AL identifier ("Sales-Post").
        if (stream.peek() === '"') {
            stream.next();
            while (!stream.eol()) {
                const ch = stream.next();
                if (ch === '"') break;
            }
            // A quoted name in either expected-name slot is still the
            // declared name — colour as definition if pending, otherwise
            // a regular AL identifier.
            const tok = state.expectDefinitionName ? "def" : "variableName";
            state.expectObjectName = false;
            state.expectDefinitionName = false;
            return tok;
        }
        // Single-quoted string literal.
        if (stream.peek() === "'") {
            stream.next();
            while (!stream.eol()) {
                const ch = stream.next();
                if (ch === "'") {
                    if (stream.peek() === "'") {
                        stream.next(); // escaped quote
                        continue;
                    }
                    break;
                }
            }
            state.expectObjectName = false;
            state.expectDefinitionName = false;
            return "string";
        }
        if (stream.match(/^\d+(\.\d+)?/)) {
            // A numeric literal between an object keyword and the name
            // (`table 5721 Purchasing`) is fine — keep the expectation
            // alive so the next identifier still gets the variableName
            // colour. Other numerics clear it.
            return "number";
        }
        if (stream.match(/^[A-Za-z_][A-Za-z0-9_]*/)) {
            const word = stream.current().toLowerCase();
            // Built-in types come first so `Integer`, `Text`, `Boolean`,
            // `HttpClient`, etc. render as types regardless of whether
            // they appear in a declaration or a type-cast position.
            if (AL_BUILTIN_TYPES.has(word)) {
                state.expectObjectName = false;
                state.expectDefinitionName = false;
                return "typeName";
            }
            if (AL_KEYWORDS.has(word)) {
                state.expectObjectName = AL_OBJECT_KEYWORDS.has(word);
                state.expectDefinitionName = AL_DEFINITION_KEYWORDS.has(word);
                return "keyword";
            }
            if (state.expectDefinitionName) {
                // First identifier after `procedure` / `trigger` is the
                // name being declared. `def` maps to CodeMirror's
                // definition tag — typically bold or accent-coloured.
                state.expectDefinitionName = false;
                state.expectObjectName = false;
                return "def";
            }
            if (state.expectObjectName) {
                state.expectObjectName = false;
                return "variableName";
            }
            return null;
        }
        // Any other character — punctuation, operator — drops the
        // object-name expectation. We hit this for `{`, `:`, `=`, etc.,
        // any of which means the declaration has moved past its name.
        state.expectObjectName = false;
        state.expectDefinitionName = false;
        stream.next();
        return null;
    },
};

let nextId = 1;
const editors = new Map();

// Browser-level guard against losing edits to a full reload, tab close, or a
// browser back that exits the SPA. In-app navigation goes through Blazor's
// LocationChangingHandler instead — see the callers under Components/Pages.
let beforeUnloadAttached = false;
function beforeUnloadHandler(e) {
    e.preventDefault();
    // Modern browsers ignore the message text and show their own copy, but
    // Chrome still requires a non-empty returnValue to actually trigger the
    // dialog.
    e.returnValue = "";
    return "";
}
function syncBeforeUnload() {
    const anyDirty = [...editors.values()].some(rec => rec.dirty);
    if (anyDirty && !beforeUnloadAttached) {
        window.addEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = true;
    } else if (!anyDirty && beforeUnloadAttached) {
        window.removeEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = false;
    }
}

// Best-effort read of the active theme — matches the rules in
// wwwroot/theme.js so the editor flips with the rest of the page.
function isDarkTheme() {
    const attr = document.documentElement.getAttribute("data-theme");
    if (attr === "dark") return true;
    if (attr === "light") return false;
    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false;
}

// ── Palette ──────────────────────────────────────────────────────────
//
// Every colour the editor paints with comes from the --code-* custom
// properties in wwwroot/tokens.css, so light and dark follow the same
// switch as the rest of the app and nobody has to re-mount the editor to
// change theme. Before this, the editor carried an off-the-shelf dark theme
// on dark and CodeMirror's own stock palette on light — two colour schemes
// that belonged to neither the app nor each other.
//
// The highlight style names its own classes (`tok-keyword`, `tok-string`,
// …) rather than letting CodeMirror generate them, so the actual colours
// live in CSS next to the rest of the design layer (wwwroot/tools.css →
// "AL syntax tinting") and are greppable from there.
const alHighlightStyle = HighlightStyle.define([
    { tag: tags.comment, class: "tok-comment" },
    { tag: tags.lineComment, class: "tok-comment" },
    { tag: tags.blockComment, class: "tok-comment" },
    { tag: tags.string, class: "tok-string" },
    { tag: tags.special(tags.string), class: "tok-string" },
    { tag: tags.number, class: "tok-number" },
    { tag: tags.bool, class: "tok-number" },
    { tag: tags.null, class: "tok-number" },
    { tag: tags.atom, class: "tok-number" },
    { tag: tags.keyword, class: "tok-keyword" },
    { tag: tags.operatorKeyword, class: "tok-keyword" },
    { tag: tags.modifier, class: "tok-keyword" },
    { tag: tags.controlKeyword, class: "tok-keyword" },
    { tag: tags.definitionKeyword, class: "tok-keyword" },
    { tag: tags.typeName, class: "tok-typeName" },
    { tag: tags.className, class: "tok-typeName" },
    { tag: tags.propertyName, class: "tok-propertyName" },
    // `procedure Foo` / `trigger OnRun` — the name being declared.
    { tag: tags.definition(tags.variableName), class: "tok-definition" },
    { tag: tags.definition(tags.propertyName), class: "tok-definition" },
    { tag: tags.variableName, class: "tok-variableName" },
    { tag: tags.labelName, class: "tok-variableName" },
    { tag: tags.invalid, class: "tok-invalid" },
]);

// The editor chrome — gutters, selection, panels, tooltips. Var-driven for
// the same reason as the palette above, so the only thing the light and
// dark builds disagree about is CodeMirror's own `dark` flag (it decides a
// handful of built-in behaviours, like which side the default panel
// shadows fall on).
const codeThemeSpec = {
    "&": {
        color: "var(--ink-2)",
        backgroundColor: "var(--code-bg)",
    },
    ".cm-content": {
        caretColor: "var(--primary)",
        fontFamily: "var(--font-mono)",
    },
    ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--primary)" },
    "&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection": {
        backgroundColor: "var(--primary-weak)",
    },
    ".cm-activeLine": { backgroundColor: "var(--surface-2)" },
    // --surface-sunken would be invisible here: it IS --code-bg on dark.
    ".cm-selectionMatch": { backgroundColor: "var(--surface-2)" },
    ".cm-searchMatch": {
        backgroundColor: "var(--st-untrans-bg)",
        color: "var(--st-untrans-text)",
        borderRadius: "2px",
    },
    ".cm-searchMatch.cm-searchMatch-selected": {
        backgroundColor: "var(--st-fuzzy-bg)",
        color: "var(--st-fuzzy-text)",
    },
    "&.cm-focused .cm-matchingBracket": {
        backgroundColor: "var(--surface-2)",
        outline: "1px solid var(--border-strong)",
    },
    "&.cm-focused .cm-nonmatchingBracket": {
        backgroundColor: "var(--danger-bg)",
        color: "var(--danger-text)",
    },
    ".cm-gutters": {
        backgroundColor: "var(--code-bg)",
        color: "var(--diff-gutter)",
        borderRight: "1px solid var(--border)",
    },
    ".cm-activeLineGutter": {
        backgroundColor: "var(--surface-2)",
        color: "var(--ink-3)",
    },
    ".cm-foldPlaceholder": {
        backgroundColor: "var(--surface-2)",
        border: "1px solid var(--border)",
        borderRadius: "3px",
        color: "var(--ink-4)",
        padding: "0 4px",
        margin: "0 2px",
    },
    ".cm-tooltip": {
        backgroundColor: "var(--surface)",
        border: "1px solid var(--border-strong)",
        borderRadius: "var(--r-sm)",
        color: "var(--ink-2)",
    },
    ".cm-tooltip .cm-tooltip-arrow:before": { borderTopColor: "var(--border-strong)", borderBottomColor: "var(--border-strong)" },
    ".cm-tooltip .cm-tooltip-arrow:after": { borderTopColor: "var(--surface)", borderBottomColor: "var(--surface)" },
    ".cm-tooltip-autocomplete > ul > li[aria-selected]": {
        backgroundColor: "var(--primary-weak)",
        color: "var(--primary-ink)",
    },
    ".cm-panels": {
        backgroundColor: "var(--surface)",
        color: "var(--ink-2)",
    },
};

const codeThemeLight = EditorView.theme(codeThemeSpec);
const codeThemeDark = EditorView.theme(codeThemeSpec, { dark: true });

function themeExtensions() {
    return [isDarkTheme() ? codeThemeDark : codeThemeLight];
}

// Returns the CodeMirror language extension for the requested mode. Unknown
// modes fall back to plain text so the editor still renders rather than
// throwing inside the EditorState constructor.
function languageExtensionFor(language) {
    switch (language) {
        case "toml": return StreamLanguage.define(toml);
        case "json": return jsonMode();
        case "al": return StreamLanguage.define(alParser);
        default: return [];
    }
}

function buildExtensions(themeCompartment, dirtyListener, language) {
    return [
        dirtyListener,
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightSpecialChars(),
        history(),
        foldGutter(),
        drawSelection(),
        dropCursor(),
        EditorState.allowMultipleSelections.of(true),
        indentOnInput(),
        syntaxHighlighting(alHighlightStyle, { fallback: true }),
        bracketMatching(),
        closeBrackets(),
        autocompletion(),
        rectangularSelection(),
        crosshairCursor(),
        highlightActiveLine(),
        highlightSelectionMatches(),
        keymap.of([
            ...closeBracketsKeymap,
            ...defaultKeymap,
            ...searchKeymap,
            ...historyKeymap,
            ...foldKeymap,
            ...completionKeymap,
            ...lintKeymap,
            indentWithTab,
        ]),
        languageExtensionFor(language),
        lintGutter(),
        themeCompartment.of(themeExtensions()),
    ];
}

export function mount(container, initialValue, language) {
    if (!container) return 0;
    const id = nextId++;
    const themeCompartment = new Compartment();
    const initial = initialValue ?? "";
    const lang = typeof language === "string" ? language : "toml";

    // Re-evaluate dirtiness on every doc change so the navigate-away guard
    // (both browser-level beforeunload and Blazor's LocationChangingHandler)
    // stays in sync without polling from C#.
    const dirtyListener = EditorView.updateListener.of((update) => {
        if (!update.docChanged) return;
        const rec = editors.get(id);
        if (!rec) return;
        const next = view.state.doc.toString() !== rec.pristine;
        if (next !== rec.dirty) {
            rec.dirty = next;
            syncBeforeUnload();
        }
    });

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initial,
            extensions: buildExtensions(themeCompartment, dirtyListener, lang),
        }),
    });

    const reconfigureTheme = () => {
        view.dispatch({ effects: themeCompartment.reconfigure(themeExtensions()) });
    };

    const themeObserver = new MutationObserver(reconfigureTheme);
    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["data-theme"],
    });

    const mql = window.matchMedia?.("(prefers-color-scheme: dark)");
    mql?.addEventListener?.("change", reconfigureTheme);

    editors.set(id, {
        view,
        pristine: initial,
        dirty: false,
        dispose: () => {
            themeObserver.disconnect();
            mql?.removeEventListener?.("change", reconfigureTheme);
            view.destroy();
        },
    });
    return id;
}

// Read-only viewer used by Object Explorer. No history, no autocomplete, no
// dirty tracking — just the language colouriser, line numbers, search,
// folding, and an EditorView.editable.of(false) so cursor placement still
// works but typing is rejected.
// options:
//   lineDecorations: { [1-basedLineNumber]: cssClass }  — full-line backgrounds (diff view)
//   fillers:         [{ before, size }] — blank alignment gaps (compare view).
//                     Inserts `size` line-heights of empty space before source
//                     line `before` (or after the last line when
//                     `before > doc.lines`) so the two compare panes stay
//                     vertically aligned KDiff3-style. Real text and gutter
//                     line numbers are untouched — a filler is empty space.
//   wordDiff:        [{ line, from, to }] — intra-line changed-word ranges
//                     (compare view). 1-based columns, `to` exclusive; painted
//                     as `cm-diff-word` marks inside the tinted line.
//   folding:         false to drop the fold gutter + keymap (compare view —
//                     folding one pane would break the filler alignment).
//   unifiedGutters:  [[oldLine, newLine], …] one pair per document line, for
//                     the inline (unified) compare view. Replaces the single
//                     line-number gutter with two, because a unified document
//                     is not a file: its row 12 is line 12 of neither side, so
//                     the numbers have to be carried rather than counted. A
//                     null means the row does not exist on that side.
//   collapse:        [{ index, header, from, to, before }] — the collapsed
//                     regions of a side-by-side pane. A region with from/to
//                     replaces those lines with a clickable band; one with
//                     `before` only banners the line it sits above. Indices are
//                     shared with the opposite pane, which is what lets a click
//                     expand both — see SideBySideCollapse.
//                     diff, rendered as a block widget above document line
//                     `before`. The rows between banners are the only ones the
//                     document holds; the collapse happened server-side.
//   declarations:    [{ line, columnStart, columnEnd, symbolId, kind, name }]
//                     ranges that get a click affordance + right-click "Find references"
//   resolvables:     [{ line, columnStart, columnEnd }] — extra ranges that
//                     show the resolvable-token underline (object references,
//                     procedure calls). Cosmetic only; the actual Go to
//                     definition still runs through the server.
//   procedures:      [{ startLine, endLine?, name, kind }] — procedure-like
//                     symbol ranges. Drives the status bar's "in CheckDates
//                     (line 13)" suffix, mapping the absolute editor line to
//                     BC's procedure-relative stack-trace numbering (the
//                     `procedure` declaration line counts as line 0).
//                     Only used when `statusBar: true`.
//   dotNetRef:        Blazor DotNetObjectReference; callbacks fire `OnFindReferences(symbolId)`
//   scrollToLine:     1-based line to scroll to + flash after mount (deep-link from references)
export function mountReadOnly(container, value, language, options) {
    if (!container) return 0;
    const id = nextId++;
    const themeCompartment = new Compartment();
    const initial = value ?? "";
    const lang = typeof language === "string" ? language : "al";
    const opts = options ?? {};

    const decorationExtensions = buildLineDecorationExtensions(opts.lineDecorations);
    // Diff change-bar gutter, only when there are diff line decorations to
    // mark (i.e. the compare page). The single-file viewer passes none, so it
    // gets no extra gutter.
    const diffGutterExtensions = buildDiffGutterExtensions(opts.lineDecorations);
    // Alignment fillers, compare page only. Blank block widgets that pad the
    // shorter side so matching lines line up across panes (single-file viewer
    // passes none, so it gets no fillers). Mounted through a compartment: the
    // initial set carries a fallback line-height estimate, then gets rebuilt
    // with the measured defaultLineHeight right after the view exists so
    // off-screen filler heights are exact (see FillerWidget.estimatedHeight).
    const fillerCompartment = new Compartment();
    const fillerExtensions = [fillerCompartment.of(buildFillerDecorationExtensions(opts.fillers, null))];
    // Intra-line changed-word ranges (compare page only): a stronger tint
    // inside already-tinted modified lines. `[{line, from, to}]`, 1-based
    // columns, `to` exclusive.
    const wordDiffExtensions = buildWordDiffExtensions(opts.wordDiff);
    // Inline compare: two carried number gutters instead of one counted one,
    // and the `@@` banners marking where the document skips ahead.
    const gutterExtensions = buildUnifiedGutterExtensions(opts.unifiedGutters);
    // Side-by-side collapse: the unchanged stretches taken out of the layout,
    // with a band in their place.
    const collapseExtensions = buildCollapseExtensions(opts.collapse);
    // Folding defaults on; the compare page passes folding:false (see the
    // extension list below for why).
    const folding = opts.folding !== false;
    const declarationExtensions = buildDeclarationDecorationExtensions(opts.declarations);
    const resolvableExtensions = buildResolvableDecorationExtensions(opts.resolvables);
    // Opt-in status bar: only the source-file viewer asks for it today.
    // The diff viewer and the admin TOML/JSON editors keep their existing
    // chrome unchanged.
    const statusBarExtensions = opts.statusBar ? [buildStatusBarExtension(opts.procedures, opts.metadata, opts.onProcedureChange)] : [];
    // Sticky "current line" highlight survives CodeMirror's row
    // virtualisation because the decoration lives in editor state rather
    // than on a DOM node. scrollToLine() dispatches setCurrentLineEffect
    // to set/clear it.
    const currentLineExtensions = [currentLineField, currentLineTheme];

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initial,
            extensions: [
                EditorView.editable.of(false),
                EditorState.readOnly.of(true),
                // Disable browser spellcheck on the editor content.
                // AL identifiers ("Sell-to Customer Name" etc.) light
                // up with red squiggles that are easy to confuse with
                // our resolvable / declaration dotted underlines.
                EditorView.contentAttributes.of({ spellcheck: "false" }),
                ...gutterExtensions,
                ...diffGutterExtensions,
                ...collapseExtensions,
                highlightSpecialChars(),
                // Folding is on by default but the compare page opts out
                // (folding: false): the alignment fillers are computed
                // server-side against the full text, so collapsing a region in
                // one pane would silently break the line-up with the other.
                ...(folding ? [foldGutter()] : []),
                drawSelection(),
                EditorState.allowMultipleSelections.of(true),
                syntaxHighlighting(alHighlightStyle, { fallback: true }),
                highlightActiveLine(),
                highlightSelectionMatches(),
                // Ctrl/Cmd-F brings up CodeMirror's search panel. `search()`
                // registers the panel state; searchKeymap binds the key.
                search({ top: true }),
                keymap.of([...defaultKeymap, ...searchKeymap, ...(folding ? foldKeymap : [])]),
                languageExtensionFor(lang),
                ...decorationExtensions,
                ...fillerExtensions,
                ...wordDiffExtensions,
                ...declarationExtensions,
                ...resolvableExtensions,
                ...statusBarExtensions,
                ...currentLineExtensions,
                themeCompartment.of(themeExtensions()),
            ],
        }),
    });

    // Re-issue the fillers once the line height is known, so a gap of n is
    // exactly n rows tall — in CodeMirror's height map and in the DOM.
    if (Array.isArray(opts.fillers) && opts.fillers.length > 0) {
        withMeasuredLineHeight(view, (lineHeight) => {
            view.dispatch({
                effects: fillerCompartment.reconfigure(
                    buildFillerDecorationExtensions(opts.fillers, lineHeight)),
            });
        });
    }

    const reconfigureTheme = () => {
        view.dispatch({ effects: themeCompartment.reconfigure(themeExtensions()) });
    };

    const themeObserver = new MutationObserver(reconfigureTheme);
    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["data-theme"],
    });

    const mql = window.matchMedia?.("(prefers-color-scheme: dark)");
    mql?.addEventListener?.("change", reconfigureTheme);

    // Context-menu wiring for click-to-find. Listens for right-clicks on the
    // editor DOM; if the click hits a declaration's name range, surface a
    // small floating menu and stop the browser's default menu. Anything
    // outside a declaration falls through (browser menu kept).
    const declarations = Array.isArray(opts.declarations) ? opts.declarations : [];
    let closeOpenMenu = null;
    const closeMenu = () => {
        const close = closeOpenMenu;
        closeOpenMenu = null;
        close?.(false);
    };

    // Right-click anywhere offers "Go to definition" when a callback is wired;
    // additionally offers "Find references" when the click lands inside a
    // declaration name range. Click outside either menu item to dismiss.
    const onContextMenu = (event) => {
        if (!opts.dotNetRef) return;
        const pos = view.posAtCoords({ x: event.clientX, y: event.clientY });
        if (pos === null) return;
        const line = view.state.doc.lineAt(pos);
        const colInLine = pos - line.from + 1; // 1-based to match C# columns
        const onDeclaration = declarations.find(d =>
            d.line === line.number
            && colInLine >= d.columnStart
            && colInLine <= d.columnEnd);

        const items = [];
        if (onDeclaration) {
            // Click landed on a declaration name range — we already know the
            // symbol id, so the existing single-arg callback is fine.
            // Two ID spaces: object headers go through OnFindReferences
            // (oe_module_objects.Id → /from-symbol/); sub-symbols
            // (procedure / field / trigger / event) go through
            // OnFindMemberReferences (oe_module_symbols.Id →
            // /from-member-symbol/). isMemberSymbol decides the route.
            const callback = onDeclaration.isMemberSymbol
                ? "OnFindMemberReferences"
                : "OnFindReferences";
            items.push({
                label: "Find references",
                keys: ["Shift", "F12"],
                action: () => opts.dotNetRef.invokeMethodAsync(
                    callback, onDeclaration.symbolId),
            });
            // "Find built-in calls" — built-in/system method calls (Insert,
            // Modify, Get, RunModal, …) on this object. Object headers only;
            // system calls target a whole object, not a member. symbolId here
            // is the oe_module_objects.Id. See #279/#291. Only hosts that
            // answer the call (opts.builtInCalls) get the item.
            if (!onDeclaration.isMemberSymbol && opts.builtInCalls) {
                items.push({
                    label: "Find built-in calls",
                    action: () => opts.dotNetRef.invokeMethodAsync(
                        "OnFindSystemReferences", onDeclaration.symbolId),
                });
            }
        } else {
            // Off-declaration click: the host decides whether the word
            // under the cursor resolves to a symbol. The two-arg variant
            // lets the host run a positional lookup server-side and fall
            // back to "no references" UI if nothing matches.
            items.push({
                label: "Find references",
                keys: ["Shift", "F12"],
                action: () => opts.dotNetRef.invokeMethodAsync(
                    "OnFindReferencesAt", line.number, colInLine),
            });
            // The same for a table or page named in the code, or a variable
            // of one: Customer in Customer.Insert().
            if (opts.builtInCalls) {
                items.push({
                    label: "Find built-in calls",
                    action: () => opts.dotNetRef.invokeMethodAsync(
                        "OnFindSystemReferencesAt", line.number, colInLine),
                });
            }
        }
        // Disable "Go to definition" when the click lands on the declaration
        // name itself — the target would be the click site, which causes the
        // viewer to navigate to its current URL and break re-mounting state.
        items.push({
            label: "Go to definition",
            keys: [usesCommandKey() ? "Cmd" : "Ctrl", "click"],
            disabled: Boolean(onDeclaration),
            action: () => opts.dotNetRef.invokeMethodAsync(
                "OnGoToDefinition", line.number, colInLine),
        });
        // "Find in this file" is the only gesture for variables / fields that
        // don't have a symbol-table entry. Always offered so users have a
        // reliable way to scan within a long file.
        items.push({
            label: "Find in this file",
            action: () => opts.dotNetRef.invokeMethodAsync(
                "OnFindInFile", line.number, colInLine),
        });

        event.preventDefault();
        closeMenu();
        // The menu owns its own click / scroll / keydown dismissal now, so
        // this no longer registers document-level listeners of its own.
        closeOpenMenu = renderMenu(event.clientX, event.clientY, items, container).close;
    };
    container.addEventListener("contextmenu", onContextMenu);

    // Cmd/Ctrl-click anywhere in the editor fires Go to definition.
    // Holding the modifier (without clicking) toggles a class on the editor
    // body so identifier-shaped tokens get a hover affordance — gives users
    // the IDE-style "what's clickable" feedback even though we can't
    // pre-resolve which tokens have a definition without a full parse.
    const onClickForDefinition = (event) => {
        if (!event.metaKey && !event.ctrlKey) return;
        if (!opts.dotNetRef) return;
        const pos = view.posAtCoords({ x: event.clientX, y: event.clientY });
        if (pos === null) return;
        const line = view.state.doc.lineAt(pos);
        const colInLine = pos - line.from + 1;
        // Cmd/Ctrl-click on the declaration name itself would resolve to the
        // current location — same URL, same line — and break the viewer
        // (see right-click handler above). Swallow the click instead.
        const onDeclaration = declarations.find(d =>
            d.line === line.number
            && colInLine >= d.columnStart
            && colInLine <= d.columnEnd);
        if (onDeclaration) {
            event.preventDefault();
            return;
        }
        event.preventDefault();
        opts.dotNetRef.invokeMethodAsync("OnGoToDefinition", line.number, colInLine)
            .catch(err => console.warn("Go to definition callback failed:", err));
    };
    container.addEventListener("click", onClickForDefinition);

    const updateModifierClass = (event) => {
        if (event.metaKey || event.ctrlKey) {
            container.classList.add("cm-modifier-down");
        } else {
            container.classList.remove("cm-modifier-down");
        }
    };
    container.addEventListener("mousemove", updateModifierClass);
    container.addEventListener("keydown", updateModifierClass);
    container.addEventListener("keyup", updateModifierClass);
    const onContainerMouseLeave = () => container.classList.remove("cm-modifier-down");
    container.addEventListener("mouseleave", onContainerMouseLeave);
    // Modifier release outside the editor still needs to clear the class.
    // Retained so dispose() can take it off `window` again — an anonymous
    // handler here leaked one closure over the container per mount, on the one
    // path that does call dispose (#711).
    const onWindowBlur = () => container.classList.remove("cm-modifier-down");
    window.addEventListener("blur", onWindowBlur);

    editors.set(id, {
        view,
        pristine: initial,
        dirty: false,
        dispose: () => {
            container.removeEventListener("contextmenu", onContextMenu);
            container.removeEventListener("click", onClickForDefinition);
            container.removeEventListener("mousemove", updateModifierClass);
            container.removeEventListener("keydown", updateModifierClass);
            container.removeEventListener("keyup", updateModifierClass);
            container.removeEventListener("mouseleave", onContainerMouseLeave);
            window.removeEventListener("blur", onWindowBlur);
            container.classList.remove("cm-modifier-down");
            closeMenu();
            themeObserver.disconnect();
            mql?.removeEventListener?.("change", reconfigureTheme);
            view.destroy();
        },
    });

    // Deferred scroll-and-flash: wait one rAF so CodeMirror has laid out
    // and our height measurements are correct before we ask it to scroll.
    if (typeof opts.scrollToLine === "number" && opts.scrollToLine >= 1) {
        requestAnimationFrame(() => scrollToLine(id, opts.scrollToLine, /*flash*/ true));
    }

    return id;
}

// Editable diff pane for the standalone Diff tool. Same visual chrome as a
// mountReadOnly compare pane (line tints, change-bar gutter, alignment
// fillers, word-diff, current-line, status-bar-free) but the doc is EDITABLE:
// the pane IS the input. The diff decorations are dynamic (setDiff swaps them
// in place) rather than baked in, so we never remount and never lose what the
// user typed. Folding stays off for the same reason the read-only compare pane
// disables it — a collapsed region would break the server-computed alignment.
//
// options:
//   lineDecorations / fillers / wordDiff — the initial (usually empty) diff.
//   onDocChanged(id) — fired on every doc edit; the caller debounces and
//                      recomputes the diff (source-viewer.js owns that policy).
export function mountCompareEditor(container, value, language, options) {
    if (!container) return 0;
    const id = nextId++;
    const themeCompartment = new Compartment();
    const initial = value ?? "";
    const lang = typeof language === "string" ? language : "text";
    const opts = options ?? {};

    const dataField = diffDataFieldFactory({
        lineDecorations: opts.lineDecorations,
        fillers: opts.fillers,
        wordDiff: opts.wordDiff,
        lineHeight: null,
    });
    const onDocChanged = typeof opts.onDocChanged === "function" ? opts.onDocChanged : null;
    const editListener = EditorView.updateListener.of((update) => {
        if (update.docChanged && onDocChanged) onDocChanged(id);
    });

    const view = new EditorView({
        parent: container,
        state: EditorState.create({
            doc: initial,
            extensions: [
                EditorView.contentAttributes.of({ spellcheck: "false" }),
                lineNumbers(),
                dynamicDiffGutter(dataField),
                highlightSpecialChars(),
                history(),
                drawSelection(),
                dropCursor(),
                EditorState.allowMultipleSelections.of(true),
                indentOnInput(),
                syntaxHighlighting(alHighlightStyle, { fallback: true }),
                bracketMatching(),
                closeBrackets(),
                highlightActiveLine(),
                highlightSelectionMatches(),
                // In-pane hint shown while the pane is empty — the affordance
                // that tells a first-time user the pane is where they paste.
                ...(typeof opts.placeholder === "string" && opts.placeholder
                    ? [placeholder(opts.placeholder)]
                    : []),
                search({ top: true }),
                keymap.of([
                    ...closeBracketsKeymap,
                    ...defaultKeymap,
                    ...searchKeymap,
                    ...historyKeymap,
                    indentWithTab,
                ]),
                languageExtensionFor(lang),
                dataField,
                dynamicLineDecoField(dataField),
                dynamicFillerField(dataField),
                dynamicWordDiffField(dataField),
                currentLineField,
                currentLineTheme,
                editListener,
                themeCompartment.of(themeExtensions()),
            ],
        }),
    });

    const reconfigureTheme = () => {
        view.dispatch({ effects: themeCompartment.reconfigure(themeExtensions()) });
    };
    const themeObserver = new MutationObserver(reconfigureTheme);
    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["data-theme"],
    });
    const mql = window.matchMedia?.("(prefers-color-scheme: dark)");
    mql?.addEventListener?.("change", reconfigureTheme);

    editors.set(id, {
        view,
        pristine: initial,
        dirty: false,
        dispose: () => {
            themeObserver.disconnect();
            mql?.removeEventListener?.("change", reconfigureTheme);
            view.destroy();
        },
    });

    // Re-issue the initial diff with the measured line height so filler block
    // widgets estimate their off-screen height exactly (see FillerWidget).
    // Deferred for the same reason as mountReadOnly's filler re-issue.
    withMeasuredLineHeight(view, (lineHeight) => {
        view.dispatch({
            effects: setDiffEffect.of({
                lineDecorations: opts.lineDecorations,
                fillers: opts.fillers,
                wordDiff: opts.wordDiff,
                lineHeight,
            }),
        });
    });

    return id;
}

// Swap the diff decorations on a live editor (line tints, change bars,
// fillers, word-diff) without touching the doc. No-op on editors that weren't
// mounted with the dynamic diff fields (the effect just goes unconsumed).
export function setDiff(id, payload) {
    const rec = editors.get(id);
    if (!rec) return;
    const p = payload ?? {};
    rec.view.dispatch({
        effects: setDiffEffect.of({
            lineDecorations: p.lineDecorations,
            fillers: p.fillers,
            wordDiff: p.wordDiff,
            lineHeight: rec.view.defaultLineHeight,
        }),
    });
}

// Public: scroll the editor to a 1-based line number, with an optional
// short fade-out highlight so the eye lands in the right place.
//
// Two-pass scroll: CM6 estimates heights for unmeasured lines, so the
// first scroll lands roughly in place (rendering new lines as a side
// effect), and the second corrects against CM's now-accurate height
// map. Both passes set view.scrollDOM.scrollTop directly rather than
// dispatching EditorView.scrollIntoView — the effect path through CM's
// transaction system was leaving the viewport in inconsistent states
// when triggered from outside a CM-initiated update.
function resetAncestorScrollLeft(start) {
    let node = start;
    while (node && node !== document.scrollingElement) {
        if (node.scrollLeft) node.scrollLeft = 0;
        node = node.parentElement;
    }
    if (document.scrollingElement) document.scrollingElement.scrollLeft = 0;
}

// `align` is "center" (default — jump targets read best mid-viewport) or
// "top" (restoring a persisted reading position, where the recorded line was
// the viewport's TOP line; centring it would drift the position half a screen
// per restore).
export function scrollToLine(id, lineNumber, flash, align) {
    const e = editors.get(id);
    if (!e) return;
    const view = e.view;
    if (!Number.isInteger(lineNumber) || lineNumber < 1) return;
    const alignTop = align === "top";
    const totalLines = view.state.doc.lines;
    const safeLine = Math.min(lineNumber, totalLines);

    const findLineEl = () => {
        const line = view.state.doc.line(safeLine);
        const dom = view.domAtPos(line.from);
        let lineEl = dom?.node instanceof Element ? dom.node : dom?.node?.parentElement;
        while (lineEl && !(lineEl.classList && lineEl.classList.contains("cm-line"))) {
            lineEl = lineEl.parentElement;
        }
        return lineEl;
    };

    const doScroll = () => {
        const line = view.state.doc.line(safeLine);
        const block = view.lineBlockAt(line.from);
        const scroller = view.scrollDOM;
        const scrollMax = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        if (scrollMax > 0) {
            // Bounded editor (default mount) — scroll the editor's own
            // scroller. Direct scrollTop avoids the inconsistent viewport
            // state we used to see going through EditorView.scrollIntoView.
            const target = alignTop
                ? block.top
                : block.top - scroller.clientHeight / 2 + block.height / 2;
            scroller.scrollTop = Math.max(0, Math.min(scrollMax, target));
            scroller.scrollLeft = 0;
        } else {
            // Fluid mount (`Fluid="true"`): the editor's scroller is
            // overflow:visible, so an outer container (.content) scrolls
            // the page. Use inline:"nearest" so scrollIntoView only
            // moves vertically — `inline:"start"` aligns the cm-line's
            // left edge with the scrollport's left edge, but since
            // cm-line begins AFTER the gutter, that scrolls the page
            // right by the gutter width and chops off the start of
            // shorter lines. The follow-up resetAncestorScrollLeft
            // call then clears any residual horizontal scroll the
            // previous jump (or a long line elsewhere) left behind.
            const lineEl = findLineEl();
            if (lineEl) {
                lineEl.scrollIntoView({ block: alignTop ? "start" : "center", inline: "nearest", behavior: "instant" });
            } else {
                view.dispatch({
                    effects: EditorView.scrollIntoView(line.from, { y: "center" }),
                });
            }
            resetAncestorScrollLeft(scroller);
        }
    };

    // Sticky-highlight the destination line via the state field. Doing
    // this before the scroll so the decoration is already in place when
    // CodeMirror paints the viewport — no first-paint flicker, and it
    // survives the user scrolling the row off-screen and back.
    if (flash) {
        view.dispatch({ effects: setCurrentLineEffect.of(safeLine) });
    }

    requestAnimationFrame(() => {
        doScroll();
        requestAnimationFrame(() => {
            doScroll();
        });
    });
}

// ── Compare-pane geometry ──────────────────────────────────
//
// Everything that positions something against a compare pane — the scroll
// sync, the change-nav blocks, the overview ruler — needs the same answer: how
// far down the pane's content does source line N sit? That used to be
// arithmetic over the server's filler list ("real lines above it, plus blank
// filler rows above it"), written out at four call sites. It can't stay
// arithmetic: once unchanged regions fold away, the rows above a line no
// longer predict where it renders, and each copy would go wrong on its own.
//
// CodeMirror's height map already tracks all of it — the lines, the filler
// block widgets, and any folded ranges — so ask it instead of re-deriving it.
// `lineBlockAt(pos).top` is the top of that line's own block, below any filler
// anchored above it, in pixels from the top of the content. Both panes mount
// with the same configuration and so share a line height, which is what makes
// a pixel top in one pane comparable with a pixel top in the other: matching
// lines sit at equal offsets by construction, since that is the job the
// fillers were computed to do.
//
// Anchoring on a position rather than mirroring raw scrollTop is what keeps
// the panes together. CodeMirror estimates the height of off-screen rows, and
// since each pane holds a different number of fillers above any given row the
// two estimates diverge — mirrored scrollTop slips apart on long jumps, where
// a measured block top does not.

/// Pixel top of a 1-based line within the editor's content, or null when the
/// editor id is unknown. Out-of-range lines clamp into the document.
export function lineTop(id, line) {
    const e = editors.get(id);
    if (!e || !Number.isFinite(line)) return null;
    const view = e.view;
    const n = Math.max(1, Math.min(Math.round(line), view.state.doc.lines));
    return view.lineBlockAt(view.state.doc.line(n).from).top;
}

/// Inverse of lineTop: the 1-based line whose block covers `top` pixels down
/// the content, or null. A `top` that lands inside a filler gap reports the
/// line the gap is anchored above — the reading the callers want, since the
/// gap stands in for the change that follows it.
export function lineAtTop(id, top) {
    const e = editors.get(id);
    if (!e || !Number.isFinite(top)) return null;
    const view = e.view;
    // Probe a pixel past the boundary. `top` is usually another pane's block
    // top, and a height that lands exactly on a boundary is ambiguous —
    // fractional row heights put it inside the block above about half the
    // time, which reads as the counterpart pane sitting one row high.
    const block = view.lineBlockAtHeight(Math.max(0, top) + 1);
    if (!block) return null;
    return view.state.doc.lineAt(block.from).number;
}

/// Runs `fn` once the pane's geometry has settled: after CodeMirror has
/// measured itself, and after the fillers have been re-issued at that measured
/// row height. Anything that reads lineTop or paneMetrics at mount time has to
/// wait for this — read a frame too early and it measures a pane whose gaps
/// are still CodeMirror's placeholder height, which is how the overview ruler
/// ended up marking rows that had since moved.
export function afterLayout(id, fn) {
    const e = editors.get(id);
    if (!e) return;
    const view = e.view;
    view.requestMeasure({
        read: () => null,
        // Two frames: the first is the one the filler re-issue dispatches on
        // (see withMeasuredLineHeight), the second is after it has applied.
        write: () => requestAnimationFrame(() => requestAnimationFrame(() => {
            if (view.dom.isConnected) fn();
        })),
    });
}

/// The denominators callers need alongside lineTop: total content height, for
/// fraction-of-document positions like the overview ruler, and one line's
/// height, for the tolerances that used to be counted in rows.
export function paneMetrics(id) {
    const e = editors.get(id);
    if (!e) return null;
    return { contentHeight: e.view.contentHeight, lineHeight: e.view.defaultLineHeight };
}

/// Syncs the destination compare pane to the source pane by position rather
/// than by raw scrollTop. Reads the source's top block and the offset into it
/// (measured, therefore exact), finds the line at the same content offset in
/// the destination, and scrolls there with scrollIntoView so CodeMirror
/// measures the target and lands precisely. Note this moves the destination
/// twice (the scrollIntoView hop, then the measured correction) — the caller
/// must ignore the destination's scroll events wholesale rather than trying to
/// recognise individual echoes (see wireCompareScrollSync in source-viewer.js).
export function syncComparePanes(srcId, dstId) {
    const src = editors.get(srcId);
    const dst = editors.get(dstId);
    if (!src || !dst) return;
    const srcView = src.view;
    const top = srcView.scrollDOM.scrollTop;
    const block = srcView.lineBlockAtHeight(top);
    if (!block) return;
    const frac = top - block.top;

    const dstLine = lineAtTop(dstId, block.top);
    if (dstLine === null) return;
    const dstView = dst.view;
    const pos = dstView.state.doc.line(dstLine).from;
    dstView.dispatch({ effects: EditorView.scrollIntoView(pos, { y: "start" }) });
    // Correct on the next frame, not in a requestMeasure read: CodeMirror
    // applies the scrollIntoView during its own measure cycle, so a correction
    // written from inside that cycle gets overwritten by it — which dropped
    // `frac` and left the follower snapped to a row boundary, up to a row out
    // of step with the pane the user was actually scrolling.
    requestAnimationFrame(() => {
        if (!dstView.dom.isConnected) return;
        const b = dstView.lineBlockAt(pos);
        const scroller = dstView.scrollDOM;
        const max = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        scroller.scrollTop = Math.max(0, Math.min(max, b.top + frac));
    });
}

/// Scrolls BOTH compare panes to a matching position in the SAME animation
/// frames, so a jump (next/previous change) moves them together instead of
/// one-then-the-other. The old path scrolled the anchor pane and synced the
/// other ~80ms later, which read as a visible two-step. `anchorLine` is a
/// 1-based line in the anchor pane; the counterpart in the other pane is the
/// line sitting at the same content offset. Two-pass over frames for CM6's
/// height-estimate correction (same as scrollToLine) — and the counterpart is
/// re-derived on each pass, because the first pass forces CodeMirror to
/// measure the rows it just scrolled into view, so the second pass maps
/// against corrected geometry instead of estimates.
export function scrollComparePanes(anchorId, otherId, anchorLine, flash) {
    const a = editors.get(anchorId);
    const o = editors.get(otherId);
    if (!a || !o) return;
    const aView = a.view;
    const oView = o.view;
    if (!Number.isInteger(anchorLine) || anchorLine < 1) return;
    const safeAnchor = Math.min(anchorLine, aView.state.doc.lines);

    if (flash) {
        aView.dispatch({ effects: setCurrentLineEffect.of(safeAnchor) });
    }

    const scrollOne = (view, line) => {
        const block = view.lineBlockAt(view.state.doc.line(line).from);
        const scroller = view.scrollDOM;
        const max = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
        scroller.scrollTop = Math.max(0, Math.min(max, block.top));
        scroller.scrollLeft = 0;
    };
    const doScroll = () => {
        scrollOne(aView, safeAnchor);
        const otherLine = lineAtTop(otherId, lineTop(anchorId, safeAnchor));
        if (otherLine !== null) scrollOne(oView, otherLine);
    };
    requestAnimationFrame(() => {
        doScroll();
        requestAnimationFrame(doScroll);
    });
}

/// The 1-based line currently at the top of the editor's viewport, or null.
/// The compare page uses it to mirror the reading position into the URL
/// (?line=) so a refresh or a shared link restores the same spot.
export function topLine(id) {
    const e = editors.get(id);
    if (!e) return null;
    const view = e.view;
    const block = view.lineBlockAtHeight(view.scrollDOM.scrollTop);
    return block ? view.state.doc.lineAt(block.from).number : null;
}

/// 1-based (line, column) of the primary cursor, or null when the editor id
/// is unknown. The Object Explorer's Shift+F12 uses it to ask the server what
/// the caret is sitting on — the same coordinates a click would report.
export function cursorPosition(id) {
    const e = editors.get(id);
    if (!e) return null;
    const head = e.view.state.selection.main.head;
    const line = e.view.state.doc.lineAt(head);
    return { line: line.number, column: head - line.from + 1 };
}

/// Clear the sticky line highlight. The viewer doesn't currently expose
/// this beyond the file-id changing (each mount starts with a fresh
/// state), but external pages can call it via the editor id when needed.
export function clearCurrentLine(id) {
    const e = editors.get(id);
    if (!e) return;
    e.view.dispatch({ effects: setCurrentLineEffect.of(0) });
}

// Builds a CodeMirror extension that wraps each declaration name range
// with a `cm-symbol-decl` class so users see what's clickable.
function buildDeclarationDecorationExtensions(declarations) {
    if (!Array.isArray(declarations) || declarations.length === 0) return [];
    // Defensive sort: the server appends object declarations before member
    // symbols, so a file shipping multiple objects (e.g. an extension with
    // several objects in one .al) yields ranges that aren't ascending by
    // `from`. RangeSetBuilder.add requires ascending order, so normalise here
    // the same way buildResolvableDecorationExtensions does.
    const sorted = declarations
        .filter(d => Number.isInteger(d.line) && d.line >= 1)
        .slice()
        .sort((a, b) => (a.line - b.line) || ((a.columnStart ?? 1) - (b.columnStart ?? 1)));
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        for (const decl of sorted) {
            const lineNo = decl.line;
            if (lineNo > view.state.doc.lines) continue;
            const line = view.state.doc.line(lineNo);
            const from = line.from + Math.max(0, (decl.columnStart ?? 1) - 1);
            const toCol = decl.columnEnd ?? (decl.columnStart ?? 1);
            const to = Math.min(line.to, line.from + Math.max(from - line.from, toCol - 1));
            if (to <= from) continue;
            // `data-symbol-id` is an oe_module_objects id for an object header
            // and an oe_module_symbols id for a member — two tables whose id
            // spaces overlap. The flag says which, so anything that looks the
            // id up (the hover card) can't fetch from the wrong table.
            const attributes = { "data-symbol-id": String(decl.symbolId) };
            if (decl.isMemberSymbol) attributes["data-member-symbol"] = "1";
            builder.add(from, to, Decoration.mark({ class: "cm-symbol-decl", attributes }));
        }
        return builder.finish();
    })];
}

// Decorates every range the server identified as a "resolvable" reference
// (object names, procedure call sites, etc.) with `cm-symbol-ref` so users
// get the same dotted underline they see on declarations. Ranges are
// pre-sorted server-side; the RangeSetBuilder still requires ascending order.
function buildResolvableDecorationExtensions(resolvables) {
    if (!Array.isArray(resolvables) || resolvables.length === 0) return [];
    // Defensive sort: ranges must be added in order of `from` to the builder,
    // and pre-sorted input is cheap to re-verify here.
    const sorted = resolvables
        .filter(r => Number.isInteger(r.line) && r.line >= 1)
        .slice()
        .sort((a, b) => (a.line - b.line) || ((a.columnStart ?? 1) - (b.columnStart ?? 1)));
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        const docLines = view.state.doc.lines;
        for (const ref of sorted) {
            if (ref.line > docLines) continue;
            const line = view.state.doc.line(ref.line);
            const from = line.from + Math.max(0, (ref.columnStart ?? 1) - 1);
            const toCol = ref.columnEnd ?? (ref.columnStart ?? 1);
            const to = Math.min(line.to, line.from + Math.max(from - line.from, toCol - 1));
            if (to <= from) continue;
            // The symbol id (when the importer resolved one) rides along on
            // the mark so the hover card can fetch without re-resolving the
            // position server-side.
            builder.add(from, to, Decoration.mark(ref.symbolId
                ? { class: "cm-symbol-ref", attributes: { "data-symbol-id": String(ref.symbolId) } }
                : { class: "cm-symbol-ref" }));
        }
        return builder.finish();
    })];
}

// Renders a small floating context menu at (x, y). Each item is
// `{ label, action }`; `action` returns the promise from a DotNet
// invokeMethodAsync call. The menu removes itself when an item is
// clicked or when the document-level click handler in mountReadOnly
// closes it.
/// True when the platform's "go to definition" modifier is Cmd rather than
/// Ctrl. Only ever affects a label — the handlers accept either modifier.
export function usesCommandKey() {
    const platform = navigator.userAgentData?.platform ?? navigator.platform ?? "";
    return /mac|iphone|ipad/i.test(platform);
}

/// The right-click menu, and — since the hover card stopped printing them —
/// the place a reader finds out which gestures have a keyboard or mouse
/// shortcut. That is where every IDE puts them, and it is the surface you are
/// already looking at when you want the action.
///
/// An item without `keys` simply has none. "Find in this file" is the one to
/// be careful with: the footer advertises Ctrl+F for a DIFFERENT feature that
/// happens to share the name — CodeMirror's search box, not this occurrence
/// list — so putting that chip here would send people to the wrong thing.
/// Returns `{ menu, close }` — `close(restoreFocus)` is the single dismissal
/// path and removes every listener the menu registered (#718).
function renderMenu(x, y, items, opener) {
    const menu = document.createElement("div");
    menu.className = "cm-symbol-menu";
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;
    // Assigned once the menu is mounted; the item handlers only read it when
    // they run, which is after that.
    let close = () => menu.remove();

    for (const item of items) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "cm-symbol-menu__item";
        btn.setAttribute("role", "menuitem");

        const label = document.createElement("span");
        label.textContent = item.label;
        btn.appendChild(label);

        if (Array.isArray(item.keys) && item.keys.length > 0) {
            const keys = document.createElement("span");
            keys.className = "cm-symbol-menu__keys";
            for (const key of item.keys) {
                const chip = document.createElement("span");
                chip.className = "kbd";
                chip.textContent = key;
                keys.appendChild(chip);
            }
            btn.appendChild(keys);
        }

        if (item.disabled) {
            btn.disabled = true;
            btn.classList.add("cm-symbol-menu__item--disabled");
        } else {
            btn.addEventListener("click", () => {
                close(false);
                try {
                    const result = item.action();
                    if (result && typeof result.catch === "function") {
                        result.catch(err => console.warn(`${item.label} failed:`, err));
                    }
                } catch (err) {
                    console.warn(`${item.label} threw:`, err);
                }
            });
        }
        menu.appendChild(btn);
    }

    document.body.appendChild(menu);
    close = wireFloatingMenu(menu, opener);
    return { menu, close };
}

/// Makes the right-click menu usable from the keyboard and dismissable
/// exactly once: `role="menu"` / `role="menuitem"`, focus on the first
/// enabled item, ArrowUp/ArrowDown between items, Escape closes and hands
/// focus back to the opener. Returns the one `close(restoreFocus = true)`
/// that removes the menu AND its click / scroll / keydown listeners, so a
/// dismissal through an item leaves nothing attached to the document (#718).
function wireFloatingMenu(menu, opener) {
    menu.setAttribute("role", "menu");
    const items = () => Array.from(menu.querySelectorAll('[role="menuitem"]'))
        .filter(el => !el.disabled);

    let closed = false;
    const close = (restoreFocus = true) => {
        if (closed) return;
        closed = true;
        document.removeEventListener("click", onOutside);
        document.removeEventListener("scroll", onOutside, true);
        document.removeEventListener("keydown", onKeydown, true);
        menu.remove();
        if (restoreFocus && opener && document.contains(opener)) opener.focus?.();
    };
    const onOutside = () => close(false);
    const onKeydown = e => {
        if (e.key === "Escape") {
            e.preventDefault();
            e.stopPropagation();
            close();
            return;
        }
        if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
        const all = items();
        if (all.length === 0) return;
        e.preventDefault();
        const at = all.indexOf(document.activeElement);
        const step = e.key === "ArrowDown" ? 1 : -1;
        const next = at < 0
            ? (step === 1 ? 0 : all.length - 1)
            : (at + step + all.length) % all.length;
        all[next].focus();
    };

    document.addEventListener("click", onOutside);
    document.addEventListener("scroll", onOutside, true);
    document.addEventListener("keydown", onKeydown, true);
    items()[0]?.focus();
    return close;
}

// Translates the C# {lineNumber: cssClass} map into a CodeMirror extension
// that decorates whole lines. Used by the Object Explorer diff view to mark
// added / removed / modified lines on each side.
function buildLineDecorationExtensions(lineDecorations) {
    if (!lineDecorations || typeof lineDecorations !== "object") return [];
    // No decorated lines means the single-file viewer, where this provider
    // would still walk every line of the document on every view update just to
    // produce an empty set. Same guard buildDiffGutterExtensions carries.
    if (Object.keys(lineDecorations).length === 0) return [];
    return [EditorView.decorations.of((view) => {
        const builder = new RangeSetBuilder();
        for (let i = 1; i <= view.state.doc.lines; i++) {
            const cls = lineDecorations[i];
            if (!cls) continue;
            const line = view.state.doc.line(i);
            builder.add(line.from, line.from, Decoration.line({ class: cls }));
        }
        return builder.finish();
    })];
}

// A block widget that renders `size` line-heights of empty space — the
// KDiff3-style alignment gap on the compare page. Height is computed from the
// editor's measured line height so it tracks the font/zoom, and `eq` lets
// CodeMirror skip re-rendering an unchanged filler.
//
// `estimatedHeight` matters as much as the real height: CodeMirror treats a
// block widget with no estimate as 0px until it scrolls into view and gets
// measured, so every off-screen filler used to under-count the document
// height and the compare page "jumped" on each scroll / overview-ruler hop as
// gaps were discovered one by one. mountReadOnly re-builds the fillers with
// the editor's measured defaultLineHeight right after mount, so the estimate
// is exact and scroll geometry is stable from the first frame.
const FILLER_LINE_HEIGHT_FALLBACK = 20;

// Calls `fn` with the editor's measured line height, once there is one.
//
// `view.defaultLineHeight` is the height oracle's value, and at construction
// time the oracle has measured nothing — read synchronously it hands back
// CodeMirror's 14px placeholder, against our 19.6px rows. Every alignment gap
// then rendered a third short, and since only one pane holds any given gap the
// two compare panes slid a full row apart over a handful of them.
//
// Two hops, and both are load-bearing: requestMeasure's read phase runs after
// the view measures itself, and the rAF gets the caller's dispatch back OUT of
// that cycle, which refuses state updates ("Calls to EditorView.update are not
// allowed while an update is in progress").
function withMeasuredLineHeight(view, fn) {
    view.requestMeasure({
        read: () => view.defaultLineHeight,
        write: (lineHeight) => {
            requestAnimationFrame(() => {
                if (view.dom.isConnected) fn(lineHeight);
            });
        },
    });
}

class FillerWidget extends WidgetType {
    constructor(size, lineHeight) {
        super();
        this._size = size;
        this._lineHeight = lineHeight;
    }
    eq(other) {
        return other instanceof FillerWidget
            && other._size === this._size
            && other._lineHeight === this._lineHeight;
    }
    get estimatedHeight() {
        return this._size * (this._lineHeight ?? FILLER_LINE_HEIGHT_FALLBACK);
    }
    toDOM(view) {
        const el = document.createElement("div");
        el.className = "cm-diff-filler";
        // The height we were built with, not a fresh read: the rendered height
        // has to match `estimatedHeight` or CodeMirror's height map and the
        // DOM disagree about where every row below this gap sits.
        el.style.height = ((this._lineHeight ?? view.defaultLineHeight) * this._size) + "px";
        el.setAttribute("aria-hidden", "true");
        return el;
    }
    // No DOM events to ignore inside a passive spacer.
    ignoreEvent() {
        return false;
    }
}

// Builds the block-widget filler set for the given editor state. `before` is a
// 1-based source line the gap precedes; `before > doc.lines` means a trailing
// gap (the opposite pane appended lines past this one's end), anchored after the
// last line. The serializer emits gaps in ascending line order, which is what
// RangeSetBuilder requires.
function buildFillerSet(state, fillers, lineHeight) {
    const builder = new RangeSetBuilder();
    const doc = state.doc;
    for (const f of fillers) {
        const size = Number(f?.size);
        const before = Number(f?.before);
        if (!Number.isFinite(size) || size < 1) continue;
        if (!Number.isFinite(before) || before < 1) continue;
        const widget = Decoration.widget({
            widget: new FillerWidget(size, lineHeight),
            block: true,
            // side -1 places the spacer above the anchored line; +1 below.
            side: before > doc.lines ? 1 : -1,
        });
        const pos = before > doc.lines
            ? doc.line(doc.lines).to
            : doc.line(before).from;
        builder.add(pos, pos, widget);
    }
    return builder.finish();
}

// Intra-line word-diff marks (compare page): each `{line, from, to}` range
// (1-based columns, `to` exclusive) gets a `cm-diff-word` mark so the changed
// words inside a modified line stand out against the whole-row tint. Static
// StateField for the same reason as the fillers: the doc is read-only, so the
// set is computed once.
function buildWordDiffExtensions(ranges) {
    if (!Array.isArray(ranges) || ranges.length === 0) return [];
    const field = StateField.define({
        create(state) {
            return buildWordDiffSet(state, ranges);
        },
        update(value, tr) {
            return tr.docChanged ? buildWordDiffSet(tr.state, ranges) : value;
        },
        provide: (f) => EditorView.decorations.from(f),
    });
    return [field];
}

function buildWordDiffSet(state, ranges) {
    const builder = new RangeSetBuilder();
    const doc = state.doc;
    const mark = Decoration.mark({ class: "cm-diff-word" });
    for (const r of ranges) {
        const line = Number(r?.line);
        const from = Number(r?.from);
        const to = Number(r?.to);
        if (!Number.isFinite(line) || line < 1 || line > doc.lines) continue;
        if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from || from < 1) continue;
        const l = doc.line(line);
        // Clamp to the line so a serializer/renderer length mismatch can't
        // spill the mark into the next line.
        const start = Math.min(l.from + (from - 1), l.to);
        const end = Math.min(l.from + (to - 1), l.to);
        if (end > start) builder.add(start, end, mark);
    }
    return builder.finish();
}

// Translates the C# [{before, size}] filler list into block-widget decorations
// that pad each compare pane so matching lines align across the two editors.
// Block widgets affect vertical layout, so CodeMirror only honours them from a
// static decoration source — a StateField here, NOT the view-function form of
// EditorView.decorations.of (which silently drops block decorations). The doc
// is read-only on the compare page, so the set is computed once at create.
function buildFillerDecorationExtensions(fillers, lineHeight) {
    if (!Array.isArray(fillers) || fillers.length === 0) return [];
    const field = StateField.define({
        create(state) {
            return buildFillerSet(state, fillers, lineHeight);
        },
        update(value, tr) {
            return tr.docChanged ? buildFillerSet(tr.state, fillers, lineHeight) : value;
        },
        provide: (f) => EditorView.decorations.from(f),
    });
    return [field];
}

// ── The inline (unified) compare view ────────────────────────────────
//
// Both of these exist because a unified document is synthesised rather than
// read: it interleaves the two sides, so CodeMirror's own line numbers count
// rows of something that is not a file, and the runs of unchanged code between
// the changes were never put in the document at all. The server says what each
// row's numbers are and where the seams fall (UnifiedDiffSerializer); this
// renders both. See #576 and #579.

// A gutter cell holding a line number — or nothing, on a row that exists on
// only one side of the diff.
class NumberMarker extends GutterMarker {
    constructor(text) {
        super();
        this.text = text;
    }
    eq(other) {
        return other instanceof NumberMarker && other.text === this.text;
    }
    toDOM() {
        return document.createTextNode(this.text);
    }
}

// Two number gutters — old side then new — driven by the server's per-row
// pairs. Falls back to CodeMirror's own counter when there are no pairs, which
// is every editor except an inline compare pane.
function buildUnifiedGutterExtensions(pairs) {
    if (!Array.isArray(pairs) || pairs.length === 0) return [lineNumbers()];
    const numberAt = (row, side) => {
        const pair = pairs[row - 1];
        const value = Array.isArray(pair) ? pair[side] : null;
        return Number.isFinite(value) ? String(value) : "";
    };
    const sideGutter = (side, cls) => gutter({
        class: `cm-lineNumbers cm-unifiedGutter ${cls}`,
        lineMarker: (view, block) =>
            new NumberMarker(numberAt(view.state.doc.lineAt(block.from).number, side)),
        // The pairs are fixed for the life of the document, so no update can
        // change a marker — telling CodeMirror that saves it re-asking on
        // every transaction.
        lineMarkerChange: () => false,
    });
    return [sideGutter(0, "cm-unifiedGutter--old"), sideGutter(1, "cm-unifiedGutter--new")];
}

// ── Collapsing a diff ────────────────────────────────────────────────
//
// Both compare layouts hide their unchanged stretches the same way: block
// REPLACE decorations, which take the rows out of the layout, with a band
// standing where they were. The lines stay in the document, so the band can
// put them back.
//
// Side-by-side has to hide in lockstep — each pane holds a real file and the
// two are kept level by blank filler rows measured against the full text, so
// the server pairs the regions and both panes hide the same number of rows
// (SideBySideCollapse). The inline pane holds one synthesised document and
// answers to itself.
//
// A hidden region shows a band in its place; an expanded one keeps the band
// above its first line, so the seam is still visible and the click still
// reverses. Both live in one state field because expanding is a state change,
// not a remount.

// Height of a `.hunk` band: 22px of row plus its two keylines (pages-power.css).
// A block widget with no estimate counts as 0px until it scrolls into view, and
// the document grows under the reader as it is discovered — the same trap the
// alignment fillers fell into.
const HUNK_HEIGHT = 24;
const toggleRegionEffect = StateEffect.define();

class CollapseBandWidget extends WidgetType {
    constructor(text, index, hidden) {
        super();
        this.text = text;
        this.index = index;
        this.hidden = hidden;
    }
    eq(other) {
        return other instanceof CollapseBandWidget
            && other.text === this.text
            && other.index === this.index
            && other.hidden === this.hidden;
    }
    // Same reason the fillers carry one: an unmeasured block widget counts as
    // 0px until it scrolls into view, and a pane whose height grows as you
    // scroll cannot stay level with the pane beside it.
    get estimatedHeight() {
        return HUNK_HEIGHT;
    }
    toDOM() {
        return hunkBand(this.text, this.index, this.hidden);
    }
    ignoreEvent() {
        return true;
    }
}

function buildCollapseExtensions(regions) {
    if (!Array.isArray(regions) || regions.length === 0) return [];
    const valid = regions.filter(r => r && Number.isFinite(r.index));

    const build = (state, expanded) => {
        const builder = new RangeSetBuilder();
        const doc = state.doc;
        for (const r of valid) {
            const from = Number(r.from);
            const to = Number(r.to);
            const hides = Number.isFinite(from) && Number.isFinite(to)
                && from >= 1 && to >= from && to <= doc.lines;
            const text = String(r.header ?? "");

            if (!hides) {
                // A band that hides nothing — the banner over a diff whose
                // first change is at the top.
                const before = Number(r.before);
                if (!Number.isFinite(before) || before < 1 || before > doc.lines) continue;
                const pos = doc.line(before).from;
                builder.add(pos, pos, Decoration.widget({
                    widget: new CollapseBandWidget(text, null, false),
                    block: true,
                    // Below a filler at the same position would put the banner
                    // inside the gap it is introducing.
                    side: -2,
                }));
                continue;
            }

            const start = doc.line(from).from;
            if (expanded.has(r.index)) {
                builder.add(start, start, Decoration.widget({
                    widget: new CollapseBandWidget(text, r.index, false),
                    block: true,
                    side: -2,
                }));
            } else {
                builder.add(start, doc.line(to).to, Decoration.replace({
                    widget: new CollapseBandWidget(text, r.index, true),
                    block: true,
                }));
            }
        }
        return builder.finish();
    };

    const expandedField = StateField.define({
        create: () => new Set(),
        update(value, tr) {
            for (const e of tr.effects) {
                if (!e.is(toggleRegionEffect)) continue;
                const next = new Set(value);
                if (next.has(e.value)) next.delete(e.value);
                else next.add(e.value);
                return next;
            }
            return value;
        },
    });

    const decorations = StateField.define({
        create: (state) => build(state, state.field(expandedField)),
        update(value, tr) {
            const toggled = tr.effects.some(e => e.is(toggleRegionEffect));
            return (tr.docChanged || toggled) ? build(tr.state, tr.state.field(expandedField)) : value;
        },
        provide: (f) => EditorView.decorations.from(f),
    });

    return [expandedField, decorations];
}

/// Shows or hides one collapsed region. The caller drives BOTH compare panes
/// with the same index — the two are only level while they hide the same rows.
export function toggleCollapsedRegion(id, index) {
    const e = editors.get(id);
    if (!e || !Number.isFinite(index)) return;
    e.view.dispatch({ effects: toggleRegionEffect.of(index) });
}

// Lucide `chevron-down`, built inline because this runs outside Blazor and so
// cannot reach the Icon component. Down means "hidden, click to reveal"; the
// stylesheet flips it when aria-expanded says the lines are already showing.
function bandChevron() {
    const NS = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(NS, "svg");
    svg.setAttribute("class", "hunk__chev");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("width", "12");
    svg.setAttribute("height", "12");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "2.5");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS(NS, "path");
    path.setAttribute("d", "m6 9 6 6 6-6");
    svg.append(path);
    return svg;
}

// The `.hunk` band itself, shared by the inline view's banners and the
// side-by-side view's collapse bands. `index` is set only when the band hides
// something: it makes the band a control, and the index is what lets the click
// reach BOTH panes (a band expanded in one pane and not the other would put
// every line below it opposite the wrong counterpart).
function hunkBand(text, index, hidden) {
    const el = document.createElement("div");
    el.className = "hunk";
    if (index === null) {
        // A band that hides nothing is not a control, so it gets no chevron:
        // the one mark that means "this opens" is never spent on something
        // inert. That is also what tells the inline view's banners apart from
        // the side-by-side view's collapse bands, which look identical
        // otherwise and behave completely differently.
        el.textContent = text;
        return el;
    }
    el.setAttribute("role", "button");
    el.tabIndex = 0;
    // aria-expanded is both the disclosure state a screen reader announces and
    // the hook the stylesheet rotates the chevron on, so the two cannot drift.
    el.setAttribute("aria-expanded", hidden ? "false" : "true");
    el.title = hidden ? "Show the unchanged lines here" : "Hide these lines again";
    el.append(bandChevron());
    const label = document.createElement("span");
    label.textContent = text;
    el.append(label);
    const fire = () => el.dispatchEvent(new CustomEvent("aldt-toggle-region", {
        bubbles: true,
        detail: { index },
    }));
    el.addEventListener("click", fire);
    el.addEventListener("keydown", (e) => {
        if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            fire();
        }
    });
    return el;
}

// A GutterMarker that renders no content — just an element class, so CSS can
// paint the gutter cell as a coloured change bar.
class DiffGutterMarker extends GutterMarker {
    constructor(cls) {
        super();
        this._cls = cls;
    }
    get elementClass() {
        return this._cls;
    }
}

// Compare-page change bar: a thin gutter that paints a coloured cell on every
// inserted / deleted / modified line, so changes are visible at a glance even
// when scrolled away from the tinted line. Driven by the same `lineDecorations`
// (`{line: "cm-diff-<kind>"}`) the line backgrounds use, so the two stay in
// step. Returns no extension when there are no diff lines (single-file viewer).
function buildDiffGutterExtensions(lineDecorations) {
    if (!lineDecorations || typeof lineDecorations !== "object") return [];
    if (Object.keys(lineDecorations).length === 0) return [];

    const markers = {};
    const markerFor = (kind) => {
        if (!markers[kind]) {
            markers[kind] = new DiffGutterMarker(`cm-diff-gutter-mark cm-diff-gutter-${kind}`);
        }
        return markers[kind];
    };

    return [gutter({
        class: "cm-diff-gutter",
        lineMarker(view, line) {
            const lineNo = view.state.doc.lineAt(line.from).number;
            const cls = lineDecorations[lineNo];
            if (typeof cls !== "string" || !cls.startsWith("cm-diff-")) return null;
            return markerFor(cls.slice("cm-diff-".length));
        },
    })];
}

// ── Live-updatable diff decorations (editable compare panes) ──────
//
// mountReadOnly bakes the diff (line tints, fillers, word-diff, gutter) in at
// mount time because its doc never changes. The editable Diff tool needs
// the opposite: the panes ARE the input, so the diff has to be swapped in
// place as the user types. These helpers hold the current diff payload in a
// StateField and rebuild the decoration sets whenever a setDiffEffect lands
// (or the doc changes). Same shape as the sticky current-line field above,
// generalised to the four diff decoration kinds. The line/word/filler set
// builders (buildLineDecoSet / buildWordDiffSet / buildFillerSet) are shared
// with the read-only path.

const setDiffEffect = StateEffect.define();

// The line-background set (extracted from buildLineDecorationExtensions so the
// dynamic field can reuse it). `{ [1-basedLine]: cssClass }` → line decorations.
function buildLineDecoSet(state, lineDecorations) {
    const builder = new RangeSetBuilder();
    if (lineDecorations && typeof lineDecorations === "object") {
        for (let i = 1; i <= state.doc.lines; i++) {
            const cls = lineDecorations[i];
            if (!cls) continue;
            const line = state.doc.line(i);
            builder.add(line.from, line.from, Decoration.line({ class: cls }));
        }
    }
    return builder.finish();
}

function normalizeDiffPayload(p) {
    return {
        lineDecorations: (p && typeof p.lineDecorations === "object" && p.lineDecorations) || {},
        fillers: Array.isArray(p?.fillers) ? p.fillers : [],
        wordDiff: Array.isArray(p?.wordDiff) ? p.wordDiff : [],
        // Measured line height for the filler block widgets; null falls back to
        // the estimate. mountCompareEditor re-dispatches with the real value
        // once the view exists.
        lineHeight: Number.isFinite(p?.lineHeight) ? p.lineHeight : null,
    };
}

// Holds the whole diff payload. Every setDiffEffect replaces it wholesale; the
// derived decoration fields below read it back out of state.
function diffDataFieldFactory(initial) {
    return StateField.define({
        create() { return normalizeDiffPayload(initial); },
        update(value, tr) {
            for (const effect of tr.effects) {
                if (effect.is(setDiffEffect)) return normalizeDiffPayload(effect.value);
            }
            return value;
        },
    });
}

// True when this transaction carries a fresh diff payload — the derived fields
// rebuild on that OR on a doc edit (which shifts line/column anchors).
function diffChanged(tr) {
    return tr.docChanged || tr.effects.some(e => e.is(setDiffEffect));
}

function dynamicLineDecoField(dataField) {
    return StateField.define({
        create(state) { return buildLineDecoSet(state, state.field(dataField).lineDecorations); },
        update(value, tr) {
            return diffChanged(tr)
                ? buildLineDecoSet(tr.state, tr.state.field(dataField).lineDecorations)
                : value;
        },
        provide: f => EditorView.decorations.from(f),
    });
}

function dynamicFillerField(dataField) {
    return StateField.define({
        create(state) {
            const d = state.field(dataField);
            return buildFillerSet(state, d.fillers, d.lineHeight);
        },
        update(value, tr) {
            if (!diffChanged(tr)) return value;
            const d = tr.state.field(dataField);
            return buildFillerSet(tr.state, d.fillers, d.lineHeight);
        },
        provide: f => EditorView.decorations.from(f),
    });
}

function dynamicWordDiffField(dataField) {
    return StateField.define({
        create(state) { return buildWordDiffSet(state, state.field(dataField).wordDiff); },
        update(value, tr) {
            return diffChanged(tr)
                ? buildWordDiffSet(tr.state, tr.state.field(dataField).wordDiff)
                : value;
        },
        provide: f => EditorView.decorations.from(f),
    });
}

// Change-bar gutter reading the live payload. lineMarkerChange forces a
// re-render on setDiffEffect so the bars track edits without a doc change.
function dynamicDiffGutter(dataField) {
    const markers = {};
    const markerFor = (kind) => {
        if (!markers[kind]) {
            markers[kind] = new DiffGutterMarker(`cm-diff-gutter-mark cm-diff-gutter-${kind}`);
        }
        return markers[kind];
    };
    return gutter({
        class: "cm-diff-gutter",
        lineMarker(view, line) {
            const dec = view.state.field(dataField).lineDecorations;
            const lineNo = view.state.doc.lineAt(line.from).number;
            const cls = dec[lineNo];
            if (typeof cls !== "string" || !cls.startsWith("cm-diff-")) return null;
            return markerFor(cls.slice("cm-diff-".length));
        },
        lineMarkerChange(update) {
            return update.transactions.some(tr => tr.effects.some(e => e.is(setDiffEffect)));
        },
    });
}

// ── Sticky current-line highlight ─────────────────────────────────
//
// A StateField holding a Decoration.set with at most one Decoration.line
// at the chosen 1-based line. Dispatched via setCurrentLineEffect from
// scrollToLine() so the row stays tinted even after the user scrolls it
// off-screen and back (DOM classes don't survive CM's row virtualisation,
// which is what the old fade-out animation suffered from).

const setCurrentLineEffect = StateEffect.define();

const currentLineField = StateField.define({
    create() {
        return Decoration.none;
    },
    update(value, tr) {
        // Map through doc edits so the highlight follows its line when
        // the document mutates (rare for the read-only viewer but the
        // editor is shared with editable mounts).
        value = value.map(tr.changes);
        for (const effect of tr.effects) {
            if (!effect.is(setCurrentLineEffect)) continue;
            const lineNo = effect.value;
            if (!Number.isInteger(lineNo) || lineNo < 1 || lineNo > tr.state.doc.lines) {
                value = Decoration.none;
                continue;
            }
            const line = tr.state.doc.line(lineNo);
            value = Decoration.set([
                Decoration.line({ class: "cm-line--current" }).range(line.from),
            ]);
        }
        return value;
    },
    provide: f => EditorView.decorations.from(f),
});

// Theme rule keeps the highlight readable in both of the themes
// themeCompartment swaps between. Anchored to the accent palette so the
// tint reads either way. We render the
// highlight as a translucent tint plus a left-edge accent stripe
// rather than a solid fill: a flat tint behind the line text hides
// the browser-native selection rectangle whenever the user drags
// across the linked line (`?line=N`), which is the same gesture
// users expect to work. The `box-shadow inset` adds the stripe
// without disturbing the line's text layout (no padding shift).
const currentLineTheme = EditorView.baseTheme({
    // The handoff's `.codev__ln.is-current` is --primary-weak. This used to
    // read `var(--editor-current-line-bg, rgba(99, 102, 241, 0.08))` and that
    // token is declared NOWHERE, so every highlighted line has been rendering
    // in the hard-coded fallback -- an indigo from outside the design system
    // entirely, on a screen whose accent is teal (#564). A var() with a
    // fallback nobody defines is not a hook, it is a hard-coded value with a
    // comment on it.
    //
    // The inset bar is ours and is kept: the handoff's current line is only
    // ever the cursor's, while ours is also where a `?line=N` deep link
    // landed, and a 5%-alpha tint alone is easy to scroll past when you have
    // arrived from a search hit rather than put the cursor there yourself.
    ".cm-line--current": {
        backgroundColor: "var(--primary-weak)",
        boxShadow: "inset 3px 0 0 var(--primary-ink)",
    },
    // Native ::selection on the highlighted line — the user expected
    // to be able to drag-select text inside a `?line=N` highlighted
    // line, but the accent-soft fill that originally sat there had
    // roughly the same hue as the browser's default selection blue,
    // making the selection invisible. Force a high-contrast inversion
    // (text/background swap) so selection always stands out, even when
    // it overlaps the highlight tint.
    ".cm-line--current ::selection": {
        backgroundColor: "var(--ink, #1f2024)",
        color: "var(--bg, #ffffff)",
    },
    ".cm-line--current::selection": {
        backgroundColor: "var(--ink, #1f2024)",
        color: "var(--bg, #ffffff)",
    },
});

/// Bottom-docked status bar — `Ln 1, Col 1 · 1,073 lines`, plus a
/// selection-length suffix when a range is selected. Mounts via CM6's
/// `showPanel` extension so the panel lives inside the editor's height
/// box and respects the same theme. Re-renders on every transaction
/// (cursor moves and document changes both flow through `update`), but
/// the DOM is cached on the panel so we only touch textContent.
///
/// When `procedures` is supplied, the bar also shows the containing
/// procedure name and a BC stack-trace-style procedure-relative line
/// number — e.g. `Ln 1247, Col 9 · in CheckDates (line 13)`. BC reports
/// stack frames as `<procedure> line N` where the `procedure`
/// declaration counts as line 0, so the relative number is
/// `cursorLine - procedure.startLine`. End of a procedure is taken
/// from `endLine` when present (modern imports) or from the next
/// procedure's `startLine - 1` (legacy fallback). When the cursor
/// doesn't sit inside any procedure the suffix is omitted entirely.
///
/// Opt-in via `mountReadOnly(..., { statusBar: true })`. The diff and
/// admin editors don't ask for it and stay untouched.
/// Resolves a 1-based line to the procedure that brackets it.
///
/// Extracted out of buildStatusBarExtension so the status bar and the
/// deep-link path answer "which procedure am I in" the same way (#564).
/// Duplicating the containment rule would be the same defect as two surfaces
/// spelling one status differently - it would just take a legacy file with no
/// endLine to make them disagree.
export function makeProcedureResolver(procedures) {
    // Pre-sort once; callers usually hand us a list already ordered by line,
    // but a defensive copy + sort means a single misordered entry can't
    // desync the lookup.
    const procs = Array.isArray(procedures) ? [...procedures] : [];
    procs.sort((a, b) => (a.startLine | 0) - (b.startLine | 0));

    /// Linear scan from the end - for a single-cursor click there's no
    /// observable difference vs. a binary search, and outlines top out in the
    /// low thousands of procedures even for the largest BC codeunits. Returns
    /// null when the cursor sits before the first procedure, between two
    /// procedures of a legacy file (no `endLine`) where the gap doesn't belong
    /// to either, or after the last procedure's explicit `endLine`.
    const findContaining = (line) => {
        for (let i = procs.length - 1; i >= 0; i--) {
            const p = procs[i];
            const start = p.startLine | 0;
            if (line < start) continue;
            // Explicit end-line wins when present. Otherwise fall back to the
            // next procedure's start - 1; the gap above that (after the last
            // procedure) is treated as in-scope.
            if (typeof p.endLine === "number" && p.endLine > 0) {
                return line <= p.endLine ? p : null;
            }
            const next = procs[i + 1];
            if (next && line >= (next.startLine | 0)) return null;
            return p;
        }
        return null;
    };
    findContaining.count = procs.length;
    return findContaining;
}

function buildStatusBarExtension(procedures, metadata, onProcedureChange) {
    // The handoff's status bar has seven cells; five of the absences are
    // accounted for (UTF-8 and "Spaces: 4" are editor settings, wrong on a
    // read-only pane; the filename and read-only badge moved into the head).
    // These two are facts about the FILE and simply fell out between the plan
    // and the code (#568). Null on the compare panes, which show two files.
    const meta = [];
    if (metadata && metadata.language) meta.push(String(metadata.language));
    if (metadata && metadata.runtime) meta.push(`runtime ${metadata.runtime}`);
    const findContaining = makeProcedureResolver(procedures);

    return showPanel.of(view => {
        const dom = document.createElement("div");
        dom.className = "cm-status-bar";
        const left = document.createElement("span");
        left.className = "cm-status-bar__left";
        const right = document.createElement("span");
        right.className = "cm-status-bar__right";
        dom.appendChild(left);
        // Between position and line count, matching the handoff's order.
        // Rendered once: neither value changes while the pane is mounted.
        for (const text of meta) {
            const cell = document.createElement("span");
            cell.className = "cm-status-bar__cell";
            cell.textContent = text;
            dom.appendChild(cell);
        }
        dom.appendChild(right);

        // The procedure the cursor was last inside, so the callback fires on
        // CHANGE rather than on every cursor move -- the outline rail only
        // needs to hear when the answer is different, and a keystroke-rate
        // callback across a thousand-row rail is the kind of thing that makes
        // a viewer feel heavy.
        let lastProc;
        const render = (state) => {
            const sel = state.selection.main;
            const line = state.doc.lineAt(sel.head);
            const col = sel.head - line.from + 1;
            const totalLines = state.doc.lines;
            const selLen = sel.to - sel.from;
            let pos = `Ln ${line.number.toLocaleString()}, Col ${col.toLocaleString()}`;
            if (findContaining.count > 0) {
                const proc = findContaining(line.number);
                // Publish the resolution the status bar already had to do
                // (#564). undefined means "not computed yet"; null is a real
                // answer meaning the cursor is outside every procedure.
                if (lastProc !== proc) {
                    lastProc = proc;
                    if (typeof onProcedureChange === "function") onProcedureChange(proc ?? null);
                }
                if (proc) {
                    // BC stack-trace convention: declaration line is 0,
                    // body counts upward from there. cursorLine -
                    // startLine reproduces the number printed in the
                    // server-side stack trace.
                    const relative = line.number - (proc.startLine | 0);
                    pos += ` · in ${proc.name} (line ${relative.toLocaleString()})`;
                }
            }
            if (selLen > 0) {
                pos += ` · ${selLen.toLocaleString()} selected`;
            }
            left.textContent = pos;
            right.textContent = `${totalLines.toLocaleString()} lines`;
        };

        render(view.state);
        return {
            dom,
            update(update) {
                if (update.docChanged || update.selectionSet || update.viewportChanged) {
                    render(update.state);
                }
            },
        };
    });
}

export function isDirty(id) {
    return editors.get(id)?.dirty ?? false;
}

// Called after a successful save (or after the editor is intentionally
// repopulated from the server) so the next edit starts a fresh dirty cycle.
export function markPristine(id) {
    const rec = editors.get(id);
    if (!rec) return;
    rec.pristine = rec.view.state.doc.toString();
    if (rec.dirty) {
        rec.dirty = false;
        syncBeforeUnload();
    }
}

export function getValue(id) {
    return editors.get(id)?.view.state.doc.toString() ?? "";
}

/// Opens CodeMirror's built-in search panel from outside the editor.
/// The default Ctrl/Cmd-F binding fires only when the editor has DOM
/// focus; this helper lets the page-level shortcut bypass that.
export function openSearch(id) {
    const e = editors.get(id);
    if (!e) return;
    e.view.focus();
    openSearchPanel(e.view);
}

/// Selects the entire document in the editor identified by id. Same
/// rationale as openSearch: defaultKeymap binds Mod-a to selectAll but
/// the read-only mount keeps contenteditable=false on the contentDOM,
/// so the browser's native "select everything on the page" wins
/// instead. The source viewer's window-level Ctrl/Cmd-A handler calls
/// this when focus is inside the editor surface.
export function selectAll(id) {
    const e = editors.get(id);
    if (!e) return;
    const view = e.view;
    view.focus();
    view.dispatch({
        selection: { anchor: 0, head: view.state.doc.length },
    });
}

/// Whether the editor identified by id contains the given DOM node.
/// Used by host-page handlers (Ctrl-A intercept, etc.) so the page
/// doesn't have to know about CodeMirror's internal DOM layout.
export function containsNode(id, node) {
    const e = editors.get(id);
    if (!e || !node) return false;
    return e.view.dom.contains(node);
}

export function setValue(id, value) {
    const e = editors.get(id);
    if (!e) return;
    const next = value ?? "";
    if (e.view.state.doc.toString() === next) return;
    e.view.dispatch({
        changes: { from: 0, to: e.view.state.doc.length, insert: next },
    });
    // setValue is server-driven (mode switch, post-save refresh) so the new
    // text is the new pristine baseline. Without resetting, the dirty flag
    // would stay sticky and the navigation guard would warn falsely.
    e.pristine = next;
    if (e.dirty) {
        e.dirty = false;
        syncBeforeUnload();
    }
}

// Issues come from the server: line is 1-based, message is human text.
// We render them as gutter markers + underlines via CodeMirror's lint
// extension so admins see exactly which line refused to parse.
export function setIssues(id, issues) {
    const e = editors.get(id);
    if (!e) return;
    const list = Array.isArray(issues) ? issues : [];
    const docLines = e.view.state.doc.lines;
    const diagnostics = list
        .filter(it => it && Number.isInteger(it.line) && it.line >= 1)
        .map(it => {
            const line = Math.max(1, Math.min(it.line, docLines));
            const li = e.view.state.doc.line(line);
            return {
                from: li.from,
                to: li.to,
                severity: it.severity === "warning" ? "warning" : "error",
                message: it.message ?? "",
            };
        });
    e.view.dispatch(setDiagnostics(e.view.state, diagnostics));
}

export function dispose(id) {
    const e = editors.get(id);
    if (!e) return;
    try { e.dispose(); } catch { /* ignore */ }
    editors.delete(id);
    syncBeforeUnload();
}
