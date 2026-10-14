using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The security identifier of a Windows service's virtual account
/// (<c>NT SERVICE\&lt;name&gt;</c>). Windows derives it from the service name alone
/// - <c>S-1-5-80-</c> followed by the SHA-1 of the upper-cased name in UTF-16, read
/// as five 32-bit numbers - so it is known before the service exists. That lets
/// <c>register</c>, which may run before <c>install</c>, grant the key to the account
/// the service will run under.
/// </summary>
public static class ServiceSid
{
    public static string For(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5)
            .Select(i => BitConverter.ToUInt32(hash, i * 4).ToString(CultureInfo.InvariantCulture));
        return "S-1-5-80-" + string.Join('-', parts);
    }
}
