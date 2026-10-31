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
// Before #1609 the whole site shared one record under this key. One left over
// from then is moved to a key of its own the first time the page looks.
const LEGACY_KEY = "current";
// Sessions nobody has open are kept for the restore prompt, newest first, up
// to this many; older ones and ones past the page's age limit are deleted.
const MAX_KEPT = 5;

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

// Runs `fn(store, setResult)` in one transaction and resolves with what it set
// once the transaction has committed.
async function withStore(mode, fn) {
    const db = await openDb();
    try {
        return await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, mode);
            let result;
            fn(tx.objectStore(STORE), (v) => { result = v; });
            tx.oncomplete = () => resolve(result);
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error);
        });
    } finally {
        db.close();
    }
}

// ── One record per open (#1595, #1609) ───────────────────────────────────
//
// Every open - an upload, a file from disk, a file from a repository, or a
// restore - gets a random id, and its record is stored under that id. The tab
// passes the id back on every later call, so one tab only ever reads or writes
// its own record: a second tab opening a file, saving or closing touches
// nothing of the first tab's.
//
// The restore prompt is the one place a tab looks at records it did not open.
// It must never offer, discard or take over a record a live tab still holds,
// so each tab holds a Web Lock named for its open while it has the file, and
// the prompt skips records whose lock is held. The browser drops a tab's locks
// when the tab closes or reloads, which is exactly when its record becomes a
// session worth offering. Continue moves the record to a new id, so the tab
// that restores it never shares an id with anyone.
//
// Where Web Locks are missing (a page served over plain http), a heartbeat in
// localStorage stands in: a record whose heartbeat is recent counts as held.

const LOCK_PREFIX = "aldt-translator-open:";
const ALIVE_PREFIX = "aldt-translator-alive:";
const ALIVE_EVERY_MS = 20_000;
// Hidden tabs may run timers only once a minute, so allow a few missed beats.
const ALIVE_FOR_MS = 3 * 60_000;

// The open this tab holds: { openId, release }.
let held = null;

function newOpenId() {
    return (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function")
        ? crypto.randomUUID()
        : Date.now().toString(36) + Math.random().toString(36).slice(2);
}

function hasLocks() {
    return typeof navigator !== "undefined" && !!navigator.locks
        && typeof navigator.locks.request === "function" && typeof navigator.locks.query === "function";
}

// Takes the lock (or starts the heartbeat) for `openId` and returns the hold.
// Does not touch `held`; callers swap it in once the record is written.
async function hold(openId) {
    const h = { openId, release: () => {} };
    if (hasLocks()) {
        await new Promise((granted) => {
            navigator.locks.request(LOCK_PREFIX + openId, () => new Promise((done) => {
                h.release = done;
                granted();
            })).catch(() => {
                // The lock could not be taken at all: hold it the other way
                // rather than not at all.
                heartbeat(h);
                granted();
            });
        });
    } else {
        heartbeat(h);
    }
    return h;
}

// The stand-in for a lock: a timestamp in localStorage, refreshed while the
// tab holds the open and removed when it lets go - or when the page goes
// away, so a reload does not hide the tab's own session from itself.
function heartbeat(h) {
    const key = ALIVE_PREFIX + h.openId;
    const beat = () => { try { localStorage.setItem(key, String(Date.now())); } catch { /* ignore */ } };
    const gone = () => { try { localStorage.removeItem(key); } catch { /* ignore */ } };
    beat();
    const timer = setInterval(beat, ALIVE_EVERY_MS);
    // A page restored from the back-forward cache holds the open again.
    const back = (e) => { if (e.persisted) beat(); };
    window.addEventListener("pagehide", gone);
    window.addEventListener("pageshow", back);
    h.release = () => {
        clearInterval(timer);
        window.removeEventListener("pagehide", gone);
        window.removeEventListener("pageshow", back);
        gone();
    };
}

// The ids with a recent heartbeat; stale ones are removed on the way.
function heartbeatIds() {
    const ids = new Set();
    try {
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const key = localStorage.key(i);
            if (!key || !key.startsWith(ALIVE_PREFIX)) continue;
            if (Date.now() - Number(localStorage.getItem(key)) < ALIVE_FOR_MS) ids.add(key.slice(ALIVE_PREFIX.length));
            else localStorage.removeItem(key);
        }
    } catch { /* ignore */ }
    return ids;
}

