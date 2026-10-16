namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>
/// One entry of a Windows access list, in plain values so the rule that judges it runs
/// (and is tested) on any platform.
/// </summary>
/// <param name="Sid">The account's security identifier, e.g. <c>S-1-5-32-545</c>.</param>
/// <param name="Rights">The access mask.</param>
/// <param name="Allow">True for an allow entry, false for a deny entry.</param>
/// <param name="InheritOnly">The entry does not apply to the object itself, only to what is created inside it.</param>
/// <param name="InheritsToChildren">The entry is passed on to files or folders inside the object (object or container inherit).</param>
public sealed record AccessEntry(string Sid, int Rights, bool Allow, bool InheritOnly, bool InheritsToChildren);

/// <summary>What <see cref="AccessRules.Evaluate"/> found.</summary>
/// <param name="OwnerTrusted">Whether the owner is an administrator account.</param>
/// <param name="Others">The accounts besides administrators that can change the object or what is put inside it, in the order found, each once.</param>
public sealed record AccessVerdict(bool OwnerTrusted, IReadOnlyList<string> Others);

/// <summary>
/// The rule <c>WindowsFileAccessCheck</c> applies to an access list, separated from
/// reading it so it can be tested without Windows. See
/// <c>.design/on-prem-gateway.md</c>, "File permissions are part of the policy".
///
/// <para>An object is safe when an administrator account owns it and no allow entry
/// gives anyone else a right that changes it. For a folder that includes entries that
/// apply only to what is created inside it (inherit-only): whoever gets write access
/// to every new file in the program folder can replace a DLL the gateway loads, so an
/// entry passed on to children counts the same as one on the folder. On a file, an
/// inherit-only entry applies to nothing and is ignored. Deny entries only take rights
/// away and are ignored.</para>
///
/// <para>CREATOR OWNER and CREATOR GROUP are placeholders replaced by whoever creates
/// the child, who already needed a right to create it there; they add nobody and are
/// ignored.</para>
/// </summary>
public static class AccessRules
{
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string SystemSid = "S-1-5-18";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const string CreatorOwnerSid = "S-1-3-0";
    public const string CreatorGroupSid = "S-1-3-1";

    private const int WriteData = 0x2;
    private const int AppendData = 0x4;
    private const int WriteExtendedAttributes = 0x10;
    private const int DeleteChild = 0x40;
    private const int WriteAttributes = 0x100;
    private const int Delete = 0x10000;
    private const int ChangePermissions = 0x40000;
    private const int TakeOwnership = 0x80000;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>Every right that changes a file or folder, or what it holds.</summary>
    public const int ChangeRights = WriteData | AppendData | WriteExtendedAttributes | DeleteChild | WriteAttributes
        | Delete | ChangePermissions | TakeOwnership | GenericAll | GenericWrite;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        AdministratorsSid, SystemSid, TrustedInstallerSid,
    };

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        CreatorOwnerSid, CreatorGroupSid,
    };

    public static bool IsTrusted(string? sid) => sid is not null && Trusted.Contains(sid);

    public static AccessVerdict Evaluate(string? ownerSid, IEnumerable<AccessEntry> entries, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var others = new List<string>();
        foreach (var entry in entries)
        {
            if (!entry.Allow) continue;
            if (entry.InheritOnly && !(isFolder && entry.InheritsToChildren)) continue;
            if (Trusted.Contains(entry.Sid) || Placeholders.Contains(entry.Sid)) continue;
            if ((entry.Rights & ChangeRights) == 0) continue;
            if (!others.Contains(entry.Sid, StringComparer.OrdinalIgnoreCase)) others.Add(entry.Sid);
        }
        return new AccessVerdict(IsTrusted(ownerSid), others);
    }
}
