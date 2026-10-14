using System.Diagnostics;
using System.Runtime.Versioning;
using ALWorkbench.Gateway.Core.PowerShell;
using Microsoft.Win32;

namespace ALWorkbench.Gateway;

/// <summary>
/// Finds a local tier's install folder from its Windows service
/// (<c>HKLM\SYSTEM\CurrentControlSet\Services\MicrosoftDynamicsNavServer$&lt;instance&gt;</c>,
/// value <c>ImagePath</c>), and its version from the server program's file version.
/// Reading the service rather than guessing a path is what lets tiers of several
/// versions on one machine each load their own administration module.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryServiceTierLocator : IServiceTierLocator
{
    public ServiceTierInstall? Find(string instance)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceTierInstall.ServiceName(instance));
        var imagePath = key?.GetValue("ImagePath") as string;
        var program = ServiceTierInstall.ProgramFromImagePath(imagePath);
        var folder = ServiceTierInstall.FolderFromImagePath(imagePath);
        if (program is null || folder is null) return null;

        Version? version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(program);
            version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (FileNotFoundException)
        {
            // The service points at a program that is gone; the version stays unknown.
        }
        return new ServiceTierInstall(folder, version);
    }
}