// The ids some tab (this one included) holds right now, by lock or heartbeat.
async function heldIds() {
    const ids = heartbeatIds();
    if (held) ids.add(held.openId);
    if (hasLocks()) {
        try {
            const state = await navigator.locks.query();
            for (const l of state.held || []) {
                if (l.name && l.name.startsWith(LOCK_PREFIX)) ids.add(l.name.slice(LOCK_PREFIX.length));
            }
        } catch { /* ignore */ }
    }
    return ids;
}

// Runs `fn` only while no live tab holds `openId`, and keeps others from
// taking it until `fn` is done. Returns null without running it otherwise.
async function whileUnheld(openId, fn) {
    if (!openId || (held && held.openId === openId)) return null;
    if (hasLocks()) {
        return await navigator.locks.request(LOCK_PREFIX + openId, { ifAvailable: true },
            (lock) => (lock && !heartbeatIds().has(openId) ? fn() : null));
    }
    return (await heldIds()).has(openId) ? null : await fn();
}

// Starts a new open: takes its lock first, so no other tab ever sees the new
// record unheld, then runs `write(previousId)` - which stores the record and
// deletes the tab's previous one, if any - and lets go of the previous lock
// only after that. Returns false (holding nothing new) when the write failed.
async function startOpen(openId, write) {
    const previous = held;
    const next = await hold(openId);
    try {
        await write(previous ? previous.openId : null);
    } catch {
        next.release();
        return false;
    }
    held = next;
    if (previous) previous.release();
    return true;
}

// Lets go of this tab's open without deleting its record. Called when the page
// goes away inside the app: whatever was left unsaved becomes a session the
// restore prompt can offer. Also called by a page that starts with no file
// before it looks for sessions: the module outlives the component (a resumed
// connection renders a fresh one in the same tab), so it may still hold the
// open the previous component had, which would hide that session from it.
export function release() {
    if (held) { held.release(); held = null; }
}

// This open's record, or null (also when `openId` is missing).
async function getOwnRec(openId) {
    if (!openId) return null;
    try {
        return await withStore("readonly", (store, done) => {
            const req = store.get(openId);
            req.onsuccess = () => {
                const rec = req.result;
                done(rec && rec.openId === openId ? rec : null);
            };
        });
    } catch {
        return null;
    }
}

// Reads, changes and writes this open's record in one transaction. `change`
// returns false to leave the record as it is. True when it was written.
async function updateRec(openId, change) {
    if (!openId) return false;
    try {
        return await withStore("readwrite", (store, done) => {
            const req = store.get(openId);
            req.onsuccess = () => {
                const rec = req.result;
                if (!rec || rec.openId !== openId || change(rec) === false) { done(false); return; }
                rec.savedAt = Date.now();
                store.put(rec, openId);
                done(true);
            };
        });
    } catch {
        return false;
    }
}

// For export and save: "missing" when this tab holds an id but its record is
// gone (the browser's storage was cleared, say), "empty" when it holds none,
// and null when the record is there.
function notOwned(rec, openId) {
    if (rec && openId && rec.openId === openId) return null;
    return openId ? "missing" : "empty";
}

// Stores a new open's record and deletes the tab's previous one in the same
// transaction: the tab has moved on from that file. Then drops the older
// copies of the same file nobody holds (#1624).
async function openRecord(rec, dropOlder = true) {
    rec.savedAt = Date.now();
    const ok = await startOpen(rec.openId, (previousId) => withStore("readwrite", (store) => {
        store.put(rec, rec.openId);
        if (previousId && previousId !== rec.openId) store.delete(previousId);
    }));
    if (ok && dropOlder) await dropOlderCopies(rec);
    return ok;
}

// ── One session per file (#1624) ─────────────────────────────────────────
//
// A stored session carries the base the file had when it was opened. Once the
// same file has been opened again - picked from disk, uploaded, fetched from
// its repository, or restored - an older session of it is out of date: its
// base predates whatever was saved since, so Continue followed by Save would
// write that old base back over the file and lose the newer work. Before
// #1609 the single record was simply replaced; with one record per open, the
// older copies have to be dropped by file. A copy a live tab holds is never
// touched (whileUnheld), as everywhere else.

