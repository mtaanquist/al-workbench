using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ALWorkbench.Gateway.Core;

namespace ALWorkbench.Gateway;

/// <summary>
/// Sets, and checks, the access lists on the gateway's data folder: Administrators
/// owns everything and has full control with SYSTEM; the service account may read the
/// folder and its settings file, and change only the state, work and log folders.
/// Nothing is inherited from <c>%ProgramData%</c>, where every local user may create
/// files - and so may create this folder first, which is why the owner is reset too:
/// an owner can always rewrite the access list, whatever it says.
/// See <c>.design/on-prem-gateway.md</c>, "Windows service".
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DataFolderAccess
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Any of these lets a holder change a file or folder, or its access list.</summary>
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x10000000 /* GENERIC_ALL */ | (FileSystemRights)0x40000000 /* GENERIC_WRITE */;

    /// <summary>
    /// Makes Administrators the owner of the folder and everything in it and sets the
    /// access lists. Throws <see cref="DataFolderAccessException"/> when the owner
    /// cannot be set, or a link inside the folder points elsewhere: the gateway must
    /// not keep its settings in a folder someone else controls.
    /// </summary>
    public static void Apply(GatewayDataFolder folder, string? serviceAccount)
    {
        var root = Directory.CreateDirectory(folder.Root);
        RefuseLinks(root);
        var service = string.IsNullOrWhiteSpace(serviceAccount)
            ? null
            : new SecurityIdentifier(CngSigningKeyStore.AccountSid(serviceAccount));

        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Rule(Administrators, FileSystemRights.FullControl));
        security.AddAccessRule(Rule(LocalSystem, FileSystemRights.FullControl));
        if (service is not null) security.AddAccessRule(Rule(service, FileSystemRights.ReadAndExecute));
        Set(root.FullName, () => root.SetAccessControl(security));

        var writable = new[] { folder.StateFolder, folder.WorkFolder, folder.LogFolder }
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in writable) Directory.CreateDirectory(path);

        // Everything below the root: owned by Administrators, inheriting the root's list,
        // with no rules of its own except change rights for the service account on the
        // three writable folders. A previous service account, or whoever made the
        // folder first, loses whatever it had.
        foreach (var entry in Below(root))
        {
            var isWritableFolder = entry is DirectoryInfo && writable.Contains(entry.FullName.TrimEnd(Path.DirectorySeparatorChar));
            if (entry is DirectoryInfo directory)
            {
                var entrySecurity = directory.GetAccessControl();
                Reset(entrySecurity);
                if (isWritableFolder && service is not null) entrySecurity.AddAccessRule(Rule(service, FileSystemRights.Modify));
                Set(directory.FullName, () => directory.SetAccessControl(entrySecurity));
            }
            else if (entry is FileInfo file)
            {
                var entrySecurity = file.GetAccessControl();
                Reset(entrySecurity);
                Set(file.FullName, () => file.SetAccessControl(entrySecurity));
            }
        }
    }

    /// <summary>
    /// What is wrong with the folder's access, in words for a Windows administrator, or
    /// nothing. Checked on every service start: the gateway refuses to run on a folder
    /// someone else can write, because that someone could point it at another
    /// AL Workbench or change what it runs.
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <param name="serviceAccounts">The SIDs that may own and change things in the state, work and log folders: the service account.</param>
    public static List<string> Verify(GatewayDataFolder folder, IReadOnlyCollection<SecurityIdentifier> serviceAccounts)
    {
        var problems = new List<string>();
        var root = new DirectoryInfo(folder.Root);
        if (!root.Exists) return problems; // Nothing registered yet; the runner says so.

        var adminsOnly = new HashSet<SecurityIdentifier> { Administrators, LocalSystem };
        var withService = new HashSet<SecurityIdentifier>(adminsOnly);
        withService.UnionWith(serviceAccounts);

        var rootSecurity = root.GetAccessControl();
        if (!rootSecurity.AreAccessRulesProtected)
        {
            problems.Add($"{root.FullName} inherits rights from the folder above it.");
        }
        Check(root.FullName, rootSecurity, adminsOnly, adminsOnly, problems);

        var writable = new[] { folder.StateFolder, folder.WorkFolder, folder.LogFolder }
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();
        foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                problems.Add($"{entry.FullName} is a link to somewhere else.");
                continue;
            }
            var underWritable = writable.Any(w => string.Equals(w, entry.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
            // The settings file and anything else directly in the root: Administrators and SYSTEM only.
            Check(entry.FullName, Security(entry), adminsOnly, underWritable ? withService : adminsOnly, problems);
            if (entry is DirectoryInfo directory)
            {
                foreach (var inner in Below(directory, problems))
                {
                    var allowed = underWritable ? withService : adminsOnly;
                    Check(inner.FullName, Security(inner), allowed, allowed, problems);
                }
            }
        }
        return problems;
    }

    private static FileSystemSecurity Security(FileSystemInfo entry) => entry switch
    {
        DirectoryInfo d => d.GetAccessControl(),
        FileInfo f => f.GetAccessControl(),
        _ => throw new InvalidOperationException("Unknown kind of entry."),
    };

    private static void Check(string path, FileSystemSecurity security, HashSet<SecurityIdentifier> owners,
        HashSet<SecurityIdentifier> writers, List<string> problems)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owners.Contains(owner))
        {
            problems.Add($"{path} is owned by {Name(security.GetOwner(typeof(SecurityIdentifier)))}, not by Administrators.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteRights) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && writers.Contains(sid)) continue;
            problems.Add($"{Name(rule.IdentityReference)} may change {path}.");
        }
    }

    private static string Name(IdentityReference? identity)
    {
        if (identity is null) return "nobody";
        try
        {
            return identity.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return identity.Value;
        }
    }

    /// <summary>Owner Administrators, no rules of its own, inheriting from its folder.</summary>
    private static void Reset(FileSystemSecurity security)
    {
        security.SetOwner(Administrators);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
    }

    /// <summary>Writes an access list, then reads the owner back: only Administrators or SYSTEM will do.</summary>
    private static void Set(string path, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException or IOException)
        {
            throw new DataFolderAccessException(
                $"Could not make Administrators the owner of {path} ({ex.Message}). Run this from a command prompt opened with " +
                "\"Run as administrator\". If the folder was not made by the gateway, remove it and try again.", ex);
        }
        var owner = File.Exists(path)
            ? new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))
            : new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier sid || (sid != Administrators && sid != LocalSystem))
        {
            throw new DataFolderAccessException(
                $"{path} is still owned by {Name(owner)}, not by Administrators. The gateway will not keep its settings in a folder " +
                "someone else controls. Remove the folder and run this again as an administrator.");
        }
    }

    /// <summary>A junction or link inside the folder would let the access lists be set on, or settings be read from, somewhere else.</summary>
    private static void RefuseLinks(DirectoryInfo root)
    {
        var problems = new List<string>();
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) problems.Add($"{root.FullName} is a link to somewhere else.");
        _ = Below(root, problems).ToList();
        if (problems.Count > 0)
        {
            throw new DataFolderAccessException(string.Join(" ", problems) +
                " The gateway will not use a folder that contains links. Remove it and run this again as an administrator.");
        }
    }

    /// <summary>Everything below <paramref name="directory"/>, never following a link; links are reported in <paramref name="problems"/>.</summary>
    private static IEnumerable<FileSystemInfo> Below(DirectoryInfo directory, List<string>? problems = null)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                problems?.Add($"{entry.FullName} is a link to somewhere else.");
                continue;
            }
            yield return entry;
            if (entry is DirectoryInfo inner)
            {
                foreach (var nested in Below(inner, problems)) yield return nested;
            }
        }
    }

    private static FileSystemAccessRule Rule(IdentityReference who, FileSystemRights rights) =>
        new(who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
}

/// <summary>The data folder's owner or access lists cannot be made safe; the message says what to do.</summary>
internal sealed class DataFolderAccessException : Exception
{
    public DataFolderAccessException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
