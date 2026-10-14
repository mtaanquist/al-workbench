using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ALWorkbench.Gateway.Core.Policy;

namespace ALWorkbench.Gateway;

/// <summary>
/// <see cref="IFileAccessCheck"/> from the file's or folder's access list. Reading
/// the list is all this class does; the rule (who may own it, which rights count,
/// which inherited entries count) is <see cref="AccessRules"/>, so it is tested on
/// any platform. The gateway's own service account is not trusted on purpose: see
/// <c>.design/on-prem-gateway.md</c>, "File permissions are part of the policy".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileAccessCheck : IFileAccessCheck
{
    /// <summary>A folder with more than this many entries is not walked further; the folder's own entries still cover what is created in it.</summary>
    private const int MaxContents = 20_000;

    public bool Exists(string path) => File.Exists(path);

    public IReadOnlyList<string> Contents(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                // A subfolder the gateway cannot list is still listed itself, and
                // Problem() on it says it could not be read.
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            }).Take(MaxContents).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Problem() on the folder says it could not be read; nothing more to list.
            return [];
        }
    }

    public string? Problem(string path)
    {
        FileSystemSecurity security;
        bool isFolder;
        try
        {
            if (Directory.Exists(path))
            {
                security = new DirectoryInfo(path).GetAccessControl();
                isFolder = true;
            }
            else if (File.Exists(path))
            {
                security = new FileInfo(path).GetAccessControl();
                isFolder = false;
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return $"The gateway could not read who may change {path} ({ex.Message}).";
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var entries = new List<AccessEntry>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier who) continue;
            entries.Add(new AccessEntry(
                who.Value,
                (int)rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow,
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly),
                rule.InheritanceFlags != InheritanceFlags.None));
        }

        var verdict = AccessRules.Evaluate(owner?.Value, entries, isFolder);
        if (!verdict.OwnerTrusted)
        {
            return $"{Name(owner)} owns {path}, and an owner can always change it. Make Administrators the owner.";
        }
        return verdict.Others.Count == 0
            ? null
            : $"{string.Join(", ", verdict.Others.Select(s => Name(new SecurityIdentifier(s))))} can change {path}. Only Administrators and SYSTEM may.";
    }

    /// <summary>The account's name, or its SID when Windows cannot name it (a deleted account, an unreachable domain).</summary>
    private static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "An unknown account";
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            // IdentityNotMappedException, and the SystemException Translate throws when
            // the domain controller cannot be reached.
            return sid.Value;
        }
    }
}
