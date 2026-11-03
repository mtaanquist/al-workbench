# Migrate repositories

Moving a customer's Azure DevOps repositories into one new GitHub repository, laid out by a
workspace template (#1698). Built in three parts: #1699 (list, clone, preview), #1700
(assemble the workspace, normalise `app.json`), #1701 (create the repository, solution and
pipelines, and keep a history).

## Decisions (Mads, 2026-11-03)

- An org **Admin** tool, under Administration → Repositories → Migrate repositories. It runs
  once per customer, for more than a hundred customers over time.
- The source is one Azure DevOps project. Any mix of single-extension repositories and
  workspaces holding several extensions can be chosen. Repositories that depend on each other
  move together.
- Only `main` is copied, as one new commit with no history. Nothing in Azure DevOps is
  changed. Pipelines, variable groups and secrets stay behind. No Git LFS.
- The template's workspace files win (`.code-workspace`, `.gitignore`, README, ruleset,
  `workspace.aldt.toml`). Files that the new `.gitignore` ignores are left behind.
- Each extension keeps its source folder name. An extension at a repository's root takes the
  repository's name.
- `app.json` is normalised to the template, but keeps `id`, `name`, `publisher`, `version`,
  `idRanges` and `dependencies`, plus `application`/`platform` when they are higher than the
  template's.
- The template's own extensions are added only when the Admin asks for them.
- The run creates a new solution, and refuses a name that already exists.
- The Admin's own Azure DevOps token is used, the one stored under Account → Repository access.

## Part 1: preview (#1699)

`Services/Migration/`:

- `AzureDevOpsProject` parses the address an Admin pastes (`dev.azure.com/{org}/{project}`,
  `{org}.visualstudio.com/{project}`, or `org/project`). Every call and clone goes to
  `dev.azure.com`, whatever was pasted, so the address picks a path, never a host.
- `AzureDevOpsClient` lists a project's repositories with the Admin's token. The clone address
  is built from the project and the repository name, never taken from the answer, so the token
  only ever goes to `dev.azure.com`. Redirects are off: a refused token comes back as a redirect
  to the sign-in page (or a 203), which the client reports as a refused token.
- `RepositoryMigrationService.StartPreviewAsync` lists the project again and keeps only the ids
  the page sent that Azure DevOps really lists there, so a page cannot ask for an arbitrary
  address to be cloned with the Admin's token.
- `RepositoryMigrationQueue` (a `JobQueue`) holds each preview's state in memory, as the
  off-site restore jobs do: a preview is a throwaway reading, so a restart simply loses it.
  One running preview per person and at most eight across the server. A preview is only shown
  to the person who started it, in the organisation they started it in. Finished previews stay
  for two hours.
- `RepositoryMigrationWorker` (a `QueueDrainWorker`) runs one preview at a time under the
  Admin's identity, so the token lookup finds their token.
- `RepositoryMigrationPreviewer` asks for `main` first (`git ls-remote`), so "no main branch" is
  its own answer. It then makes a shallow, blobless clone and a sparse checkout of everything
  except `.alpackages/` at any depth. Old versions of files are never fetched, which is what
  keeps the multi-gigabyte repositories (years of committed `.alpackages`) quick. The clones are
  deleted when the preview ends.
- `MigrationSourceAnalyzer` reads the clones. Every folder with an `app.json` outside tooling
  folders is an extension, test apps included. It blocks on: an unreadable `app.json` or one
  without an id, two extensions with the same id, two extensions that would share a folder, and
  a file over 100 MB (GitHub's limit) outside `.alpackages`. It warns about a repository with no
  extension, and about a dependency on an app by the same publisher that is not in the run,
  which usually means a repository was left unticked.

Nothing is written to GitHub or the database in parts 1 and 2.

## Part 2: the templated workspace (#1700)

The page gains step 3, "New repository": the customer's name (it names the solution and the
workspace, as on New Workspace), an optional short name, the template, and "Also add the
template's own extensions" (off). `RepositoryMigrationService` turns these into a `ProjectPlan`
at request time: the template's own id range and application version, no examples, a fresh
tenant id, and the template's default modules only when its extensions are wanted. The plan is
validated the way New Workspace validates it, so a bad template or name is refused before
anything is cloned. The plan rides on the job.

Once the sources are sound (`MigrationSourceAnalysis.CanMigrate`), the previewer hands the clones
to `MigrationWorkspaceBuilder`, which writes the workspace into the preview's temporary folder:

- **The template, generated as New Workspace generates it.** `GenerationService` builds the ZIP;
  the builder unpacks it and drops the template's extension folders unless they were asked for.
  A moved extension in the same folder as a kept template extension is a blocking problem.
