using System.Security.Cryptography;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>Pins the shared GitHub App key the connect-the-app helpers use (#1584).</summary>
public sealed class TestKeysTests
{
    [Fact]
    public void The_shared_GitHub_App_key_is_one_rsa_2048_key_for_the_process()
    {
        TestKeys.GitHubAppPem.Should().BeSameAs(TestKeys.GitHubAppPem);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(TestKeys.GitHubAppPem);
        rsa.KeySize.Should().Be(2048);
        TestKeys.GitHubAppPem.Should().StartWith("-----BEGIN RSA PRIVATE KEY-----",
            "the app stores the PKCS#1 PEM GitHub hands out");
    }
}
