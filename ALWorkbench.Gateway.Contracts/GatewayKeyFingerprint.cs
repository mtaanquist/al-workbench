using System.Security.Cryptography;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The short code a person compares by eye before approving a gateway: the gateway
/// prints it when it registers, and the Gateways page shows it beside Approve. Both
/// compute it here, so the two cannot disagree on the format.
///
/// <para>It is the first 80 bits of the SHA-256 of the public key's DER
/// <c>SubjectPublicKeyInfo</c>, in five groups of four hex digits
/// (<c>1A2B-3C4D-5E6F-7A8B-9C0D</c>). Finding a second key with the same 80 bits is
/// out of reach, which is all a leaked registration code would let an attacker try.</para>
/// </summary>
public static class GatewayKeyFingerprint
{
    private const int Bytes = 10;

    public static string Compute(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(subjectPublicKeyInfo, hash);
        var hex = Convert.ToHexString(hash[..Bytes]);
        return string.Join('-', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
