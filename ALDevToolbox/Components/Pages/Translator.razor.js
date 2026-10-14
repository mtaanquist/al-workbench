// Companion module for the Translator page (/translator).
//
// Owns three browser-side concerns the C# component can't do itself:
//   1. IndexedDB persistence of the in-progress file. The working file can be
//      several MB — too big for localStorage's ~5 MB quota — so the verbatim
//      original XML + the edit overlay live in IndexedDB and survive a refresh
//      or a dropped SignalR circuit. Nothing is sent to the server until export.
//   2. The Alt-chord keyboard dispatcher (Poedit-style). Detected by physical
//      `event.code` + `event.altKey` so it behaves identically on Windows/Linux
//      and macOS, where Option turns number/letter keys into special characters.
//   3. The export hand-off: fills a hidden form from IndexedDB and submits it,
//      so the multi-MB original never travels over the Blazor connection.
//
// Multi-MB payloads cross the circuit via streaming, never as plain interop
// args: .NET -> JS uses a DotNetStreamReference (putBaseStream); JS -> .NET
// returns an ArrayBuffer that .NET reads as an IJSStreamReference
// (loadOriginalStream). Both bypass the 32 KB hub message limit.

const DB_NAME = "aldt-translator";
const STORE = "session";
const KEY = "current";

let keyHandler = null;
let dotNetRef = null;

function openDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE);
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

async function getRec() {
    try {
        const db = await openDb();
        return await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readonly");
            const req = tx.objectStore(STORE).get(KEY);
            req.onsuccess = () => resolve(req.result || null);
            req.onerror = () => reject(req.error);
        });
    } catch {
        return null;
    }
}

async function putRec(rec) {
    try {
        rec.savedAt = Date.now();
        const db = await openDb();
        await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readwrite");
            tx.objectStore(STORE).put(rec, KEY);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
        return true;
    } catch {
        return false;
    }
}

// Called once when a file is opened. The original XML arrives as a
// DotNetStreamReference so multi-MB files don't hit the interop arg limit.
// Resets the edit overlay.
export async function putBaseStream(fileName, sourceLang, targetLang, streamRef) {
    const buf = await streamRef.arrayBuffer();
    const originalXml = new TextDecoder("utf-8").decode(buf);
    await putRec({ fileName, originalXml, sourceLang, targetLang, edits: {} });
}

// Debounced incremental edit save (for resume). `editsJson` is the full overlay.
export async function putEdits(editsJson) {
    const rec = await getRec();
    if (!rec) return;
    try { rec.edits = JSON.parse(editsJson); } catch { return; }
    await putRec(rec);
}

export async function putMeta(fileName, targetLang) {
    const rec = await getRec();
    if (!rec) return;
    rec.fileName = fileName;
    rec.targetLang = targetLang;
    await putRec(rec);
}

// Lightweight check for the restore prompt — the file name + when it was last
// saved (epoch ms, 0 for sessions written before timestamps existed).
export async function peek() {
    const rec = await getRec();
    if (!rec) return null;
    return { fileName: rec.fileName, savedAt: rec.savedAt || 0 };
}

// Small metadata for an explicit restore (no original XML — that streams).
export async function loadMeta() {
    const rec = await getRec();
    if (!rec) return null;
    return {
        fileName: rec.fileName,
        sourceLang: rec.sourceLang || null,
        targetLang: rec.targetLang || null,
        edits: rec.edits || {},
    };
}

// The verbatim original, returned as an ArrayBuffer so .NET receives it as an
// IJSStreamReference and reads it as a stream (bypasses the hub size limit).
export async function loadOriginalStream() {
    const rec = await getRec();
    const xml = rec && rec.originalXml ? rec.originalXml : "";
    return new TextEncoder().encode(xml).buffer;
}

export async function clear() {
    try {
        const db = await openDb();
        await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readwrite");
            tx.objectStore(STORE).delete(KEY);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    } catch { /* ignore */ }
}

