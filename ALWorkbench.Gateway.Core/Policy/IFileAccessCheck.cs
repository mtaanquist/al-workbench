namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>
/// Who can change a file or folder. Whoever can write the gateway's program folder
/// (its program and every DLL it loads), its policy file, a script the file names, or
/// the Business Central administration module the gateway loads for a tier gets code
/// running as a Business Central administrator, so each of them must be changeable by
/// administrators only: Administrators, SYSTEM and Windows' own installer account, and
/// owned by one of them. Not even the gateway's service account, so a compromised
/// gateway process cannot widen its own policy. The Windows implementation reads the
/// access list (<c>ALWorkbench.Gateway/WindowsFileAccessCheck</c>) and judges it with
/// <see cref="AccessRules"/>; tests use a fake. See <c>.design/on-prem-gateway.md</c>,
/// "File permissions are part of the policy".
/// </summary>
public interface IFileAccessCheck
{
    /// <summary>
    /// Null when only administrators can change <paramref name="path"/> (a file, or a
    /// folder, which includes adding, renaming and deleting what is in it, and what is
    /// passed on to files created in it). Otherwise one sentence for customer IT naming
    /// who else can, e.g. <c>BUILTIN\Users can change C:\ALWorkbench\before-deploy.ps1.</c>
    /// </summary>
    string? Problem(string path);

    /// <summary>Whether the file exists. Here rather than <c>File.Exists</c> so the Windows paths in a policy file can be tested on Linux.</summary>
    bool Exists(string path);

    /// <summary>Every file and folder inside <paramref name="folder"/>, at any depth. Empty when it does not exist.</summary>
    IReadOnlyList<string> Contents(string folder);
}

/// <summary>Checks over whole folders, shared by the policy file's check and the PowerShell runner's.</summary>
public static class FileAccessChecks
{
    /// <summary>How many files inside one folder are named before the rest are counted.</summary>
    public const int NamedPerFolder = 5;

    /// <summary>
    /// The problems with <paramref name="folder"/> and everything in it: a program or
    /// module loads its DLLs from there, so one file anyone can replace is as bad as the
    /// folder. A folder where everything is open names a few files and counts the rest,
    /// so the report stays readable.
    /// </summary>
    public static List<string> FolderAndContents(IFileAccessCheck access, string folder)
    {
        ArgumentNullException.ThrowIfNull(access);
        var problems = new List<string>();
        if (access.Problem(folder) is { } own) problems.Add(own);
        var inside = access.Contents(folder).Select(access.Problem).OfType<string>().ToList();
        problems.AddRange(inside.Take(NamedPerFolder));
        if (inside.Count > NamedPerFolder)
        {
            problems.Add($"{inside.Count - NamedPerFolder} more files or folders in {folder} can be changed by someone other than administrators.");
        }
        return problems;
    }
}
