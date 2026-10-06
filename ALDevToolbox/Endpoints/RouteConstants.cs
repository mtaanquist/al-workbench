namespace ALDevToolbox.Endpoints;

/// <summary>
/// Centralises the redirect target URLs used by the endpoint handlers so
/// renames are a single-file change. Only the paths that recur across
/// multiple endpoints live here; one-off redirects can stay inline.
/// </summary>
internal static class RouteConstants
{
    public const string Login = "/login";
    public const string Account = "/account";
    public const string AccountSecurity = "/account/security";
    public const string AdminUsers = "/admin/administration/users";
    public const string AdminUsersNew = "/admin/administration/users/new";
    public const string AdminTemplates = "/admin/templates";
    public const string AdminExport = "/admin/administration/export";
    public const string SiteAdminUsers = "/site-admin/users";
    public const string SiteAdminSettings = "/site-admin/settings";
    public const string SiteAdminBackups = "/site-admin/backup-storage/database";
    public const string SiteAdminStorage = "/site-admin/backup-storage/storage";
    public const string SiteAdminTenantBackups = "/site-admin/backup-storage/snapshots";

    public const string OkQuery = "ok";
    public const string ErrQuery = "err";
    public const string MsgQuery = "msg";
}
