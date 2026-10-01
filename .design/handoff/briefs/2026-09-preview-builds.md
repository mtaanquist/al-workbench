# The nightly preview check: additions shipped without a sheet

An addendum to the design project's `briefs/2026-09-shipped-without-a-sheet.md`, checked in
here first (issue #994) and waiting to be folded into it upstream. Nothing here needs a new
sheet; these are small additions to ported ones, recorded so the sheets do not drift.

Named user: an AL developer who maintains a dozen customer solutions and wants to know,
before the upgrade team moves a customer to the next Business Central version, which of
their extensions would break, without setting anything up per version.

## The build pipeline editor dialog

One new field after "Branch (optional)": a `.check` box, unticked by default, labelled
**Check against upcoming Business Central versions every night**, with one `.field__hint`:

> Builds against Microsoft's previews of the next minor and next major Business Central
> versions, to catch breaking changes before they reach your customers. These preview builds
> can't be deployed; their results show beside the pipeline in the pipelines list.

Nothing else in the dialog changes; the pipeline's own builds and publishing are untouched.

## The result, wherever a pipeline is shown

A small inline group, `PreviewCheckSummary`, one item per upcoming version: a 13px glyph and
"Next minor: Passed" / "Next major: Failed" / "...: Running" in `--text-xs`, coloured
`--success-text` / `--danger-text` / muted, each a link to that build. Before the first night
it reads "Preview check hasn't run yet" (moon glyph, muted); when the check cannot run it reads
"Preview check paused - see why" in `--warning-text`, a link to the pipeline page.

- **Build pipelines list** (`PagePipelines.dc.html`, Builds): as a second `.cell-stack__sub`
  line under the pipeline name, below the branch line. The Failed tab includes a pipeline
  whose check did not pass.
- **Pipeline page** (`PageDetail.dc.html`): a "Preview check" `.meta-item` in the facts strip
  holding the group. When paused, a warning `.alert` above the body says why, with an outline
  "Run the check as me" button for someone who can manage the solution.

## The pipeline page, showing a check build

Following a result opens the pipeline page with that build in the hero (`?build={id}`):

- Head: a second `.status-pill--info` reading "Preview build" after the state pill.
- The card title reads "Next major preview build #212" with a ghost "Back to the latest build"
  link; the topline says "on BC 29.0.52914.0", and a `.pb-note` below it: "This preview build
  checks your extensions against the next major Business Central version (29.0.52914.0). It
  can't be deployed; fix any errors before that version reaches your customers."
- Without `?build`, the hero is the pipeline's own latest build, never a check build.
- Build history, Description cell: a `.tag` "Next major preview build" (a link to the same view) on
  a check build's row, then "on BC 29.0.52914.0" in the muted `.pb-more` style on every row.
  A check build's row has no "Deploy..." action.

## The Pipelines dashboard (`PagePipelines.dc.html`)

The Failed builds tile counts pipelines whose own newest build failed or whose preview check
did not pass (decided with the maintainer on 2026-10-01). When some are the check, its foot
reads "2 failed (1 in a preview check)" in place of "Latest failed ...". Styling
unchanged: still `cue--attention`. In Recent activity a check build is the pipeline's, with
the pipeline avatar, named "CRONUS Base (Next major check)".

## For a design pass

- Whether the history row wants a BC column of its own rather than the muted line under the
  description, now that every row carries a version.
- Whether "Preview build" deserves its own pill tone rather than `info`.
- Whether the result group wants a shape of its own in the list, rather than a sub-line.
