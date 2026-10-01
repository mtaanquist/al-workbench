// Object Explorer source-file viewer — client glue.
//
// The page is server-rendered HTML (no Blazor circuit). This module mounts
// the CodeMirror viewer against the rendered DOM and wires every
// in-document interaction directly to JS handlers, with a JSON-endpoint
// roundtrip for the gestures that need server data. The shape mirrors
// code-editor.js's expected dotNetRef interface so we can reuse
// mountReadOnly verbatim. See .design/source-viewer-redesign.md.

// Forward this module's cache-bust query (?v=…) to its imports so
// /code-editor.js doesn't stay cached after a deploy that bumped both.
const moduleVersion = new URL(import.meta.url).searchParams.get("v") ?? "";
const codeEditorUrl = moduleVersion ? `/code-editor.js?v=${moduleVersion}` : "/code-editor.js";
const { mountReadOnly, mountCompareEditor, makeProcedureResolver, setDiff, getValue, setValue, scrollToLine, scrollComparePanes, openSearch, selectAll, containsNode, syncComparePanes, topLine, lineTop, lineAtTop, paneMetrics, afterLayout, toggleCollapsedRegion, cursorPosition, dispose, usesCommandKey } = await import(codeEditorUrl);

const FILE_URL_PREFIX = "/object-explorer/file/";

// ── Per-mount lifecycle (#711) ────────────────────────────────────
//
// initOne runs again for the same page on every enhanced navigation and from
// the MutationObserver at the bottom of this file, and until now nothing tore
// the previous mount down: only the Diff tool's inline pane ever reached
// dispose(). Every file hop therefore left an EditorView, its whole document,
// a MutationObserver on documentElement and a capture-phase document scroll
// listener installed — plus this module's own window/document listeners, whose
// `document.contains(root)` guards suppress the effect but keep the listener
// and the DOM it closes over alive.
//
// So each mount now records its editor id and an AbortController. Every
// listener that outlives the root's own subtree is registered with that
// controller's `signal`, and teardownMount takes the whole set off in one go
// alongside the dispose().
const mounts = new Set();

function registerMount(root, editorId, controller) {
    const record = { root, editorId, controller };
    root.__svMount = record;
    mounts.add(record);
    return record;
}

/// Disposes the editor mounted on `root` and removes every listener the mount
/// registered. Safe to call on a root that was never mounted.
function teardownMount(root) {
    const record = root?.__svMount;
    if (!record) return;
    mounts.delete(record);
    delete root.__svMount;
    record.controller.abort();
    dispose(record.editorId);
    if (inlinePane && inlinePane.root === root) inlinePane = null;
    if (changeNavPanes
        && (changeNavPanes.left.root === root || changeNavPanes.right.root === root)) {
        changeNavPanes = null;
    }
}

/// Sweeps mounts whose root has left the document — an enhanced navigation
/// that replaced the page's DOM rather than patching it in place. Driven from
/// the MutationObserver at the bottom of this file, which already fires on
/// exactly those mutations.
function teardownDetachedMounts() {
    for (const record of Array.from(mounts)) {
        if (!document.contains(record.root)) teardownMount(record.root);
    }
}

function init() {
    // Reap mounts whose root left the document before looking for new ones —
    // init() is the enhanced-navigation entry point, so this is where a
    // replaced page's editors and listeners get released (#711).
    teardownDetachedMounts();
    const roots = document.querySelectorAll(".source-viewer");
    if (roots.length === 0) return;
    const editorsByPane = [];
    roots.forEach(root => {
        // The inline pane starts hidden, and CodeMirror measures nothing
        // useful inside a hidden container — it is mounted the first time the
        // reader switches to it (see wireLayoutToggle). Skipping it here also
        // keeps the side-by-side pair a PAIR: the check below counts compare
        // roots, and a third one would stop the two panes ever being wired.
        if (root.classList.contains("source-viewer--inline")) return;
        const eid = initOne(root);
        if (eid !== null) {
            editorsByPane.push({ root, editorId: eid });
        }
    });

    // Compare-page scroll-sync: two .source-viewer--compare roots side-by-side.
    if (editorsByPane.length === 2
        && editorsByPane[0].root.classList.contains("source-viewer--compare")
        && editorsByPane[1].root.classList.contains("source-viewer--compare")) {
        const [left, right] = editorsByPane;
        wireCompareScrollSync(left, right);
        wireCompareChangeNav(left, right);
        // The .pw frame around the two panes — the change rail, its filter,
        // the rail's drag handle and the Ctrl/Cmd key labels — is not inside
        // either .source-viewer root, so the per-root wiring in initOne never
        // reaches it.
        wireComparePage();
        // Editable Diff tool: both panes editable → wire the live re-diff,
        // Swap/Clear, and the summary read-out.
        if (left.root.__editableCompare && right.root.__editableCompare) {
            wireEditableCompare(left, right);
        }
    }
}

// Vertical sync is line-anchored (syncComparePanes maps the source's top line to
// its counterpart and scrolls there via CodeMirror's measured geometry) so the
// panes don't drift the way raw scrollTop mirroring did once filler block
// widgets enter CodeMirror's height estimation. Horizontal sync stays a plain
// mirror — columns aren't affected by fillers.
//
// Only the pane the pointer is over is allowed to DRIVE the sync. The old
// two-way binding tried to recognise its own programmatic echoes by target
// scrollTop, but CodeMirror moves the destination twice per sync (an
// intermediate scrollIntoView hop, then the measured correction), so one of
// the two events always leaked through as a "user" scroll and synced the
// source pane straight back — which read as jumping mid-scroll and, after an
// overview-ruler jump, as a scroll position you couldn't get back above.
// Gating on the hovered pane makes every echo on the other pane inert, no
// echo bookkeeping needed. (Wheel and scrollbar gestures both require the
// pointer over the pane they scroll, so the gate matches how the panes are
// actually driven.)
function wireCompareScrollSync(left, right) {
    const leftScroller = left.root.querySelector(".cm-scroller");
    const rightScroller = right.root.querySelector(".cm-scroller");
    if (!leftScroller || !rightScroller) return;

    // Which pane the user is interacting with. Tracked on the pane ROOT (not
    // the scroller) so clicks on the overview ruler count as driving that
    // pane. pointerenter alone misses the "page loaded with the cursor
    // already inside a pane" case (no entry event fires until the pointer
    // moves across a boundary), so wheel and pointerdown claim the pane too.
    //
    // These three are the only listeners here that go on an element the
    // navigation RE-USES — the scrollers below are new with every mount — so
    // without a lifetime they piled up one set per file hop, each retaining a
    // dead pane pair. They ride the mount's AbortController instead, which
    // initOne aborts before it builds the next mount on the same root (#711),
    // so the set is replaced rather than added to (#717).
    let active = null;
    for (const [pane, name] of [[left, "left"], [right, "right"]]) {
        const claim = () => { active = name; };
        const signal = pane.root.__svMount?.controller.signal;
        pane.root.addEventListener("pointerenter", claim, { signal });
        pane.root.addEventListener("pointerdown", claim, { signal });
        pane.root.addEventListener("wheel", claim, { passive: true, signal });
    }

    leftScroller.addEventListener("scroll", () => {
        if (active !== "left") return;
        syncComparePanes(left.editorId, right.editorId);
        if (rightScroller.scrollLeft !== leftScroller.scrollLeft) {
            rightScroller.scrollLeft = leftScroller.scrollLeft;
        }
    });
    rightScroller.addEventListener("scroll", () => {
        if (active !== "right") return;
        syncComparePanes(right.editorId, left.editorId);
        if (leftScroller.scrollLeft !== rightScroller.scrollLeft) {
            leftScroller.scrollLeft = rightScroller.scrollLeft;
        }
    });

    // ── Reading-position deep link ───────────────────────────────
    //
    // ?line=N is the RIGHT (newer) pane's top line. On load, jump both
    // panes there; while scrolling, mirror the current top line back into
    // the URL (debounced replaceState) so a refresh keeps your place and
    // the address bar is always a shareable link to the spot you're
    // looking at.
    const initialLine = Number(new URLSearchParams(location.search).get("line"));
    if (Number.isFinite(initialLine) && initialLine >= 1) {
        requestAnimationFrame(() => {
            scrollToLine(right.editorId, initialLine, false, "top");
            // scrollToLine settles over two animation frames; bring the left
            // pane along once it has.
            setTimeout(() => syncComparePanes(right.editorId, left.editorId), 80);
        });
    }

    let urlTimer = 0;
    const scheduleUrlSync = () => {
        clearTimeout(urlTimer);
        urlTimer = setTimeout(() => {
            const ln = topLine(right.editorId);
            if (!ln) return;
            const url = new URL(location.href);
            if (ln > 1) url.searchParams.set("line", String(ln));
            else url.searchParams.delete("line");
            history.replaceState(null, "", url.pathname + url.search);
        }, 300);
    };
    leftScroller.addEventListener("scroll", scheduleUrlSync);
    rightScroller.addEventListener("scroll", scheduleUrlSync);
}

// ── Next/previous change navigation ──────────────────────────────
//
// Steps both compare panes through the diff's change blocks. Blocks are
// computed in the same *visual* space as the overview ruler (source line
// plus the filler gaps above it) so a deletion that only exists in the
// left pane and an insertion that only exists in the right pane still
// order correctly against each other; runs from the two panes whose
// visual ranges touch (a Modified block appears in both) collapse into
// one stop.
//
// Wired to optional [data-diff-nav="next"/"prev"] buttons anywhere on the
// page and to Ctrl/Cmd+ArrowDown / ArrowUp. The buttons sit OUTSIDE the
// panes, so the hovered-pane scroll-sync gate never fires for these
// programmatic jumps — each jump scrolls its anchor pane and then
// explicitly syncs the other one, mirroring the ?line= deep-link path.
function wireCompareChangeNav(left, right) {
    // The panes both the buttons and the keyboard drive. Read at event time,
    // not captured: the buttons live in the .pw frame, which an enhanced
    // navigation PRESERVES while it swaps the editors underneath - so a
    // handler that closed over `left`/`right` would keep scrolling the pane
    // pair from the page before last.
    changeNavPanes = { left, right };

    document.querySelectorAll("[data-diff-nav]").forEach(btn => {
        if (btn.__compareNavBound) return;
        btn.__compareNavBound = true;
        btn.addEventListener("click", () => {
            if (changeNavPanes) goFor(changeNavPanes, btn.dataset.diffNav === "prev" ? -1 : 1);
        });
    });

    // One keydown listener for the life of the document. The
    // `document.contains` guard inside it used to be the whole defence, and it
    // does not hold here: both pane roots survive an enhanced navigation, so
    // every hop bound another listener that still passed the check. After
    // three rail clicks one Ctrl+Down advanced four changes.
    if (changeNavBound) return;
    changeNavBound = true;

    window.addEventListener("keydown", e => {
        if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
        if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey) return;
        const panes = changeNavPanes;
        if (!panes) return;
        if (!document.contains(panes.left.root) || !document.contains(panes.right.root)) return;
        const active = document.activeElement;
        if (active instanceof HTMLElement
            && (active.tagName === "INPUT" || active.tagName === "TEXTAREA" || active.isContentEditable)) {
            return;
        }
        e.preventDefault();
        goFor(panes, e.key === "ArrowDown" ? 1 : -1);
    });
}

// Which panes the document-level change-nav listener and the toolbar buttons
// drive right now, and which layout is on screen — next/previous has to move
// whichever diff the reader is actually looking at.
let changeNavPanes = null;
let changeNavBound = false;
let changeNavMode = "side";

// Steps one pane pair to the next / previous change block.
function goFor(panes, delta) {
    if (changeNavMode === "inline") {
        goInline(delta);
        return;
    }
    goSideBySide(panes, delta);
}

/// Steps the inline pane to the next / previous changed row. No cross-pane
/// merge to do here — the two sides are already one document, so a change is
/// simply a run of changed rows in it.
function goInline(delta) {
    if (!inlinePane) return;
    const lines = [...new Set((inlinePane.rows ?? [])
        .filter(r => r && Number.isFinite(r.line))
        .map(r => r.line))].sort((a, b) => a - b);
    // Coalesce runs: consecutive changed rows are one change to the reader.
    const starts = lines.filter((l, i) => i === 0 || l !== lines[i - 1] + 1);
    if (starts.length === 0) return;

    const current = topLine(inlinePane.editorId) ?? 1;
    const target = delta > 0
        ? starts.find(l => l > current + 1) ?? null
        : [...starts].reverse().find(l => l < current - 1) ?? null;
    if (target === null) return;
    // Top-aligned, like the side-by-side jump: the change goes to the top of
    // the pane with its context below it. Centring instead clamps to 0 on any
    // diff shorter than a viewport, so "next change" would look broken on
    // exactly the small files where the collapse leaves least to scroll.
    scrollToLine(inlinePane.editorId, target, true, "top");
}

function goSideBySide({ left, right }, delta) {
    // Blocks are recomputed on each jump, not captured once: the editable
    // Diff tool re-diffs live, so __compareDiffRows changes under us and
    // the panes relay out beneath it. (For the read-only OE page both are
    // stable, so this is just a cheap recompute.)
    const blocks = computeChangeBlocks(left, right);
    if (blocks.length === 0) return;
    // Where the user currently is, as an offset into the pane's content. The
    // right pane is the reference (same choice the URL deep-link makes).
    const ln = topLine(right.editorId);
    const current = (ln ? lineTop(right.editorId, ln) : 0) ?? 0;
    // A "top"-aligned jump typically leaves the previous line still
    // peeking at the viewport top, so topLine reads one below the block
    // we just landed on. Tolerate a row and a half either way or "next"
    // would keep re-selecting the current block.
    const slack = (paneMetrics(right.editorId)?.lineHeight ?? 20) * 1.5;
    let target = null;
    if (delta > 0) {
        target = blocks.find(b => b.top > current + slack) ?? null;
    } else {
        for (const b of blocks) {
            if (b.top < current - slack) target = b;
            else break;
        }
    }
    if (!target) return;
    const pane = target.pane === "left" ? left : right;
    const other = pane === left ? right : left;
    // Move both panes together in the same frames — no visible one-then-
    // the-other step (see scrollComparePanes).
    scrollComparePanes(pane.editorId, other.editorId, target.line, true);
}

/// Coalesces each pane's changed lines into blocks, positions each block by
/// where its first line actually renders, and merges blocks from opposite
/// panes whose ranges overlap or touch (preferring the right pane's anchor —
/// it's the pane the scroll-sync and URL treat as primary). Positions come
/// from the panes themselves (lineTop), so the two sides are directly
/// comparable and a folded or filler-padded region can't skew them. Returns
/// [{pane: "left"|"right", line, top}] sorted top-down.
function computeChangeBlocks(left, right) {
    const paneBlocks = (pane, name) => {
        const rows = (pane.root.__compareDiffRows ?? [])
            .filter(r => r && Number.isFinite(r.line))
            .sort((a, b) => a.line - b.line);
        const blocks = [];
        for (const r of rows) {
            const top = lineTop(pane.editorId, r.line);
            if (top === null) continue;
            const last = blocks[blocks.length - 1];
            if (last && r.line === last.endLine + 1) {
                last.endLine = r.line;
                last.endTop = top;
            } else {
                blocks.push({ pane: name, line: r.line, endLine: r.line, top, endTop: top });
            }
        }
        return blocks;
    };

    // Two blocks a row apart are one change to the reader, so touching counts
    // as overlapping.
    const slack = paneMetrics(right.editorId)?.lineHeight ?? 20;
    const all = [...paneBlocks(left, "left"), ...paneBlocks(right, "right")]
        .sort((a, b) => a.top - b.top);
    const merged = [];
    for (const b of all) {
        const last = merged[merged.length - 1];
        if (last && b.top <= last.endTop + slack) {
            last.endTop = Math.max(last.endTop, b.endTop);
            if (last.pane === "left" && b.pane === "right") {
                last.pane = "right";
                last.line = b.line;
                last.top = Math.min(last.top, b.top);
            }
        } else {
            merged.push({ ...b });
        }
    }
    return merged;
}

// ── Compare page chrome, outside the two panes ───────────────────
//
// Both compare screens wrap their panes in the .pw power-tool frame. The
// Object Explorer's file diff also carries a change rail listing every other
// file that differs between the two versions; this wires its filter box and
// its drag handle. Everything here is document-scoped on purpose — a compare
// page is one .pw and two panes, and the chrome belongs to the frame.
function wireComparePage() {
    wireModifierKeyLabels(document);
    wireCompareRailFilter();
    wireRailPathFit();
    wireLayoutToggle();
    wireCollapseToggle();
    const frame = document.querySelector(".pw");
    if (frame) wirePaneSplits(frame);
}

// Expanding a collapsed stretch in SIDE BY SIDE is a PAIR operation. The two
// panes are level only while they hide the same rows, so a band clicked in one
// pane has to open the same region in the other — which is why the bands carry
// a shared index rather than a line range. One document-level listener, because
// the bands are CodeMirror widgets and get rebuilt under us on every toggle.
//
// The inline pane is one document and answers to itself. Its indices are its
// own numbering and mean nothing to the side panes, so the click has to be
// routed by which pane it came from — driving all three would expand unrelated
// stretches of the layout the reader is not even looking at, and they would be
// waiting like that on the way back.
function wireCollapseToggle() {
    if (document.__collapseToggleBound) return;
    document.__collapseToggleBound = true;
    document.addEventListener("aldt-toggle-region", (e) => {
        const index = e.detail?.index;
        if (!Number.isFinite(index)) return;

        const fromInline = e.target instanceof Element
            && e.target.closest('[data-layout-pane="inline"]') !== null;
        if (fromInline) {
            if (inlinePane) toggleCollapsedRegion(inlinePane.editorId, index);
            return;
        }

        const panes = changeNavPanes;
        if (!panes) return;
        toggleCollapsedRegion(panes.left.editorId, index);
        toggleCollapsedRegion(panes.right.editorId, index);
    });
}

// ── Side by side / Inline ────────────────────────────────────────
//
// Two renderings of one diff, and which one helps depends on the change — a
// renamed field reads better side by side, a block moved into a procedure
// reads better inline. So it is the reader's choice, remembered, rather than
// something the page decides.
//
// The inline pane's document is built server-side and shipped with the page,
// so switching is a class toggle. What it cannot be is a mount at page load:
// CodeMirror measures its own rows, and inside a `hidden` container every
// measurement is zero. It mounts the first time it is shown.
const LAYOUT_KEY = "aldt-compare-layout";

// The inline pane, once mounted, so the change navigation can drive it.
let inlinePane = null;

// The editable Diff tool's inline document, and whether the mounted pane is
// still showing it. Null on the Object Explorer's file diff, where the document
// is baked into the page once and never moves.
//
// This is what makes the inline layout possible on a tool whose panes ARE the
// input: the unified text is a function of two texts the reader is still
// typing, so it arrives with every re-diff rather than with the page. The pane
// is read-only and holds no cursor, selection or undo history worth keeping, so
// a fresh mount is the whole update — cheaper to reason about than teaching the
// gutter and band decorations to accept a new document underneath them.
let inlineDoc = null;
let inlineDocStale = false;

function wireLayoutToggle() {
    const tabs = document.querySelector("[data-diff-layout]");
    if (!tabs) return;

    const panes = new Map();
    document.querySelectorAll("[data-layout-pane]").forEach(el => panes.set(el.dataset.layoutPane, el));
    if (panes.size < 2) return;

    const apply = (mode) => {
        for (const [name, el] of panes) el.hidden = name !== mode;
        // Foot hints that are only true of one layout.
        document.querySelectorAll("[data-layout-only]").forEach(el => {
            el.hidden = el.dataset.layoutOnly !== mode;
        });
        tabs.querySelectorAll("[data-layout]").forEach(b =>
            b.classList.toggle("is-active", b.dataset.layout === mode));
        if (mode === "inline") mountInlinePane(panes.get("inline"));
        changeNavMode = mode;
    };

    // Per wiring, because this has to follow the page (#717): `apply` closes
    // over the pane elements THIS wiring found, and the change rail's rows are
    // enhanced navigations that replace the panes while re-using the tabs. It
    // sits above the bound-once block below for the same reason wireTreeGrouping
    // separates the two — a navigation must land in a real layout, and
    // changeNavMode must go back to what the server just rendered rather than
    // keeping the reader's choice from the page before.
    let saved = null;
    try { saved = localStorage.getItem(LAYOUT_KEY); } catch { /* private mode */ }
    // The tool starts with nothing to lay out, so its tabs ship hidden and a
    // remembered "inline" would open an empty pane over an empty page. It
    // reopens in that layout as soon as there is a diff (setLayoutAvailable).
    apply(saved === "inline" && !tabs.hidden ? "inline" : "side");
    applyLayout = apply;

    // Bound once per button (#602): the tabs outlive the panes. The handler
    // goes through `applyLayout` rather than its own `apply` so a listener
    // installed on the first page still drives the current one.
    tabs.querySelectorAll("[data-layout]").forEach(btn => {
        bindOnce(btn, "layout-tab", "click", () => {
            const mode = btn.dataset.layout;
            applyLayout?.(mode);
            try { localStorage.setItem(LAYOUT_KEY, mode); } catch { /* private mode */ }
        });
    });
}