// Reads the verbatim original from IndexedDB, fills the hidden export form with
// it + the (authoritative) edit overlay + the chosen file name, and submits.
// The original never crosses the SignalR circuit — this is a plain browser POST
// that returns the file as a download.
export async function exportNow(fileName, editsJson) {
    const rec = await getRec();
    if (!rec || !rec.originalXml) return false;
    const form = document.getElementById("tr-export-form");
    if (!form) return false;
    // The form submit below is a navigation, which would otherwise fire the
    // unsaved-changes beforeunload prompt. Detach it first — exporting is a
    // deliberate save, not an accidental exit.
    setBeforeUnload(false);
    form.querySelector("#tr-export-filename").value = fileName;
    form.querySelector("#tr-export-original").value = rec.originalXml;
    form.querySelector("#tr-export-edits").value = editsJson;
    // The language being translated into. The server only uses it to name the
    // language of a file still marked as untranslated (one started from the
    // generated .g.xlf); every other file already says its language.
    const lang = form.querySelector("#tr-export-target-language");
    if (lang) lang.value = rec.targetLang || "";
    form.submit();
    return true;
}

// ── In-place file editing (File System Access API) ──────────────────────────
//
// On Chromium browsers a picked or dropped file yields a *writable*
// FileSystemFileHandle, so edits can be saved straight back to the original
// file on disk — no download-and-replace. The handle is structured-cloneable,
// so it lives in the same IndexedDB session record as everything else and
// survives a refresh. Firefox/Safari lack the API; the caller feature-detects
// with supportsFsAccess() and keeps the upload/download flow there.

export function supportsFsAccess() {
    return typeof window.showOpenFilePicker === "function";
}

// Reads a picked/dropped handle into a fresh session record and persists the
// handle for later in-place saves. Returns { fileName } or null.
async function adoptHandle(handle) {
    const file = await handle.getFile();
    const originalXml = await file.text();
    await putRec({ fileName: file.name, originalXml, sourceLang: null, targetLang: null, edits: {}, handle });
    return { fileName: file.name };
}

// Click-to-open on supported browsers. Wired directly to the button's click in
// initPicker (not through Blazor's @onclick) so showOpenFilePicker runs inside
// the user gesture — a SignalR round-trip could drop the transient activation
// it requires. Notifies C# via OpenFromHandleAsync once a file is picked.
let pickerTeardown = null;

export function initPicker(buttonSelector) {
    teardownPicker();
    const btn = document.querySelector(buttonSelector);
    if (!btn) return;
    const onClick = async () => {
        if (!supportsFsAccess()) return;
        let handle;
        try {
            [handle] = await window.showOpenFilePicker({
                multiple: false,
                types: [{ description: "XLIFF file", accept: { "application/xml": [".xlf", ".xliff"] } }],
            });
        } catch {
            // AbortError when the picker is dismissed — treat as a no-op.
            return;
        }
        const res = await adoptHandle(handle);
        if (res && dotNetRef) await dotNetRef.invokeMethodAsync("OpenFromHandleAsync", res.fileName);
    };
    btn.addEventListener("click", onClick);
    pickerTeardown = () => btn.removeEventListener("click", onClick);
}

export function teardownPicker() {
    if (pickerTeardown) { pickerTeardown(); pickerTeardown = null; }
}

// True when the current session was opened with a writable handle (so Save can
// write in place rather than download). Used after a refresh/restore.
export async function hasHandle() {
    const rec = await getRec();
    return !!(rec && rec.handle);
}

async function ensureWritePermission(handle) {
    const opts = { mode: "readwrite" };
    if (await handle.queryPermission(opts) === "granted") return true;
    // requestPermission must run inside a user gesture — this is always reached
    // from the Save click or the Ctrl/Cmd+S keydown, both of which qualify.
    if (await handle.requestPermission(opts) === "granted") return true;
    return false;
}

