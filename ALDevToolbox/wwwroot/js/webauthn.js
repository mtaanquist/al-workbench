// Minimal WebAuthn glue. Server returns CredentialCreateOptions /
// AssertionOptions as JSON via Fido2NetLib's serialization (base64url for
// byte fields). We decode those into ArrayBuffers, call
// navigator.credentials.{create,get}, then POST the response back as JSON.

function b64uToBuf(s) {
    s = s.replace(/-/g, '+').replace(/_/g, '/');
    while (s.length % 4) s += '=';
    const bin = atob(s);
    const arr = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
    return arr.buffer;
}

function bufToB64u(buf) {
    const bytes = new Uint8Array(buf);
    let s = '';
    for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function decodeCreateOptions(opts) {
    opts.challenge = b64uToBuf(opts.challenge);
    opts.user.id = b64uToBuf(opts.user.id);
    if (Array.isArray(opts.excludeCredentials)) {
        opts.excludeCredentials = opts.excludeCredentials.map(c => ({ ...c, id: b64uToBuf(c.id) }));
    }
    return opts;
}

function decodeAssertOptions(opts) {
    opts.challenge = b64uToBuf(opts.challenge);
    if (Array.isArray(opts.allowCredentials)) {
        opts.allowCredentials = opts.allowCredentials.map(c => ({ ...c, id: b64uToBuf(c.id) }));
    }
    return opts;
}

window.aldtPasskey = {
    async register() {
        const optionsRes = await fetch('/account/passkeys/registration-options', { method: 'GET' });
        if (!optionsRes.ok) throw new Error('Failed to start passkey registration.');
        const opts = decodeCreateOptions(await optionsRes.json());

        const cred = await navigator.credentials.create({ publicKey: opts });
        if (!cred) throw new Error('Passkey registration cancelled.');

        const response = {
            id: cred.id,
            rawId: bufToB64u(cred.rawId),
            type: cred.type,
            response: {
                attestationObject: bufToB64u(cred.response.attestationObject),
                clientDataJSON: bufToB64u(cred.response.clientDataJSON),
                transports: typeof cred.response.getTransports === 'function' ? cred.response.getTransports() : [],
            },
            extensions: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
        };
        const nickname = prompt('Name this passkey:', 'Passkey') || 'Passkey';
        const completeRes = await fetch('/account/passkeys', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ nickname, response }),
        });
        if (!completeRes.ok) {
            const txt = await completeRes.text();
            throw new Error(txt || 'Passkey registration failed.');
        }
        window.location.href = '/account?ok=passkey-added';
    },

    async login(email) {
        const optionsRes = await fetch('/auth/passkey/login/options', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ email: email || null }),
        });
        if (!optionsRes.ok) throw new Error('Failed to start passkey sign-in.');
        const opts = decodeAssertOptions(await optionsRes.json());

        const assertion = await navigator.credentials.get({ publicKey: opts });
        if (!assertion) throw new Error('Passkey sign-in cancelled.');

        const response = {
            id: assertion.id,
            rawId: bufToB64u(assertion.rawId),
            type: assertion.type,
            response: {
                authenticatorData: bufToB64u(assertion.response.authenticatorData),
                clientDataJSON: bufToB64u(assertion.response.clientDataJSON),
                signature: bufToB64u(assertion.response.signature),
                userHandle: assertion.response.userHandle ? bufToB64u(assertion.response.userHandle) : null,
            },
            extensions: assertion.getClientExtensionResults ? assertion.getClientExtensionResults() : {},
        };
        const completeRes = await fetch('/auth/passkey/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(response),
        });
        if (!completeRes.ok) {
            const txt = await completeRes.text();
            throw new Error(txt || 'Passkey sign-in failed.');
        }
        window.location.href = '/';
    },

    // Step-up: same ceremony as login, against the endpoints that pin the
    // allow-list to the signed-in user and require user verification. The
    // server answers with where to go next.
    async stepUp() {
        const optionsRes = await fetch('/auth/step-up/passkey/options', { method: 'POST' });
        if (!optionsRes.ok) throw new Error('Failed to start passkey check.');
        const opts = decodeAssertOptions(await optionsRes.json());

        const assertion = await navigator.credentials.get({ publicKey: opts });
        if (!assertion) throw new Error('Passkey check cancelled.');

        const response = {
            id: assertion.id,
            rawId: bufToB64u(assertion.rawId),
            type: assertion.type,
            response: {
                authenticatorData: bufToB64u(assertion.response.authenticatorData),
                clientDataJSON: bufToB64u(assertion.response.clientDataJSON),
                signature: bufToB64u(assertion.response.signature),
                userHandle: assertion.response.userHandle ? bufToB64u(assertion.response.userHandle) : null,
            },
            extensions: assertion.getClientExtensionResults ? assertion.getClientExtensionResults() : {},
        };
        const completeRes = await fetch('/auth/step-up/passkey', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(response),
        });
        if (!completeRes.ok) {
            const txt = await completeRes.text();
            throw new Error(txt || 'Passkey check failed.');
        }
        const result = await completeRes.json();
        window.location.href = result.redirect || '/';
    },
};

window.aldtDownload = {
    text(name, content) {
        const blob = new Blob([content], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    },
};

window.aldtCopy = async (text) => {
    try { await navigator.clipboard.writeText(text); return true; }
    catch { return false; }
};
