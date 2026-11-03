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

Nothing is written to GitHub or the database in this part.