// Writes edited XLIFF back to the file the user opened. The byte-faithful merge
// still runs server-side (the same /api/translator/export endpoint the download
// path posts to) so XliffTargetWriter stays the single source of truth — we
// just fetch the result instead of navigating to it, then write the bytes to
// the handle. Returns a status: "saved" | "empty" | "no-handle" | "denied" | "error".
export async function saveInPlace(fileName, editsJson) {
    const rec = await getRec();
    if (!rec || !rec.originalXml) return "empty";
    if (!rec.handle) return "no-handle";
    const handle = rec.handle;
    // What the session record held when the save began, to tell afterwards
    // whether newer edits were persisted while the request was in flight.
    const editsAtStart = JSON.stringify(rec.edits || {});

    if (await ensureWritePermission(handle) !== true) return "denied";

    // Source the antiforgery token from the hidden export form (it renders an
    // <AntiforgeryToken />); the endpoint validates it the same way whether the
    // request arrives via form submit or fetch.
    const form = document.getElementById("tr-export-form");
    const token = form ? form.querySelector('input[name="__RequestVerificationToken"]') : null;
    const body = new FormData();
    body.append("FileName", fileName);
    body.append("OriginalXml", rec.originalXml);
    body.append("Edits", editsJson);
    if (token) body.append("__RequestVerificationToken", token.value);

    let bytes;
    try {
        const res = await fetch("/api/translator/export", { method: "POST", body });
        if (!res.ok) return "error";
        bytes = await res.arrayBuffer();
    } catch {
        return "error";
    }

    try {
        const writable = await handle.createWritable();
        await writable.write(bytes);
        await writable.close();
    } catch {
        return "error";
    }

    // Persist the just-saved overlay so a later refresh restores the saved
    // state - but from a fresh read, and only when nothing newer arrived while
    // the save was in flight. Writing back the record read at the start used
    // to overwrite edits typed during the save (#1268, L44); a newer overlay
    // already holds everything this one does, so it is left as it is.
    try {
        const latest = await getRec();
        if (latest && JSON.stringify(latest.edits || {}) === editsAtStart) {
            latest.edits = JSON.parse(editsJson);
            await putRec(latest);
        }
    } catch { /* best effort */ }
    return "saved";
}

// Browser-level guard against losing edits to a full reload, tab close, or a
// browser back that exits the SPA. In-app navigation goes through Blazor's
// LocationChangingHandler + a proper modal instead (see Translator.razor).
let beforeUnloadAttached = false;
function beforeUnloadHandler(e) {
    e.preventDefault();
    // Modern browsers ignore the message and show their own copy, but Chrome
    // still needs a non-empty returnValue to trigger the dialog.
    e.returnValue = "";
    return "";
}

export function setBeforeUnload(enabled) {
    if (enabled && !beforeUnloadAttached) {
        window.addEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = true;
    } else if (!enabled && beforeUnloadAttached) {
        window.removeEventListener("beforeunload", beforeUnloadHandler);
        beforeUnloadAttached = false;
    }
}

// #306 follow-up: a native <input type=file> does NOT reliably fire `change`
// when a file is *dropped* on it — Firefox never does, and in every browser a
// drop that lands even a pixel outside the input is ignored. The browser then
// falls back to its default action for a file dropped on the page: navigate to
// it, which the user sees as the file "downloading". So we wire the drop
// ourselves: stash the dropped file on the hidden input via a DataTransfer and
// dispatch `change`, which runs Blazor's InputFile -> OnUploadAsync exactly as
// a click-to-browse pick would. A window-level guard swallows drops that miss
// the zone so an off-target drop can't navigate the page and lose the session.
let dropTeardown = null;