// The Diff tool's tabs, once wired — so the live re-diff can reveal them and
// send the page back to Side by side when the text goes away.
let applyLayout = null;

// Shows or hides the layout choice. Called with false on an empty page and
// after Clear both: a control that switches between two renderings of nothing
// is not a choice, it is furniture.
function setLayoutAvailable(available) {
    const tabs = document.querySelector("[data-diff-layout]");
    if (!tabs) return;
    const wasHidden = tabs.hidden;
    tabs.hidden = !available;
    if (!available) {
        applyLayout?.("side");
        return;
    }
    // First diff on the page: honour the layout the reader last chose here,
    // which was not safe to apply while there was nothing to show.
    if (wasHidden) {
        let saved = null;
        try { saved = localStorage.getItem(LAYOUT_KEY); } catch { /* private mode */ }
        if (saved === "inline") applyLayout?.("inline");
    }
}

// Mounts the inline pane on first reveal. initOne carries the whole mount, so
// the only thing this adds is the timing — and the guard, since initOne
// returns null on an already-mounted host.
//
// On the Diff tool it also carries the refresh: a pane showing a document
// two keystrokes out of date is worse than one that has not opened yet,
// because it looks current.
function mountInlinePane(container) {
    if (!container) return;
    const root = container.querySelector(".source-viewer--inline");
    if (!root) return;

    // `inlinePane` is module state and outlives the page it was mounted on: on
    // the compare page the change-rail rows are enhanced navigations that
    // re-use the pane containers while replacing the editors inside them. A
    // remembered pane naming the previous page's root (or one whose editor the
    // diff removed) would make every branch below bail and leave the Inline tab
    // an empty box for the rest of the session, so validate rather than trust
    // it (#717).
    if (inlinePane && (inlinePane.root !== root || !root.querySelector(".cm-editor"))) {
        inlinePane = null;
    }
    const live = inlinePane !== null;

    if (inlineDoc !== null) {
        if (live && !inlineDocStale) return;
        remountInline(root);
        return;
    }

    if (live) return;
    const editorId = initOne(root);
    if (editorId === null) return;
    inlinePane = { root, editorId, rows: root.__compareDiffRows ?? [] };
}

// Rebuilds the Diff tool's inline pane from the latest server payload.
// Writes the payload back onto the host's data-* attributes and lets initOne
// do the mount, so the pane is put together by exactly the same code path as
// the Object Explorer's — one mount to keep correct, not two.
function remountInline(root) {
    const codeHost = root.querySelector(".source-viewer__code");
    if (!codeHost) return;

    // Takes the editor AND the mount's listeners off in one call.
    teardownMount(root);
    inlinePane = null;
    // dispose() destroys the view but leaves the host; initOne refuses a host
    // that still has an editor in it, so make sure the old one is gone.
    codeHost.querySelector(".cm-editor")?.remove();

    codeHost.dataset.content = inlineDoc.content ?? "";
    codeHost.dataset.diff = JSON.stringify(inlineDoc.rows ?? []);
    codeHost.dataset.unifiedGutters = JSON.stringify(inlineDoc.gutters ?? []);
    codeHost.dataset.collapse = JSON.stringify(inlineDoc.collapse ?? []);
    codeHost.dataset.wordDiff = JSON.stringify(inlineDoc.wordDiff ?? []);

    const editorId = initOne(root);
    if (editorId === null) return;
    inlinePane = { root, editorId, rows: root.__compareDiffRows ?? [] };
    inlineDocStale = false;
}

// Called by the editable Diff tool after every re-diff. Refreshes the pane
// when the reader is looking at it, and otherwise just records that it has
// moved on — mounting a CodeMirror on every keystroke for a pane nobody has
// opened is work for nothing.
function setInlineDocument(payload) {
    inlineDoc = payload ?? { content: "", rows: [], gutters: [], collapse: [], wordDiff: [] };
    inlineDocStale = true;
    const container = document.querySelector('[data-layout-pane="inline"]');
    if (container && !container.hidden) mountInlinePane(container);
}

// ── Change-rail paths, cut on a folder boundary ──────────────────
//
// CSS truncation is free and cuts wherever the width lands. On a rail of BC
// paths that produced rows like
//
//     …US Discount Mgt.Codeunit.al
//     …NUS Legacy Hook.Codeunit.al
//
// where "…US" and "…NUS" are the tail of CRONUS with its head bitten off. The
// reader's eye tries to parse a fragment that is not a word. Cutting at a "/"
// instead reads as a path with its head dropped, which is what it is.
//
// This has to measure text, so it runs on the client and re-runs whenever the
// rail changes width — it is drag-resizable, and a fit computed at one width
// is wrong at every other.
const RAIL_ELLIPSIS = "\u2026";

// One canvas for the whole rail: every row shares a font, and creating a
// measuring context per row is the slow way to do this.
function textMeasurer(sample) {
    const font = getComputedStyle(sample).font;
    if (!font) return null;
    const ctx = document.createElement("canvas").getContext("2d");
    if (!ctx) return null;
    ctx.font = font;
    return (text) => ctx.measureText(text).width;
}

/// The longest tail of `path` that starts after a "/" and fits in `width`.
///
/// When not even the filename fits, it is cut from the RIGHT instead — the
/// opposite end from everywhere else in this rail, and deliberately. A BC
/// filename carries what distinguishes it at the front and ends in the same
/// `.Codeunit.al` as its neighbours, so keeping the front keeps whole words
/// and keeps the row identifiable. Cutting that end is what produced
/// "…US Discount Mgt.Codeunit.al" — the tail of CRONUS, which is not a word.
function fitPath(path, width, measure) {
    if (measure(path) <= width) return path;

    for (let i = 0; i < path.length; i++) {
        if (path[i] !== "/") continue;
        const candidate = RAIL_ELLIPSIS + path.slice(i + 1);
        if (measure(candidate) <= width) return candidate;
    }

    const name = path.slice(path.lastIndexOf("/") + 1);
    for (let end = name.length - 1; end > 0; end--) {
        const candidate = name.slice(0, end) + RAIL_ELLIPSIS;
        if (measure(candidate) <= width) return candidate;
    }
    return name;
}

function fitRailPaths() {
    const names = document.querySelectorAll(".crail .crow[data-row-path] > .crow__name");
    if (names.length === 0) return;
    const measure = textMeasurer(names[0]);
    if (!measure) return;

    // Every width is read before any text is written. Reading one row's width
    // after writing the previous row's text forces a synchronous layout of the
    // whole page, both editors included, once per row; with the rail at its
    // 500-row cap that froze the tab for seconds on opening a file compare.
    const widths = Array.from(names, el => el.clientWidth);
    for (const [i, el] of names.entries()) {
        const full = el.parentElement?.dataset.rowPath ?? "";
        if (full === "") continue;
        // A row the filter has hidden measures zero, and fitting a path to no
        // width would replace every one of them with a bare ellipsis. They are
        // re-fitted when the filter puts them back.
        const width = widths[i];
        if (width === 0) continue;

        const fitted = fitPath(full, width, measure);
        el.textContent = fitted;
        // The stylesheet truncates with `direction: rtl`, which keeps the
        // filename end when CSS does the cutting. Now that the string is
        // already cut, that same rule would push the leading ellipsis to the
        // far side of the row — so hand direction back only when we are
        // leaving the job to CSS.
        el.style.direction = fitted === full ? "" : "ltr";
    }
}

// Re-fit on any width change: the rail's drag handle, the window, and the
// filter putting rows back. One observer on the container rather than one per
// row — they all change together.
function wireRailPathFit() {
    const rail = document.querySelector(".crail");
    if (!rail || rail.__pathFitBound) return;
    rail.__pathFitBound = true;

    let frame = 0;
    const schedule = () => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(fitRailPaths);
    };
    // The observer's first notification is the initial fit. Scheduling one
    // here as well ran the whole fit twice on every open: the notification
    // lands after this frame's callbacks, so the cancel never caught it.
    if (typeof ResizeObserver === "function") {
        new ResizeObserver(schedule).observe(rail);
    } else {
        schedule();
    }
    rail.__refitPaths = schedule;
}

// Substring filter over the rendered change rail. Filters what is on the page
// — when the rail is capped the server says so in the pane count and puts the
// full list one link away, so a filter that finds nothing is never the last
// word on whether a file changed.
function wireCompareRailFilter() {
    // Found by data-* attribute, not by class. A styling-shaped name that only
    // JavaScript reads is exactly the trap #562 walked into from the other
    // side: the next person retiring CSS sees an `oe-compare-*` class with no
    // rule and takes it.
    const filter = document.querySelector("[data-rail-filter]");
    if (!filter || filter.__railFilterBound) return;
    filter.__railFilterBound = true;
    const rows = Array.from(document.querySelectorAll(".crail .crow"));
    const empty = document.querySelector("[data-rail-empty]");

    filter.addEventListener("input", () => {
        const needle = filter.value.trim().toLowerCase();
        let anyVisible = false;
        for (const row of rows) {
            const match = needle.length === 0
                || (row.dataset.rowPath ?? "").toLowerCase().includes(needle);
            row.hidden = !match;
            if (match) anyVisible = true;
        }
        if (empty) empty.hidden = anyVisible || needle.length === 0;
        // Rows the filter had hidden measured zero and were skipped; now that
        // they are back they need their paths fitted to the rail's width.
        document.querySelector(".crail")?.__refitPaths?.();
    });
}

// What the read-out says when the server refuses a re-diff. Every branch ends
// on the same instruction, because the retry is the same in all of them: the
// next edit to either pane schedules another comparison.
function compareRequestError(status) {
    if (status === 429) {
        return "Too many comparisons at once. Wait a few seconds, then edit either side to retry.";
    }
    if (status === 413) {
        return "The texts are too large to compare. Trim them, then edit either side to retry.";
    }
    return "Could not compare. Edit either side to retry.";
}

// The same sentence the server answers an over-cap paste with, so the two
// routes to it read alike. Said here first where we can: the text is already
// in the browser, and shipping four megabytes across the wire to be told it is
// four megabytes is a slow way to hear it.
function compareTooLargeMessage(longest, max) {
    return `One of the texts is too large (${longest.toLocaleString()} characters). `
        + `Trim it under ${max.toLocaleString()} and try again.`;
}

// ── Editable Diff tool: live re-diff across two editable panes ──
//
// Both panes are editable CodeMirror editors (mountCompareEditor). On any
// edit we debounce, POST both texts to /api/compare/diff, and swap the diff
// decorations into both panes via setDiff — no remount, so typed text and
// undo history survive. The server reuses DiffPlex + SideBySideDiffSerializer
// (the same output the read-only OE compare page consumes), so the two
// surfaces stay visually identical. The page shell (summary read-out, Swap /
// Clear buttons, Prev/Next-change nav) is plain SSR markup wired here — no
// Blazor circuit, matching the source-viewer redesign.
function wireEditableCompare(left, right) {
    const summaryEl = document.querySelector("[data-compare-summary]");
    const swapBtn = document.querySelector("[data-compare-swap]");
    const clearBtn = document.querySelector("[data-compare-clear]");
    const navBtns = Array.from(document.querySelectorAll("[data-diff-nav]"));
    // Ignore whitespace, on by default — see CompareDiffRequest. Not
    // remembered between visits: it changes what counts as a change, so the
    // counts mean something different with it on, and a setting silently
    // carried over from last week is a bad thing to read numbers through.
    const ignoreWsBox = document.querySelector("[data-compare-ignore-ws]");
    // The endpoint's per-side cap, rendered into the page by Diff.razor from
    // the constant the endpoint itself checks against (#673). Zero when the
    // attribute is missing, which just means the check falls to the server.
    const maxInputLength = Number(
        document.querySelector("[data-compare-max-length]")?.dataset.compareMaxLength ?? 0) || 0;

    const HINT = "Nothing to compare yet.";
    // What the read-out says while only one pane has text (#615). Names the
    // side that is still missing, in the same words as that pane's own
    // placeholder, so the read-out and the empty pane ask for the same thing.
    const oneSidedHint = (leftText, rightText) => {
        if (leftText === "" && rightText === "") return HINT;
        return leftText === ""
            ? "Paste the original version to compare."
            : "Paste the changed version to compare.";
    };
    const setSummary = (text, isError) => {
        if (!summaryEl) return;
        summaryEl.textContent = text;
        summaryEl.classList.remove("crow__stat", "crow__stat--loose");
        summaryEl.classList.toggle("is-error", !!isError);
    };
    // The same +/-/~ the Object Explorer's file diff paints on its own view
    // bar, from the same three numbers. Built here rather than written as a
    // sentence so the two compare screens read alike in the cell the eye goes
    // to first.
    const setStat = (added, removed, modified) => {
        if (!summaryEl) return;
        summaryEl.classList.remove("is-error");
        summaryEl.classList.add("crow__stat", "crow__stat--loose");
        summaryEl.replaceChildren(
            statPart("crow__plus", `+${added}`),
            statPart("crow__minus", `-${removed}`),
            statPart("crow__mod", `~${modified}`));
        summaryEl.title = `${added} lines added, ${removed} removed, ${modified} modified`;
    };
    const statPart = (cls, text) => {
        const el = document.createElement("span");
        el.className = cls;
        el.textContent = text;
        return el;
    };
    const setNavEnabled = (enabled) => {
        for (const b of navBtns) b.disabled = !enabled;
    };
    // Swap / Clear only do something once there's text in a pane; disable them
    // on an empty page so nothing looks actionable before the user starts.
    const refreshActionButtons = () => {
        const hasText = getValue(left.editorId) !== "" || getValue(right.editorId) !== "";
        if (swapBtn) swapBtn.disabled = !hasText;
        if (clearBtn) clearBtn.disabled = !hasText;
    };

    // Whether the panes are currently free of diff decorations. Guards the
    // repeat calls #615 introduced: recompute() now lands on clearDiff() for
    // every debounced keystroke while the second pane is still empty, and
    // re-dispatching an empty diff into both panes each time is work with
    // nothing to show for it. Starts false so the first call — from the
    // initial recompute() below — still runs and puts the page in a known
    // state.
    let diffCleared = false;

    const applyPane = (pane, side) => {
        diffCleared = false;
        const rows = Array.isArray(side.diff) ? side.diff : [];
        const fillers = Array.isArray(side.fillers) ? side.fillers : [];
        const wordDiff = Array.isArray(side.wordDiff) ? side.wordDiff : [];
        setDiff(pane.editorId, { lineDecorations: diffRowsToDecorations(rows), fillers, wordDiff });
        pane.root.__compareDiffRows = rows;
        // Deferred for the same reason the read-only mount defers it: the
        // fillers this diff just handed the pane are block widgets, and their
        // heights are not in the pane's geometry until they have been measured
        // a frame or two later. Measuring now would place every mark against
        // the layout of the PREVIOUS diff.
        //
        // The identity check keeps the deferred call from painting a ruler for
        // a diff the pane has already moved past — the measure lands a couple
        // of frames later, and a fast reply to the next keystroke can beat it.
        afterLayout(pane.editorId, () => {
            if (pane.root.__compareDiffRows !== rows) return;
            buildDiffOverview(pane.root, pane.editorId, rows);
        });
    };

    const clearDiff = () => {
        if (diffCleared) return;
        diffCleared = true;
        for (const pane of [left, right]) {
            setDiff(pane.editorId, { lineDecorations: {}, fillers: [], wordDiff: [] });
            pane.root.__compareDiffRows = [];
            buildDiffOverview(pane.root, pane.editorId, []);
        }
        // There is no diff to lay out, so the choice goes away and the page
        // returns to the panes that take input.
        setLayoutAvailable(false);
        setInlineDocument(null);
    };

    // Guards against an out-of-order response: a slow diff for an old keystroke
    // must not overwrite a newer one.
    let seq = 0;
    const recompute = async () => {
        refreshActionButtons();
        const leftText = getValue(left.editorId);
        const rightText = getValue(right.editorId);
        // A comparison needs two texts, so nothing is asked of the server
        // until both panes have one (#615). Diffing against an empty pane
        // answered "every line was removed", and the alignment fillers that
        // came back with that answer were installed into the EMPTY pane —
        // pushing its placeholder, and the place the caret lands on a click,
        // a screenful down just as the user went to paste the second text
        // there. Say which side is still missing instead.
        if (leftText === "" || rightText === "") {
            // Bumping the sequence retires any diff already in flight: its
            // reply would otherwise land on panes that have since been
            // emptied and paint the decorations straight back in.
            seq++;
            clearDiff();
            setSummary(oneSidedHint(leftText, rightText), false);
            setNavEnabled(false);
            return;
        }
        // Past the cap there is no diff to be had, and the request would come
        // back with this same sentence after uploading both texts to earn it.
        if (maxInputLength > 0
            && (leftText.length > maxInputLength || rightText.length > maxInputLength)) {
            seq++;
            clearDiff();
            setSummary(compareTooLargeMessage(
                Math.max(leftText.length, rightText.length), maxInputLength), true);
            setNavEnabled(false);
            return;
        }
        const mine = ++seq;
        setSummary("Comparing...", false);
        let data;
        try {
            const res = await fetch("/api/compare/diff", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    left: leftText,
                    right: rightText,
                    ignoreWhitespace: ignoreWsBox ? ignoreWsBox.checked : true,
                }),
            });
            // A refused request carries no diff and usually no body either: an
            // oversized paste is dropped before the handler runs, and so is a
            // throttled one. Reading it as JSON would throw and land in the
            // catch below, which would blame the connection for something the
            // reader can actually act on. Say what happened instead; the panes
            // keep the diff they already have and the next edit retries.
            if (!res.ok) {
                if (mine !== seq) return;
                setSummary(compareRequestError(res.status), true);
                return;
            }
            data = await res.json();
        } catch {
            setSummary("Could not compare. Check your connection, then edit either side to retry.", true);
            return;
        }
        if (mine !== seq) return; // superseded by a newer edit
        if (data && data.error) {
            setSummary(data.error, true);
            return;
        }
        applyPane(left, data.left ?? {});
        applyPane(right, data.right ?? {});
        // Inline is a rendering of the RESULT, so it is offered exactly when
        // there is one - and past the early return above, there always is:
        // both sides are filled in.
        setInlineDocument(data.inline);
        setLayoutAvailable(true);
        const s = data.summary ?? {};
        if (s.identical) {
            // "Identical" is a strong claim and it is false when the two
            // differ by a reindent — which, with Ignore whitespace on, is
            // exactly when the diff reports nothing. The client has both
            // texts, so it can tell the two cases apart without asking.
            const whitespaceOnly = leftText !== rightText;
            setSummary(whitespaceOnly
                ? "Identical apart from whitespace."
                : "The two texts are identical.", false);
            setNavEnabled(false);
        } else {
            const total = (s.added ?? 0) + (s.removed ?? 0) + (s.modified ?? 0);
            setStat(s.added ?? 0, s.removed ?? 0, s.modified ?? 0);
            setNavEnabled(total > 0);
        }
    };

    let timer = 0;
    const schedule = () => {
        clearTimeout(timer);
        timer = setTimeout(recompute, 300);
    };
    left.root.__onCompareEdit = schedule;
    right.root.__onCompareEdit = schedule;

    if (swapBtn) {
        swapBtn.addEventListener("click", () => {
            const l = getValue(left.editorId);
            const r = getValue(right.editorId);
            setValue(left.editorId, r);
            setValue(right.editorId, l);
            recompute();
        });
    }
    if (clearBtn) {
        clearBtn.addEventListener("click", () => {
            setValue(left.editorId, "");
            setValue(right.editorId, "");
            // Through recompute() rather than by repeating what the empty
            // state does: it is the one place that knows the whole of it, and
            // it retires an in-flight diff whose reply would otherwise repaint
            // the decorations onto the panes this click just emptied.
            recompute();
        });
    }

    if (ignoreWsBox) {
        // Immediate, not debounced: this is a deliberate click, and the wait
        // that makes sense for a keystroke reads as a control that did nothing.
        ignoreWsBox.addEventListener("change", () => recompute());
    }

    setNavEnabled(false);
    recompute();
}

