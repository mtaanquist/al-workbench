# Preview builds: additions shipped without a sheet

An addendum to the design project's `briefs/2026-09-shipped-without-a-sheet.md`, checked in
here first (issue #994) and waiting to be folded into it upstream. Nothing here needs a new
sheet; these are small additions to ported ones, recorded so the sheets do not drift.

Named user: an AL developer who maintains a dozen customer solutions and, the week Microsoft
publishes the next-major preview, wants every solution built against it once and a list of
which ones broke.

## The build pipeline editor dialog

One new field after "Branch (optional)": **Build against**, a `.select` with three options,
Current (default), Next minor, Next major, and one `.field__hint`:

> Current is the version your extensions declare. Next minor and Next major compile against
> Microsoft's preview builds to catch breaking changes early; those builds cannot be deployed.

The Name field's placeholder and hint now allow for a pipeline named for the version it
checks ("e.g. Production, Test environment or Next major check"). With Next minor or Next
major chosen, "Publish successful builds to GitHub" is disabled and its hint reads "Builds
against Next minor or Next major are never published, because they cannot be deployed."

## The build pipelines list (`PagePipelines.dc.html`, Builds)

A pipeline that builds against Next minor or Next major gets a `.tag` with that word after
its name in the Pipeline cell. Current pipelines show nothing.

## The pipeline page (`PageDetail.dc.html`)

- Head: when the latest build is a preview build, a second `.status-pill--info` reading
  "Preview build" after the state pill.
- Facts strip: a "Builds against" `.meta-item` (Next minor / Next major), only on a preview
  pipeline. "Latest BC" now shows the exact build number (`29.0.52914.0`) where it is known.
- Latest build topline: "on BC 29.0.52914.0" in place of "BC 29.0". Below it, for a preview
  build, a `.pb-note`: "This build checks your extensions against the next Business Central
  version. It can't be deployed; fix any errors before that version reaches your customers."
- Build history, Description cell: a `.tag` "Preview build" on a preview build's row, then
  "on BC 29.0.52914.0" in the muted `.pb-more` style. A preview build's row has no
  "Deploy..." action.

## The Pipelines dashboard (`PagePipelines.dc.html`)

The Failed builds tile keeps counting pipelines whose newest build failed, preview or not
(decided with the maintainer on 2026-10-01). When some of them are preview builds its foot
reads "2 failed, 1 on a preview build" in place of "Latest failed ...". Styling unchanged:
still `cue--attention`.

## For a design pass

- Whether the history row wants a BC column of its own rather than the muted line under the
  description, now that every row carries a version.
- Whether "Preview build" deserves its own pill tone rather than `info`.