- **Each extension in its folder.** Copied without links, `.git`, `.alpackages`, or the folders
  of extensions nested inside it (a test app inside its app's folder gets a folder of its own).
  An extension at a repository's root also leaves behind that repository's own `.gitignore`,
  `.code-workspace`, `azure-pipelines*.yml`, `.azuredevops/`, `.pipelines/` and `.github/`.
- **Everything else in a source stays behind and is listed:** files outside every extension, as
  whole folders where nothing inside moves. A source file the template has its own of (README,
  `.code-workspace`) is listed as replaced.
- **`app.json` rebuilt on the template's** (`MigrationAppJson`): the template's first extension's
  `app.json`, as generated, is the base. Always the source's, and removed when the source has
  none: `id`, `name`, `publisher`, `version`, `idRanges`/`idRange`, `dependencies`. The source's
  when it has a value, otherwise the template's: `brief`, `description`, `logo`, `screenshots`,
  `supportedLocales`, `target`, `internalsVisibleTo`, `propagateDependencies`,
  `preprocessorSymbols`, `resourceFolders`, `applicationInsightsConnectionString`,
  `applicationInsightsKey`, `keyVaultUrls`. The higher of the two: `application`, `platform`,
  `runtime`. `features` is the union. Everything else is the template's, and a setting only the
  source has is removed. The result is laid out like the template's `app.json`: every property
  sits where the template puts it, spelled as the template spells it, and kept settings the
  template lacks follow (Mads, so sources written over the years end up reading alike).
  The extra kept settings beyond Mads's list describe or configure one app (an on-premises target, a telemetry key, a test app's `internalsVisibleTo`); overwriting
  them would break the app rather than tidy it.
- **The workspace files** point at the moved folders: the template's `.code-workspace` lists them
  instead of its own extension folders, and `workspace.aldt.toml` records each moved extension
  with kind `migrated`, its id, range, source repository and folder, and the commit read.
- **`ARCHIVE.md` at the root** (Mads): the date of the move, a link to the Azure DevOps project,
  and a table linking each source repository and the commit copied from it, since the history
  itself stays behind.
- **The template's `.gitignore` decides the rest.** The builder runs `git init` and
  `git ls-files --others --ignored --exclude-standard --directory` in the folder, removes what
  git ignores inside the moved extensions and lists it, then removes the `.git` folder again.
  Asking git means the preview and the later push agree exactly.

The preview shows the template's root files, each extension's `app.json` changes (settings
that only gain an empty value from the template are summarised in one line), and the list of
what stays behind. The folder is deleted with the clones; part 3 builds it again and pushes it.

## Part 3: create the repository (#1701)

A ready preview (nothing blocking in the sources or the workspace) gets step 5, "Create the
repository": the repository name (suggested from the customer's name in the organisation's
repository style), private (on), and "Set up build pipelines" (on). "Create repository" is
then the page's primary action; the preview button turns outline.

`RepositoryMigrationService.StartMigrationAsync` takes the preview's id, never the page's copy of
it. It rebuilds the target from what the preview recorded, lists the repositories from Azure
DevOps again, and checks on the request everything that would refuse the creation before
anything exists: the name, GitHub's readiness and the Admin's membership
(`GitHubWorkspaceRepositoryService.CheckCanCreateAsync`, shared with New Workspace), and a
solution of the same name. It then queues a **migration run**: the same job on the same queue,
carrying a `MigrationCreateRequest` with the commit of `main` the preview read in each
repository, keyed by Azure DevOps repository id. The page shows the run in place of the
preview, with a stage line. A preview starts at most one run; a second tab is told it was used.

The template itself is read again at run time, so an edit to the template between the preview
and the run is picked up. The repositories are what is pinned, not the template.

The run clones again, and stops with "main has changed ... preview again" if any repository's
`main` moved since the preview (or a repository the preview did not read is in the run), so
what is pushed is what the Admin looked at. It builds the
workspace again and hands it to `RepositoryMigrationPublisher`, which:

- creates the repository through `GitHubWorkspaceRepositoryService.CreateAndFillAsync`, the same
  sequence New Workspace runs (repository, standard files, branches, branch rules, solution,
  pipelines), with a different fill;
- **fills it with one git push**, not the Contents API: the organisation's standard files are
  laid over the workspace, everything left is added with `git add --force` (the preview already
  removed what the template's `.gitignore` leaves out, and a standard `.gitignore` must not
  silently drop files the preview listed), then one commit (author: the Admin's linked GitHub account; message:
  the Azure DevOps project, each repository and the commit read, and the template) is pushed to
  the default branch on the installation token. A customer's source is thousands of files, and
  an API call each would spend the installation's hourly allowance that builds and webhooks
  share;
- records the run in `repository_migrations` (who, when, from where, which commits, into which
  repository and solution, and the outcome), whatever the outcome: created, refused, failed
  after the repository was made (the row names it), or stopped before GitHub because `main`
  moved or the sources no longer move cleanly.

A push that fails leaves an empty repository behind; the refusal says so and says to delete it
and run again. A push the organisation's branch rules refuse says which setting an owner has to
change first. Nothing else is undone automatically.

**Running again.** Deleting the GitHub repository and the solution's pipelines is enough to run a
customer again, except that the solution keeps its name. So when a solution of the customer's
name exists, the page offers **"Replace the existing solution"** only if a successful earlier
migration created that solution and GitHub says its repository is gone (asked with the
installation token, which can see every repository the app created, and only for a repository in
the GitHub organisation connected now: one elsewhere is out of sight, not gone). After a
confirmation, the old solution and its pipelines are soft-deleted once the new repository holds
the code and before the new solution is registered, so a run refused earlier (the name is taken,
the push failed) leaves the old solution as it was. Any other
solution of that name is refused: the Admin picks another customer name or deletes it first.

**Earlier migrations** lists the organisation's runs, newest first, below the steps, so with a
hundred customers to move the Admin can see which are done. Run state itself stays in memory
like a preview's. A restart in the middle of a run loses it without a history row, and may
leave a half-made repository on GitHub; the run's failure message says to check for one.
The page serialises its own database reads, because the poll that follows a run and a button
press can otherwise reach the shared context at the same time.
