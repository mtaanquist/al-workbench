# Legacy route redirects

Redirect-only pages. Nothing in the app links to them: they exist so bookmarks and
in-the-wild links that predate a route rename keep landing somewhere useful. Each
page renders nothing and calls `NavigationManager.NavigateTo(..., replace: true)`
from `OnInitialized`, which is a 302 under static server rendering.

They are kept, not deleted, because the repo cannot prove nothing still points at
the old URLs (#700). Every one carries the same window:

> Redirect kept since 2026-09-03; safe to delete after 2027-03-01 if access logs
> show no hits.

Deleting one before that needs the same evidence: a look at access logs for the old
path. The `[Authorize]` attribute on each page mirrors the page it forwards to, so a
signed-out visitor still hits the login redirect first rather than learning the new
route.

`ALDevToolbox.Tests/Routing/LegacyRedirectTests.cs` asserts each row below still
redirects to the route in the second column. Keep the table and that test in step.

| Old route | New route | Page |
| --- | --- | --- |
| `/compare` | `/diff` | `CompareLegacyRedirect.razor` |
| `/artifacts` | `/pipelines/builds` | `LegacyArtifactsRedirect.razor` |
| `/artifacts/{id}` | `/solutions/{id}` | `LegacyArtifactsRedirect.razor` |
| `/admin/configuration` | `/admin/administration/identity` | `AdminLegacyConfigurationRedirect.razor` |
| `/admin/configuration/identity` | `/admin/administration/identity` | `AdminLegacyConfigurationRedirect.razor` |
| `/admin/configuration/defaults` | `/admin/templates/defaults` | `AdminLegacyConfigurationDefaultsRedirect.razor` |
| `/admin/configuration/files` | `/admin/templates/files` | `AdminLegacyConfigurationFilesRedirect.razor` |
| `/admin/configuration/logo` | `/admin/templates/defaults` | `AdminLegacyConfigurationLogoRedirect.razor` |
| `/admin/templates/logo` | `/admin/templates/defaults` | `AdminLegacyConfigurationLogoRedirect.razor` |
| `/admin/configuration/workspace` | `/admin/templates/workspace` | `AdminLegacyConfigurationWorkspaceRedirect.razor` |
| `/admin/configuration/mcp` | `/admin/administration/tools` | `AdminLegacyConfigurationMcpRedirect.razor` |
| `/admin/administration/mcp` | `/admin/administration/tools` | `LegacyAdminAdministrationMcpRedirect.razor` |
| `/admin/export` | `/admin/administration/export` | `AdminLegacyExportRedirect.razor` |
| `/admin/oauth-clients` | `/admin/administration/oauth-clients` | `AdminLegacyOAuthClientsRedirect.razor` |
| `/admin/users` | `/admin/administration/users` | `AdminLegacyUsersRedirect.razor` |
| `/admin/users/new` | `/admin/administration/users/new` | `AdminLegacyUsersRedirect.razor` |
| `/site-admin/access-tokens` | `/site-admin/connections/access-tokens` | `SiteAdminLegacyAccessTokensRedirect.razor` |
| `/site-admin/oauth-clients` | `/site-admin/connections/oauth-clients` | `SiteAdminLegacyOAuthClientsRedirect.razor` |
| `/site-admin/backups` | `/site-admin/backup-storage/database` | `SiteAdminLegacyBackupsRedirect.razor` |
| `/site-admin/storage` | `/site-admin/backup-storage/storage` | `SiteAdminLegacyStorageRedirect.razor` |
| `/site-admin/tenant-backups` | `/site-admin/backup-storage/snapshots` | `SiteAdminLegacyTenantBackupsRedirect.razor` |
| `/site-admin/settings/mcp` | `/site-admin/settings/tools` | `LegacySiteAdminSettingsMcpRedirect.razor` |

`Endpoints/LegacyRedirectEndpoints.cs` holds the same idea for routes that were never
Blazor pages (the `/snippets` -> `/cookbook` rename, `/projects/extension`, the
`/projects` -> `/solutions` rename, and the
`/releases` -> `/pipelines/deployments` move); it is
covered by the same test and the same window.