/// Marks the outline row for the procedure the cursor is inside (#564).
///
/// Matched on START LINE, not on name: an AL object can carry two procedures
/// with the same name at different overload-ish positions, and the outline row
/// already publishes its own line as `data-line`. The procedure payload's
/// `startLine` is the same number from the same outline, so the two cannot
/// disagree.
///
/// Only symbol rows carry `data-line` (object header rows key on
/// `data-object-id` instead), which is what we want: the enclosing thing is
/// always a procedure, never the object.
///
/// Deliberately does NOT scroll the rail to the row. The reader scrolls the
/// outline to look around while the cursor stays put; yanking it back on every
/// cursor move would fight them for control of a pane they are using.
function markOutlineRow(root, proc) {
    // Scoped to the viewer root rather than to the rail: there is exactly one
    // outline on the page, and .orow appears nowhere else.
    const previous = root.querySelector(".orow.is-active");
    const next = proc && typeof proc.startLine === "number"
        ? root.querySelector(`.orow[data-line="${proc.startLine}"]`)
        : null;
    if (previous === next) return;
    if (previous) {
        previous.classList.remove("is-active");
        previous.removeAttribute("aria-current");
    }
    if (next) {
        next.classList.add("is-active");
        // Not aria-current="page" - the row is not the current page, it is the
        // current location within one.
        next.setAttribute("aria-current", "location");
    }
}

// [{line, kind}] diff rows → the {lineNumber: cssClass} map setDiff/mountReadOnly
// consume. The read-only mount path in initOne calls this too.
function diffRowsToDecorations(rows) {
    const map = {};
    if (Array.isArray(rows)) {
        for (const row of rows) {
            if (!row || !Number.isFinite(row.line)) continue;
            map[row.line] = `cm-diff-${row.kind}`;
        }
    }
    return map;
}

/// Builds (or rebuilds) the compare-pane overview ruler. Coalesces consecutive
/// same-kind changed lines into runs (so a block of edits reads as one bar,
/// like KDiff3), positions each run proportionally over the full file height,
/// and wires a click to jump to its first line.
///
/// Positions come from the pane's own layout, not from source-line numbers:
/// alignment fillers push real lines down and folds pull them up, so a mark's
/// fraction is the line's rendered top over the pane's content height. Both
/// panes lay out to the same height, so a change on either side reads at the
/// same place on both ruler strips.
///
/// The DOM is rebuilt only when it would come out DIFFERENT. The Diff tool
/// calls this for both panes after every debounced re-diff, and a 4000-line
/// diff is around 1700 runs — so a session of typing was throwing away and
/// re-creating a few thousand buttons every 300ms to redraw the same picture.
/// Most keystrokes edit within a line: the kinds and line numbers hold, the
/// pane's height and row positions hold, and the ruler that comes out is
/// identical to the one already on screen.
///
/// The skip is exact rather than a heuristic, so it cannot go stale: the key
/// below is built from the very numbers that get written to the marks (the
/// percentages, not the raw geometry they came from) plus the kind, the line
/// span and the editor id each mark's click closes over. Anything that would
/// move, recolour, relabel or re-target a single mark — new diff rows, a
/// changed document height, a re-measure after a theme or font change, a
/// fold, a remount handing the pane a new editor id — lands in the key and
/// forces the rebuild. A container RESIZE that leaves the content height
/// alone deliberately does not: the marks are positioned in percentages, so
/// the strip rescales itself and there is nothing to redraw.
function buildDiffOverview(paneRoot, edId, rows) {
    const drop = () => {
        paneRoot.querySelector(".oe-diff-overview")?.remove();
        paneRoot.__diffOverviewKey = null;
    };

    if (!Array.isArray(rows) || rows.length === 0) { drop(); return; }
    const metrics = paneMetrics(edId);
    if (!metrics || !(metrics.contentHeight > 0)) { drop(); return; }
    const sorted = rows
        .filter(r => r && Number.isFinite(r.line) && r.kind)
        .sort((a, b) => a.line - b.line);
    if (sorted.length === 0) { drop(); return; }

    const runs = [];
    for (const r of sorted) {
        const last = runs[runs.length - 1];
        if (last && last.kind === r.kind && r.line === last.end + 1) {
            last.end = r.line;
        } else {
            runs.push({ start: r.line, end: r.line, kind: r.kind });
        }
    }

    // Resolve every run to the mark it would produce, before touching the DOM.
    // Measuring is the cheap half of the old rebuild (one lineTop pair per RUN,
    // which the previous code also paid); creating the buttons is the expensive
    // half, and that is what the key below buys back.
    const marks = [];
    for (const run of runs) {
        // Rendered top of the run's first line, and a height that runs to the
        // bottom of its last — which absorbs any filler sitting between them
        // (interior gaps happen when the opposite side inserts mid-run).
        const top = lineTop(edId, run.start);
        const endTop = lineTop(edId, run.end);
        if (top === null || endTop === null) continue;
        const height = (endTop - top) + metrics.lineHeight;
        marks.push({
            kind: run.kind,
            start: run.start,
            end: run.end,
            topPct: (top / metrics.contentHeight) * 100,
            heightPct: (height / metrics.contentHeight) * 100,
        });
    }

    const key = edId + "|" + marks
        .map(m => `${m.kind}:${m.start}-${m.end}:${m.topPct}:${m.heightPct}`)
        .join(",");
    // The element check is half the guard: an enhanced navigation or a manual
    // removal can take the ruler away while the pane root (and its key) stay,
    // and a key match on a pane with no ruler must still rebuild.
    const existing = paneRoot.querySelector(".oe-diff-overview");
    if (existing && paneRoot.__diffOverviewKey === key) return;

    const overview = document.createElement("div");
    overview.className = "oe-diff-overview";
    overview.title = "Changes overview — click a mark to jump";
    for (const m of marks) {
        const mark = document.createElement("button");
        mark.type = "button";
        mark.className = `oe-diff-overview__mark oe-diff-overview__mark--${m.kind}`;
        mark.style.top = m.topPct + "%";
        mark.style.height = `max(3px, ${m.heightPct}%)`;
        const span = m.end > m.start ? `lines ${m.start}-${m.end}` : `line ${m.start}`;
        mark.title = `${m.kind} · ${span}`;
        mark.setAttribute("aria-label", `Jump to ${m.kind} change at ${span}`);
        mark.addEventListener("click", () => scrollToLine(edId, m.start, true));
        overview.appendChild(mark);
    }
    existing?.remove();
    paneRoot.appendChild(overview);
    paneRoot.__diffOverviewKey = key;
}

