# Email

How AL Workbench's emails look and how they are built. Sending, queueing and retries are
covered in `auth-and-audit.md` and the outbox comments (issue #790); this is the message
itself. Issue #1028 introduced it; the visual addendum for the design project is
`handoff/briefs/2026-10-email-layout.md`.

## The look

Light on purpose: an email should look like it came from AL Workbench, not like a
newsletter. One frame for every email:

- **Wordmark** above the card: the 10px `--primary` square and "AL Workbench" in `--ink`,
  15px semibold. Text, not an image, so it shows with images blocked.
- **Card**: `--surface` on a `--bg` page, 1px `--border`, 4px corners, 560px wide at most,
  28px padding. Body text 16px/24px in `--ink-2`.
- **One primary button** at most, filled `--primary-strong` with `--on-primary-strong` text and
  2px corners, the same as the app's primary button. Its address is written out underneath in
  13px `--ink-3` for when a mail filter breaks the button.
- **Footer** under the card in 13px `--ink-3`: "Sent by AL Workbench for {organisation}." (or
  "Sent by AL Workbench." when there is none) and one sentence saying why the recipient got it.
- **Preheader**: an optional hidden line that inbox lists show after the subject.

No images, no icons, no colour bands, no dark-mode variant. The emails declare
`color-scheme: light`; clients that force dark mode recolour them, and the colours above
survive that.

## How an email is built

- Each email is a Razor component in `Components/Email/` that composes `EmailLayout`,
  `EmailParagraph`, `EmailButton`, for one-time codes `EmailCode`, and for a quoted message
  such as a failure reason `EmailQuote` (`Mono` for build output; one line break per line of
  the text), and exposes a typed
  `RenderAsync` that sets its subject (`PasswordResetEmail` is the shape to copy).
- `EmailRenderer` renders it with Blazor's `HtmlRenderer`, so every value is HTML-encoded by
  Razor. The subject only has control characters collapsed.
- Styles are inline and the layout is tables, because email clients drop stylesheets and
  ignore custom properties. `EmailTheme` holds the tokens above as literal values, each naming
  the token it copies. No `class` attributes.
- Every email gets a plain-text part, and the message goes out as `multipart/alternative`.
  The text is converted from the rendered HTML (`EmailPlainText`), so the two cannot drift.
  Two markers decide what HTML-only pieces become in text: `data-email-text="skip"` leaves an
  element out (the preheader, the written-out address under a button) and
  `data-email-text="button"` writes a button as its label and its address on two lines. Any
  other link keeps its address in brackets after the text.
- Links are absolute: an email has no page to be relative to. Build them from `PublicOrigin`
  (`PUBLIC_BASE_URL`) rather than the request's host, so a forged `Host` header cannot point a
  link somewhere else. When it is unset, links fall back to the request host and startup warns.
- Every email has an entry in `EmailPreviews` with sample data, which is what the Previews tab
  on /site-admin/email shows (HTML in a sandboxed frame, the text part beside it) and what
  "Send to me" mails, with "Preview:" in front of the subject. Sample links point at
  `https://workbench.cronus.example`, so they go nowhere. `EmailPreviewTests` fails while an
  email has no entry.

## Copy

The house style in `CLAUDE.md` applies: CRONUS for placeholders, ASCII punctuation, plain
words. Greet by name ("Hi Mads,"), say what happened in the first sentence, label the button
with the verb the reader would use ("Reset password"), and end the footer reason with why
this address got the email.
