using System.Security.Cryptography;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Key material shared by the whole test process (#1584).
/// </summary>
public static class TestKeys
{
    private static readonly Lazy<string> GitHubAppKey = new(() =>
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    });

    /// <summary>
    /// An RSA-2048 private key in PEM, for tests that connect the GitHub App.
    /// Making one costs a fifth of a second or more, and about fifty classes
    /// connected the app with a fresh key in nearly every test without ever
    /// reading it back. A test that does check the key itself (a JWT signature,
    /// a key rotation) makes its own.
    /// </summary>
    public static string GitHubAppPem => GitHubAppKey.Value;
}
