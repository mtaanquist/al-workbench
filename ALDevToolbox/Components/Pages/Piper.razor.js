// Module for the Piper page (/piper). Owns the document-level Alt+1/Alt+2
// keybind dispatcher, the localStorage round-trip for remembering which
// input mode the user was last on (Text or Table), and sending the input
// box's value to the server however long it is.

const MODE_KEY = "piper.inputMode";

// Text up to this many characters goes to the server as an ordinary call
// argument. JSON-escaping can take a character to six bytes, so this keeps the
// message under the hub's 32 KB limit; anything longer is streamed, which the
// framework sends in chunks under that limit (#1253).
const SMALL_INPUT_CHARS = 4000;

let currentRef = null;
let keyHandler = null;
let inputEl = null;
let inputHandler = null;

// One send at a time, newest value wins: a stream takes longer than a plain
// call, so letting them overlap could land an older value after a newer one.
let sending = false;
let pending = null;

export function init(dotNetRef, input) {
    // Replace any previous handler from an earlier page mount (Blazor
    // Server tears modules down when the user navigates away, but be
    // defensive in case dispose was skipped on disconnect).
    detach();

    currentRef = dotNetRef;
    if (input) {
        inputEl = input;
        inputHandler = () => queueInput(inputEl.value);
        inputEl.addEventListener("input", inputHandler);
    }
    keyHandler = (ev) => {
        if (!ev.altKey || ev.ctrlKey || ev.metaKey) return;
        // Alt+1 → Text, Alt+2 → Table. Both `event.key` (which honours
        // keyboard layout) and `event.code` (which is physical) cover the
        // common cases — Mac Option layouts produce printable characters
        // for some digit rows.
        let mode = null;
        if (ev.key === "1" || ev.code === "Digit1") mode = "Text";
        else if (ev.key === "2" || ev.code === "Digit2") mode = "Table";
        if (mode === null) return;

        ev.preventDefault();
        // Fire-and-forget: a dropped circuit rejects here, and an unhandled
        // rejection per keypress would bury real errors in the console.
        currentRef.invokeMethodAsync("SetModeFromKeybind", mode).catch(() => {});
    };
    document.addEventListener("keydown", keyHandler);
}

export function detach() {
    if (keyHandler) {
        document.removeEventListener("keydown", keyHandler);
        keyHandler = null;
    }
    if (inputEl && inputHandler) {
        inputEl.removeEventListener("input", inputHandler);
    }
    inputEl = null;
    inputHandler = null;
    pending = null;
    currentRef = null;
}

// The page changed the input itself ("Use result as input", "Clear both
// fields"). Setting .value fires no input event, so nothing is sent back.
export function setInput(input, value) {
    if (input) input.value = value ?? "";
}

function queueInput(value) {
    pending = value;
    if (!sending) pump();
}

async function pump() {
    sending = true;
    try {
        while (pending !== null && currentRef) {
            const value = pending;
            pending = null;
            try {
                if (value.length <= SMALL_INPUT_CHARS) {
                    await currentRef.invokeMethodAsync("OnInputChanged", value);
                } else {
                    const bytes = new TextEncoder().encode(value);
                    await currentRef.invokeMethodAsync(
                        "OnLargeInputChanged", DotNet.createJSStreamReference(bytes));
                }
            } catch {
                // A dropped circuit rejects here; the reconnect UI says so.
            }
        }
    } finally {
        sending = false;
    }
}

export function readMode() {
    try {
        return localStorage.getItem(MODE_KEY);
    } catch {
        return null;
    }
}

export function writeMode(mode) {
    try {
        localStorage.setItem(MODE_KEY, mode);
    } catch {
        // Quota exceeded, private mode, etc. — non-fatal.
    }
}
