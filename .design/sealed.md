# Sealed: one-time secret links

Sealed lets a colleague send a secret (an Entra app registration's client secret, a
password) to someone outside the workbench as a link, instead of pasting the secret into
an email. Asked for by Mads on 2026-10-26; the proposal and his answers are in the
project thread "One-time secret sharing links".

## What it does

- A signed-in user pastes the secret on `/sealed`, picks when the link expires (an hour,
  a day, three days or a week; three days by default, since Mads found recipients often
  miss a link on its first day) and how many times it can be opened (1 to 5; once by
  default), optionally adds a passphrase and a note for themselves, and presses Create link.
- They copy the link into their email. The full link is shown once.
- The recipient opens `/s/{token}#{key}` without an account, sees which organisation sent
  it, presses Show secret (typing the passphrase first, if there is one), and copies it.
- When the last allowed view is used, the link expires, or the sender revokes it, the link
  stops working and the encrypted copy is deleted.
- The sender (or an Admin) can extend a link that still works: keep it working for one,
  three or seven days from now, offered only when that adds at least an hour. Views
  left are unchanged. There is no cap on how many times a link can be extended; a link
  never works for more than a week from any given moment. A link that has stopped working cannot be extended, since its
  encrypted copy is already gone. Extensions are in the audit log (asked for by Mads on
  2026-10-30).
- The sender sees their links on `/sealed`: opened how many times, when and from which
  address, and whether it is still open. Admins can switch to everyone's links in the
  organisation and revoke any of them. Closed links disappear from the list 30 days after
  they closed.
- The sender is notified (category "Sealed links you send") when a link is opened (opens
  within the same minute arrive as one notification), and when one locks after wrong
  passphrases.

## Security model

The server never sees the secret or the key.

1. The sender's browser (`wwwroot/sealed.js`) picks a random 32-byte key and encrypts the
   secret with AES-256-GCM. The key goes into the part of the link after `#`, which
   browsers do not send to servers, so it is never in the database, the logs, the proxy's
   logs, backups or `Referer` headers.
2. Key derivation, all in the browser:
   - AES key = HKDF-SHA256(key, or key plus half A of the stretched passphrase; salt;
     `aldt-sealed-key-v1`).
   - Link check = HKDF-SHA256(key, empty salt, `aldt-sealed-check-v1`).
   - Passphrase = PBKDF2-SHA256(passphrase, 16-byte random salt, 600,000 rounds), 64 bytes:
     half A goes into the AES key, half B is the passphrase check.
3. The server stores the ciphertext, the nonce, the passphrase salt, and SHA-256 of the
   link check and the passphrase check. Neither check can be turned back into the key or
   the passphrase. A database or backup leak gives nothing usable without the link.
4. A reveal must present the link check, so a link with its key cut off (or none) is
   refused without spending a view. A wrong passphrase does not spend a view either; five
   wrong ones lock the link for good and drop the ciphertext.
5. Opening the page reveals nothing. The secret only comes back from a POST when a person
   presses Show secret, so mail scanners that fetch every link in an email (Outlook Safe
   Links and the like) cannot use a link up.
6. Views are counted in one conditional `UPDATE` (`view_count < max_views`, still open,
   not expired), so two reveals at once cannot both take the last view. The same statement
   closes the link and clears the ciphertext when it takes the last view, so an aborted
   request cannot leave a used-up link holding its secret. Wrong passphrases are counted
   and the link locked the same way, so a burst of guesses sent at once still gets five.
7. The reveal endpoint is anonymous, rate limited to 20 requests per address per 10
   minutes, takes a body of at most 1 KB and answers `Cache-Control: no-store`. The
   recipient page sets `noindex` and `no-referrer`.
8. The secret and passphrase boxes on the sender's page are not bound to the Blazor
   component, so what is typed never travels over the circuit. The browser hands .NET only
   ciphertext and the two checks, and puts the link together itself.

## Tenant fence

The sender's half (create, list, revoke) runs under the normal organisation filter. The
recipient has no account and so no organisation, so `SealedLinkService.GetForRecipientAsync`
and `RevealAsync` find the link with `IgnoreQueryFilters()`, pinned to its random 128-bit
token or the id that token resolved to. Mads approved this on 2026-10-26; it is filed under
fence category 1 in `CLAUDE.md`. The background sweep needs no bypass: like the digest
scheduler, it runs once per organisation inside an `AmbientOrganizationScope`.

## Data

- `sealed_links`: one row per link. `ciphertext` is null once the link has closed;
  `closed_reason` is `UsedUp`, `Revoked`, `Expired` or `LockedOut`. `notified_view_count`
  and `lockout_notified` record what the sender has been told.
- `sealed_link_events`: each open and each wrong passphrase, with the time and the
  recipient's address (behind the trusted proxy), deleted with the link.
- The audit log records a link being created and revoked (entity type `SealedLink`, named
  by the sender's note). Opens are in `sealed_link_events` instead, for the sender.

## Background sweep

`SealedLinkScheduler` runs every minute (opt out with `DISABLE_SEALED_LINK_SCHEDULER=1`).
For each organisation it closes links past their expiry and drops their ciphertext, sends
the opened and locked notifications, and deletes links closed more than 30 days ago. A
link past its expiry is refused at reveal time even before the sweep runs.

## Tool switch

`ToolKey.Sealed` gates `/sealed` like every tool. The recipient page and the reveal
endpoint answer only to the site-wide switch, since a recipient has no organisation whose
switch could apply: switching Sealed off for the whole site closes every link.

## Deliberately left out

- Files: text only, up to 16 KB.
- An MCP tool: an assistant should not be handling secrets.
- Step-up on the tool: it reaches nothing outside the workbench.
