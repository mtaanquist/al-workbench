using System.Runtime.CompilerServices;
using ALDevToolbox.Services.Account;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Hashes recovery codes at bcrypt's minimum work factor for the whole test
/// run. Issuing codes is ten hashes and each guess up to ten verifies, which at
/// the production factor made the two-step sign-in tests take minutes (#1584).
/// <c>RecoveryCodeServiceTests</c> pins the production value.
/// </summary>
internal static class CheapHashDefaults
{
    [ModuleInitializer]
    internal static void Apply() => RecoveryCodeService.WorkFactor = TestPasswords.WorkFactor;
}