export function initDropZone(zoneSelector, inputSelector) {
    teardownDropZone();
    const zone = document.querySelector(zoneSelector);
    // On supported browsers the click-to-open control is a picker button, not
    // an <input type=file>, so the input may be absent — the handle-based drop
    // path below doesn't need it. Only the Firefox/Safari fallback does. Query
    // it lazily at drop time so a re-render swapping the control doesn't leave
    // us holding a stale reference.
    if (!zone) return;

    // Routes a dropped File through the InputFile upload pipeline (Firefox/
    // Safari, or when no writable handle could be obtained). No-op if the input
    // isn't in the DOM.
    const useFileInput = (file) => {
        const input = document.querySelector(inputSelector);
        if (!input || !file) return;
        const dt = new DataTransfer();
        dt.items.add(file);
        input.files = dt.files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
    };

    const swallow = (e) => { e.preventDefault(); };
    const onDragEnter = (e) => { e.preventDefault(); zone.classList.add("is-over"); };
    const onDragOver = (e) => {
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = "copy";
    };
    const onDragLeave = (e) => {
        // Child elements bubble their own dragleave; only clear the highlight
        // when the pointer has actually left the zone's subtree.
        if (!zone.contains(e.relatedTarget)) zone.classList.remove("is-over");
    };
    const onDrop = (e) => {
        e.preventDefault();
        zone.classList.remove("is-over");
        // Capture both views synchronously — dataTransfer goes inert the moment
        // this handler returns, so the handle grab and the File fallback must
        // both be taken before any await.
        const files = e.dataTransfer && e.dataTransfer.files;
        const droppedFile = files && files.length ? files[0] : null;
        const items = e.dataTransfer && e.dataTransfer.items;
        const item = items && items.length ? items[0] : null;

        // On supported browsers a dropped item yields the same writable handle
        // the picker would, so the dropped file becomes editable in place.
        if (supportsFsAccess() && item && typeof item.getAsFileSystemHandle === "function") {
            item.getAsFileSystemHandle().then(async (handle) => {
                if (handle && handle.kind === "file" && dotNetRef) {
                    const res = await adoptHandle(handle);
                    if (res) { await dotNetRef.invokeMethodAsync("OpenFromHandleAsync", res.fileName); return; }
                }
                useFileInput(droppedFile);
            }).catch(() => useFileInput(droppedFile));
            return;
        }
        useFileInput(droppedFile);
    };

    zone.addEventListener("dragenter", onDragEnter);
    zone.addEventListener("dragover", onDragOver);
    zone.addEventListener("dragleave", onDragLeave);
    zone.addEventListener("drop", onDrop);
    window.addEventListener("dragover", swallow);
    window.addEventListener("drop", swallow);

    dropTeardown = () => {
        zone.removeEventListener("dragenter", onDragEnter);
        zone.removeEventListener("dragover", onDragOver);
        zone.removeEventListener("dragleave", onDragLeave);
        zone.removeEventListener("drop", onDrop);
        window.removeEventListener("dragover", swallow);
        window.removeEventListener("drop", swallow);
    };
}

export function teardownDropZone() {
    if (dropTeardown) { dropTeardown(); dropTeardown = null; }
}

export function initKeys(ref) {
    detachKeys();
    dotNetRef = ref;
    keyHandler = (ev) => {
        // Ctrl/Cmd+S → in-place save. Swallow the browser's own save dialog;
        // the C# handler no-ops gracefully when there's no writable handle.
        if ((ev.ctrlKey || ev.metaKey) && !ev.altKey && (ev.code === "KeyS" || ev.key === "s" || ev.key === "S")) {
            ev.preventDefault();
            // Fire-and-forget: a dropped circuit rejects here, and an unhandled
            // rejection per keypress would bury real errors in the console.
            dotNetRef.invokeMethodAsync("SaveFromKey").catch(() => {});
            return;
        }
        if (!ev.altKey || ev.ctrlKey || ev.metaKey) return;
        if (ev.code === "Enter") { ev.preventDefault(); dotNetRef.invokeMethodAsync("SaveAndNextFromKey").catch(() => {}); return; }
        if (ev.code === "ArrowUp") { ev.preventDefault(); dotNetRef.invokeMethodAsync("NavFromKey", -1).catch(() => {}); return; }
        if (ev.code === "ArrowDown") { ev.preventDefault(); dotNetRef.invokeMethodAsync("NavFromKey", 1).catch(() => {}); return; }
        // #307: Alt+Left/Right are the browser's Back/Forward on Windows/Linux —
        // swallow them so an accidental press doesn't throw the translator off
        // the page mid-edit. They are deliberately NOT unit navigation (that
        // stays on Alt+Up/Down).
        if (ev.code === "ArrowLeft" || ev.code === "ArrowRight") { ev.preventDefault(); return; }
        const m = /^Digit([1-9])$/.exec(ev.code);
        if (m) { ev.preventDefault(); dotNetRef.invokeMethodAsync("ApplySuggestionFromKey", parseInt(m[1], 10)).catch(() => {}); }
    };
    document.addEventListener("keydown", keyHandler);
}

