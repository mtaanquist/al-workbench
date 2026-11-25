using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ALWorkbench.Gateway;

/// <summary>
/// The service account's membership of the local Administrators group. Business
/// Central's administration cmdlets refuse anyone who is not a local administrator
/// ("You need to be a member of the local Administrators group on the server to run
/// this cmdlet"), with no narrower right to grant instead, so <c>install</c> adds the
/// account, <c>check</c> says whether it is there, and <c>uninstall</c> takes it out
/// again. See <c>.design/on-prem-gateway.md</c>, "The Windows service, as built".
///
/// <para>The group is named by its well-known SID, not "Administrators", because the
/// name is translated on a Danish or German Windows. Members are passed as SIDs for
/// the same reason. A virtual account (<c>NT SERVICE\ALWorkbenchGateway</c>) only
/// resolves to a SID once its service exists, so add after creating the service and
/// remove before deleting it.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LocalAdministrators
{
    private const int ErrorMemberNotInAlias = 1377;
    private const int ErrorMemberInAlias = 1378;
    private const int NerrSuccess = 0;
    private const uint MaxPreferredLength = 0xFFFFFFFF;

    private static readonly SecurityIdentifier Group = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>The group's name on this computer, e.g. "Administrators" or "Administratorer".</summary>
    public static string GroupName => ((NTAccount)Group.Translate(typeof(NTAccount))).Value.Split('\\')[^1];

    /// <summary>Adds the account. Already being a member is not an error.</summary>
    public static void Add(string account) => Change(account, add: true);

    /// <summary>Removes the account. Not being a member is not an error.</summary>
    public static void Remove(string account) => Change(account, add: false);

    /// <summary>Whether the account is a direct member of the group. Null when the account cannot be resolved.</summary>
    public static bool? IsMember(string account)
    {
        if (Resolve(account) is not { } sid) return null;
        var status = NetLocalGroupGetMembers(null, GroupName, 0, out var buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);
        try
        {
            if (status != NerrSuccess) throw new Win32Exception(status);
            var size = Marshal.SizeOf<LocalGroupMembersInfo0>();
            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupMembersInfo0>(buffer + i * size);
                if (new SecurityIdentifier(entry.Sid) == sid) return true;
            }
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    private static void Change(string account, bool add)
    {
        var sid = Resolve(account) ?? throw new InvalidOperationException($"Windows does not know the account {account}.");
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var member = new LocalGroupMembersInfo0 { Sid = handle.AddrOfPinnedObject() };
            var status = add
                ? NetLocalGroupAddMembers(null, GroupName, 0, ref member, 1)
                : NetLocalGroupDelMembers(null, GroupName, 0, ref member, 1);
            if (status is NerrSuccess or ErrorMemberInAlias or ErrorMemberNotInAlias) return;
            throw new Win32Exception(status, $"Could not {(add ? "add" : "remove")} {account} {(add ? "to" : "from")} the local {GroupName} group: {new Win32Exception(status).Message}");
        }
        finally
        {
            handle.Free();
        }
    }

    private static SecurityIdentifier? Resolve(string account)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr Sid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? serverName, string groupName, int level, ref LocalGroupMembersInfo0 buffer, int totalEntries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupDelMembers(string? serverName, string groupName, int level, ref LocalGroupMembersInfo0 buffer, int totalEntries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer,
        uint preferredMaxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
