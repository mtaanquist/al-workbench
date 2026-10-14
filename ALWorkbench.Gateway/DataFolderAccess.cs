using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ALWorkbench.Gateway.Core;

namespace ALWorkbench.Gateway;

/// <summary>
/// Sets the access lists on the gateway's data folder: Administrators and SYSTEM
/// full control throughout; the service account may read the folder and its settings
/// file, and change only the state, work and log folders. Nothing is inherited from
/// <c>%ProgramData%</c>, where every local user may create files.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DataFolderAccess
{
    public static void Apply(GatewayDataFolder folder, string? serviceAccount)
    {
        var root = Directory.CreateDirectory(folder.Root);
        var service = string.IsNullOrWhiteSpace(serviceAccount)
            ? null
            : new SecurityIdentifier(CngSigningKeyStore.AccountSid(serviceAccount));

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Rule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl));
        security.AddAccessRule(Rule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl));
        if (service is not null) security.AddAccessRule(Rule(service, FileSystemRights.ReadAndExecute));
        root.SetAccessControl(security);

        foreach (var writable in new[] { folder.StateFolder, folder.WorkFolder, folder.LogFolder })
        {
            var sub = Directory.CreateDirectory(writable);
            // Inherits the root's list, plus change rights for the service account here.
            // Explicit rules go first, so a previous service account loses its rights.
            var subSecurity = sub.GetAccessControl();
            foreach (FileSystemAccessRule explicitRule in subSecurity.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                subSecurity.RemoveAccessRuleSpecific(explicitRule);
            }
            if (service is not null) subSecurity.AddAccessRule(Rule(service, FileSystemRights.Modify));
            sub.SetAccessControl(subSecurity);
        }
    }

    private static FileSystemAccessRule Rule(IdentityReference who, FileSystemRights rights) =>
        new(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
}