// #304: move the cursor into the target box for the current selection and put
// it at the end of any existing text, so the user can start typing immediately
// after picking a unit. List view only — there's no .tr-tgtarea in grid view,
// where the clicked cell already holds focus.
export function focusTarget() {
    const el = document.querySelector(".tr-tgtarea");
    if (!el) return;
    el.focus();
    const end = el.value.length;
    try { el.setSelectionRange(end, end); } catch { /* not all inputs support it */ }
}

export function detachKeys() {
    if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
    }
    dotNetRef = null;
}

// ── Editor-rail resizer ───────────────────────────────────────────────────
//
// The .pw-split handle between the units pane and the editor. The width lives
// in a CSS custom property on .tr-split rather than in component state, so a
// drag is a style write per pointermove and never a SignalR round-trip. The
// chosen width is remembered per browser; the server only learns about it on
// the next page load, which is all it needs it for.
const SPLIT_KEY = "aldt-translator-rail";
const SPLIT_MIN = 300;
const SPLIT_MAX = 720;
let splitCleanup = null;

const clampRail = (px) => Math.max(SPLIT_MIN, Math.min(SPLIT_MAX, Math.round(px)));

export function initSplit() {
    teardownSplit();
    const handle = document.querySelector("[data-tr-split]");
    const split = handle?.closest(".tr-split");
    if (!handle || !split) return;

    // The stacked layout below 1100px resizes rows, not columns, and the
    // stored width would fight the percentage track. Leave it to the CSS.
    const horizontal = () => window.matchMedia("(min-width: 1101px)").matches;

    const apply = (px) => split.style.setProperty("--tr-rail", clampRail(px) + "px");
    const stored = Number(localStorage.getItem(SPLIT_KEY));
    if (stored > 0 && horizontal()) apply(stored);

    const onMove = (e) => {
        if (!horizontal()) return;
        apply(split.getBoundingClientRect().right - e.clientX);
        e.preventDefault();
    };
    const onUp = () => {
        handle.releasePointerCapture?.(handle.dataset.trPointer);
        delete handle.dataset.trPointer;
        document.removeEventListener("pointermove", onMove);
        document.removeEventListener("pointerup", onUp);
        document.body.style.removeProperty("user-select");
        const w = split.style.getPropertyValue("--tr-rail");
        if (w) localStorage.setItem(SPLIT_KEY, parseInt(w, 10));
    };
    const onDown = (e) => {
        if (!horizontal()) return;
        handle.dataset.trPointer = e.pointerId;
        handle.setPointerCapture?.(e.pointerId);
        // Without this a drag selects the strings either side of the handle.
        document.body.style.userSelect = "none";
        document.addEventListener("pointermove", onMove);
        document.addEventListener("pointerup", onUp);
    };
    // Keyboard: the handle is a focusable separator, so arrows must move it.
    const onKey = (e) => {
        const step = e.shiftKey ? 48 : 16;
        const now = parseInt(split.style.getPropertyValue("--tr-rail"), 10)
            || split.querySelector(".pane:last-child")?.getBoundingClientRect().width
            || 420;
        if (e.key === "ArrowLeft") apply(now + step);
        else if (e.key === "ArrowRight") apply(now - step);
        else return;
        e.preventDefault();
        localStorage.setItem(SPLIT_KEY, parseInt(split.style.getPropertyValue("--tr-rail"), 10));
    };

    handle.addEventListener("pointerdown", onDown);
    handle.addEventListener("keydown", onKey);
    splitCleanup = () => {
        handle.removeEventListener("pointerdown", onDown);
        handle.removeEventListener("keydown", onKey);
        document.removeEventListener("pointermove", onMove);
        document.removeEventListener("pointerup", onUp);
        document.body.style.removeProperty("user-select");
    };
}

export function teardownSplit() {
    if (splitCleanup) { splitCleanup(); splitCleanup = null; }
}
