# Email layout: shipped without a sheet

Checked in here first (issue #1028) and waiting to be folded into the design project. There
is no email sheet yet; this records what shipped so one can be drawn to match. The rules and
the build are in `.design/email.md`.

Named user: anyone who gets an email from AL Workbench, usually a BC consultant or developer
reading it in Outlook or on a phone between meetings, who should see at a glance that it is
from the tool and what to do next.

## What to draw

One frame, light: just enough to look like it came from the system.

- A `--bg` page with the wordmark top left: a 10px `--primary` square (2px corners) and
  "AL Workbench", 15px semibold `--ink`. Text only, no image.
- A white card (`--surface`, 1px `--border`, 4px corners, 560px max, 28px padding) holding
  the message: 16px/24px `--ink-2` paragraphs, 16px apart.
- At most one primary button, the app's `.btn--primary` look: `--primary-strong` fill,
  white 15px semibold label, 2px corners, 10px by 18px padding. Under it, 13px `--ink-3`:
  "If the button does not work, copy this link into your browser:" and the address in
  `--primary-ink`.
- A footer under the card, 13px `--ink-3`: "Sent by AL Workbench for CRONUS A/S." and one
  line saying why the reader got it.

Show it at 600px and at 375px, and the same message with no button.

## Open questions for the sheet

- Whether the wordmark square should carry the app's hammer glyph. It is left plain because
  many clients block images and SVG.
- Whether notification emails (phase 2) need a status line above the first paragraph, for
  example "Build failed" in `--danger-text`. Not drawn until there is a notification email.
