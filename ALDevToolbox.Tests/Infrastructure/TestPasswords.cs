using System.Collections.Concurrent;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Password hashes for seeding users the real sign-in accepts, made at bcrypt's
/// minimum work factor and cached for the process (#1552).
///
/// <para>At the app's work factor a hash and a verify each cost about a third
/// of a second, and most endpoint tests seed a user and then sign in. bcrypt
/// stores the work factor in the hash and <c>BCrypt.Verify</c> reads it back,
/// so a sign-in against one of these hashes is cheap too. The app never
/// rehashes a stored password, so the low factor cannot leak into anything a
/// test asserts on. Tests about hashing itself use <c>AuthService.HashPassword</c>.</para>
/// </summary>
public static class TestPasswords
{
    /// <summary>bcrypt's minimum.</summary>
    public const int WorkFactor = 4;

    private static readonly ConcurrentDictionary<string, string> Hashes = new();

    /// <summary>A bcrypt hash of <paramref name="password"/> that sign-in verifies.</summary>
    public static string Hash(string password) =>
        Hashes.GetOrAdd(password, p => BCrypt.Net.BCrypt.HashPassword(p, WorkFactor));
}
