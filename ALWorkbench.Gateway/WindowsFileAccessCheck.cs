using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway;

/// <summary>
/// <see cref="IFileAccessCheck"/> from the file's or folder's access list: it must be
/// owned by Administrators, SYSTEM or TrustedInstaller, and no one else may hold a
/// right that changes it (write, append, attributes, delete, delete children, change
/// permissions, take ownership, or the generic write and all rights). Deny entries
/// are ignored, since they only take rights away, and so are inherit-only entries,
/// which do not apply to the object itself. The gateway's own service account is not
/// on the list on purpose: see <c>.design/on-prem-gateway.md</c>, "File permissions
/// are part of the policy".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileAccessCheck : IFileAccessCheck
{
    /// <summary><c>NT SERVICE\TrustedInstaller</c>, which owns and may write everything Windows installs under Program Files.</summary>
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static readonly HashSet<SecurityIdentifier> Trusted =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        TrustedInstaller,
    ];

    private const FileSystemRights ChangeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    public bool Exists(string path) => File.Exists(path);

    public string? Problem(string path)
    {
        FileSystemSecurity security;
        try
        {
            if (Directory.Exists(path)) security = new DirectoryInfo(path).GetAccessControl();
            else if (File.Exists(path)) security = new FileInfo(path).GetAccessControl();
            else return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return $"The gateway could not read who may change {path} ({ex.Message}).";
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !Trusted.Contains(owner))
        {
            return $"{Name(owner)} owns {path}, and an owner can always change it. Make Administrators the owner.";
        }

        var others = new List<string>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if (rule.IdentityReference is not SecurityIdentifier who || Trusted.Contains(who)) continue;
            var rights = (int)rule.FileSystemRights;
            if ((rule.FileSystemRights & ChangeRights) == 0 && (rights & (GenericAll | GenericWrite)) == 0) continue;
            var name = Name(who);
            if (!others.Contains(name)) others.Add(name);
        }
        return others.Count == 0
            ? null
            : $"{string.Join(", ", others)} can change {path}. Only Administrators and SYSTEM may.";
    }

    private static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "An unknown account";
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
