using ALWorkbench.Gateway.Core.Policy;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <see cref="AccessRules"/>: the rule the gateway applies to a Windows access list,
/// over plain values so it runs here. Only administrator accounts may own or change
/// its files; on a folder, an entry passed on to what is created inside counts like
/// one on the folder; deny entries and placeholder accounts add nobody.
/// </summary>
public sealed class GatewayAccessRulesTests
{
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string ServiceAccount = "S-1-5-80-1111111111-2222222222-3333333333-4444444444-5555555555";

    private const int FullControl = 0x1F01FF;
    private const int ReadAndExecute = 0x1200A9;
    private const int Modify = 0x1301BF;
    private const int WriteData = 0x2;
    private const int AppendData = 0x4;
    private const int ChangePermissions = 0x40000;
    private const int TakeOwnership = 0x80000;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;
    private const int GenericRead = unchecked((int)0x80000000);

    private static AccessEntry Allow(string sid, int rights, bool inheritOnly = false, bool toChildren = false) =>
        new(sid, rights, true, inheritOnly, toChildren);

    /// <summary>What Program Files gives a folder installed in it.</summary>
    private static readonly AccessEntry[] ProgramFilesDefaults =
    [
        Allow(AccessRules.TrustedInstallerSid, FullControl),
        Allow(AccessRules.SystemSid, FullControl, toChildren: true),
        Allow(AccessRules.AdministratorsSid, FullControl, toChildren: true),
        Allow(Users, ReadAndExecute, toChildren: true),
        Allow(AccessRules.CreatorOwnerSid, GenericAll, inheritOnly: true, toChildren: true),
        Allow("S-1-15-2-1", ReadAndExecute, toChildren: true),
    ];

    /// <summary>What Windows gives a system drive's root: users may create folders there, and own what they create.</summary>
    private static readonly AccessEntry[] DriveRootDefaults =
    [
        Allow(AccessRules.SystemSid, FullControl, toChildren: true),
        Allow(AccessRules.AdministratorsSid, FullControl, toChildren: true),
        Allow(Users, ReadAndExecute, toChildren: true),
        Allow(AuthenticatedUsers, AppendData),
        Allow(AuthenticatedUsers, Modify, inheritOnly: true, toChildren: true),
        Allow(AccessRules.CreatorOwnerSid, GenericAll, inheritOnly: true, toChildren: true),
    ];

    [Fact]
    public void A_drive_root_with_its_defaults_is_a_safe_folder_above_a_script()
    {
        // Creating a new folder in C:\ is allowed; renaming or replacing an existing one
        // is not, and an inherit-only entry reaches only folders created later.
        AccessRules.EvaluateAncestor(AccessRules.TrustedInstallerSid, DriveRootDefaults).Others.Should().BeEmpty();
        AccessRules.Evaluate(AccessRules.TrustedInstallerSid, DriveRootDefaults, isFolder: true).Others.Should().Contain(AuthenticatedUsers,
            "by the full rule the root is open, which is why the folders above a script get their own rule");
    }

    [Theory]
    [InlineData(Modify)]
    [InlineData(0x40)] // delete child
    [InlineData(0x10000)] // delete
    [InlineData(ChangePermissions)]
    [InlineData(TakeOwnership)]
    public void A_folder_above_a_script_others_can_rename_or_replace_things_in_is_not_safe(int rights)
    {
        AccessRules.EvaluateAncestor(AccessRules.AdministratorsSid, [Allow(Users, rights)]).Others.Should().Equal(Users);
    }

    [Fact]
    public void A_folder_above_a_script_owned_by_someone_else_is_not_safe()
    {
        AccessRules.EvaluateAncestor(Users, []).OwnerTrusted.Should().BeFalse();
    }

    [Fact]
    public void A_folder_with_the_Program_Files_defaults_is_safe()
    {
        var verdict = AccessRules.Evaluate(AccessRules.AdministratorsSid, ProgramFilesDefaults, isFolder: true);

        verdict.OwnerTrusted.Should().BeTrue();
        verdict.Others.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AccessRules.AdministratorsSid, true)]
    [InlineData(AccessRules.SystemSid, true)]
    [InlineData(AccessRules.TrustedInstallerSid, true)]
    [InlineData(ServiceAccount, false)]
    [InlineData(Users, false)]
    [InlineData(null, false)]
    public void Only_an_administrator_account_may_own_it_not_even_the_gateways_own(string? owner, bool trusted)
    {
        AccessRules.Evaluate(owner, [], isFolder: false).OwnerTrusted.Should().Be(trusted);
    }

    [Theory]
    [InlineData(WriteData)]
    [InlineData(AppendData)]
    [InlineData(ChangePermissions)]
    [InlineData(TakeOwnership)]
    [InlineData(Modify)]
    [InlineData(FullControl)]
    [InlineData(GenericAll)]
    [InlineData(GenericWrite)]
    public void Any_right_that_changes_it_counts(int rights)
    {
        AccessRules.Evaluate(AccessRules.AdministratorsSid, [Allow(Users, rights)], isFolder: false)
            .Others.Should().Equal(Users);
    }

    [Theory]
    [InlineData(ReadAndExecute)]
    [InlineData(GenericRead)]
    public void Reading_does_not_count(int rights)
    {
        AccessRules.Evaluate(AccessRules.AdministratorsSid, [Allow(Users, rights)], isFolder: false)
            .Others.Should().BeEmpty();
    }

    [Fact]
    public void The_gateways_service_account_with_write_access_counts()
    {
        AccessRules.Evaluate(AccessRules.AdministratorsSid, [Allow(ServiceAccount, Modify, toChildren: true)], isFolder: true)
            .Others.Should().Equal(ServiceAccount);
    }

    [Fact]
    public void On_a_folder_an_inherit_only_entry_passed_to_new_files_counts()
    {
        // C:\'s default: Authenticated Users may modify whatever is created below.
        var entries = ProgramFilesDefaults.Append(Allow(AuthenticatedUsers, Modify, inheritOnly: true, toChildren: true));

        AccessRules.Evaluate(AccessRules.AdministratorsSid, entries, isFolder: true)
            .Others.Should().Equal(AuthenticatedUsers);
    }

    [Fact]
    public void On_a_file_an_inherit_only_entry_applies_to_nothing()
    {
        AccessRules.Evaluate(AccessRules.AdministratorsSid, [Allow(AuthenticatedUsers, Modify, inheritOnly: true, toChildren: true)], isFolder: false)
            .Others.Should().BeEmpty();
    }

    [Fact]
    public void Deny_entries_and_creator_placeholders_add_nobody()
    {
        AccessEntry[] entries =
        [
            new(Users, FullControl, Allow: false, InheritOnly: false, InheritsToChildren: true),
            Allow(AccessRules.CreatorOwnerSid, GenericAll, inheritOnly: true, toChildren: true),
            Allow(AccessRules.CreatorGroupSid, Modify, inheritOnly: true, toChildren: true),
        ];

        AccessRules.Evaluate(AccessRules.AdministratorsSid, entries, isFolder: true).Others.Should().BeEmpty();
    }

    [Fact]
    public void Each_account_is_named_once_in_the_order_found()
    {
        AccessEntry[] entries =
        [
            Allow(Users, WriteData),
            Allow(AuthenticatedUsers, Modify, toChildren: true),
            Allow(Users, AppendData, toChildren: true),
        ];

        AccessRules.Evaluate(AccessRules.AdministratorsSid, entries, isFolder: true).Others.Should().Equal(Users, AuthenticatedUsers);
    }
}