function initOne(root) {
    const codeHost = root.querySelector(".source-viewer__code");
    if (!codeHost) return null;

    // Guard against double-mount via Blazor enhanced navigation.
    if (codeHost.querySelector(".cm-editor")) return null;

    // The host carries no editor but may still carry the RECORD of one: an
    // enhanced navigation that swapped the .cm-editor out while leaving the
    // root in place, or remountInline stripping it deliberately. Tear that
    // mount down before building the next one on the same host (#711).
    teardownMount(root);
    const mountController = new AbortController();
    const signal = mountController.signal;

    const fileId = Number(root.dataset.fileId);
    // fileId is optional on the side-by-side compare page (each pane carries
    // a data-file-id but the cross-pane handlers don't use it). The rest of
    // the wiring only runs when this is a navigable single-file viewer.
    const isCompare = root.classList.contains("source-viewer--compare");

    const initialLineAttr = root.dataset.initialLine;
    const initialLine = initialLineAttr
        ? Number(initialLineAttr)
        : Number(new URLSearchParams(location.search).get("line"));

    const declarations = parseJsonAttr(codeHost.dataset.declarations) ?? [];
    const resolvables = parseJsonAttr(codeHost.dataset.resolvables) ?? [];
    // Procedure-like outline rows (start line + optional end line + name + kind)
    // drive the "in CheckDates (line 13)" suffix in the status bar. Maps the
    // editor's absolute line number to BC's procedure-relative stack-trace
    // numbering, where the `procedure` declaration counts as line 0.
    const procedures = parseJsonAttr(codeHost.dataset.procedures) ?? [];
    const content = codeHost.dataset.content ?? "";
    const language = codeHost.dataset.language ?? "al";
    // Release the user is viewing from (?from=). When set, find-references
    // session mints seed here so a base object opened from a customer Release
    // surfaces the customer's own code. Empty for the normal same-Release case.
    const viewRelease = codeHost.dataset.viewRelease || "";

    // Clear the data-content payload from the DOM — the editor owns it now.
    codeHost.removeAttribute("data-content");
    codeHost.removeAttribute("data-declarations");
    codeHost.removeAttribute("data-resolvables");
    codeHost.removeAttribute("data-procedures");
    codeHost.removeAttribute("data-view-release");

    // Appends &from=/?from= to a session-mint URL so the seed Release rides
    // along. No-op when the viewer isn't tagged with a view Release.
    const withFrom = (url) =>
        viewRelease ? url + (url.includes("?") ? "&" : "?") + "from=" + viewRelease : url;

    const notice = root.querySelector(".source-viewer__notice");
    const tabs = new TabController(root);

    // Track the last pointer position inside the editor so notice toasts
    // can pop up near the user's mouse, rather than at the bottom of the
    // outline panel where they may be off-screen. Updated on
    // mousemove/contextmenu/click anywhere inside the editor surface.
    const pointerTracker = { x: 0, y: 0, fresh: false };
    const updatePointer = (ev) => {
        pointerTracker.x = ev.clientX;
        pointerTracker.y = ev.clientY;
        pointerTracker.fresh = true;
    };
    // On the mount's signal: the root survives a navigation that replaces the
    // editor inside it, so without a lifetime these three pile up one set per
    // file hop, each retaining the pane it was wired for (same shape as the
    // compare pane-claim listeners in #717).
    root.addEventListener("mousemove", updatePointer, { signal });
    root.addEventListener("contextmenu", updatePointer, { signal });
    root.addEventListener("click", updatePointer, { signal });

    const jsBridge = {
        invokeMethodAsync(method, ...args) {
            switch (method) {
                case "OnFindReferences":
                    return onFindReferences(args[0]);
                case "OnFindSystemReferences":
                    return mintObjectSystemSession(args[0]);
                case "OnFindMemberReferences":
                    return mintMemberSession(args[0]);
                case "OnFindReferencesAt":
                    return onFindReferencesAt(args[0], args[1]);
                case "OnGoToDefinition":
                    return onGoToDefinition(args[0], args[1]);
                case "OnFindInFile":
                    return onFindInFile(args[0], args[1]);
                default:
                    return Promise.resolve();
            }
        },
    };

    // Diff overlay (compare page only). data-diff carries a JSON array
    // `[{line, kind}, …]` where kind ∈ inserted | deleted | modified.
    // Imaginary rows are NOT in here - SerializeSide drops them and they
    // arrive as data-fillers instead. Convert to the {lineNumber: cssClass}
    // shape mountReadOnly already understands and pass through as
    // lineDecorations.
    const diffData = parseJsonAttr(codeHost.dataset.diff);
    const lineDecorations = diffRowsToDecorations(diffData);
    codeHost.removeAttribute("data-diff");

    // Alignment fillers (compare page only). data-fillers carries
    // `[{before, size}, …]` — blank gaps of `size` line-heights anchored
    // before source line `before`, so the two panes stay aligned KDiff3-style.
    const fillerData = parseJsonAttr(codeHost.dataset.fillers);
    codeHost.removeAttribute("data-fillers");

    // Inline (unified) compare only: the per-row line-number pairs and the
    // `@@` banners. A unified document is synthesised rather than read, so
    // neither can be counted off the text (see UnifiedDiffSerializer).
    const unifiedGutters = parseJsonAttr(codeHost.dataset.unifiedGutters);
    codeHost.removeAttribute("data-unified-gutters");

    // Side-by-side collapse (read-only compare only): which stretches of this
    // pane are hidden, and the band that stands in for each. The indices are
    // shared with the opposite pane — see wireCollapseToggle.
    const collapseData = parseJsonAttr(codeHost.dataset.collapse);
    codeHost.removeAttribute("data-collapse");

    // Intra-line changed-word ranges (compare page only) — the stronger tint
    // inside modified lines. `[{line, from, to}]`, 1-based, `to` exclusive.
    const wordDiffData = parseJsonAttr(codeHost.dataset.wordDiff);
    codeHost.removeAttribute("data-word-diff");

    // Editable compare pane (the standalone Diff tool). The pane IS the
    // input: mount an editable editor with dynamic diff decorations and let
    // init() wire the live re-diff across the two panes. The diff itself is
    // recomputed server-side (POST /api/compare/diff) on a debounce; nothing
    // is baked in at mount, so we never remount and never lose typed text.
    //
    // `data-editable` and `data-placeholder` are NOT consumed the way the
    // payload attributes above are, and the difference matters: the payload is
    // a document this mount takes ownership of (and a remounter re-supplies —
    // see remountInline), while these two say what KIND of pane this host is
    // and what it says when empty. That is a property of the markup, true
    // every time the host is mounted. Clearing it made the answer a one-shot:
    // anything that mounted this host a second time — a remount after a
    // dispose, or an enhanced navigation that left the host in place with its
    // editor gone — read no flag and quietly handed the reader a read-only
    // pane on a page whose whole point is typing into it.
    if (isCompare && codeHost.dataset.editable === "true") {
        const placeholderText = codeHost.dataset.placeholder ?? "";
        const editorId = mountCompareEditor(codeHost, content, language, {
            lineDecorations,
            fillers: Array.isArray(fillerData) ? fillerData : [],
            wordDiff: Array.isArray(wordDiffData) ? wordDiffData : [],
            placeholder: placeholderText,
            // Late-bound so a keystroke before init() finishes wiring the pair
            // is simply ignored (nothing to diff against yet).
            onDocChanged: () => root.__onCompareEdit?.(),
        });
        registerMount(root, editorId, mountController);
        root.__compareDiffRows = Array.isArray(diffData) ? diffData : [];
        root.__compareEditorId = editorId;
        root.__editableCompare = true;
        return editorId;
    }

    const editorId = mountReadOnly(codeHost, content, language, {
        declarations,
        resolvables,
        lineDecorations,
        fillers: Array.isArray(fillerData) ? fillerData : [],
        wordDiff: Array.isArray(wordDiffData) ? wordDiffData : [],
        unifiedGutters: Array.isArray(unifiedGutters) ? unifiedGutters : [],
        collapse: Array.isArray(collapseData) ? collapseData : [],
        // Folding one compare pane would break the server-computed filler
        // alignment with the other, so the compare mounts opt out.
        folding: !isCompare,
        procedures,
        dotNetRef: jsBridge,
        // VS Code-style status bar at the bottom of the editor. Shows
        // cursor line/col, total lines, selection size when a range is
        // active, and — when the cursor sits inside a procedure — the
        // containing procedure name plus the BC stack-trace-style
        // procedure-relative line number ("in CheckDates (line 13)").
        statusBar: true,
        // Two facts about the FILE, for the status bar's middle cells (#568).
        // Not the editor settings the handoff's mock also shows (UTF-8,
        // Spaces: 4) - those imply you can change them, and this pane is
        // read-only. Deliberately omitted on the compare panes: they show two
        // files with two runtimes and one cell cannot say both.
        metadata: isCompare ? null : {
            language: (codeHost.dataset.language ?? "al").toUpperCase(),
            runtime: codeHost.dataset.runtime || null,
        },
        // "You are here", threaded through the outline rail (#564). The
        // status-bar extension already resolves the cursor to its enclosing
        // procedure to print "in LogPosting (line 3)"; this publishes that same
        // answer so the rail can mark the matching row. Compare panes have no
        // outline rail, so they pass nothing.
        onProcedureChange: isCompare ? null : (proc) => markOutlineRow(root, proc),
    });

    registerMount(root, editorId, mountController);

    // Compare-page panes don't carry the outline / refs / tabs DOM so the
    // wiring below would no-op anyway, but skipping it makes the flow
    // explicit. Likewise initial line isn't useful when both panes start
    // pinned to line 1.
    if (isCompare) {
        // KDiff3-style overview ruler: a full-height strip mapping every
        // changed line in the WHOLE file to a coloured mark, so the spread of
        // changes is visible without scrolling (the inline change-gutter only
        // covers on-screen rows). Click a mark to jump there.
        // Deferred: the ruler positions every mark against the pane's own
        // layout, and at this point the pane has not been measured yet.
        afterLayout(editorId, () => buildDiffOverview(root, editorId, diffData));
        // Stash the parsed changed-line list on the pane root (the data-*
        // attributes were consumed above) so the cross-pane change
        // navigation in init() can compute jump targets without
        // re-serialising anything through the DOM. Where those lines *render*
        // comes from the pane itself, so nothing else needs stashing.
        root.__compareDiffRows = Array.isArray(diffData) ? diffData : [];
        return editorId;
    }

    if (Number.isFinite(initialLine) && initialLine >= 1) {
        requestAnimationFrame(() => scrollToLine(editorId, initialLine, true));
        // Arriving from a search hit or a reference is the COMMON way into
        // this page, and scrollToLine deliberately does not move the cursor -
        // so without this the code line is highlighted, the status bar says
        // "Ln 1", and the outline says nothing about where you landed (#564).
        // Resolved through the editor's own resolver rather than a second copy
        // of the containment rule.
        markOutlineRow(root, makeProcedureResolver(procedures)(initialLine));
    }

    wireOutlineFilter(root);
    wireSectionToggles(root);
    wireSameFileLinks(root, editorId, fileId);
    wireOutlineFindReferences(root);
    wireRefsCloseButton(root);
    wireSearchShortcut(root, editorId, signal);
    wireFindReferencesShortcut(root, editorId, onFindReferencesAt, signal);
    wireSelectAllShortcut(root, editorId, codeHost, signal);
    wirePopstate(root, editorId, signal);
    wirePaneSplits(root);
    wireExplorerTree(root);
    wireModifierKeyLabels(root);
    wireSymbolCard(root, codeHost, editorId, fileId, {
        onFindReferences: mintMemberSession,
    }, signal);
    if (Number.isFinite(fileId)) {
        wireFileDependencies(root, fileId);
    }

    // If the server already rendered a session into the references panel's
    // data-session attribute (page loaded with ?refSet=token), parse it
    // and render the panel client-side so all rendering paths funnel
    // through renderReferencesPanel.
    const refsPanel = root.querySelector('[data-panel="references"]');
    if (refsPanel) {
        const inlineSession = parseJsonAttr(refsPanel.dataset.session);
        refsPanel.removeAttribute("data-session");
        if (inlineSession) {
            renderReferencesPanel(root, inlineSession, fileId, editorId);
        }
    }

    /// Right-click anywhere on an outline row to "Find references" for
    /// the symbol it represents. Procedure / field / trigger rows mint
    /// a member-scoped session (server side composes declarations +
    /// owner-type refs + call-site refs once those land). Object rows
    /// mint the existing object-scoped session.
    function wireOutlineFindReferences(panelRoot) {
        const outlinePanel = panelRoot.querySelector('[data-panel="outline"]');
        if (!outlinePanel) return;
        // The menu it opens mints sessions against THIS page's viewer, so the
        // handler is replaced per wiring while the listener is bound once —
        // two menus per right-click otherwise (#602/#717).
        bindOnceLatest(outlinePanel, "outline-refs-menu", "contextmenu", e => {
            const row = e.target instanceof Element
                ? e.target.closest(".sv-row")
                : null;
            if (!row) return;
            const symbolId = row.dataset.symbolId;
            const objectId = row.dataset.objectId;
            if (!symbolId && !objectId) return;
            e.preventDefault();
            openOutlineRefsMenu(e.clientX, e.clientY, row, symbolId, objectId);
        });
    }

    function openOutlineRefsMenu(x, y, row, symbolId, objectId) {
        // One-item menu for now: "Find references". A second item ("Go to
        // definition") would be redundant — the outline row IS the
        // declaration; left-click jumps to it.
        const menu = document.createElement("div");
        menu.className = "source-viewer__outline-menu";
        menu.style.left = x + "px";
        menu.style.top = y + "px";
        // Assigned once the menu is in the DOM; the item handlers below only
        // read it when they run, which is after that.
        let close = () => menu.remove();

        const item = document.createElement("button");
        item.type = "button";
        item.className = "source-viewer__outline-menu-item";
        item.setAttribute("role", "menuitem");
        item.textContent = "Find references";
        item.addEventListener("click", async () => {
            close(false);
            if (symbolId) {
                await mintMemberSession(symbolId);
            } else if (objectId) {
                await mintObjectSession(objectId);
            }
        });
        menu.appendChild(item);

        // "Find system references" — built-in/system method calls (Insert,
        // Modify, SetRange, …) on the object. Object rows only; system calls
        // target a whole object, not a member. See #279.
        if (objectId) {
            const sysItem = document.createElement("button");
            sysItem.type = "button";
            sysItem.className = "source-viewer__outline-menu-item";
            sysItem.setAttribute("role", "menuitem");
            sysItem.textContent = "Find built-in calls (Insert, Modify, SetRange...)";
            sysItem.addEventListener("click", async () => {
                close(false);
                await mintObjectSystemSession(objectId);
            });
            menu.appendChild(sysItem);
        }

        document.body.appendChild(menu);
        close = wireFloatingMenu(menu, row);
    }

    async function mintMemberSession(symbolId) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/from-member-symbol/${symbolId}`),
                { credentials: "same-origin" });
            if (!res.ok) {
                showNotice("Couldn't look up references for that name. Try again.");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("from-member-symbol failed:", err);
            showNotice("Couldn't reach the server.");
        } finally {
            busyEnd();
        }
    }

    async function mintObjectSession(objectId) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/from-symbol/${objectId}`),
                { credentials: "same-origin" });
            if (!res.ok) {
                showNotice("Couldn't look up references for that name. Try again.");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("from-symbol failed:", err);
            showNotice("Couldn't reach the server.");
        } finally {
            busyEnd();
        }
    }

    async function mintObjectSystemSession(objectId) {
        clearNotice();
        busyStart("Searching system references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/system-references/sessions/from-object/${objectId}`),
                { credentials: "same-origin" });
            if (!res.ok) {
                showNotice("Couldn't look up built-in calls for that object. Try again.");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("system-references from-object failed:", err);
            showNotice("Couldn't reach the server.");
        } finally {
            busyEnd();
        }
    }

    async function onFindReferencesAt(line, column) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/at-position?fileId=${fileId}&line=${line}&column=${column}`),
                { credentials: "same-origin" });
            if (res.status === 204 || res.status === 404) {
                // The server couldn't tie the clicked token to anything it
                // tracks — a local variable, a built-in, or a keyword. Objects
                // AND members both resolve here (CreateAtPositionAsync routes
                // to CreateFromMemberSymbolAsync when the click lands on one),
                // so this is no longer the "objects only" case the message
                // used to claim it was.
                showNotice("Couldn't work out what that name refers to. Try again on the line where it's declared.");
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't search references (server error).");
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("Find references at position failed:", err);
            showNotice("Couldn't reach the server.");
        } finally {
            busyEnd();
        }
    }

    async function onFindReferences(symbolId) {
        clearNotice();
        busyStart("Searching references...");
        try {
            const res = await fetch(
                withFrom(`/api/object-explorer/references/sessions/from-symbol/${symbolId}`),
                { credentials: "same-origin" });
            if (!res.ok) {
                location.assign(`/object-explorer/object/${symbolId}#find-references`);
                return;
            }
            const session = await res.json();
            applyReferenceSession(session);
        } catch (err) {
            console.warn("Find references failed:", err);
            location.assign(`/object-explorer/object/${symbolId}#find-references`);
        } finally {
            busyEnd();
        }
    }

    /// Render the References panel client-side and stash the token in the
    /// URL via replaceState so a refresh keeps the panel visible. Doesn't
    /// navigate — the user stays on their current file and clicks results
    /// to jump.
    function applyReferenceSession(session) {
        if (!session) return;
        renderReferencesPanel(root, session, fileId, editorId);
        tabs.show("references");
        tabs.activate("references");
        const url = new URL(location.href);
        url.searchParams.set("refSet", session.token);
        history.replaceState(null, "", url.pathname + url.search);
    }

    async function onGoToDefinition(line, column) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/files/${fileId}/goto?line=${line}&column=${column}`,
                { credentials: "same-origin" });
            if (res.status === 204) {
                showNotice("No definition found for that name.");
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't find that definition right now. Try again.");
                return;
            }
            const target = await res.json();
            if (target.fileId === fileId) {
                jumpInThisFile(target.lineNumber);
                return;
            }
            location.assign(`${FILE_URL_PREFIX}${target.fileId}?line=${target.lineNumber}${preservedQueryTail()}`);
        } catch (err) {
            console.warn("Go to definition failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    async function onFindInFile(line, column) {
        clearNotice();
        try {
            const res = await fetch(
                `/api/object-explorer/files/${fileId}/find-in-file?line=${line}&column=${column}`,
                { credentials: "same-origin" });
            if (res.status === 204) {
                renderFindResults(null);
                return;
            }
            if (!res.ok) {
                showNotice("Couldn't search this file (server error).");
                return;
            }
            const data = await res.json();
            renderFindResults(data);
        } catch (err) {
            console.warn("Find in file failed:", err);
            showNotice("Couldn't reach the server.");
        }
    }

    function renderFindResults(data) {
        const findHost = root.querySelector(".source-viewer__find-host");
        if (!findHost) return;
        findHost.innerHTML = "";
        if (!data) {
            tabs.show("find", false);
            return;
        }
        const section = document.createElement("div");
        section.className = "pane__sec";
        const heading = document.createElement("div");
        heading.className = "pane__sec-h sv-sec-h";
        heading.append("Occurrences of ");
        heading.appendChild(sectionName(data.word));
        const countChip = document.createElement("span");
        countChip.className = "pane__count";
        heading.appendChild(countChip);
        section.appendChild(heading);
        findHost.appendChild(section);

        if (!data.occurrences || data.occurrences.length === 0) {
            countChip.textContent = "0";
            const p = document.createElement("p");
            p.className = "u-muted source-viewer__panel-empty";
            p.textContent = "No occurrences in this file.";
            section.appendChild(p);
        } else {
            countChip.textContent = data.occurrences.length.toLocaleString();
            const list = document.createElement("div");
            list.className = "refs";
            for (const occ of data.occurrences) {
                list.appendChild(buildHitRow({
                    line: occ.line,
                    text: occ.lineText,
                    match: data.word,
                    onActivate: () => jumpToLineInThisFile(occ.line),
                }));
            }
            section.appendChild(list);
        }

        tabs.show("find", true);
        tabs.activate("find");
    }

    function jumpToLineInThisFile(line) {
        jumpInThisFile(line);
    }

    /// Jumps to a line and pushes a history entry so the browser back
    /// button restores the previous position. Outline / Find-in-file
    /// / Cmd-click jumps all route through here. The URL preserves any
    /// non-line query (e.g. ?refSet=) so the references panel survives.
    function jumpInThisFile(line) {
        scrollToLine(editorId, line, true);
        const url = `${FILE_URL_PREFIX}${fileId}?line=${line}${preservedQueryTail()}`;
        // Skip the push when the URL is identical to the current one —
        // back button shouldn't have to press through duplicates.
        if (location.pathname + location.search !== url) {
            history.pushState(null, "", url);
        }
    }

    /// Wires window.popstate so the editor scrolls to whatever line the
    /// URL points at after back/forward navigation. The page itself is
    /// the same SSR document; only the line jumps.
    function wirePopstate(_root, eid, signal) {
        window.addEventListener("popstate", () => {
            const ln = Number(new URLSearchParams(location.search).get("line"));
            if (Number.isFinite(ln) && ln >= 1) {
                scrollToLine(eid, ln, true);
            }
        }, { signal });
    }

    /// Shows a transient notice as a floating toast anchored near the
    /// user's last pointer position inside the editor surface. The
    /// toast fades out on its own after a short delay so the user
    /// isn't left looking at a stale status line at the bottom of the
    /// outline (which is frequently off-screen on tall files). Falls
    /// back to the bottom-of-outline notice element when we don't have
    /// a fresh pointer position (keyboard-driven gestures).
    function showNotice(text) {
        if (!text) return;
        if (pointerTracker.fresh) {
            showFloatingToast(text, pointerTracker.x, pointerTracker.y);
            return;
        }
        if (!notice) return;
        notice.textContent = text;
        notice.hidden = false;
    }
    function clearNotice() {
        if (!notice) return;
        notice.textContent = "";
        notice.hidden = true;
    }

    return editorId;
}

// ── Floating notice toast ────────────────────────────────────────
//
// The source-viewer used to surface "No definition found", "Server
// error", etc. into a tiny <p class="source-viewer__notice"> docked
// at the bottom of the outline. On long files that paragraph sits
// well below the viewport, so users never noticed the response to
// their gesture. A floating toast anchored to the most recent
// pointer position inside the editor keeps the feedback in view,
// then fades itself out so it doesn't linger.

// ── Busy indicator ───────────────────────────────────────────────
//
// Find-references on a heavily-used object can take a few seconds
// server-side; without feedback the right-click gesture looks dead
// and users click again. A small fixed pill with a spinner sits at
// the bottom-centre of the viewport (never under the pointer) while
// a session-mint request is in flight, and the page cursor flips to
// `progress`. Counted, so overlapping requests keep it up until the
// last one settles.

/// Puts a module-owned element back under <body> when it isn't there.
///
/// The busy pill, the floating toast and the references tooltip are each built
/// once and cached in module scope, but Blazor's enhanced navigation diffs the
/// whole document and drops body children the server never rendered. From the
/// second page onward the cached element is detached, so writing into it shows
/// the reader nothing — a progress cursor with no message, a "Couldn't reach
/// the server" toast that never appears. Existence is therefore not the test;
/// connectedness is (#717).
function ensureBodyChild(el) {
    if (!el.isConnected) document.body.appendChild(el);
    return el;
}

let busyEl = null;
let busyCount = 0;

function busyStart(text) {
    busyCount++;
    if (!busyEl) {
        busyEl = document.createElement("div");
        busyEl.className = "source-viewer__busy";
        busyEl.setAttribute("role", "status");
        busyEl.setAttribute("aria-live", "polite");
        const spinner = document.createElement("span");
        spinner.className = "source-viewer__busy-spinner";
        spinner.setAttribute("aria-hidden", "true");
        const label = document.createElement("span");
        label.className = "source-viewer__busy-text";
        busyEl.appendChild(spinner);
        busyEl.appendChild(label);
    }
    ensureBodyChild(busyEl);
    busyEl.querySelector(".source-viewer__busy-text").textContent = text;
    busyEl.hidden = false;
    document.documentElement.style.cursor = "progress";
}

function busyEnd() {
    busyCount = Math.max(0, busyCount - 1);
    if (busyCount === 0) {
        if (busyEl) busyEl.hidden = true;
        document.documentElement.style.cursor = "";
    }
}

let floatingToastEl = null;
let floatingToastHideTimer = 0;
let floatingToastRemoveTimer = 0;

const TOAST_VISIBLE_MS = 1800;   // Time the toast stays at full opacity.
const TOAST_FADE_MS = 350;        // Length of the fade-out transition.

function showFloatingToast(text, clientX, clientY) {
    clearTimeout(floatingToastHideTimer);
    clearTimeout(floatingToastRemoveTimer);

    if (!floatingToastEl) {
        floatingToastEl = document.createElement("div");
        floatingToastEl.className = "source-viewer__toast";
        floatingToastEl.setAttribute("role", "status");
    }
    const el = ensureBodyChild(floatingToastEl);
    el.textContent = text;
    el.style.transition = "";
    el.style.opacity = "1";
    el.style.pointerEvents = "none";

    // Position relative to the pointer. Default: just below + to the
    // right of the cursor so the text doesn't sit under the mouse
    // arrow. Flip across the boundary when we'd otherwise spill off
    // the viewport.
    el.style.left = "0px";
    el.style.top = "0px";
    el.hidden = false;
    const rect = el.getBoundingClientRect();
    const margin = 8;
    let x = clientX + 14;
    let y = clientY + 14;
    if (x + rect.width + margin > window.innerWidth) {
        x = Math.max(margin, clientX - rect.width - 14);
    }
    if (y + rect.height + margin > window.innerHeight) {
        y = Math.max(margin, clientY - rect.height - 14);
    }
    el.style.left = `${Math.round(x + window.scrollX)}px`;
    el.style.top  = `${Math.round(y + window.scrollY)}px`;

    floatingToastHideTimer = setTimeout(() => {
        el.style.transition = `opacity ${TOAST_FADE_MS}ms ease-out`;
        el.style.opacity = "0";
        floatingToastRemoveTimer = setTimeout(() => {
            el.hidden = true;
        }, TOAST_FADE_MS);
    }, TOAST_VISIBLE_MS);
}

// Inline copies of the Lucide glyphs the client-side renderers need.
// The server-rendered markup reaches these through <Icon Name="..." />, which
// is what IconCatalog checks; the JS builders can't, so the paths live here
// instead. ObjectExplorerInspectorTests pins them against
// Resources/Icons/{chevron-right,x,search}.svg so the copies cannot drift.
const CARET_ICON_SVG =
    '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" ' +
    'stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18 6-6-6-6"/></svg>';
const CLOSE_ICON_SVG =
    '<svg class="btn__icon" viewBox="0 0 24 24" width="12" height="12" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M18 6 6 18"/><path d="m6 6 12 12"/></svg>';
const SEARCH_ICON_SVG =
    '<svg class="search__icon" viewBox="0 0 24 24" width="15" height="15" fill="none" ' +
    'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="m21 21-4.34-4.34"/><circle cx="11" cy="11" r="8"/></svg>';
// The explorer tree's four glyphs, same rules as above. Pinned against
// Resources/Icons/{package,folder,file-code,chevron-right}.svg.
const PACKAGE_ICON_SVG =
    '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" ' +
    'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z"/>' +
    '<path d="M12 22V12"/><polyline points="3.29 7 12 12 20.71 7"/><path d="m7.5 4.27 9 5.15"/></svg>';
const FOLDER_ICON_SVG =
    '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" ' +
    'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/></svg>';
const FILE_CODE_ICON_SVG =
    '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" ' +
    'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z"/>' +
    '<path d="M14 2v5a1 1 0 0 0 1 1h5"/><path d="M10 12.5 8 15l2 2.5"/><path d="m14 12.5 2 2.5-2 2.5"/></svg>';
const CHEVRON_RIGHT_ICON_SVG =
    '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" ' +
    'stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m9 18 6-6-6-6"/></svg>';

/// Wraps one of the inline glyphs above in the span its CSS class expects,
/// the same shape the <Icon> component produces server-side.
function inlineIcon(svg, className) {
    const el = document.createElement("span");
    el.className = className;
    el.innerHTML = svg;
    el.setAttribute("aria-hidden", "true");
    return el;
}

// ── Tab controller ───────────────────────────────────────────────

class TabController {
    constructor(root) {
        this.root = root;
        this.tabs = Array.from(root.querySelectorAll(".source-viewer__tab"));
        this.panels = Array.from(root.querySelectorAll(".source-viewer__panel"));
        // Bound once per tab (#602): a re-used tab button with two listeners
        // activates twice per click. `activate` is looked up on the CURRENT
        // controller rather than captured, so a listener installed on the first
        // page still switches the panels of the page in front of the reader —
        // the previous controller's panel elements are detached by then (#717).
        this.tabs.forEach(t => bindOnceLatest(t, "panel-tab", "click",
            () => this.activate(t.dataset.tab)));
    }

    activate(name) {
        for (const tab of this.tabs) {
            const match = tab.dataset.tab === name;
            tab.classList.toggle("is-active", match);
            // The shortcuts control is a toggle button, not a tab — it sits
            // outside the tablist, so it takes aria-pressed instead.
            const attr = tab.getAttribute("role") === "tab" ? "aria-selected" : "aria-pressed";
            tab.setAttribute(attr, match ? "true" : "false");
        }
        for (const panel of this.panels) {
            panel.classList.toggle("is-active", panel.dataset.panel === name);
        }
    }

    /// Make a tab visible (or hide it). The corresponding panel's data-panel
    /// attribute and the tab's data-tab attribute must match.
    show(name, visible = true) {
        const tab = this.tabs.find(t => t.dataset.tab === name);
        if (tab) tab.hidden = !visible;
        const panel = this.panels.find(p => p.dataset.panel === name);
        if (panel) panel.hidden = !visible;
    }
}

// ── References panel renderer ────────────────────────────────────

function renderReferencesPanel(root, session, fileId, editorId) {
    const panel = root.querySelector('[data-panel="references"]');
    if (!panel) return;
    panel.innerHTML = "";
    panel.hidden = false;

    const count = session.results?.length ?? 0;
    const groups = count > 0 ? groupByObject(session.results) : [];

    const tabBtn = root.querySelector('.source-viewer__tab[data-tab="references"]');
    const tabCountEl = tabBtn?.querySelector(".source-viewer__tab-count");
    if (tabBtn) tabBtn.hidden = false;
    if (tabCountEl) {
        tabCountEl.textContent = count.toLocaleString();
    } else if (tabBtn) {
        const c = document.createElement("span");
        c.className = "pill-tab__count source-viewer__tab-count";
        c.textContent = count.toLocaleString();
        tabBtn.appendChild(c);
    }

    const section = document.createElement("div");
    section.className = "pane__sec";
    panel.appendChild(section);

    const heading = document.createElement("div");
    heading.className = "pane__sec-h sv-sec-h";
    heading.append("References to ");
    heading.appendChild(sectionName(session.targetName || "this name"));
    const countChip = document.createElement("span");
    countChip.className = "pane__count";
    // Just the total. "in N objects" would be true but the group headers
    // below already say it, and the rail is too narrow to spend a line on.
    countChip.textContent = count.toLocaleString();
    countChip.title = groups.length > 0
        ? `${count.toLocaleString()} in ${groups.length.toLocaleString()} ${groups.length === 1 ? "object" : "objects"}`
        : "no references";
    heading.appendChild(countChip);
    const spacer = document.createElement("span");
    spacer.className = "pw__spacer";
    heading.appendChild(spacer);
    const close = document.createElement("button");
    close.type = "button";
    close.className = "btn btn--icon btn--sm btn--ghost source-viewer__refs-close";
    close.dataset.action = "close-refs";
    close.title = `Close: ${session.targetLabel ?? "these references"}`;
    close.setAttribute("aria-label", "Close these references");
    close.innerHTML = CLOSE_ICON_SVG;
    heading.appendChild(close);
    section.appendChild(heading);

    // The server caps very large reference sets (see ReferenceQueryService
    // MaxReferenceMatches, issue #366). When it trimmed, tell the user the
    // list is partial so a missing row isn't read as "no such reference".
    if (session.truncated) {
        const notice = document.createElement("p");
        notice.className = "u-muted source-viewer__refs-truncated";
        notice.setAttribute("role", "status");
        notice.textContent =
            `Showing the first ${count.toLocaleString()} matches. Use the filter box below to narrow the list.`;
        section.appendChild(notice);
    }

    if (count === 0) {
        const p = document.createElement("p");
        p.className = "u-muted source-viewer__panel-empty";
        p.textContent = "No references found in this release or the apps it depends on.";
        section.appendChild(p);
        return;
    }

    // Filter box — mirrors the Outline's, so you can narrow a long
    // reference set by object, enclosing member, target member, or the
    // snippet text. Wired after the groups are built.
    const search = document.createElement("span");
    search.className = "search";
    search.innerHTML = SEARCH_ICON_SVG;
    const filter = document.createElement("input");
    filter.type = "text";
    filter.className = "input source-viewer__refs-filter";
    filter.placeholder = "Filter references...";
    filter.setAttribute("aria-label", "Filter references");
    search.appendChild(filter);
    // A sibling of the heading's section, not a child of it: nested
    // `.pane__sec` doubles both the border-bottom and the vertical padding.
    const tools = document.createElement("div");
    tools.className = "sv-sec-tools";
    tools.appendChild(search);
    section.appendChild(tools);

    // Group references by their source object so every place that touches
    // the target clusters under one header — repeated calls from the same
    // table / page no longer scatter down a flat list.
    const refs = document.createElement("div");
    refs.className = "refs";
    for (const group of groups) {
        refs.appendChild(buildRefGroup(group, session, fileId, editorId));
    }
    section.appendChild(refs);

    const empty = document.createElement("p");
    empty.className = "u-muted sv-empty source-viewer__refs-empty";
    empty.hidden = true;
    empty.textContent = "No references match the filter.";
    section.appendChild(empty);

    wireRefsFilter(panel);
}

/// One `.refgrp`: a collapsible header naming the object the references sit
/// in, over its `.refhit` rows.
function buildRefGroup(group, session, fileId, editorId) {
    const grp = document.createElement("div");
    grp.className = "refgrp is-open";
    if (group.objectId != null) grp.dataset.objectId = String(group.objectId);

    const head = document.createElement("button");
    head.type = "button";
    head.className = "refgrp__h";
    head.setAttribute("aria-expanded", "true");
    if (group.objectKind) head.title = kindBadgeLabel(group.objectKind);

    const caret = document.createElement("span");
    caret.className = "otree__caret is-open";
    caret.innerHTML = CARET_ICON_SVG;
    head.appendChild(caret);

    const title = document.createElement("span");
    title.className = "refgrp__name";
    title.textContent = group.objectName;
    head.appendChild(title);

    const n = document.createElement("span");
    n.className = "refgrp__n";
    n.textContent = group.rows.length.toLocaleString();
    head.appendChild(n);
    grp.appendChild(head);

    const list = document.createElement("div");
    list.className = "refgrp__rows";
    for (const r of group.rows) {
        list.appendChild(buildRefsRow(r, session, fileId, editorId));
    }
    grp.appendChild(list);

    head.addEventListener("click", () => {
        const open = grp.classList.toggle("is-open");
        head.setAttribute("aria-expanded", open ? "true" : "false");
        caret.classList.toggle("is-open", open);
        list.hidden = !open;
    });
    return grp;
}

// Filters the rendered reference rows by object / member / snippet text,
// mirroring wireOutlineFilter: hides non-matching rows, collapses sections
// with no surviving rows, and shows an empty-state when nothing matches.
function wireRefsFilter(panel) {
    const filter = panel.querySelector(".source-viewer__refs-filter");
    if (!filter) return;
    const sections = Array.from(panel.querySelectorAll(".refgrp"));
    // A session renders up to MaxReferenceMatches (5000) rows. Query them
    // once at wiring time, the way wireCompareRailFilter does, rather than
    // per group per keystroke (#718).
    const rowsBySection = sections.map(section =>
        Array.from(section.querySelectorAll(".refhit")));
    const empty = panel.querySelector(".source-viewer__refs-empty");

    // Only write `hidden` where it actually changes: "one more character
    // narrows the set" then costs a handful of style writes instead of 5000.
    const setHidden = (el, value) => { if (el.hidden !== value) el.hidden = value; };

    const run = () => {
        const needle = filter.value.trim().toLowerCase();
        if (needle.length === 0) {
            // Same reason as wireOutlineFilter: clearing the box restores
            // everything, rather than leaving whatever held no rows hidden.
            for (let i = 0; i < sections.length; i++) {
                setHidden(sections[i], false);
                for (const item of rowsBySection[i]) setHidden(item, false);
            }
            if (empty) empty.hidden = true;
            return;
        }
        let anyVisible = false;
        for (let i = 0; i < sections.length; i++) {
            const section = sections[i];
            let sectionVisible = false;
            for (const item of rowsBySection[i]) {
                const hay = item.dataset.filter ?? "";
                const match = hay.includes(needle);
                setHidden(item, !match);
                if (match) sectionVisible = true;
            }
            setHidden(section, !sectionVisible);
            // Re-open a collapsed group that has a match, or the row the user
            // just searched for is "found" with nothing under it.
            if (sectionVisible) {
                const rows = section.querySelector(".refgrp__rows");
                if (rows) setHidden(rows, false);
                section.classList.add("is-open");
                section.querySelector(".refgrp__h")?.setAttribute("aria-expanded", "true");
                section.querySelector(".otree__caret")?.classList.add("is-open");
                anyVisible = true;
            }
        }
        if (empty) empty.hidden = anyVisible;
    };

    // Same 220 ms window wireTreeSearch uses: typing an eight-character
    // member name should cost one pass, not eight.
    let timer = null;
    filter.addEventListener("input", () => {
        clearTimeout(timer);
        timer = setTimeout(run, SEARCH_DEBOUNCE_MS);
    });
}

// Within an object, order rows by reference category (declarations first,
// then call sites), then by line so they read top-to-bottom like the file.
const REFS_CATEGORY_ORDER = { declaration: 0, call: 1, implementation: 2, owner_type: 3, object: 4 };

function groupByObject(rows) {
    const groups = new Map();
    for (const r of rows ?? []) {
        const key = r.sourceObjectId ?? `${r.sourceObjectKind ?? ""}/${r.sourceObjectName ?? ""}`;
        let g = groups.get(key);
        if (!g) {
            g = {
                objectId: r.sourceObjectId,
                objectKind: r.sourceObjectKind ?? "",
                objectName: r.sourceObjectName ?? "",
                rows: [],
            };
            groups.set(key, g);
        }
        g.rows.push(r);
    }
    const list = Array.from(groups.values());
    for (const g of list) {
        g.rows.sort((a, b) => {
            const ca = REFS_CATEGORY_ORDER[a.category] ?? 9;
            const cb = REFS_CATEGORY_ORDER[b.category] ?? 9;
            if (ca !== cb) return ca - cb;
            return (a.lineNumber ?? 0) - (b.lineNumber ?? 0);
        });
    }
    // Stable, scannable order: by object kind, then name.
    list.sort((a, b) =>
        a.objectKind.localeCompare(b.objectKind) ||
        a.objectName.localeCompare(b.objectName));
    return list;
}

function categoryLabel(category) {
    switch (category) {
        case "declaration": return "Declarations";
        case "call":        return "Calls";
        case "owner_type":  return "Reached through a variable of this type";
        case "object":      return "References";
        default:            return category;
    }
}

/// Plain phrase for a raw reference_kind column value. These reach the user as
/// the visible text of a row whenever the reference has no code snippet, so
/// "variable_type" with the underscores swapped out is not good enough.
function referenceKindLabel(kind) {
    switch ((kind ?? "").toLowerCase()) {
        case "variable_type":   return "Declared as a variable of this type";
        case "parameter_type":  return "Taken as a parameter of this type";
        case "return_type":     return "Returned as this type";
        case "extends_target":  return "Extends this object";
        case "implements":      return "Implements this interface";
        case "method_call":     return "Calls a method on it";
        case "field_access":    return "Reads or writes one of its fields";
        case "event_publisher": return "Publishes one of its events";
        case "event_subscriber":return "Subscribes to one of its events";
        case "label_use":       return "Uses one of its labels";
        case "property_object": return "Named in a property";
        default:                return "";
    }
}

/// One result row, shared by the References panel and the Find-in-file
/// panel: a right-aligned line number and the source line itself, with the
/// matched name marked inside it. The handoff's `.refhit` is deliberately
/// one line — you read the code, not a stack of metadata about it — so the
/// enclosing procedure, module and reference kind moved to the hover
/// tooltip rather than being dropped.
///
/// Returns an <a> when `href` is given (so the row is middle-clickable and
/// opens in a new tab like any link) and a <button> otherwise.
/// Trims a long source line so the marked name stays visible. Cutting only
/// from the right is what put the interesting token behind the ellipsis.
const HIT_LEAD_CHARS = 20;
const ELLIPSIS = "...";
function elideToMatch(text, match) {
    if (!match) return text;
    const at = text.toLowerCase().indexOf(match.toLowerCase());
    // The ellipsis costs three characters, so cutting fewer than four is a net
    // loss — the "shortened" line would come out longer than the original.
    if (at <= HIT_LEAD_CHARS + ELLIPSIS.length) return text;
    return ELLIPSIS + text.slice(at - HIT_LEAD_CHARS);
}

function buildHitRow({ line, text, match, href, onActivate, title, filter, current }) {
    const row = document.createElement(href ? "a" : "button");
    row.className = current ? "refhit is-active" : "refhit";
    if (href) {
        row.href = href;
    } else {
        row.type = "button";
    }
    if (title) row.title = title;
    if (filter) row.dataset.filter = filter;

    const n = document.createElement("span");
    n.className = "refhit__n";
    n.textContent = line != null ? String(line) : "";
    row.appendChild(n);

    const c = document.createElement("span");
    c.className = "refhit__c";
    appendMarked(c, elideToMatch((text ?? "").trim(), match), match);
    row.appendChild(c);

    if (onActivate) {
        row.addEventListener("click", e => {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            e.preventDefault();
            onActivate(e);
        });
    }
    return row;
}

/// Appends `text` to `host`, wrapping each case-insensitive occurrence of
/// `match` in a <mark>. textContent throughout — the snippet is source code
/// from another org's release and never reaches innerHTML.
function appendMarked(host, text, match) {
    if (!match) {
        host.textContent = text;
        return;
    }
    const needle = match.toLowerCase();
    const hay = text.toLowerCase();
    let from = 0;
    for (;;) {
        const at = hay.indexOf(needle, from);
        if (at < 0) break;
        if (at > from) host.appendChild(document.createTextNode(text.slice(from, at)));
        const mark = document.createElement("mark");
        mark.textContent = text.slice(at, at + match.length);
        host.appendChild(mark);
        from = at + match.length;
    }
    if (from < text.length) host.appendChild(document.createTextNode(text.slice(from)));
}

function buildRefsRow(r, session, fileId, editorId) {
    const srcFid = r.sourceFileId;
    const ln = r.lineNumber;
    const hasLoc = srcFid != null && ln != null;
    const inSameFile = hasLoc && srcFid === fileId;

    const href = hasLoc
        ? `${FILE_URL_PREFIX}${srcFid}?line=${ln}&refSet=${encodeURIComponent(session.token)}`
        : `/object-explorer/object/${r.sourceObjectId}`;

    // What to mark inside the snippet: the member the reference names. Object-
    // scope rows (variable_type, extends_target) name no member, so they stay
    // unmarked rather than guessing at a span.
    const match = r.memberName || session.targetName;

    // No snippet (object-scope references carry none) — fall back to the
    // humanised reference kind so the row still says what it is.
    const text = r.snippet
        || referenceKindLabel(r.referenceKind)
        || categoryLabel(r.category);

    const row = buildHitRow({
        line: ln,
        text,
        // The handoff marks the hit you are sitting on. We know it: the row
        // that matches the file and line currently open is the one the user
        // followed to get here.
        current: hasLoc && srcFid === fileId && ln === currentLine(),
        match: r.snippet ? match : null,
        href,
        title: refsRowTitle(r),
        filter: [r.sourceObjectName, r.sourceMemberName, r.memberName, r.referenceKind, r.snippet]
            .filter(Boolean).join(" ").toLowerCase(),
        onActivate: inSameFile
            ? () => {
                scrollToLine(editorId, ln, true);
                const target = href.replace(location.origin, "");
                if (location.pathname + location.search !== target) {
                    history.pushState(null, "", href);
                }
            }
            : null,
    });
    attachRefsTooltip(row, r);
    return row;
}

/// Plain-text fallback for the row's `title`, for keyboard users and for
/// the moment before the hover card appears.
/// The line the viewer is currently parked on, from the ?line= deep link the
/// references panel itself writes when a row is followed.
function currentLine() {
    const n = Number(new URL(location.href).searchParams.get("line"));
    return Number.isFinite(n) && n > 0 ? n : null;
}

function refsRowTitle(r) {
    const parts = [];
    if (r.sourceMemberName) parts.push(`in ${r.sourceMemberName}`);
    if (r.sourceFilePath) parts.push(r.sourceFilePath);
    const kind = referenceKindLabel(r.referenceKind);
    if (kind) parts.push(kind);
    return parts.join(" - ");
}

// ── Symbol hover card ────────────────────────────────────────────
//
// Hovering an underlined name in the code pane shows the handoff's
// `.symcard`: the declaration's signature, where it lives, and the two
// jumps you would otherwise have to guess at. It is the payoff for
// reading someone else's extension — the signature of a procedure
// declared three files away, without leaving the line you are on.
//
// The card is fetched by symbol id, cached per page, and rendered into
// <body> so it can overhang the code pane's scroll box.

const SYMBOL_CARD_DELAY_MS = 320;
const SYMBOL_CARD_GRACE_MS = 140;
const symbolCardCache = new Map();

async function fetchSymbolCard(symbolId) {
    if (symbolCardCache.has(symbolId)) return symbolCardCache.get(symbolId);
    try {
        const res = await fetch(`/api/object-explorer/symbols/${symbolId}/card`,
            { credentials: "same-origin" });
        // A 404 is a real answer and worth remembering. A transport failure or
        // a 500 is not: caching it would mean one dropped request costs that
        // symbol its card for the life of the page.
        if (res.status === 404) {
            symbolCardCache.set(symbolId, null);
            return null;
        }
        if (!res.ok) return null;
        const card = await res.json();
        symbolCardCache.set(symbolId, card);
        return card;
    } catch (err) {
        console.warn("Symbol card fetch failed:", err);
        return null;
    }
}

function wireSymbolCard(root, codeHost, editorId, fileId, handlers, signal) {
    let card = null;
    let showTimer = null;
    let hideTimer = null;
    let shownFor = null;
    let overCard = false;
    // Bumped by every hover and every hide. A fetch that resolves against a
    // stale generation is discarded: without this, hovering a slow uncached
    // symbol and then a fast cached one renders the second card and then
    // replaces it with the first, anchored under a token the pointer left.
    let generation = 0;
    // True while the pointer is somewhere over the code pane. A hover whose
    // fetch outlives the pointer must not append a card the user can no
    // longer dismiss with the mouse — mouseleave has already fired by then.
    let pointerInside = false;

    const clearTimers = () => {
        clearTimeout(showTimer); showTimer = null;
        clearTimeout(hideTimer); hideTimer = null;
    };

    const hide = () => {
        generation++;
        clearTimers();
        shownFor = null;
        overCard = false;
        if (card) { card.remove(); card = null; }
    };

    const scheduleHide = () => {
        clearTimeout(hideTimer);
        hideTimer = setTimeout(() => { if (!overCard) hide(); }, SYMBOL_CARD_GRACE_MS);
    };

    codeHost.addEventListener("mouseenter", () => { pointerInside = true; });

    codeHost.addEventListener("mouseover", e => {
        pointerInside = true;
        const token = e.target instanceof Element
            ? e.target.closest("[data-symbol-id]")
            : null;
        // Object headers stamp an oe_module_objects id here, not a symbol id
        // (see buildDeclarationDecorationExtensions) — no card for those.
        const isSymbol = token
            && (token.classList.contains("cm-symbol-ref")
                || token.dataset.memberSymbol === "1");
        if (!isSymbol) {
            if (shownFor) scheduleHide();
            return;
        }
        const id = Number(token.dataset.symbolId);
        if (!Number.isFinite(id) || id <= 0) return;
        if (shownFor === token) { clearTimeout(hideTimer); return; }
        clearTimers();
        const mine = ++generation;
        showTimer = setTimeout(async () => {
            // Nothing awaits this callback, so an exception inside it would
            // reject silently — with shownFor already set to the token, which
            // makes the state machine believe a card is up and suppresses
            // every later hover. One bad payload must not wedge the page
            // (#718): reset the state and drop the card instead.
            try {
                const data = await fetchSymbolCard(id);
                if (mine !== generation) return;
                if (!data || !pointerInside || !document.contains(token)) return;
                if (card) { card.remove(); card = null; }
                shownFor = token;
                card = buildSymbolCard(data, fileId, editorId, handlers, hide);
                card.addEventListener("mouseenter", () => { overCard = true; clearTimeout(hideTimer); });
                card.addEventListener("mouseleave", () => { overCard = false; scheduleHide(); });
                document.body.appendChild(card);
                placeSymbolCard(card, token);
            } catch (err) {
                console.warn("Symbol card failed:", err);
                shownFor = null;
                if (card) { card.remove(); card = null; }
            }
        }, SYMBOL_CARD_DELAY_MS);
    });

    codeHost.addEventListener("mouseleave", () => {
        pointerInside = false;
        overCard = false;
        scheduleHide();
    });
    codeHost.addEventListener("scroll", hide, true);
    // Scoped the way the other window-level handlers here are: init() re-runs
    // on every enhanced navigation, so an unguarded listener would retain this
    // page's DOM for the life of the tab.
    window.addEventListener("keydown", e => {
        if (!document.contains(root)) return;
        if (e.key === "Escape") hide();
    }, { signal });
    window.addEventListener("popstate", () => {
        if (document.contains(root)) hide();
    }, { signal });
    // The card lives on document.body, outside the root the teardown removes.
    signal?.addEventListener("abort", hide, { once: true });
}

function buildSymbolCard(data, fileId, editorId, handlers, dismiss) {
    const el = document.createElement("div");
    el.className = "symcard";
    el.setAttribute("role", "tooltip");

    const sig = document.createElement("span");
    sig.className = "symcard__sig";
    sig.textContent = declarationOf(data);
    el.appendChild(sig);

    // The declaration's XML doc summary, when the module was imported with
    // source and someone wrote one. Appended only when there is text: an
    // empty node would still take the card's row gap, so an undocumented
    // procedure would look like a documented one with the words missing.
    if (data.doc) {
        const doc = document.createElement("span");
        doc.className = "symcard__doc";
        doc.textContent = data.doc;
        el.appendChild(doc);
    }

    const meta = document.createElement("span");
    meta.className = "symcard__meta";
    // Two facts, not three: the meta row is one 356px monospace line, and the
    // file name already says which object the member sits on. The owner is on
    // the card's title attribute for the cases where it doesn't.
    // Null-safe on `kind` the way every other reader of it in this file is
    // (declarationOf, expandKindLabel, kindBadgeLabel): a row whose kind was
    // never populated must not throw out of the hover path (#718).
    el.title = `${(data.kind ?? "").replace(/_/g, " ")} ${data.name} - ${data.ownerKind} ${data.ownerName} (${data.moduleName})`;
    // Two facts, and no longer three: the visibility used to be the third
    // item here, where it was the first thing to be cut short on a long path
    // and read as a property of the LOCATION rather than of the member. It is
    // in the declaration line now, which is where AL puts it.
    for (const part of [
        data.moduleName,
        data.filePath ? `${baseName(data.filePath)}:${data.lineNumber}` : null,
    ]) {
        if (!part) continue;
        const span = document.createElement("span");
        span.textContent = part;
        meta.appendChild(span);
    }
    el.appendChild(meta);

    const acts = document.createElement("span");
    acts.className = "symcard__acts";

    if (data.fileId != null) {
        const go = document.createElement("a");
        go.className = "symcard__act";
        go.href = `${FILE_URL_PREFIX}${data.fileId}?line=${data.lineNumber}`;
        go.append("Go to definition");
        // Dismiss on any left-click, not only the same-file one: a cross-file
        // jump is an <a href> under enhanced navigation, which fires no
        // popstate, and the card lives in <body> outside the Blazor root — so
        // without this it survives the navigation it just caused.
        go.addEventListener("click", e => {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            dismiss();
            if (data.fileId === fileId) {
                e.preventDefault();
                scrollToLine(editorId, data.lineNumber, true);
            }
        });
        acts.appendChild(go);
    }

    const refs = document.createElement("button");
    refs.type = "button";
    refs.className = "symcard__act";
    refs.append("Find references");
    refs.addEventListener("click", () => {
        dismiss();
        handlers.onFindReferences(data.symbolId);
    });
    acts.appendChild(refs);

    el.appendChild(acts);
    return el;
}

/// A `.pane__sec-h` is an uppercased micro-label, which is right for a
/// category ("FIELDS") and wrong for an AL identifier — uppercasing
/// `GetLegalEntityName` throws away the camelCase that makes it readable.
/// Section headings that name user data put it in one of these instead.
function sectionName(text) {
    const span = document.createElement("span");
    span.className = "sv-sec-name";
    span.textContent = text;
    return span;
}

/// The handoff's third meta slot. "Can I call this from my own extension?" is
/// the question a card about someone else's code most has to answer, and AL
/// encodes the answer in the symbol kind rather than a separate column.
/// The card's heading, written the way AL writes the declaration.
///
/// `signature` is the parameter list ON ITS OWN — the extractor never puts a
/// name, a keyword or a return type in it — so every part of the line has to
/// be reassembled here. Rendering it raw showed a card headed `()`; joining it
/// to the name with nothing between showed a field headed
/// `Amount Including VATDecimal`.
///
/// Visibility rides the keyword rather than sitting in the meta row, because
/// that is what it modifies. Note that AL has no `public` modifier: a
/// procedure with none IS public, so the public case is a bare `procedure`
/// and not a word we would have to invent.
///
/// Event publishers and subscribers deliberately get no keyword. In source
/// they are a `procedure` under an `[IntegrationEvent]` / `[EventSubscriber]`
/// attribute, and calling one "procedure" here would drop the only thing that
/// makes it interesting. The outline section and the card's title attribute
/// both name the kind.
function declarationOf(data) {
    const name = data.name ?? "";
    const params = data.signature ?? "";
    const returns = data.returnType ? `: ${data.returnType}` : "";

    switch ((data.kind ?? "").toLowerCase()) {
        case "procedure":
            return `procedure ${name}${params}${returns}`;
        case "local_procedure":
            return `local procedure ${name}${params}${returns}`;
        case "internal_procedure":
            return `internal procedure ${name}${params}${returns}`;
        case "protected_procedure":
            return `protected procedure ${name}${params}${returns}`;
        case "trigger":
            return `trigger ${name}${params}`;
        // A field's "signature" is its AL type, not a parameter list, so it
        // needs a separator or the two words run together.
        case "table_field":
        case "page_field":
            return params ? `${name}: ${params}` : name;
        default:
            return `${name}${params}${returns}`;
    }
}


function baseName(path) {
    const slash = (path ?? "").lastIndexOf("/");
    return slash < 0 ? path : path.slice(slash + 1);
}

/// Anchors the card under the hovered token, then pulls it back inside the
/// viewport — flipping above the token when there is no room below.
function placeSymbolCard(card, token) {
    const anchor = token.getBoundingClientRect();
    const box = card.getBoundingClientRect();
    const margin = 8;
    let left = anchor.left + window.scrollX;
    left = Math.min(left, window.scrollX + document.documentElement.clientWidth - box.width - margin);
    left = Math.max(left, window.scrollX + margin);

    let top = anchor.bottom + window.scrollY + 6;
    if (anchor.bottom + box.height + 6 > document.documentElement.clientHeight) {
        top = anchor.top + window.scrollY - box.height - 6;
    }
    top = Math.max(top, window.scrollY + margin);

    card.style.left = `${Math.round(left)}px`;
    card.style.top = `${Math.round(top)}px`;
}

// ── Refs-row hover tooltip ───────────────────────────────────────
//
// One reusable popover element rendered against the page so the
// rows can stay visually clean. Hovering a refs row shows the
// extension (module name), source file path, line number, and the
// full reference kind / category. Positioned to the left of the
// row so it never collides with the row contents; flips above the
// row when it would otherwise spill off the bottom of the panel.

let refsTooltipEl = null;
let refsTooltipHideTimer = 0;

function ensureRefsTooltip() {
    if (!refsTooltipEl) {
        refsTooltipEl = document.createElement("div");
        refsTooltipEl.className = "source-viewer__refs-tooltip";
        refsTooltipEl.setAttribute("role", "tooltip");
        refsTooltipEl.hidden = true;
    }
    return ensureBodyChild(refsTooltipEl);
}

function attachRefsTooltip(anchor, r) {
    anchor.addEventListener("mouseenter", () => showRefsTooltip(anchor, r));
    anchor.addEventListener("mouseleave", hideRefsTooltip);
    anchor.addEventListener("focusin", () => showRefsTooltip(anchor, r));
    anchor.addEventListener("focusout", hideRefsTooltip);
}

function showRefsTooltip(anchor, r) {
    clearTimeout(refsTooltipHideTimer);
    const el = ensureRefsTooltip();
    el.innerHTML = "";

    // Module · Object — the "where" line.
    const where = document.createElement("div");
    where.className = "source-viewer__refs-tooltip-where";
    where.textContent = `${r.sourceModuleName} › ${r.sourceObjectName}`;
    el.appendChild(where);

    // File path · line — the "what file" line, or "no source" when the
    // module didn't ship sources we could ingest.
    const loc = document.createElement("div");
    loc.className = "source-viewer__refs-tooltip-loc";
    if (r.sourceFileId != null && r.lineNumber != null) {
        const path = r.sourceFilePath ?? "(source file)";
        loc.textContent = `${path} · L${r.lineNumber}`;
    } else {
        loc.textContent = "no source available";
    }
    el.appendChild(loc);

    // Enclosing procedure / trigger the reference sits inside, when known.
    if (r.sourceMemberName) {
        const inMember = document.createElement("div");
        inMember.className = "source-viewer__refs-tooltip-member";
        const enclosingKind = expandKindLabel(r.sourceMemberKind);
        inMember.textContent = enclosingKind
            ? `in ${enclosingKind} ${r.sourceMemberName}`
            : `in ${r.sourceMemberName}`;
        el.appendChild(inMember);
    }

    // Member kind+name when present (the reference is to a specific
    // procedure / field, not just the owner object).
    if (r.memberName) {
        const member = document.createElement("div");
        member.className = "source-viewer__refs-tooltip-member";
        const memberKind = expandKindLabel(r.memberKind);
        const memberSig = r.memberSignature ?? "";
        member.textContent = memberKind
            ? `${memberKind} ${r.memberName}${memberSig}`
            : `${r.memberName}${memberSig}`;
        el.appendChild(member);
    }

    // Reference kind chip + category description.
    const kindRow = document.createElement("div");
    kindRow.className = "source-viewer__refs-tooltip-kind u-muted";
    kindRow.textContent = describeReferenceCategory(r.category, r.referenceKind);
    el.appendChild(kindRow);

    positionRefsTooltip(el, anchor);
    el.hidden = false;
}

function hideRefsTooltip() {
    // Tiny delay lets the cursor cross the small gap between the row and
    // a neighbouring row without the tooltip flickering off.
    clearTimeout(refsTooltipHideTimer);
    refsTooltipHideTimer = setTimeout(() => {
        if (refsTooltipEl) refsTooltipEl.hidden = true;
    }, 60);
}

function positionRefsTooltip(el, anchor) {
    el.style.left = "0";
    el.style.top = "0";
    el.style.maxWidth = "420px";
    // Width must be measured pre-position so the flip math works.
    el.hidden = false;
    const rowRect = anchor.getBoundingClientRect();
    const tipRect = el.getBoundingClientRect();
    const margin = 8;
    // Preferred: place to the LEFT of the row, vertically centred. The
    // refs panel lives on the right edge of the screen, so this keeps
    // the tooltip inside the viewport on the common layout.
    let x = rowRect.left - tipRect.width - margin;
    let y = rowRect.top + (rowRect.height - tipRect.height) / 2;
    // Off-screen left → flip to the right of the row instead.
    if (x < margin) {
        x = rowRect.right + margin;
    }
    // Clamp vertical so the tooltip stays inside the viewport.
    const maxY = window.innerHeight - tipRect.height - margin;
    if (y < margin) y = margin;
    if (y > maxY) y = maxY;
    el.style.left = `${Math.round(x + window.scrollX)}px`;
    el.style.top  = `${Math.round(y + window.scrollY)}px`;
}

/// The one table of symbol / object kind labels. `long` is the phrase used in
/// running text (the refs tooltip), `badge` the abbreviation used where space
/// is tight (the reference group header's tooltip, mirroring KindBadgeLabel in
/// SourceFileViewer.razor - keep those two in step).
///
/// A new kind from the extractor is added here once. Anything not listed falls
/// back to the raw key with its underscores opened up, so an unknown
/// "event_publisher_v2" reads as "event publisher v2" rather than leaking the
/// column value into the UI.
const SYMBOL_KIND_LABELS = {
    procedure:               { long: "procedure",             badge: "proc" },
    internal_procedure:      { long: "internal procedure",    badge: "internal" },
    protected_procedure:     { long: "protected procedure",   badge: "protected" },
    local_procedure:         { long: "local procedure",       badge: "local" },
    event_publisher:         { long: "event publisher",       badge: "event pub" },
    event_subscriber:        { long: "event subscriber",      badge: "event sub" },
    field:                   { long: "field",                 badge: "field" },
    action:                  { long: "action",                badge: "action" },
    trigger:                 { long: "trigger",               badge: "trigger" },
    codeunit:                { long: "codeunit",              badge: "codeunit" },
    table:                   { long: "table",                 badge: "table" },
    tableextension:          { long: "table extension",       badge: "table ext" },
    page:                    { long: "page",                  badge: "page" },
    pageextension:           { long: "page extension",        badge: "page ext" },
    report:                  { long: "report",                badge: "report" },
    reportextension:         { long: "report extension",      badge: "report ext" },
    xmlport:                 { long: "xmlport",               badge: "xmlport" },
    query:                   { long: "query",                 badge: "query" },
    controladdin:            { long: "control add-in",        badge: "controladd" },
    enum:                    { long: "enum",                  badge: "enum" },
    enumextension:           { long: "enum extension",        badge: "enum ext" },
    interface:               { long: "interface",             badge: "interface" },
    permissionset:           { long: "permission set",        badge: "permset" },
    permissionsetextension:  { long: "permission set extension", badge: "permset ext" },
    profile:                 { long: "profile",               badge: "profile" },
};

/// Shared fallback for a kind the table doesn't know: the raw key with its
/// underscores opened up. Empty for a missing kind, as before.
function humaniseKind(kind) {
    return (kind ?? "").toLowerCase().replace(/_/g, " ").trim();
}

function symbolKindLabel(kind, form) {
    const key = (kind ?? "").toLowerCase();
    return SYMBOL_KIND_LABELS[key]?.[form] ?? humaniseKind(kind);
}

function expandKindLabel(kind) {
    return symbolKindLabel(kind, "long");
}

function describeReferenceCategory(category, referenceKind) {
    const cat = category ?? "object";
    const kind = referenceKind ?? "";
    switch (cat) {
        case "declaration": return "Declaration of the same symbol elsewhere";
        case "call":        return referenceKindLabel(kind) || "Call site";
        case "owner_type":  return "Reached through a variable of this type";
        case "object":      return referenceKindLabel(kind) || "Reference";
        default:            return referenceKindLabel(kind) || cat;
    }
}

/// Humanises a raw symbol / object kind ("event_publisher" -> "event pub")
/// for the reference group header's tooltip.
function kindBadgeLabel(kind) {
    return symbolKindLabel(kind, "badge");
}

/// Lazy-loads the outline's "Using" and "Used by" sections via one fetch
/// to /api/object-explorer/files/{id}/dependencies. Empty sections
/// collapse and show "(none)". Targets without ingested source render with
/// the kind badge but no clickable link; the tooltip explains why.
function wireFileDependencies(root, fileId) {
    const usingList = root.querySelector('[data-deps-list="using"]');
    const usedByList = root.querySelector('[data-deps-list="used-by"]');
    if (!usingList && !usedByList) return;
    fetch(`/api/object-explorer/files/${fileId}/dependencies`, {
        credentials: "same-origin",
    }).then(r => r.ok ? r.json() : Promise.reject(r.statusText))
      .then(data => {
          renderDepsSection(root, "using", usingList, data.using ?? []);
          renderDepsSection(root, "used-by", usedByList, data.usedBy ?? []);
      })
      .catch(() => {
          // Not renderDepsSection(…, []) — "0 / (none)" is the answer to a
          // different question, and a consultant tracing wiring would read it
          // as "nothing references this" and stop looking.
          renderDepsFailure(usingList);
          renderDepsFailure(usedByList);
      });
}

function renderDepsFailure(list) {
    if (!list) return;
    list.innerHTML = "";
    const p = document.createElement("p");
    p.className = "u-muted sv-empty";
    p.textContent = "Couldn't load this. Reload the page to try again.";
    list.appendChild(p);
    const count = list.closest("[data-deps-section]")?.querySelector("[data-deps-count]");
    if (count) count.textContent = "";
}

function renderDepsSection(root, key, list, rows) {
    if (!list) return;
    const section = root.querySelector(`[data-deps-section="${key}"]`);
    const countChip = section?.querySelector("[data-deps-count]");
    list.innerHTML = "";
    if (countChip) countChip.textContent = String(rows.length);
    if (rows.length === 0) {
        const p = document.createElement("p");
        p.className = "u-muted source-viewer__panel-empty";
        p.textContent = "(none)";
        list.appendChild(p);
        // Collapse the empty section so it doesn't take vertical space.
        if (section) {
            section.classList.remove("is-open");
            const toggle = section.querySelector(".sv-section__toggle");
            toggle?.setAttribute("aria-expanded", "false");
            const chevron = section.querySelector(".otree__caret");
            chevron?.classList.remove("is-open");
            list.hidden = true;
        }
        return;
    }
    for (const row of rows) {
        list.appendChild(buildDepsRow(row));
    }
}

/// One `.orow` in the outline's Using / Used-by sections. Targets without
/// ingested source render as a non-clickable row; the title says why.
function buildDepsRow(row) {
    const navigable = row.targetFileId != null;
    const el = document.createElement(navigable ? "a" : "span");
    // `sv-row` and `data-row-name` are what wireOutlineFilter scans for. Without
    // them these sections hold zero matchable rows, so the filter hides them and
    // — because the "empty needle" escape lives inside the per-row loop — never
    // brings them back, even after the box is cleared.
    el.className = navigable ? "orow sv-row" : "orow sv-row is-inert";
    el.dataset.rowName = row.targetObjectName ?? "";
    if (navigable) {
        el.href = `/object-explorer/file/${row.targetFileId}?line=${row.targetLineNumber ?? 1}`;
        el.title = [row.targetModuleName, (row.referenceKind || "").replace(/_/g, " ")]
            .filter(Boolean).join(" - ");
    } else {
        el.title = "No source imported for this object.";
    }

    // Empty, like every kind outside the handoff's three (see KindGlyph in
    // SourceFileViewer.razor). The span still holds the column's 13px, which
    // is what keeps these rows' names aligned with the outline's above.
    const glyph = document.createElement("span");
    glyph.className = "orow__glyph";
    glyph.setAttribute("aria-hidden", "true");
    el.appendChild(glyph);

    const name = document.createElement("span");
    name.className = navigable ? "orow__name" : "orow__name u-muted";
    name.textContent = row.targetObjectName ?? "";
    el.appendChild(name);

    const mod = document.createElement("span");
    mod.className = "orow__type";
    mod.textContent = row.targetModuleName ?? "";
    el.appendChild(mod);
    return el;
}

function wireRefsCloseButton(root) {
    // Delegated on the root and looking everything up per click, so binding it
    // once for the root's life is enough — and necessary: a second listener
    // would close the panel and then re-run the whole close on the same click
    // (#602/#717).
    bindOnce(root, "refs-close", "click", e => {
        const target = e.target instanceof Element ? e.target.closest('[data-action="close-refs"]') : null;
        if (!target) return;
        e.preventDefault();
        const panel = root.querySelector('[data-panel="references"]');
        if (panel) panel.hidden = true;
        const tab = root.querySelector('.source-viewer__tab[data-tab="references"]');
        if (tab) tab.hidden = true;
        const url = new URL(location.href);
        url.searchParams.delete("refSet");
        history.replaceState(null, "", url.pathname + url.search);
        // Activate Outline so the panel area isn't blank.
        const outlineTab = root.querySelector('.source-viewer__tab[data-tab="outline"]');
        if (outlineTab) outlineTab.click();
    });
}

// ── Ctrl/Cmd-F intercept ─────────────────────────────────────────
//
// CodeMirror's searchKeymap only fires when the editor has DOM focus.
// Browsers grab Ctrl/Cmd-F otherwise. Bind a window-level keydown that
// claims the shortcut for the editor whenever the source viewer is on
// screen.
/// Shift+F12 -> find references to whatever the caret is on. Chosen over
/// F12-for-definition because a bare F12 opens the browser's developer tools
/// on every desktop browser; Cmd/Ctrl-click carries definition instead.
///
/// Bound on window rather than the editor DOM for the same reason
/// wireSearchShortcut is: the viewer's CodeMirror is read-only, so its
/// content element never takes focus and a listener on it would never hear
/// the key. The document.contains(root) guard keeps it scoped to the page.
function wireFindReferencesShortcut(root, editorId, findReferencesAt, signal) {
    window.addEventListener("keydown", e => {
        if (e.key !== "F12" || !e.shiftKey || e.ctrlKey || e.metaKey || e.altKey) return;
        if (!document.contains(root)) return;
        const at = cursorPosition(editorId);
        if (!at) return;
        e.preventDefault();
        findReferencesAt(at.line, at.column);
    }, { signal });
}

function wireSearchShortcut(root, editorId, signal) {
    window.addEventListener("keydown", e => {
        const isFind = e.key === "f" || e.key === "F";
        if (!isFind) return;
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.shiftKey || e.altKey) return;
        if (!document.contains(root)) return;
        e.preventDefault();
        openSearch(editorId);
    }, { signal });
}

// ── Ctrl/Cmd-A intercept ─────────────────────────────────────────
//
// The read-only mount keeps EditorView.editable.of(false) on the
// editor's contentDOM (contenteditable="false"). That means
// defaultKeymap's Mod-a binding never sees the keystroke — the
// browser's native "select everything on the page" wins, which
// selects the outline, the breadcrumb, and the rest of the surface
// alongside the code. Worse, a non-editable contentDOM never takes
// DOM focus, so clicking around the code leaves document.activeElement
// on <body> and a focus check alone can't tell "user is working in the
// code" from "user is elsewhere". Track where the last pointer-down
// landed instead (seeded true — the code IS the page's main surface),
// and route Ctrl/Cmd-A to CodeMirror's selectAll whenever the user's
// attention is in the editor. Real text inputs (the outline filter,
// the search panel) keep their native select-all.
function wireSelectAllShortcut(root, editorId, codeHost, signal) {
    let pointerInEditor = true;
    document.addEventListener("pointerdown", e => {
        if (!document.contains(root)) return;
        pointerInEditor = e.target instanceof Node && codeHost.contains(e.target);
    }, { signal });
    window.addEventListener("keydown", e => {
        const isA = e.key === "a" || e.key === "A";
        if (!isA) return;
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.shiftKey || e.altKey) return;
        if (!document.contains(root)) return;
        const active = document.activeElement;
        // Focus in a real input/textarea → native select-all of that field.
        if (active instanceof HTMLElement
            && (active.tagName === "INPUT" || active.tagName === "TEXTAREA" || active.isContentEditable)) {
            return;
        }
        const inEditor = pointerInEditor
            || (active && codeHost.contains(active))
            || (active && containsNode(editorId, active));
        if (!inEditor) return;
        e.preventDefault();
        selectAll(editorId);
    }, { signal });
}

// ── Outline pieces (unchanged from prior version) ────────────────

// ── Pane splits ──────────────────────────────────────────────────
//
// Two drag handles: explorer | editor | inspector. Each writes a CSS
// custom property that .oe's grid-template-columns reads, so a drag is
// one style write and no relayout of our own, and persists the chosen
// width in localStorage so the next file inherits it. Widths are
// clamped to the same range on both sides of the drag — the panes stay
// readable and the editor keeps room.

const SPLIT_SPECS = {
    left: {
        key: "aldt.source-viewer.explorer-width",
        // The explorer's width rides in a cookie so the server can render the
        // pane at it. Applying it client-side left the pane at its default
        // width for a frame on every navigation (#603). The localStorage key
        // stays as the migration source for a width chosen before that.
        cookie: "aldt-oe-explorer-width",
        prop: "--oe-left",
        pane: ".oe__left",
        min: 180,
        max: 520,
        // Dragging right widens the pane on the handle's left.
        sign: 1,
    },
    // The compare page's change rail. Its grid is .cmp, not .oe, and the
    // width lands on --cmp-rail.
    rail: {
        key: "aldt.compare.rail-width",
        prop: "--cmp-rail",
        pane: ".cmp > .pane",
        grid: ".cmp",
        min: 180,
        max: 520,
        sign: 1,
    },
    right: {
        key: "aldt.source-viewer.outline-width",
        prop: "--oe-right",
        pane: ".oe__right",
        min: 220,
        max: 720,
        // Dragging right narrows the pane on the handle's right.
        sign: -1,
    },
};

function wirePaneSplits(root) {
    for (const handle of root.querySelectorAll(".pw-split[data-split]")) {
        const spec = SPLIT_SPECS[handle.dataset.split];
        if (spec) wireSplit(root, handle, spec);
    }
}

function wireSplit(root, handle, spec) {
    const pane = root.querySelector(spec.pane);
    if (!pane) return;
    const splitName = handle.dataset.split;
    // A handle can outlive the pane it resizes. The compare page's rail rows
    // are enhanced navigations: Blazor swaps the editors and keeps the .pw
    // frame, so this same handle element comes back around on every hop. Bound
    // twice, two pointer/key handlers each read the width and write it, and
    // one ArrowRight moved the rail 40px instead of 20 - 80px after three
    // clicks, persisted to localStorage.
    if (handle.__splitBound) return;
    handle.__splitBound = true;

    // Rehydrate before first paint, or the layout flashes at the default
    // width and settles a frame later.
    const stored = readStoredWidth(spec);
    if (stored !== null) {
        root.style.setProperty(spec.prop, clamp(stored, spec.min, maxFor(root, pane, spec)) + "px");
    }

    let pointerId = null;
    let startX = 0;
    let startWidth = 0;
    // Fixed for the duration of a drag. Recomputing it per move meant a
    // getBoundingClientRect() on two elements immediately before writing the
    // new width — a read-write-read-write thrash that forces a synchronous
    // layout of the whole three-pane grid every frame, on top of the one the
    // write already causes. Neither input changes mid-drag: the grid keeps its
    // width, and the pane's own is what we are computing.
    let dragMax = spec.max;

    // A handle can also outlive its own identity, and that is worse than being
    // bound twice. Blazor's enhanced navigation re-uses DOM nodes across
    // routes, so this same <div> is handed to the next page as some other
    // element with these listeners still attached — it came back as the right
    // pane of the standalone diff tool (/diff, the Compare tool before this
    // branch renamed it), where the unconditional preventDefault() below
    // swallowed every pointerdown: no click reached CodeMirror, the pane never
    // took focus, and nothing could be typed into it (issue #535). The
    // arrow-key nudge ate ArrowLeft/ArrowRight there for the same reason. The
    // listeners cannot be removed from in here, so each one re-checks that it
    // is still driving a live split handle before acting.
    const isLiveHandle = () =>
        handle.classList.contains("pw-split")
        && handle.dataset.split === splitName
        && document.contains(root)
        && root.contains(handle);

    handle.addEventListener("pointerdown", e => {
        if (!isLiveHandle()) return;
        if (e.button !== 0) return;
        pointerId = e.pointerId;
        startX = e.clientX;
        startWidth = pane.getBoundingClientRect().width;
        dragMax = maxFor(root, pane, spec);
        handle.setPointerCapture(pointerId);
        handle.classList.add("is-hover");
        document.body.style.cursor = "col-resize";
        e.preventDefault();
    });

    // A pointermove can fire several times per frame, and each write relayouts
    // the whole three-pane grid and makes CodeMirror re-measure its viewport —
    // which is what made dragging the inspector feel like it was catching. One
    // write per frame, carrying only the newest position.
    let pendingX = null;
    let frame = 0;
    const applyPending = () => {
        frame = 0;
        if (pendingX === null) return;
        const next = clamp(startWidth + spec.sign * (pendingX - startX), spec.min, dragMax);
        pendingX = null;
        root.style.setProperty(spec.prop, next + "px");
    };

    handle.addEventListener("pointermove", e => {
        if (pointerId === null || e.pointerId !== pointerId) return;
        if (!isLiveHandle()) return;
        pendingX = e.clientX;
        if (!frame) frame = requestAnimationFrame(applyPending);
    });

    const endDrag = e => {
        if (pointerId === null || (e && e.pointerId !== pointerId)) return;
        try { handle.releasePointerCapture(pointerId); } catch { /* already released */ }
        pointerId = null;
        // Land the last position the drag reached, then stop the loop.
        if (frame) { cancelAnimationFrame(frame); frame = 0; }
        applyPending();
        handle.classList.remove("is-hover");
        document.body.style.cursor = "";
        storeWidth(spec, pane.getBoundingClientRect().width);
    };
    handle.addEventListener("pointerup", endDrag);
    handle.addEventListener("pointercancel", endDrag);

    // Keyboard: the handle is focusable, so arrows have to move it. 20px a
    // press, 60 with Shift.
    handle.addEventListener("keydown", e => {
        if (!isLiveHandle()) return;
        const step = e.shiftKey ? 60 : 20;
        let delta = 0;
        if (e.key === "ArrowLeft") delta = -step;
        else if (e.key === "ArrowRight") delta = step;
        else return;
        e.preventDefault();
        const current = pane.getBoundingClientRect().width;
        const next = clamp(current + spec.sign * delta, spec.min, maxFor(root, pane, spec));
        root.style.setProperty(spec.prop, next + "px");
        storeWidth(spec, next);
    });
}

function clamp(v, lo, hi) {
    return Math.min(Math.max(v, lo), Math.max(lo, hi));
}

/// The absolute maximum, further capped so the code column keeps at least
/// CODE_MIN_PX. Both rails at their own maximum used to leave nothing between
/// them on a 1200px window — and the choice persisted, so every file after
/// that opened with no code visible.
const CODE_MIN_PX = 360;

function maxFor(root, pane, spec) {
    const grid = root.querySelector(spec.grid ?? ".oe");
    if (!grid) return spec.max;
    const others = grid.getBoundingClientRect().width - pane.getBoundingClientRect().width;
    return Math.min(spec.max, Math.max(spec.min, others - CODE_MIN_PX));
}

function readStoredWidth(spec) {
    if (spec.cookie) {
        const fromCookie = Number(readCookie(spec.cookie));
        if (Number.isFinite(fromCookie) && fromCookie > 0) {
            return clamp(fromCookie, spec.min, spec.max);
        }
    }

    let raw = null;
    try {
        raw = window.localStorage?.getItem(spec.key) ?? null;
    } catch {
        return null;
    }
    if (!raw) return null;
    const n = Number(raw);
    if (!Number.isFinite(n)) return null;
    // A width chosen before it moved into a cookie: carry it across on the
    // first read, so the next navigation is served at that width.
    if (spec.cookie) {
        writeCookie(spec.cookie, String(Math.round(n)));
        clearFlag(spec.key);
    }
    return clamp(n, spec.min, spec.max);
}

function storeWidth(spec, px) {
    if (spec.cookie) {
        writeCookie(spec.cookie, String(Math.round(px)));
        return;
    }
    try {
        window.localStorage?.setItem(spec.key, String(Math.round(px)));
    } catch {
        /* storage disabled — width still applies for the session. */
    }
}

// ── Explorer tree ────────────────────────────────────────────────
//
// The page ships the branch that leads to the open file and nothing
// else, because a Base Application module runs to thousands of files.
// Every other caret asks the server for its children the first time it
// is opened, then keeps them: re-closing hides rows rather than
// discarding them, so a second open is instant and the scroll position
// of a folder you have already been inside survives.

/// Makes a floating right-click menu usable from the keyboard and dismissable
/// exactly once. Marks it up as a menu, moves focus onto the first item,
/// wires ArrowUp/ArrowDown between items and Escape to close, and returns the
/// single `close(restoreFocus = true)` that removes the menu AND all three
/// document listeners. The dismiss listeners used to be `{ once: true }`,
/// which self-remove only when they FIRE — so dismissing by clicking an item
/// left both attached and a second right-click added two more (#718).
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
        // A keyboard user opened this from a row; leaving focus on a detached
        // menu button drops them back at the top of the document.
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

/// Binds a listener to an element that outlives the page it was wired on.
/// Blazor's enhanced navigation re-uses these elements across file hops —
/// only their contents and attributes are patched — so wiring them again per
/// navigation leaves the previous page's listener attached alongside the new
/// one and every gesture fires twice (#602; `wireSplit` guards the same way
/// for the drag handles, #535). Only the LISTENERS are once. State that has to
/// follow the new page — which rows arrived expanded, the grouping select's
/// value — is applied on every wiring, outside this.
function bindOnce(el, key, type, handler, options) {
    const flag = `__svBound_${key}`;
    if (el[flag]) return;
    el[flag] = true;
    el.addEventListener(type, handler, options);
}

/// bindOnce for a handler that closes over the page it was wired on.
///
/// Several of these handlers capture an editor id, a file id or a list of
/// sections gathered at wire-up. Binding those once would keep the FIRST
/// page's handler on a re-used element and drive the editor of the page before
/// last; binding them per wiring is the #602 double-fire. So the listener is
/// installed once and the handler it calls is replaced on every wiring (#717).
function bindOnceLatest(el, key, type, handler, options) {
    const slot = `__svHandler_${key}`;
    el[slot] = handler;
    bindOnce(el, key, type, e => el[slot]?.(e), options);
}

function wireExplorerTree(root) {
    const tree = root.querySelector(".sv-tree");
    if (!tree) return;

    // The branch leading to the open file arrives with its children already
    // in the DOM. Without this it looks unloaded, so closing and re-opening
    // one of those folders fetched a second copy of every child and inserted
    // it alongside the first.
    for (const open of tree.querySelectorAll('[data-tree-toggle][aria-expanded="true"]')) {
        open.dataset.treeLoaded = "1";
    }

    restoreOpenFolders(tree);

    bindOnce(tree, "tree-click", "click", async e => {
        const host = e.currentTarget;
        const row = e.target.closest("[data-tree-toggle]");
        if (!row || !host.contains(row)) return;
        e.preventDefault();
        await toggleTreeRow(host, row, host.dataset.viewRelease || "");
    });

    const collapse = root.querySelector(".sv-tree-collapse");
    if (collapse) {
        bindOnce(collapse, "tree-collapse", "click", () => {
            const live = collapse.closest(".source-viewer")?.querySelector(".sv-tree");
            if (live) collapseTree(live);
        });
    }

    wireExplorerVisibility(root);
    wireTreeGrouping(root, tree);
    wireTreeSearch(root, tree);
}

// ── Carrying the tree across a navigation ────────────────────────
//
// Opening a file is a real navigation, and the server renders the tree
// opened just far enough to show that one file. A reader who had opened
// three other apps lost all three on one click — the tear-down and
// rebuild is the flash, and the lost branches are the reason it matters.
//
// Blazor reuses the `.sv-tree` element and diffs its children, so there
// is nothing to carry over in the DOM; the rows are simply gone by the
// time any of this code runs. What survives is a note of which folders
// were open, re-applied on the way in.

const TREE_OPEN_KEY = "aldt.source-viewer.tree-open";

function openFolderKeys(tree) {
    return Array.from(tree.querySelectorAll('[data-tree-toggle][aria-expanded="true"]'))
        .map(r => `${r.dataset.treeModule}|${r.dataset.treePath ?? ""}`);
}

function rememberOpenFolders(tree) {
    const release = tree.dataset.releaseId;
    if (!release) return;
    try {
        window.sessionStorage?.setItem(
            `${TREE_OPEN_KEY}.${release}`, JSON.stringify(openFolderKeys(tree)));
    } catch {
        /* storage disabled - the tree just starts where the server left it. */
    }
}

/// Re-opens what the reader had open, shallowest first so a folder's
/// parent is already expanded by the time we look for it. Anything that
/// has since gone — a release re-imported, a folder renamed — simply is
/// not found, and is skipped.
async function restoreOpenFolders(tree) {
    const release = tree.dataset.releaseId;
    if (!release || (tree.dataset.grouping || "folder") !== "folder") return;

    let wanted;
    try {
        wanted = JSON.parse(window.sessionStorage?.getItem(`${TREE_OPEN_KEY}.${release}`) ?? "[]");
    } catch {
        return;
    }
    if (!Array.isArray(wanted) || wanted.length === 0) return;

    wanted.sort((a, b) => depthOfKey(a) - depthOfKey(b));
    for (const key of wanted) {
        const sep = key.indexOf("|");
        if (sep < 0) continue;
        const row = tree.querySelector(
            `[data-tree-toggle][data-tree-module="${CSS.escape(key.slice(0, sep))}"]`
            + `[data-tree-path="${CSS.escape(key.slice(sep + 1))}"]`);
        if (!row || row.getAttribute("aria-expanded") === "true") continue;
        await toggleTreeRow(tree, row, tree.dataset.viewRelease || "");
    }
}

function depthOfKey(key) {
    const path = key.slice(key.indexOf("|") + 1);
    return path === "" ? 0 : path.split("/").length;
}

// ── Explorer visibility ──────────────────────────────────────────
//
// The pane is navigation, so it is the first thing to give up room —
// and the reader has to be able to get it back. Below the width where
// three panes stop fitting it starts folded, and the toolbar button is
// the way in either way.
//
// The choice rides in a cookie rather than localStorage, for the same
// reason the grouping does: the server renders this pane. A folded pane
// the client alone knows about is rendered OPEN, painted, and folded a
// frame later — which is the flash on every navigation, and the reason
// the pane also sticks at its default width before the stored one lands
// (#603). The inline pre-paint script App.razor uses for the theme
// cannot stand in here: hopping between files is a Blazor enhanced
// navigation, and scripts in a patched response are not executed.
//
// The pre-#603 localStorage keys are still read once, to carry an
// existing reader's choice over into the cookie.

const EXPLORER_HIDDEN_COOKIE = "aldt-oe-explorer-hidden";
const EXPLORER_HIDDEN_KEY = "aldt.source-viewer.explorer-hidden";
const EXPLORER_MIN_PX = 1100;

function wireExplorerVisibility(root) {
    const button = root.querySelector(".sv-explorer-toggle");
    const grid = root.querySelector(".oe");
    if (!button || !grid) return;

    // A stored choice wins at any width, and the server has already applied
    // it to this response. With none, the viewport decides — and keeps
    // deciding, so dragging a window narrow folds the pane rather than
    // squeezing the code column to nothing.
    const stored = readExplorerHidden();
    applyExplorerHidden(grid, button, stored ?? window.innerWidth < EXPLORER_MIN_PX);

    wireExplorerDelegates();
}

function applyExplorerHidden(grid, button, hidden) {
    grid.classList.toggle("is-explorer-hidden", hidden);
    button.setAttribute("aria-pressed", hidden ? "false" : "true");
}

/// The live toggle button and grid, whichever viewer is on screen. Looked
/// up per event rather than captured, so a handler installed on one page
/// cannot end up driving a detached element on the next.
function explorerParts(node) {
    const root = node?.closest?.(".source-viewer") ?? document.querySelector(".source-viewer");
    const button = root?.querySelector(".sv-explorer-toggle");
    const grid = root?.querySelector(".oe");
    return button && grid ? { root, button, grid } : null;
}

function setExplorerHidden(parts, hidden) {
    applyExplorerHidden(parts.grid, parts.button, hidden);
    writeCookie(EXPLORER_HIDDEN_COOKIE, hidden ? "1" : "0");
}

// Below the three-pane width the pane is an overlay over the code with a
// scrim behind it (#569). An overlay you can only close from the toolbar is a
// detour with the exit in the wrong place, so the scrim and Escape both close
// it. Neither does anything at wide widths, where the pane is a track and the
// scrim is not rendered.
function isExplorerOverlaid(grid) {
    return window.innerWidth < EXPLORER_MIN_PX && !grid.classList.contains("is-explorer-hidden");
}

/// The toggle's handlers are installed on `document` once for the session,
/// not on the button once per page. Blazor's enhanced navigation re-uses the
/// button element across file hops — it is in .pw__head, which the response
/// only patches text into — so re-wiring it per navigation left the previous
/// page's listener attached alongside the new one. Both ran on one click, the
/// second read the class the first had just written and put it straight back,
/// and the button looked dead until a reload (#602). The document keydown and
/// window resize listeners were accumulating the same way.
let explorerDelegatesWired = false;

function wireExplorerDelegates() {
    if (explorerDelegatesWired) return;
    explorerDelegatesWired = true;

    document.addEventListener("click", (e) => {
        const button = e.target?.closest?.(".sv-explorer-toggle");
        if (button) {
            const parts = explorerParts(button);
            if (!parts) return;
            const hidden = !parts.grid.classList.contains("is-explorer-hidden");
            setExplorerHidden(parts, hidden);
            if (!hidden) parts.root.querySelector(".sv-tree-search")?.focus();
            return;
        }
        // The scrim is a pseudo-element, so its clicks land on .oe itself;
        // a click inside any real child is the reader using the pane.
        if (e.target?.classList?.contains("oe")) {
            const parts = explorerParts(e.target);
            if (parts && parts.grid === e.target && isExplorerOverlaid(parts.grid)) {
                setExplorerHidden(parts, true);
            }
        }
    });

    document.addEventListener("keydown", (e) => {
        if (e.key !== "Escape") return;
        const parts = explorerParts(e.target);
        if (!parts || !isExplorerOverlaid(parts.grid)) return;
        setExplorerHidden(parts, true);
        parts.button.focus();
    });

    window.addEventListener("resize", () => {
        // Only while the reader has expressed no choice — once they have, it
        // holds at every width.
        if (readCookie(EXPLORER_HIDDEN_COOKIE) !== null) return;
        const parts = explorerParts(null);
        if (parts) applyExplorerHidden(parts.grid, parts.button, window.innerWidth < EXPLORER_MIN_PX);
    });
}

/// True/false when the reader has chosen, null when they have not. Carries a
/// pre-#603 localStorage choice across into the cookie the first time it is
/// read, so the very next navigation is rendered folded by the server rather
/// than folding itself after paint.
function readExplorerHidden() {
    const cookie = readCookie(EXPLORER_HIDDEN_COOKIE);
    if (cookie === "1" || cookie === "0") return cookie === "1";

    const legacy = readFlag(EXPLORER_HIDDEN_KEY);
    if (legacy === null) return null;
    writeCookie(EXPLORER_HIDDEN_COOKIE, legacy ? "1" : "0");
    clearFlag(EXPLORER_HIDDEN_KEY);
    return legacy;
}

function readFlag(key) {
    try {
        const raw = window.localStorage?.getItem(key);
        return raw === null || raw === undefined ? null : raw === "1";
    } catch {
        return null;
    }
}

function clearFlag(key) {
    try {
        window.localStorage?.removeItem(key);
    } catch {
        /* storage disabled - nothing to clear. */
    }
}

// ── Grouping ─────────────────────────────────────────────────────
//
// A vendor's folder layout is somebody else's filing system, and a
// reader usually knows what KIND of object they are after rather than
// which folder it was filed in. Three arrangements of one app's files:
// its folders, its object kinds, or one flat list.
//
// The choice rides in a cookie, not localStorage, because the server
// renders this pane. Reading it there means a navigation paints the
// right arrangement immediately; restoring it client-side would flash
// through the folder view on every single click.

const TREE_GROUPING_COOKIE = "aldt-oe-grouping";

function wireTreeGrouping(root, tree) {
    const select = root.querySelector(".sv-tree-group select");
    if (!select) return;

    // Per wiring, because it has to follow the page: the response carries the
    // arrangement this file was rendered in.
    const collapse = root.querySelector(".sv-tree-collapse");
    const current = tree.dataset.grouping || "folder";
    select.value = current;
    if (collapse) collapse.hidden = current === "none";

    // Everything the handler touches is looked up again per change, from the
    // select outwards. It is bound once for the element's life (#602), and the
    // select outlives the rest: if a navigation ever re-uses it while replacing
    // the tree, the count or the collapse button, elements captured at wire-up
    // would be detached and the handler would rebuild a tree nobody can see -
    // and ask the server for the file id of the page before last. The search
    // box refreshes a state object per wiring for the same reason; the pieces
    // here are all reachable by selector, so they are just re-found.
    bindOnce(select, "tree-grouping", "change", async () => {
        const grouping = select.value;
        writeCookie(TREE_GROUPING_COOKIE, grouping);

        const liveRoot = select.closest(".source-viewer");
        const liveTree = liveRoot?.querySelector(".sv-tree");
        if (!liveTree) return;
        const liveCollapse = liveRoot.querySelector(".sv-tree-collapse");
        const liveCount = liveRoot.querySelector(".sv-tree-count");

        liveTree.dataset.grouping = grouping;
        if (liveCollapse) liveCollapse.hidden = grouping === "none";

        const moduleId = liveTree.dataset.moduleId;
        if (!moduleId) return;

        if (grouping === "folder") {
            // The folder view is the server's to build - it is the only one
            // that carries the other apps, and the branch down to this file.
            window.location.reload();
            return;
        }

        const rows = await fetchModuleTree(moduleId, grouping, liveRoot.dataset.fileId);
        if (rows === null) return;
        liveTree.replaceChildren(
            ...rows.map(r => buildTreeRow(r, r.depth ?? 0, liveTree.dataset.viewRelease || "")));
        if (liveCount) {
            const files = rows.filter(r => r.kind === "file").length;
            liveCount.textContent = `${files.toLocaleString()} ${files === 1 ? "file" : "files"}`;
        }
        liveTree.querySelector(".is-active")?.scrollIntoView({ block: "center" });
    });
}

function writeCookie(name, value) {
    // A year, path-wide, SameSite=Lax: it is a display preference, and it has
    // to be readable by the server render of any file page.
    document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; samesite=lax`;
}

