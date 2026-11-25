using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ALDevToolbox.Data.Migrations
{
    /// <summary>
    /// Gives every existing organisation the default repository standard that new
    /// organisations now start with: <c>.github/workflows/refresh-test.yml</c>, which
    /// keeps <c>test</c> up to date with <c>main</c> in the repositories the
    /// workbench creates from here on. Repositories that already exist are not
    /// touched.
    /// </summary>
    /// <remarks>
    /// The body is a copy of <c>DefaultRepositoryStandardFiles.RefreshTestWorkflowContent</c>
    /// as it was when this migration was written, not a reference to it, so editing
    /// the constant later never changes what this migration did. An organisation that
    /// already has a file at that path keeps its own (<c>ON CONFLICT DO NOTHING</c>
    /// on the unique <c>(organization_id, path)</c> index). The row goes after the
    /// organisation's existing standards.
    /// </remarks>
    public partial class SeedRefreshTestWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(""""
                INSERT INTO github_repository_standard_files
                    (organization_id, path, content, ordering, updated_at)
                SELECT
                    o.id,
                    '.github/workflows/refresh-test.yml',
                    $wf$# Keeps the test branch up to date with main.
                #
                # Every push to main is merged into test. Nothing on test is thrown away:
                # this is a merge, not a reset. If the merge cannot be done here (conflicts,
                # branch rules on test, or a change to a workflow file, which this workflow is
                # not allowed to push), a pull request into test is opened instead, from the
                # branch refresh-test-from-main, so someone can finish it by hand.
                #
                # Merge that pull request with a merge commit, not a squash. A squash leaves
                # main's commits out of test's history, and the next run hits the same
                # conflicts again.
                #
                # For that pull request, the repository (or its organisation) must allow
                # GitHub Actions to create pull requests: Settings -> Actions -> General ->
                # "Allow GitHub Actions to create and approve pull requests". Without it,
                # this run fails and says so, rather than leaving test behind quietly.
                # Pull requests opened by this workflow do not start other workflows, so if
                # test requires status checks from GitHub Actions, push to the branch or
                # close and reopen the pull request to start them.
                #
                # Added by AL Workbench when the repository was created. Edit or delete it
                # freely; AL Workbench does not touch it again.

                name: Refresh test from main

                on:
                  push:
                    branches: [main]
                  workflow_dispatch:

                permissions:
                  contents: write
                  pull-requests: write

                concurrency:
                  group: refresh-test
                  cancel-in-progress: false

                jobs:
                  refresh:
                    runs-on: ubuntu-latest
                    env:
                      SOURCE: main
                      TARGET: test
                      PR_BRANCH: refresh-test-from-main
                      GH_TOKEN: ${{ github.token }}
                      REPO: ${{ github.repository }}
                    steps:
                      - name: Merge main into test
                        run: |
                          set -euo pipefail
                          api() { gh api -H "X-GitHub-Api-Version: 2022-11-28" "$@"; }
                          http() {
                            curl -sS -o response.json -w '%{http_code}' \
                              -H "Authorization: Bearer $GH_TOKEN" \
                              -H "Accept: application/vnd.github+json" \
                              -H "X-GitHub-Api-Version: 2022-11-28" "$@"
                          }

                          status=$(http "$GITHUB_API_URL/repos/$REPO/branches/$TARGET")
                          case "$status" in
                            200) ;;
                            404) echo "This repository has no $TARGET branch, so there is nothing to refresh."; exit 0 ;;
                            *) echo "::error::Could not read the $TARGET branch (HTTP $status): $(cat response.json)"; exit 1 ;;
                          esac

                          # A server-side merge: 201 merged, 204 already up to date. A refusal
                          # (409 conflict, 403 or 422 from branch rules or a workflow file)
                          # falls through to the pull request below.
                          status=$(http -X POST "$GITHUB_API_URL/repos/$REPO/merges" \
                            -d "{\"base\":\"$TARGET\",\"head\":\"$SOURCE\",\"commit_message\":\"Merge $SOURCE into $TARGET\"}")
                          case "$status" in
                            201) echo "Merged $SOURCE into $TARGET."; exit 0 ;;
                            204) echo "$TARGET already has everything on $SOURCE."; exit 0 ;;
                            403|409|422) echo "The merge could not be done here (HTTP $status): $(cat response.json)" ;;
                            *) echo "::error::GitHub did not merge $SOURCE into $TARGET (HTTP $status): $(cat response.json)"; exit 1 ;;
                          esac

                          sha=$(api "repos/$REPO/git/ref/heads/$SOURCE" --jq .object.sha)
                          owner="${REPO%%/*}"
                          open_pr() {
                            api "repos/$REPO/pulls?state=open&base=$TARGET&head=$owner:$1" --jq '.[0].html_url // empty'
                          }

                          existing=$(open_pr "$PR_BRANCH")
                          if [ -n "$existing" ]; then
                            # Fast-forward only: whoever is resolving it keeps their work.
                            if api -X PATCH "repos/$REPO/git/refs/heads/$PR_BRANCH" \
                                -f sha="$sha" -F force=false --silent; then
                              echo "Brought the open pull request up to date: $existing"
                            else
                              echo "::warning::The open pull request $existing does not include $SOURCE at $sha. Merge $SOURCE into $PR_BRANCH by hand, or run this workflow again after that pull request is merged."
                            fi
                            exit 0
                          fi
                          existing=$(open_pr "$SOURCE")
                          if [ -n "$existing" ]; then
                            echo "A pull request from $SOURCE is already open: $existing"
                            exit 0
                          fi

                          # No open pull request, so nobody is working on the branch: start it
                          # again from main. It is a branch of its own so that resolving a
                          # conflict in the browser commits there, not to main.
                          head="$PR_BRANCH"
                          note=""
                          if ! api -X PATCH "repos/$REPO/git/refs/heads/$PR_BRANCH" \
                                -f sha="$sha" -F force=true --silent 2>/dev/null \
                             && ! api -X POST "repos/$REPO/git/refs" \
                                -f ref="refs/heads/$PR_BRANCH" -f sha="$sha" --silent; then
                            echo "::warning::Could not create $PR_BRANCH, so the pull request comes from $SOURCE."
                            head="$SOURCE"
                            note=" If GitHub reports conflicts, resolve them on your own machine, not in the browser: in the browser the resolution would be committed to $SOURCE."
                          fi

                          body="$SOURCE moved on, and the change could not be merged into $TARGET automatically. Merge this pull request with a merge commit (not a squash) to bring $TARGET up to date; the work that is only on $TARGET is kept.$note"
                          if ! url=$(api -X POST "repos/$REPO/pulls" \
                              -f base="$TARGET" -f head="$head" \
                              -f title="Merge $SOURCE into $TARGET" -f body="$body" --jq .html_url); then
                            echo "::error::$TARGET is behind $SOURCE and the pull request could not be opened (see the error above). If it says GitHub Actions may not create pull requests, allow it under Settings -> Actions -> General, or merge $SOURCE into $TARGET by hand."
                            exit 1
                          fi
                          echo "Opened $url"
                $wf$,
                    COALESCE((SELECT MAX(f.ordering) + 1
                              FROM github_repository_standard_files f
                              WHERE f.organization_id = o.id), 0),
                    NOW()
                FROM organizations o
                ON CONFLICT (organization_id, path) DO NOTHING;
                """");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Left in place: by now an Admin may have edited the row, and a down
            // migration cannot tell their copy from ours.
        }
    }
}
