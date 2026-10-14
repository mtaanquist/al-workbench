namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The layout of the gateway's data folder (on Windows
/// <c>%ProgramData%\AL Workbench Gateway</c>). The root and <see cref="SettingsFile"/>
/// are written only by administrators (<c>register</c>, <c>install</c>); the service
/// account may write <see cref="StateFolder"/>, <see cref="WorkFolder"/> and
/// <see cref="LogFolder"/> and nothing else.
/// </summary>
public sealed record GatewayDataFolder(string Root)
{
    /// <summary>Which AL Workbench, which gateway id, which key. Not secret.</summary>
    public string SettingsFile => Path.Combine(Root, "gateway.json");

    /// <summary>The run-once list and anything else the service keeps between runs.</summary>
    public string StateFolder => Path.Combine(Root, "state");

    public string RunOnceFile => Path.Combine(StateFolder, "started-commands.json");

    /// <summary>Each running command's downloaded files, removed when it ends.</summary>
    public string WorkFolder => Path.Combine(Root, "work");

    /// <summary>The daily log files customer IT reads.</summary>
    public string LogFolder => Path.Combine(Root, "logs");
}