/// The cookie's value, or null when it is not set.
function readCookie(name) {
    const prefix = `${name}=`;
    for (const part of document.cookie.split(";")) {
        const entry = part.trim();
        if (entry.startsWith(prefix)) {
            try {
                return decodeURIComponent(entry.slice(prefix.length));
            } catch {
                return null;
            }
        }
    }
    return null;
}

async function fetchModuleTree(moduleId, grouping, activeFileId) {
    try {
        const res = await fetch(
            `/api/object-explorer/modules/${encodeURIComponent(moduleId)}/tree`
            + `?grouping=${encodeURIComponent(grouping)}`
            + (activeFileId ? `&activeFileId=${encodeURIComponent(activeFileId)}` : ""),
            { headers: { Accept: "application/json" } });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch {
        return null;
    }
}

/// One place that asks the server for a folder's children, so the flat
/// toggle and the lazy carets cannot drift on the URL they build.
/// Returns null on failure; the caller decides what to say.
async function fetchTreeChildren(moduleId, path, flat) {
    try {
        const res = await fetch(
            `/api/object-explorer/modules/${encodeURIComponent(moduleId)}/tree`
            + `?path=${encodeURIComponent(path)}${flat ? "&flat=true" : ""}`,
            { headers: { Accept: "application/json" } });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return await res.json();
    } catch {
        return null;
    }
}

// ── Explorer search ──────────────────────────────────────────────
//
// Across the release, not across the rows on screen: the tree holds
// only what has been opened, so filtering it would search a handful of
// apps out of eighty-six. While the box has content the results replace
// the tree; clearing it puts the tree back exactly as it was, which is
// what makes the box safe to use mid-navigation.

const SEARCH_DEBOUNCE_MS = 220;

function wireTreeSearch(root, tree) {
    const input = root.querySelector(".sv-tree-search");
    const releaseId = root.querySelector(".sv-tree-tools")?.dataset.releaseId;
    if (!input || !releaseId) return;

    // Ctrl/Cmd+Shift+F, the way VS Code opens search-across-files. Free in
    // every browser we care about, unlike the plain Ctrl+F the editor already
    // takes for find-in-file. Reveals the pane first if it is folded away.
    //
    // On `window` rather than on the viewer root, for the same reason the
    // find-references and find-in-file shortcuts are: the viewer's CodeMirror
    // is read-only and its content element never takes focus, so most of the
    // time the key lands on `<body>` and a listener on the viewer root never
    // hears it. Bound once for the session and resolved against whatever
    // viewer is on screen — a listener per navigation, each holding the root
    // it was wired on, focused the box and toggled the pane once per hop.
    bindOnce(window, "tree-search-key", "keydown", e => {
        const mod = usesCommandKey() ? e.metaKey : e.ctrlKey;
        if (!mod || !e.shiftKey || e.altKey || e.key.toLowerCase() !== "f") return;
        const live = document.querySelector(".source-viewer");
        const box = live?.querySelector(".sv-tree-search");
        if (!box) return;
        e.preventDefault();
        const grid = live.querySelector(".oe");
        if (grid?.classList.contains("is-explorer-hidden")) {
            live.querySelector(".sv-explorer-toggle")?.click();
        }
        box.focus();
        box.select();
    });

    // The listeners below are bound once, but their state belongs to the page
    // on screen: the parked rows are THIS file's tree, and a navigation
    // replaced them. Re-created on every wiring and read through the element,
    // so the once-bound handlers always see the current page's.
    input.__svSearchState = { parked: null, timer: 0, generation: 0, tree, releaseId };

    const restore = () => {
        const st = input.__svSearchState;
        if (!st.parked) return;
        st.tree.replaceChildren(...st.parked);
        st.parked = null;
        st.tree.classList.remove("is-results");
    };

    const run = async (needle) => {
        const st = input.__svSearchState;
        const t = st.tree;
        const mine = ++st.generation;
        try {
            const res = await fetch(
                `/api/object-explorer/releases/${encodeURIComponent(st.releaseId)}/tree-search`
                + `?q=${encodeURIComponent(needle)}`,
                { headers: { Accept: "application/json" } });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const hits = await res.json();
            // A slower earlier search must not overwrite a faster later one.
            if (mine !== st.generation) return;
            if (st.parked === null) st.parked = Array.from(t.children);
            t.classList.add("is-results");
            if (hits.length === 0) {
                const empty = document.createElement("span");
                empty.className = "otree__row sv-tree-overflow";
                empty.textContent = `Nothing in this release matches "${needle}".`;
                t.replaceChildren(empty);
                return;
            }
            t.replaceChildren(...hits.map(h => buildTreeRow(h, 0, t.dataset.viewRelease || "")));
        } catch {
            if (mine !== st.generation) return;
            if (st.parked === null) st.parked = Array.from(t.children);
            t.classList.add("is-results");
            const failed = document.createElement("span");
            failed.className = "otree__row sv-tree-failed";
            failed.textContent = "Couldn't search this release. Try again.";
            t.replaceChildren(failed);
        }
    };

    bindOnce(input, "tree-search-input", "input", () => {
        const st = input.__svSearchState;
        clearTimeout(st.timer);
        const needle = input.value.trim();
        if (needle.length < 2) {
            st.generation++;          // abandon anything in flight
            restore();
            return;
        }
        st.timer = setTimeout(() => run(needle), SEARCH_DEBOUNCE_MS);
    });

    bindOnce(input, "tree-search-esc", "keydown", e => {
        if (e.key !== "Escape") return;
        // An EMPTY box has nothing to clear, so let Escape keep travelling and
        // mean whatever it means further out - closing the overlay explorer
        // (#569), which is focused here the moment it opens. Swallowing it
        // unconditionally made Escape a no-op the one time the reader most
        // wants it. Typed text still wins: clear first, close on the second
        // press, which is the order every layered Escape works in.
        if (input.value === "") return;
        e.stopPropagation();
        input.value = "";
        input.__svSearchState.generation++;
        restore();
    });
}

/// Closes every open folder. Deepest first, so each row's descendants are
/// still visible when its own walk computes them — closing the outermost one
/// first would hide the rest and leave them stuck reporting themselves open.
function collapseTree(tree) {
    const open = Array.from(tree.querySelectorAll('[data-tree-toggle][aria-expanded="true"]'));
    for (const row of open.reverse()) setTreeRowOpen(tree, row, false);
    // Back to the top. In a release with 86 apps the open branch is usually
    // off-screen, so collapsing it changed nothing the reader could see and
    // the button looked broken. Landing on the roots is the result.
    tree.parentElement?.scrollTo({ top: 0 });
}

async function toggleTreeRow(tree, row, viewRelease) {
    const open = row.getAttribute("aria-expanded") === "true";
    if (open) {
        setTreeRowOpen(tree, row, false);
        return;
    }

    if (row.dataset.treeLoaded === "1") {
        setTreeRowOpen(tree, row, true);
        return;
    }
    // A second click while the first fetch is in flight would insert the same
    // children twice.
    if (row.dataset.treeLoaded === "pending") return;

    // A retry starts from a clean row: drop the message the last failure left.
    row.nextElementSibling?.classList.contains("sv-tree-failed")
        && row.nextElementSibling.remove();

    row.dataset.treeLoaded = "pending";
    row.classList.add("is-loading");
    try {
        const children = await fetchTreeChildren(
            row.dataset.treeModule, row.dataset.treePath ?? "", false);
        if (children === null) throw new Error("fetch failed");
        const depth = Number(row.dataset.treeDepth || 0) + 1;
        const frag = document.createDocumentFragment();
        // Kept as an array: inserting a DocumentFragment MOVES its children
        // out of it, so reading frag.children after row.after() finds nothing.
        const built = children.map(child => buildTreeRow(child, depth, viewRelease));
        for (const el of built) frag.appendChild(el);
        row.after(frag);
        row.dataset.treeLoaded = "1";
        // If an ancestor was collapsed while this fetch was in flight, the row
        // itself is hidden — opening it now would leave its children on screen
        // with no visible parent, indented under nothing. Record the state and
        // let the ancestor's own re-open reveal them.
        if (row.hidden) {
            row.setAttribute("aria-expanded", "true");
            row.classList.add("is-open");
            for (const el of built) el.hidden = true;
        } else {
            setTreeRowOpen(tree, row, true);
        }
    } catch {
        // A failed fetch must not cache the failure, and it must not leave the
        // caret claiming to be open: the row stays closed so the very next
        // click is the retry the message promises, not a close.
        delete row.dataset.treeLoaded;
        const failed = document.createElement("span");
        failed.className = "otree__row sv-tree-failed";
        failed.style.setProperty("--d", String(Number(row.dataset.treeDepth || 0) + 1));
        failed.textContent = "Couldn't load this folder. Click the folder above to try again.";
        row.after(failed);
    } finally {
        row.classList.remove("is-loading");
    }
}

/// Opens or closes one row. Descendants are found by walking forward
/// while the depth stays greater than this row's — the tree is a flat
/// list, so there is no subtree to recurse into.
///
/// `closed` is a stack of the depths of collapsed folders we are still
/// inside, so re-opening a folder does not also re-open the ones the
/// reader had closed within it. It has to be popped on the way back
/// out: a plain "is this deeper than anything we closed" test keeps
/// hiding rows long after the collapsed folder's subtree has ended,
/// which hid every deep row that followed a closed one.
function setTreeRowOpen(tree, row, open) {
    row.setAttribute("aria-expanded", open ? "true" : "false");
    row.classList.toggle("is-open", open);
    rememberOpenFolders(tree);

    const depth = Number(row.dataset.treeDepth || 0);
    const closed = [];
    let node = row.nextElementSibling;
    while (node) {
        const nodeDepth = treeRowDepth(node);
        if (nodeDepth === null || nodeDepth <= depth) break;
        while (closed.length > 0 && closed[closed.length - 1] >= nodeDepth) closed.pop();
        node.hidden = !open || closed.length > 0;
        if (node.getAttribute("aria-expanded") === "false") closed.push(nodeDepth);
        node = node.nextElementSibling;
    }
}

function treeRowDepth(el) {
    if (!el || !el.classList.contains("otree__row")) return null;
    const raw = el.dataset.treeDepth ?? el.style.getPropertyValue("--d");
    const n = Number(String(raw).trim());
    return Number.isFinite(n) ? n : null;
}

/// The client-side twin of OeTreeRow.razor. Keep the two in step — the
/// server renders the open branch, this renders everything opened after
/// load, and a user cannot tell which row came from where.
function buildTreeRow(node, depth, viewRelease) {
    if (node.kind === "overflow") {
        const el = document.createElement("span");
        el.className = "otree__row sv-tree-overflow";
        el.style.setProperty("--d", String(depth));
        el.appendChild(spanWith("otree__caret"));
        const label = spanWith("otree__name");
        label.textContent = node.name;
        el.appendChild(label);
        return el;
    }

    const isFile = node.kind === "file";
    const el = document.createElement(isFile ? "a" : "button");
    el.className = "otree__row";
    if (node.kind === "module") el.classList.add("otree__row--app");
    el.style.setProperty("--d", String(depth));

    if (node.isActive) {
        el.classList.add("is-active");
        if (isFile) el.setAttribute("aria-current", "page");
    }

    if (isFile) {
        el.href = viewRelease
            ? `/object-explorer/file/${node.fileId}?from=${viewRelease}`
            : `/object-explorer/file/${node.fileId}`;
        el.dataset.fileId = String(node.fileId);
        // Same order as OeTreeRow.razor's Tooltip: the row's own name first,
        // because that is the part the column truncates.
        const parts = [node.name];
        if (node.fileName && node.fileName !== node.name) parts.push(node.fileName);
        if (node.objectKind) parts.push(node.objectKind);
        el.title = parts.join(" - ");
        el.appendChild(spanWith("otree__caret"));
        const glyph = okindGlyph(node.objectKind);
        if (glyph) {
            const badge = spanWith("okind " + okindTint(node.objectKind));
            badge.textContent = glyph;
            el.appendChild(badge);
        } else {
            el.appendChild(inlineIcon(FILE_CODE_ICON_SVG, "otree__ico"));
        }
    } else {
        el.type = "button";
        el.dataset.treeToggle = "";
        el.dataset.treeModule = String(node.moduleId);
        el.dataset.treePath = node.path ?? "";
        el.dataset.treeDepth = String(depth);
        // A `section` arrives with its files already beside it, so it opens
        // and closes without ever asking the server again.
        if (node.kind === "section") {
            el.dataset.treeLoaded = "1";
            el.classList.add("sv-tree-section");
        }
        el.setAttribute("aria-expanded", node.isOpen ? "true" : "false");
        if (node.isOpen) el.classList.add("is-open");
        // A caret-width blank rather than a caret when there is nothing to
        // open: a module whose .app shipped without source has no files, and a
        // chevron that expands to nothing is worse than no chevron.
        el.appendChild(node.hasChildren
            ? inlineIcon(CHEVRON_RIGHT_ICON_SVG, "otree__caret")
            : spanWith("otree__caret"));
        if (!node.hasChildren) delete el.dataset.treeToggle;
        el.appendChild(inlineIcon(
            node.kind === "module" ? PACKAGE_ICON_SVG : FOLDER_ICON_SVG, "otree__ico"));
    }

    const name = spanWith("otree__name");
    name.textContent = node.name;
    el.appendChild(name);

    if (node.badge) {
        const id = spanWith("otree__id");
        id.textContent = node.badge;
        el.appendChild(id);
    }
    return el;
}

function spanWith(className) {
    const el = document.createElement("span");
    el.className = className;
    return el;
}

/// Mirrors ObjectKindGlyph.For / .TintClass on the server. Pinned
/// against them by ObjectExplorerShellTests so the two cannot drift.
const OKIND_GLYPHS = {
    table: "T", page: "P", codeunit: "C", report: "R", query: "Q",
    xmlport: "X", enum: "E", interface: "I", permissionset: "PS",
    controladdin: "CA", tableextension: "TE", pageextension: "PE",
    reportextension: "RE", enumextension: "EE",
    permissionsetextension: "PSE", menusuite: "MS", profile: "PR",
};

const OKIND_TINTS = {
    table: "okind--tab", tableextension: "okind--tab",
    page: "okind--pag", pageextension: "okind--pag",
    codeunit: "okind--cod",
    report: "okind--rep", reportextension: "okind--rep",
};

function okindGlyph(kind) {
    return OKIND_GLYPHS[String(kind ?? "").toLowerCase()] ?? "";
}

function okindTint(kind) {
    return OKIND_TINTS[String(kind ?? "").toLowerCase()] ?? "";
}

// ── Modifier key labels ──────────────────────────────────────────
//
// The status line spells one modifier and the page is static SSR, so
// the server cannot know which. Rendered as Ctrl and corrected here
// rather than sniffed off the User-Agent.

function wireModifierKeyLabels(root) {
    if (!usesCommandKey()) return;
    for (const el of root.querySelectorAll("[data-mod-key]")) {
        el.textContent = "Cmd";
    }
}

function wireOutlineFilter(root) {
    const filter = root.querySelector(".sv-filter");
    if (!filter) return;
    const sections = Array.from(root.querySelectorAll(".sv-section"));
    const empty = root.querySelector(".sv-empty");

    // The section list is gathered per wiring, so the handler is replaced per
    // wiring while the listener stays bound once (#602/#717).
    bindOnceLatest(filter, "outline-filter", "input", () => {
        const needle = filter.value.trim().toLowerCase();
        // An empty filter means "show everything", including sections that hold
        // no matchable rows at all — an empty Used-by, or a dependency list
        // still loading. Deciding that per row leaves those hidden forever,
        // because the per-row loop never runs to un-hide them.
        if (needle.length === 0) {
            for (const section of sections) {
                section.hidden = false;
                for (const row of section.querySelectorAll(".sv-row")) row.hidden = false;
            }
            if (empty) empty.hidden = true;
            return;
        }
        let anyVisible = false;
        for (const section of sections) {
            const rows = Array.from(section.querySelectorAll(".sv-row"));
            let sectionVisible = false;
            for (const row of rows) {
                const name = (row.dataset.rowName ?? "").toLowerCase();
                const match = needle.length === 0 || name.includes(needle);
                row.hidden = !match;
                if (match) sectionVisible = true;
            }
            section.hidden = !sectionVisible;
            if (sectionVisible) anyVisible = true;
        }
        if (empty) empty.hidden = anyVisible || needle.length === 0;
    });
}

function wireSectionToggles(root) {
    const toggles = root.querySelectorAll(".sv-section__toggle");
    toggles.forEach(btn => {
        // Bound once (#602): two listeners on a re-used header would toggle the
        // class twice per click and the section would look dead (#717).
        bindOnce(btn, "section-toggle", "click", () => {
            const section = btn.parentElement;
            if (!section) return;
            const open = section.classList.toggle("is-open");
            btn.setAttribute("aria-expanded", open ? "true" : "false");
            const chevron = btn.querySelector(".otree__caret");
            if (chevron) chevron.classList.toggle("is-open", open);
            const list = section.querySelector(".sv-list");
            if (list) list.hidden = !open;
        });
    });
}

function wireSameFileLinks(root, editorId, fileId) {
    const links = root.querySelectorAll("a[data-line]");
    links.forEach(a => {
        // Bound once (#602): a second listener on a re-used link pushed two
        // history entries per click, so Back appeared to do nothing. The
        // handler carries this page's editor and file, so it is replaced per
        // wiring (#717).
        bindOnceLatest(a, "same-file-link", "click", e => {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            const line = Number(a.dataset.line);
            if (!Number.isFinite(line) || line < 1) return;
            e.preventDefault();
            scrollToLine(editorId, line, true);
            const url = `${FILE_URL_PREFIX}${fileId}?line=${line}${preservedQueryTail()}`;
            // pushState (not replace) so back button restores the prior
            // line position. Skip when URL is unchanged so back doesn't
            // press through duplicates.
            if (location.pathname + location.search !== url) {
                history.pushState(null, "", url);
            }
        });
    });
}

function preservedQueryTail() {
    const current = new URLSearchParams(location.search);
    current.delete("line");
    const rest = current.toString();
    return rest.length === 0 ? "" : `&${rest}`;
}

function parseJsonAttr(raw) {
    if (!raw) return null;
    try {
        return JSON.parse(raw);
    } catch (err) {
        console.warn("source-viewer: failed to parse data attribute", err);
        return null;
    }
}

/// First-load resilience: enhanced-nav into this page can run the module
/// before the patched .source-viewer DOM is queryable, AND Blazor's
/// enhanced-nav response diffing has been observed to skip script
/// execution on the first navigation entirely. Belt-and-braces:
///
///   1. Try init synchronously, plus across the first frame.
///   2. Listen for DOMContentLoaded for full page loads.
///   3. Listen for Blazor's `enhancedload` event for SPA-style navs.
///   4. Watch the body via MutationObserver — if .source-viewer
///      appears later (Blazor finishing its DOM patch after the
///      module loaded), init runs the moment it lands.
///
/// init() is idempotent thanks to its own cm-editor guard, so calling
/// it repeatedly is harmless. The MutationObserver stays alive for the
/// session so subsequent enhanced navs to other source-viewer pages
/// also fire it.
///
/// The 50 ms and 200 ms retries this used to carry were covering for the
/// observer below: it woke on every mutation in the document, so it could not
/// be trusted as the thing that catches a late DOM patch. Now that it wakes on
/// exactly the batches that add a .source-viewer, a root landing after this
/// frame is its job rather than a timer's (#717). The single rAF retry stays
/// for the case the observer cannot see: a root already in the DOM when the
/// module loaded but not yet queryable in this task.
function tryInit() {
    init();
    requestAnimationFrame(() => init());
}

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", tryInit, { once: true });
} else {
    tryInit();
}

