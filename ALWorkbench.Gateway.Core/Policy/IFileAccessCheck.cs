namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>
/// Who can change a file or folder. Whoever can write the gateway's program folder,
/// its policy file or a script the file names gets code running as a Business
/// Central administrator, so each of them must be changeable by administrators only:
/// Administrators, SYSTEM and Windows' own installer account, and owned by one of
/// them. Not even the gateway's service account, so a compromised gateway process
/// cannot widen its own policy. The Windows implementation reads the access list
/// (<c>ALWorkbench.Gateway/WindowsFileAccessCheck</c>); tests use a fake. See
/// <c>.design/on-prem-gateway.md</c>, "File permissions are part of the policy".
/// </summary>
public interface IFileAccessCheck
{
    /// <summary>
    /// Null when only administrators can change <paramref name="path"/> (a file, or a
    /// folder, which includes adding, renaming and deleting what is in it). Otherwise
    /// one sentence for customer IT naming who else can, e.g.
    /// <c>BUILTIN\Users can change C:\ALWorkbench\before-deploy.ps1.</c>
    /// </summary>
    string? Problem(string path);

    /// <summary>Whether the file exists. Here rather than <c>File.Exists</c> so the Windows paths in a policy file can be tested on Linux.</summary>
    bool Exists(string path);
}
