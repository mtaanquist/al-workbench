# Design docs

Living specification for the AL Workbench. The code in `ALDevToolbox/` is the implementation; these documents are the contract it's built against. When the code and a doc disagree, fix one of them — don't leave them out of sync.

## What's here

| File | Covers |
|------|--------|
| `architecture.md` | Stack, layers, request flow, services. |
| `domain-model.md` | Tables, columns, validation rules. |
| `generation-engine.md` | Generated ZIP layout, mustache substitution, ID-range allocation. |
| `customer-naming.md` | How a customer's name becomes the folder, repository, extension and Solution names; the Solution picker on New Workspace. |
| `templates-and-seeding.md` | Template TOML schema; how the system org seeds other organisations via `TemplateImportService`. |
| `auth-and-audit.md` | Email/password accounts, organisations, signup approval, audit interceptor. |
| `teams-and-visibility.md` | Teams and their membership; the per-project visibility model they will grant. |
| `ui-design.md` | Page layout, copy, components in `Components/Shared/`. |
| `email.md` | How emails look and are built: the shared layout, inline styles, the plain-text part. |
| `deployment.md` | Docker, env vars, health checks, backups. |
| `object-explorer.md` | `.app` symbol-package ingest, Release/Module model, cross-module reference resolution. |
| `bcquality.md` | Mirroring Microsoft's BCQuality knowledge base into Postgres, and the MCP tools over it. |
| `saas-delivery.md` | Publishing a build to a Business Central SaaS environment: connection, release pipelines, deliveries, update windows. |
| `environment-updates.md` | The Upgrades fleet page: the team-scoped grant, the mirrored next platform update, the two date writes, the action-and-history table. |
| `solution-customer-info.md` | Customer information on a Solution: hosting and on-premises Solutions, the Customer tab (getting in, contacts, modules, who knows the customer), the Solutions list's side panel. |
| `command-palette.md` | The command palette: why it is a script and an endpoint rather than a Blazor island, the hotkey, the source contract, matching and ranking, what it may return and what it never does. |
| `completed-milestones.md` | The record of what each shipped milestone added (M1–M21). |
| `roadmap.md` | Uncommitted forward-looking ideas (successor to the retired `milestones.md`). |
| `migration-history.md` | Where to find the EF migration history. |

`template.toml` and `well-known-deps.toml` are reference samples for the seed format documented in `templates-and-seeding.md`.

## Contributing changes

If you change behaviour, update the relevant doc in the same PR. Don't leave a "this changed in phase X" footnote — rewrite the doc to describe the current state. The repo's `CLAUDE.md` covers conventions for new code.
