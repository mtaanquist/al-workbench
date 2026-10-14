using System.Security.Cryptography;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// Where the gateway's ECDSA P-256 private key lives. On Windows it is the Windows
/// key store (the TPM where the machine has one), non-exportable and readable only by
/// the gateway's service account, Administrators and SYSTEM; tests use an in-memory
/// key. The private half never leaves the store: callers get an <see cref="ECDsa"/>
/// that signs inside it. See <c>.design/on-prem-gateway.md</c>, "Registration and
/// authentication".
///
/// <para>Keys are named, so registering again (a rotation code) can create the new
/// key beside the old one and only delete the old one once AL Workbench has accepted
/// the new one. A failed exchange then leaves the working key in place.</para>
/// </summary>
public interface ISigningKeyStore
{
    /// <summary>True when a key called <paramref name="name"/> exists.</summary>
    bool Exists(string name);

    /// <summary>
    /// Creates a new P-256 key called <paramref name="name"/>, readable by
    /// <paramref name="serviceAccount"/> (a Windows account name, or null for none
    /// beyond Administrators and SYSTEM), and returns its public half as DER
    /// <c>SubjectPublicKeyInfo</c>. Fails when the name is taken.
    /// </summary>
    byte[] Create(string name, string? serviceAccount);

    /// <summary>Opens the key to sign with. The caller disposes the returned object; the key stays in the store.</summary>
    ECDsa Open(string name);

    /// <summary>The public half as DER <c>SubjectPublicKeyInfo</c>.</summary>
    byte[] ExportPublicKey(string name);

    /// <summary>
    /// Lets <paramref name="serviceAccount"/> use the key, replacing whichever service
    /// account could before. Used when the service is installed under another account
    /// after registering.
    /// </summary>
    void GrantAccess(string name, string serviceAccount);

    /// <summary>Deletes the key. Nothing happens when it does not exist.</summary>
    void Delete(string name);
}