// Which file a record is of, by name - and for a repository file also by
// repository and path. The name is part of it because a translation started
// from the generated file is named for its language, while the path it was
// opened under is not updated when the language changes: a da-DK and a de-DE
// translation of one App.g.xlf are different files.
function fileKey(r) {
    const name = "\n" + (r.fileName || "");
    if (r.repo && r.repo.repository && r.repo.path) return "repo\n" + r.repo.repository + "\n" + r.repo.path + name;
    return "file" + name;
}

// True when two records are of the same file. Two files picked from disk can
// share a name and still be different files (the same app in two folders), so
// when both have a handle the handle decides; an upload has only its name.
async function sameFile(a, b) {
    if (fileKey(a) !== fileKey(b)) return false;
    if (a.handle && b.handle) {
        if (typeof a.handle.isSameEntry !== "function") return false;
        try { return await a.handle.isSameEntry(b.handle); } catch { return false; }
    }
    return true;
}

// True when another record of `r`'s file in `all` was saved after it.
async function superseded(r, all) {
    for (const o of all) {
        if (!o || !o.openId || o.openId === r.openId) continue;
        if ((o.savedAt || 0) > (r.savedAt || 0) && await sameFile(o, r)) return true;
    }
    return false;
}

// Deletes the other sessions of `rec`'s file saved before it that no live tab
// holds. A newer one is left alone: Continue on an older session must not
// delete newer work.
async function dropOlderCopies(rec) {
    try {
        const live = await heldIds();
        const all = await withStore("readonly", (store, done) => {
            const req = store.getAll();
            req.onsuccess = () => done(req.result || []);
        });
        for (const r of all) {
            if (!r || !r.openId || r.openId === rec.openId || live.has(r.openId)) continue;
            if ((r.savedAt || 0) > (rec.savedAt || 0)) continue;
            if (await sameFile(r, rec)) await whileUnheld(r.openId, () => deleteKey(r.openId));
        }
    } catch { /* best effort: the prompt also offers only the newest of each file */ }
}

// For a file picked or dropped from disk, which is stored before anything
// parses it: drops the older copies of its file only once the page has
// opened it, so a file that fails to open touches no other session.
export async function opened(openId) {
    const rec = await getOwnRec(openId);
    if (rec) await dropOlderCopies(rec);
}

// Called once when a file is opened. The original XML arrives as a
// DotNetStreamReference so multi-MB files don't hit the interop arg limit.
// Resets the edit overlay. `repo` ({ repository, path }) is where a file opened
// from a repository came from, null otherwise: a restored session has to know,
// so its export still teaches the translation memory nothing (#1540).
// Returns the open's id, which the tab passes back on every later call, or
// null when nothing could be stored.
// `replaceOpenId`, when passed, rebases the tab's own record rather than opening
// a file: it keeps the id, and writes only for the open this tab holds (or
// whose record is still there), returning null and writing nothing otherwise.
// A repository save so also puts back a record the browser lost.
export async function putBaseStream(fileName, sourceLang, targetLang, streamRef, repo, replaceOpenId) {
    const rebase = arguments.length > 5;
    if (rebase && !(held && held.openId === replaceOpenId) && !(await getOwnRec(replaceOpenId))) return null;
    const buf = await streamRef.arrayBuffer();
    const originalXml = new TextDecoder("utf-8").decode(buf);
    if (rebase) {
        const rec = { openId: replaceOpenId, fileName, originalXml, sourceLang, targetLang, edits: {}, repo: repo || null, savedAt: Date.now() };
        try {
            await withStore("readwrite", (store) => { store.put(rec, replaceOpenId); });
            return replaceOpenId;
        } catch {
            return null;
        }
    }
    const openId = newOpenId();
    return await openRecord({ openId, fileName, originalXml, sourceLang, targetLang, edits: {}, repo: repo || null })
        ? openId : null;
}

