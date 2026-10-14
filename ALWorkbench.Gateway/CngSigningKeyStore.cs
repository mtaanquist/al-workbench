using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using ALWorkbench.Gateway.Core;

namespace ALWorkbench.Gateway;

/// <summary>
/// <see cref="ISigningKeyStore"/> on the Windows key store (CNG). Keys are machine
/// keys, created non-exportable: the private half can sign but can never be read out,
/// not even by an administrator. They go in the TPM (Microsoft Platform Crypto
/// Provider) where the machine has one, otherwise in the Microsoft Software Key
/// Storage Provider. Each key's access list allows Administrators and SYSTEM full
/// control and the service account read, which is what using it to sign needs;
/// nobody else can open it. See <c>.design/on-prem-gateway.md</c>, "Registration and
/// authentication".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CngSigningKeyStore : ISigningKeyStore
{
    /// <summary>NCrypt's DACL_SECURITY_INFORMATION: which part of the security descriptor a "Security Descr" write sets.</summary>
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)4;
    private const string SecurityDescriptorProperty = "Security Descr";

    private static readonly CngProvider[] Providers =
    [
        CngProvider.MicrosoftPlatformCryptoProvider,
        CngProvider.MicrosoftSoftwareKeyStorageProvider,
    ];

    /// <summary>Where the last <see cref="Create"/> put the key, in words for the console.</summary>
    public string? LastCreatedIn { get; private set; }

    /// <summary>Why the TPM was not used for the last key, when it was not.</summary>
    public string? PlatformFallbackReason { get; private set; }

    public bool Exists(string name) => FindProvider(name) is not null;

    public byte[] Create(string name, string? serviceAccount)
    {
        var descriptor = Descriptor(serviceAccount);
        try
        {
            var key = CreateIn(CngProvider.MicrosoftPlatformCryptoProvider, name, descriptor);
            LastCreatedIn = "the TPM (Microsoft Platform Crypto Provider)";
            PlatformFallbackReason = null;
            return key;
        }
        catch (CryptographicException ex)
        {
            // No TPM, a TPM the provider cannot use, or one that refuses machine keys.
            PlatformFallbackReason = ex.Message;
        }
        var software = CreateIn(CngProvider.MicrosoftSoftwareKeyStorageProvider, name, descriptor);
        LastCreatedIn = "the Windows key store (Microsoft Software Key Storage Provider)";
        return software;
    }

    public ECDsa Open(string name)
    {
        using var key = OpenKey(name);
        return new ECDsaCng(key);
    }

    public byte[] ExportPublicKey(string name)
    {
        using var ecdsa = Open(name);
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    public void GrantAccess(string name, string serviceAccount)
    {
        using var key = OpenKey(name);
        key.SetProperty(new CngProperty(SecurityDescriptorProperty, Descriptor(serviceAccount), DaclSecurityInformation));
    }

    public void Delete(string name)
    {
        if (FindProvider(name) is null) return;
        using var key = OpenKey(name);
        key.Delete();
    }

    private static byte[] CreateIn(CngProvider provider, string name, byte[] descriptor)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = provider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        parameters.Parameters.Add(new CngProperty(SecurityDescriptorProperty, descriptor, DaclSecurityInformation));
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, parameters);
        using var ecdsa = new ECDsaCng(key);
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    private static CngKey OpenKey(string name)
    {
        var provider = FindProvider(name)
            ?? throw new InvalidOperationException($"The gateway's key '{name}' is not in the Windows key store. Register the gateway again.");
        return CngKey.Open(name, provider, CngKeyOpenOptions.MachineKey);
    }

    private static CngProvider? FindProvider(string name)
    {
        foreach (var provider in Providers)
        {
            try
            {
                if (CngKey.Exists(name, provider, CngKeyOpenOptions.MachineKey)) return provider;
            }
            catch (CryptographicException)
            {
                // The platform provider is missing on a machine without a TPM.
            }
        }
        return null;
    }

    /// <summary>
    /// Protected access list: Administrators and SYSTEM full control, the service
    /// account read (enough to sign), nobody else, nothing inherited.
    /// </summary>
    internal static byte[] Descriptor(string? serviceAccount)
    {
        var sddl = "D:P(A;;GA;;;BA)(A;;GA;;;SY)";
        if (!string.IsNullOrWhiteSpace(serviceAccount)) sddl += $"(A;;GR;;;{AccountSid(serviceAccount)})";
        var descriptor = new RawSecurityDescriptor(sddl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// The SID of a Windows account. A service's virtual account (<c>NT SERVICE\x</c>)
    /// is computed rather than looked up, because it exists only once the service does.
    /// </summary>
    internal static string AccountSid(string account)
    {
        const string virtualPrefix = @"NT SERVICE\";
        if (account.StartsWith(virtualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceSid.For(account[virtualPrefix.Length..]);
        }
        return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
    }
}