if (typeof globalThis.Blazor !== "undefined" && globalThis.Blazor.addEventListener) {
    globalThis.Blazor.addEventListener("enhancedload", tryInit);
}

const RELEVANT_REMOVED = ".source-viewer, .cm-editor";

/// True when a mutation batch put a .source-viewer into the document or took a
/// mounted one out of it. Every other batch is none of this observer's
/// business, and on a large AL file that is nearly all of them: CodeMirror
/// recycles rows inside .cm-content as you scroll, so a single flick of the
/// wheel is a stream of childList mutations. The module also loads on every
/// page (App.razor), so the admin grids and the Translator wake it too.
/// Checking the added and removed nodes here keeps the document-wide
/// querySelectorAll below off the scroll path (#717) — the same shape
/// generate.js and theme.js use for their observers.
function touchesSourceViewer(mutations) {
    for (const m of mutations) {
        for (const node of m.addedNodes) {
            if (node.nodeType !== 1) continue;
            if (node.matches?.(".source-viewer") || node.querySelector?.(".source-viewer")) return true;
        }
        // Removals matter too, in two shapes: a navigation that replaced the
        // page's DOM outright (the whole root goes, and teardownDetachedMounts
        // has to hear about it even if the replacement lands in a later batch),
        // and the commoner one where the root is RE-USED and only the editor
        // inside it is diffed away — that root needs re-mounting and nothing
        // matching .source-viewer was added to announce it. Rows recycled by
        // CodeMirror while scrolling contain neither, which is the case this
        // whole check exists to drop.
        for (const node of m.removedNodes) {
            if (node.nodeType !== 1) continue;
            if (node.matches?.(RELEVANT_REMOVED) || node.querySelector?.(RELEVANT_REMOVED)) return true;
        }
    }
    return false;
}

// MutationObserver fallback. Fires whenever .source-viewer appears
// in the DOM — whether through enhanced nav, a full page load, or
// anything else. Cheap to keep alive: batches that touch no viewer are
// dropped before any document query, and we only re-call init when the
// editor isn't already mounted.
if (typeof MutationObserver !== "undefined") {
    const observer = new MutationObserver(mutations => {
        if (!touchesSourceViewer(mutations)) return;
        // A navigation that replaced the page's DOM outright leaves mounts
        // whose root is no longer in the document. Reap those first, so their
        // editors and listeners go before the replacement is mounted (#711).
        teardownDetachedMounts();
        // Any unmounted .source-viewer on the page triggers init.
        const hasUnmounted = Array.from(document.querySelectorAll(".source-viewer"))
            .some(r => !r.querySelector(".cm-editor"));
        if (hasUnmounted) init();
    });
    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    } else {
        // body not yet parsed — wait for DOMContentLoaded to attach.
        document.addEventListener("DOMContentLoaded", () => {
            observer.observe(document.body, { childList: true, subtree: true });
        }, { once: true });
    }
}