// Debounced incremental edit save (for resume). `editsJson` is the full overlay;
// `learnedJson` is what each unit last taught the translation memory, kept
// beside it so a reload does not teach the same units again (#1343).
// `openId` is the open this tab holds; the record is read and written in one
// transaction, so nothing can slip in between.
export async function putEdits(openId, editsJson, learnedJson) {
    let edits, learned;
    try {
        edits = JSON.parse(editsJson);
        learned = learnedJson ? JSON.parse(learnedJson) : null;
    } catch { return; }
    await updateRec(openId, (rec) => {
        rec.edits = edits;
        if (learned) rec.learned = learned;
    });
}

export async function putMeta(openId, fileName, targetLang) {
    await updateRec(openId, (rec) => {
        rec.fileName = fileName;
        rec.targetLang = targetLang;
    });
}

// Moves a record from before #1609 to a key of its own, once, in one
// transaction, so two tabs loading at the same moment cannot both move it.
// It keeps its savedAt, so the prompt says how old it really is.
async function migrateLegacy() {
    await withStore("readwrite", (store) => {
        const req = store.get(LEGACY_KEY);
        req.onsuccess = () => {
            const rec = req.result;
            if (!rec) return;
            store.delete(LEGACY_KEY);
            if (!rec.fileName || !rec.originalXml) return;
            rec.openId = newOpenId();
            store.put(rec, rec.openId);
        };
    });
}

function deleteKey(key) {
    return withStore("readwrite", (store) => { store.delete(key); }).catch(() => {});
}

// For the restore prompt: the newest session no live tab holds, as its id,
// file name and when it was last saved (epoch ms). `maxAgeMs` is how old a
// session may be and still be offered. Sessions nobody holds that are past
// it, or beyond the newest MAX_KEPT, or older than another record of the same
// file, are deleted here, so the store does not grow with every file ever
// opened and an out-of-date copy is never offered.
export async function peek(maxAgeMs) {
    try {
        await migrateLegacy();
        const live = await heldIds();
        const all = await withStore("readonly", (store, done) => {
            const req = store.getAll();
            req.onsuccess = () => done(req.result || []);
        });
        const now = Date.now();
        const free = all.filter((r) => r && r.openId && !live.has(r.openId));
        // A session with a newer record of its file, held or not, is out of
        // date, so it is never offered and goes with the rest (#1624).
        const current = [];
        for (const r of free) {
            if (!(await superseded(r, all))) current.push(r);
        }
        const offerable = current
            .filter((r) => r.fileName && r.savedAt > 0 && now - r.savedAt <= maxAgeMs)
            .sort((a, b) => b.savedAt - a.savedAt);
        const keep = new Set(offerable.slice(0, MAX_KEPT).map((r) => r.openId));
        for (const r of free) {
            if (!keep.has(r.openId)) await whileUnheld(r.openId, () => deleteKey(r.openId));
        }
        const top = offerable[0];
        return top ? { openId: top.openId, fileName: top.fileName, savedAt: top.savedAt } : null;
    } catch {
        return null;
    }
}

// Continue on the restore prompt: takes over the session `openId` names, as
// long as no live tab holds it, by moving its record to a fresh id this tab
// then holds. Returns the small metadata (the original XML streams separately)
// with the new id, or { refused } saying why nothing was taken over: "held"
// when another tab has it, "gone" when it was deleted since the prompt
// appeared, "error" when the browser's storage failed.
export async function loadMeta(openId) {
    try {
        const result = await whileUnheld(openId, async () => {
            const rec = await getOwnRec(openId);
            if (!rec) return { refused: "gone" };
            const newId = newOpenId();
            const moved = { ...rec, openId: newId };
            const ok = await startOpen(newId, (previousId) => withStore("readwrite", (store) => {
                store.put(moved, newId);
                store.delete(openId);
                if (previousId) store.delete(previousId);
            }));
            if (!ok) return { refused: "error" };
            await dropOlderCopies(moved);
            return {
                openId: newId,
                fileName: rec.fileName,
                sourceLang: rec.sourceLang || null,
                targetLang: rec.targetLang || null,
                edits: rec.edits || {},
                learned: rec.learned || null,
                repo: rec.repo || null,
            };
        });
        return result || { refused: "held" };
    } catch {
        return { refused: "error" };
    }
}

