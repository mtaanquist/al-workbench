using System.Runtime.CompilerServices;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Services.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Gives the hosts the suite boots a key folder of this test process's own, set up
/// before the first one starts. Without it they fall back to the production default
/// (<c>/var/lib/aldevtoolbox/dp-keys</c>), shared with every other test run on the
/// machine. Endpoint test hosts boot in parallel (#1535), and the boot-time key files
/// are written with a plain check-then-write, so two first boots could race: one host
/// reading a key file the other is halfway through writing. Creating the OAuth keys
/// and the deployment id here, once, leaves every host only reading them.
///
/// <para>Set once and never restored, so this is not a knob a test flips. A key
/// directory already set in the environment is left alone.</para>
/// </summary>
internal static class KeyDirectoryDefaults
{
    [ModuleInitializer]
    internal static void Apply()
    {
        // OAUTH_KEY_DIR falls back to DATA_PROTECTION_KEY_DIR (BootKeyPaths), so
        // the one variable covers both.
        if (Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY_DIR") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("OAUTH_KEY_DIR") is { Length: > 0 })
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "aldt-test-keys-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("DATA_PROTECTION_KEY_DIR", dir);

        OAuthKeyMaterial.LoadOrCreate(dir, NullLogger.Instance);
        DeploymentIdentity.LoadOrCreate(dir, NullLogger.Instance);
    }
}
