// Sealed: the browser half of one-time secret links. See .design/sealed.md.
//
// Everything secret happens here. The sender's browser picks a random 32-byte key,
// encrypts the secret with AES-GCM, and sends the server only the ciphertext and two
// one-way proofs; the key goes into the part of the link after "#", which browsers
// never send to a server. The recipient's browser reads the key back out of its own
// address bar, proves it has it, gets the ciphertext and decrypts it.
//
//   aes key     = HKDF-SHA256(key [+ passphrase half A], salt, "aldt-sealed-key-v1")
//   link check  = HKDF-SHA256(key, "", "aldt-sealed-check-v1")       server keeps SHA-256 of it
//   passphrase  = PBKDF2-SHA256(passphrase, salt, 600000) -> half A (into the aes key)
//                                                          + half B (the passphrase check)
//
// The checks let the server refuse a link with its key cut off, or a wrong passphrase,
// before a view is spent; neither tells it anything about the key or the passphrase.
//
// Loaded once in the shell (App.razor) like the other behaviour scripts. The sender's
// page calls aldtSealed.seal / .finish through JS interop; the recipient's page is
// static, so its button is wired by one delegated listener.

(function () {
    const enc = new TextEncoder();
    const dec = new TextDecoder();
    const AAD = enc.encode("aldt-sealed-v1");
    const PBKDF2_ITERATIONS = 600000;

    function toB64u(bytes) {
        let s = "";
        for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
        return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    function fromB64u(s) {
        if (!/^[A-Za-z0-9_-]*$/.test(s)) return null;
        s = s.replace(/-/g, "+").replace(/_/g, "/");
        while (s.length % 4) s += "=";
        try {
            const bin = atob(s);
            const out = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
            return out;
        } catch (e) {
            return null;
        }
    }

    function concat(a, b) {
        const out = new Uint8Array(a.length + b.length);
        out.set(a, 0);
        out.set(b, a.length);
        return out;
    }

    async function hkdf(ikm, salt, info) {
        const base = await crypto.subtle.importKey("raw", ikm, "HKDF", false, ["deriveBits"]);
        const bits = await crypto.subtle.deriveBits(
            { name: "HKDF", hash: "SHA-256", salt: salt, info: enc.encode(info) }, base, 256);
        return new Uint8Array(bits);
    }

    async function stretch(passphrase, salt) {
        const base = await crypto.subtle.importKey("raw", enc.encode(passphrase), "PBKDF2", false, ["deriveBits"]);
        const bits = await crypto.subtle.deriveBits(
            { name: "PBKDF2", hash: "SHA-256", salt: salt, iterations: PBKDF2_ITERATIONS }, base, 512);
        return new Uint8Array(bits);
    }

    // The same passphrase typed with a stray space, or composed differently, still opens.
    function normalisePassphrase(value) {
        return (value || "").trim().normalize("NFC");
    }

    async function deriveKeys(key, passphrase, salt) {
        let ikm = key;
        let keySalt = new Uint8Array(0);
        let passphraseCheck = null;
        if (passphrase) {
            const stretched = await stretch(passphrase, salt);
            ikm = concat(key, stretched.slice(0, 32));
            keySalt = salt;
            passphraseCheck = stretched.slice(32);
        }
        const aesBits = await hkdf(ikm, keySalt, "aldt-sealed-key-v1");
        const aesKey = await crypto.subtle.importKey("raw", aesBits, "AES-GCM", false, ["encrypt", "decrypt"]);
        const linkCheck = await hkdf(key, new Uint8Array(0), "aldt-sealed-check-v1");
        return { aesKey: aesKey, linkCheck: linkCheck, passphraseCheck: passphraseCheck };
    }

    // ---- sender ----

    // Keys of sealed secrets the server has not answered for yet, by a handle .NET
    // holds. The key itself never crosses into .NET: finish() puts the link together.
    const pending = new Map();

    async function seal(secretEl, passphraseEl, maxBytes) {
        if (!window.isSecureContext || !crypto.subtle) {
            return { error: "This page needs a secure (https) connection to seal a secret." };
        }
        const secret = secretEl ? secretEl.value : "";
        if (!secret || !secret.trim()) return { error: "Paste the secret you want to send." };
        const bytes = enc.encode(secret);
        if (bytes.length > maxBytes) {
            return { error: "That is too long to send. Keep it under " + Math.floor(maxBytes / 1024) + " KB." };
        }
        const rawPassphrase = passphraseEl ? passphraseEl.value : "";
        const passphrase = normalisePassphrase(rawPassphrase);
        if (rawPassphrase && !passphrase) {
            return { error: "The passphrase is only spaces. Type a passphrase, or clear the box to send without one." };
        }

        const key = crypto.getRandomValues(new Uint8Array(32));
        const salt = passphrase ? crypto.getRandomValues(new Uint8Array(16)) : null;
        const keys = await deriveKeys(key, passphrase, salt);
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
            { name: "AES-GCM", iv: iv, additionalData: AAD }, keys.aesKey, bytes));

        const handle = toB64u(crypto.getRandomValues(new Uint8Array(12)));
        pending.set(handle, toB64u(key));
        return {
            handle: handle,
            ciphertext: toB64u(ciphertext),
            iv: toB64u(iv),
            linkCheck: toB64u(keys.linkCheck),
            passphraseSalt: salt ? toB64u(salt) : null,
            passphraseCheck: keys.passphraseCheck ? toB64u(keys.passphraseCheck) : null,
        };
    }

    // Builds the link from the server's token and the key kept back in seal(), writes
    // it into the read-only box, and clears the secret and passphrase from the form.
    function finish(handle, token, linkEl, secretEl, passphraseEl) {
        const key = pending.get(handle);
        pending.delete(handle);
        if (!key || !linkEl) return false;
        linkEl.value = location.origin + "/s/" + encodeURIComponent(token) + "#" + key;
        if (secretEl) secretEl.value = "";
        if (passphraseEl) passphraseEl.value = "";
        linkEl.focus();
        linkEl.select();
        return true;
    }

    function discard(handle) {
        pending.delete(handle);
    }

    async function copy(el) {
        if (!el || !el.value) return false;
        try {
            await navigator.clipboard.writeText(el.value);
            return true;
        } catch (e) {
            el.select();
            return false;
        }
    }

    window.aldtSealed = { seal: seal, finish: finish, discard: discard, copy: copy };

    // ---- recipient ----

    function keyFromAddress() {
        const raw = (location.hash || "").replace(/^#/, "");
        const key = fromB64u(raw);
        return key && key.length === 32 ? key : null;
    }

    function show(root, name) {
        root.querySelectorAll("[data-sealed-state]").forEach(function (el) {
            el.hidden = el.getAttribute("data-sealed-state") !== name;
        });
    }

    function say(root, text) {
        const el = root.querySelector("[data-sealed-error]");
        const line = el ? el.querySelector("[data-sealed-error-text]") : null;
        if (!el || !line) return;
        line.textContent = text || "";
        el.hidden = !text;
    }

    // The expiry in the reader's own time zone; the page says UTC until this runs.
    function localTimes(root) {
        root.querySelectorAll("[data-sealed-local-time]").forEach(function (el) {
            const when = new Date(el.getAttribute("data-sealed-local-time"));
            if (isNaN(when)) return;
            el.textContent = when.toLocaleString(undefined, {
                day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit",
            });
        });
    }

    function prepare(root) {
        if (root._sealedReady) return;
        root._sealedReady = true;
        localTimes(root);
        if (!keyFromAddress()) show(root, "incomplete");
    }

    function prepareAll() {
        document.querySelectorAll("[data-sealed-open]").forEach(prepare);
    }

    async function reveal(root, button) {
        const key = keyFromAddress();
        if (!key) { show(root, "incomplete"); return; }
        if (!window.isSecureContext || !crypto.subtle) {
            say(root, "Open the link exactly as it appears in the email, then try again.");
            return;
        }

        const token = root.getAttribute("data-token");
        const saltText = root.getAttribute("data-salt") || "";
        const salt = saltText ? fromB64u(saltText) : null;
        const passphraseEl = root.querySelector("[data-sealed-passphrase]");
        const passphrase = salt ? normalisePassphrase(passphraseEl ? passphraseEl.value : "") : "";
        if (salt && !passphrase) {
            say(root, "Type the passphrase the sender gave you.");
            if (passphraseEl) passphraseEl.focus();
            return;
        }

        say(root, "");
        button.disabled = true;
        button.setAttribute("aria-busy", "true");
        try {
            const keys = await deriveKeys(key, passphrase, salt);
            const res = await fetch("/api/sealed/" + encodeURIComponent(token) + "/reveal", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    check: toB64u(keys.linkCheck),
                    passphrase: keys.passphraseCheck ? toB64u(keys.passphraseCheck) : null,
                }),
                cache: "no-store",
                credentials: "omit",
            });

            if (res.status === 429) {
                say(root, "Too many tries from your network. Wait a few minutes, then try again.");
                return;
            }
            const body = await res.json().catch(function () { return {}; });
            if (res.status === 403 && body.error === "wrong-passphrase") {
                const left = body.attemptsLeft || 0;
                say(root, "That passphrase is wrong. " + left + (left === 1 ? " try" : " tries") + " left before the link locks.");
                if (passphraseEl) { passphraseEl.focus(); passphraseEl.select(); }
                return;
            }
            if (res.status === 403 && body.error === "locked") { show(root, "locked"); return; }
            if (!res.ok) { show(root, "gone"); return; }

            const plain = await crypto.subtle.decrypt(
                { name: "AES-GCM", iv: fromB64u(body.iv), additionalData: AAD },
                keys.aesKey, fromB64u(body.ciphertext));
            const out = root.querySelector("[data-sealed-secret]");
            if (out) out.value = dec.decode(plain);
            const left = root.querySelector("[data-sealed-left]");
            if (left) {
                left.textContent = body.viewsLeft > 0
                    ? "This link can be opened " + (body.viewsLeft === 1 ? "once more." : body.viewsLeft + " more times.")
                    : "Copy it now and keep it somewhere safe. This link has been used up, so it can't be opened again.";
            }
            show(root, "revealed");
            if (body.viewsLeft === 0) history.replaceState(null, "", location.pathname);
            if (out) { out.focus(); out.select(); }
        } catch (e) {
            say(root, "The secret could not be opened. Check that you copied the whole link, then try again.");
        } finally {
            button.disabled = false;
            button.removeAttribute("aria-busy");
        }
    }

    document.addEventListener("click", function (e) {
        const button = e.target.closest("[data-sealed-reveal]");
        if (button) {
            const root = button.closest("[data-sealed-open]");
            if (root) { e.preventDefault(); reveal(root, button); }
            return;
        }
        const copyButton = e.target.closest("[data-sealed-copy]");
        if (copyButton) {
            const root = copyButton.closest("[data-sealed-open]");
            const out = root ? root.querySelector("[data-sealed-secret]") : null;
            e.preventDefault();
            copy(out).then(function (ok) {
                const label = copyButton.querySelector("[data-copy-label]") || copyButton;
                const was = label.textContent;
                label.textContent = ok ? "Copied" : "Select the text above and copy it";
                setTimeout(function () { label.textContent = was; }, 1500);
            });
        }
    });

    // Enter in the passphrase box presses Show secret.
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Enter") return;
        const input = e.target.closest && e.target.closest("[data-sealed-passphrase]");
        if (!input) return;
        const root = input.closest("[data-sealed-open]");
        const button = root ? root.querySelector("[data-sealed-reveal]") : null;
        if (button) { e.preventDefault(); reveal(root, button); }
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", prepareAll);
    } else {
        prepareAll();
    }
    window.addEventListener("hashchange", function () {
        document.querySelectorAll("[data-sealed-open]").forEach(function (root) {
            show(root, keyFromAddress() ? "ready" : "incomplete");
        });
    });
})();