// The verbatim original, returned as an ArrayBuffer so .NET receives it as an
// IJSStreamReference and reads it as a stream (bypasses the hub size limit).
// Empty when this open's record is gone.
export async function loadOriginalStream(openId) {
    const rec = await getOwnRec(openId);
    const xml = rec && rec.originalXml ? rec.originalXml : "";
    return new TextEncoder().encode(xml).buffer;
}

// Deletes a session's record. For the open this tab holds (Close), it also
// lets go of it. For any other (Discard on the restore prompt), only while no
// live tab holds it. True when the record was deleted.
export async function clear(openId) {
    if (!openId) return false;
    if (held && held.openId === openId) {
        await deleteKey(openId);
        release();
        return true;
    }
    try {
        return (await whileUnheld(openId, async () => { await deleteKey(openId); return true; })) === true;
    } catch {
        return false;
    }
}

// Reads the verbatim original from IndexedDB, fills the hidden export form with
// it + the (authoritative) edit overlay + the chosen file name, and submits.
// The original never crosses the SignalR circuit — this is a plain browser POST
// that returns the file as a download.
// learnJson lists the unit ids the translation memory should learn from this
// export: the edits whose text changed since the last save (#1343).
// Returns "exported", "empty", or "missing" when this open's record is gone,
// in which case nothing is posted.
export async function exportNow(openId, fileName, editsJson, learnJson) {
    const rec = await getOwnRec(openId);
    const refused = notOwned(rec, openId);
    if (refused) return refused;
    if (!rec.originalXml) return "empty";
    const form = document.getElementById("tr-export-form");
    if (!form) return "empty";
    // The form submit below is a navigation, which would otherwise fire the
    // unsaved-changes beforeunload prompt. Detach it first — exporting is a
    // deliberate save, not an accidental exit.
    setBeforeUnload(false);
    form.querySelector("#tr-export-filename").value = fileName;
    form.querySelector("#tr-export-original").value = rec.originalXml;
    form.querySelector("#tr-export-edits").value = editsJson;
    const learn = form.querySelector("#tr-export-learn");
    if (learn) learn.value = learnJson || "[]";
    // The language being translated into. The server only uses it to name the
    // language of a file still marked as untranslated (one started from the
    // generated .g.xlf); every other file already says its language.
    const lang = form.querySelector("#tr-export-target-language");
    if (lang) lang.value = rec.targetLang || "";
    form.submit();
    return "exported";
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
// handle for later in-place saves. Returns { fileName, openId } or null.
async function adoptHandle(handle) {
    const file = await handle.getFile();
    const originalXml = await file.text();
    const openId = newOpenId();
    // Older copies of the file are dropped once the page has opened it (opened).
    const ok = await openRecord({ openId, fileName: file.name, originalXml, sourceLang: null, targetLang: null, edits: {}, handle }, false);
    return ok ? { fileName: file.name, openId } : null;
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
        if (res && dotNetRef) await dotNetRef.invokeMethodAsync("OpenFromHandleAsync", res.fileName, res.openId);
    };
    btn.addEventListener("click", onClick);
    pickerTeardown = () => btn.removeEventListener("click", onClick);
}

export function teardownPicker() {
    if (pickerTeardown) { pickerTeardown(); pickerTeardown = null; }
}

// True when the current session was opened with a writable handle (so Save can
// write in place rather than download). Used after a refresh/restore.
export async function hasHandle(openId) {
    const rec = await getOwnRec(openId);
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
// the handle. Returns a status: "saved" | "empty" | "missing" | "no-handle" |
// "denied" | "error". "missing" means this open's record (and with it the
// original and the handle) is gone, so nothing is posted or written.
export async function saveInPlace(openId, fileName, editsJson, learnJson) {
    const rec = await getOwnRec(openId);
    const refused = notOwned(rec, openId);
    if (refused) return refused;
    if (!rec.originalXml) return "empty";
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
    body.append("Learn", learnJson || "[]");
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
        const saved = JSON.parse(editsJson);
        await updateRec(openId, (latest) => {
            if (JSON.stringify(latest.edits || {}) !== editsAtStart) return false;
            latest.edits = saved;
        });
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
                    if (res) { await dotNetRef.invokeMethodAsync("OpenFromHandleAsync", res.fileName, res.openId); return; }
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
