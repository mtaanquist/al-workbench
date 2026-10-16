using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALWorkbench.Gateway.Contracts;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>The policy as reported to AL Workbench, and a number that moves each time the report changes.</summary>
public sealed record PolicySnapshot(GatewayPolicyReport Report, long Version);

/// <summary>
/// The local policy as <see cref="GatewayRunner"/> sees it: read at start, kept up to
/// date, and asked about every command before anything is done for it. The seam lets
/// the loop's tests run without a policy file; <see cref="GatewayPolicySource"/> is the
/// real one.
/// </summary>
public interface IGatewayPolicy
{
    /// <summary>
    /// Reads the policy at start. Returns the reasons the gateway must not start
    /// (someone other than administrators can change its files), or none.
    /// </summary>
    IReadOnlyList<string> Start();

    /// <summary>Reads the file again if it changed, checks the file permissions again, and returns what is in force.</summary>
    PolicySnapshot Refresh();

    /// <summary>Whether the policy allows <paramref name="command"/> right now. Refreshes first.</summary>
    PolicyDecision Check(GatewayCommandMessage command);
}

/// <summary>Where the policy lives and which computer the gateway is on.</summary>
/// <param name="PolicyFile">Full path of <c>gateway.toml</c>, beside the program.</param>
/// <param name="ProgramFolder">The folder the gateway program runs from.</param>
/// <param name="MachineName">This computer's name.</param>
public sealed record GatewayPolicyOptions(string PolicyFile, string ProgramFolder, string MachineName);

/// <summary>
/// <see cref="IGatewayPolicy"/> over <c>gateway.toml</c>. See
/// <c>.design/on-prem-gateway.md</c>, "The local policy file".
///
/// <para><b>Noticing changes.</b> The file is read again at every heartbeat (each
/// minute) and before every command, and parsed again only when its content hash
/// changed. Re-reading a small file is cheaper than it is to get a file watcher right
/// (missed events, editors that save by delete-and-rename, a watcher on a folder the
/// service account cannot list), and checking before each command means a command
/// can never run under a version of the file older than the one on disk.</para>
///
/// <para><b>A file with mistakes.</b> The gateway keeps running and keeps reading
/// the tiers the last good version listed, but refuses every change until the file is
/// fixed, and says so in its log and to AL Workbench. Keeping the last good version in
/// full would let an edit meant to take a permission away (say, after an incident) be
/// silently ignored because of a typo; refusing everything, reads included, would hide
/// the tiers from AL Workbench for no gain in safety. Reads change nothing. With no
/// good version yet (or no file at all), nothing is allowed.</para>
///
/// <para><b>File permissions.</b> Checked on the same schedule. At start, a problem
/// stops the gateway (<see cref="Start"/>); found later, every command, reads too,
/// is refused until it is fixed, because whoever could change the files may have.</para>
/// </summary>
public sealed class GatewayPolicySource : IGatewayPolicy
{
    private static readonly JsonSerializerOptions ReportJson = new();

    private readonly GatewayPolicyOptions _options;
    private readonly IFileAccessCheck _access;
    private readonly IGatewayClock _clock;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    private string? _fileHash;
    private PolicyParseResult? _parsed;
    private GatewayPolicy? _lastGood;
    private GatewayPolicy _inForce = GatewayPolicy.Empty;
    private string _state = GatewayPolicyStates.Missing;
    private IReadOnlyList<string> _problems = [];
    private string? _reportJson;
    private GatewayPolicyReport _report = new();
    private GatewayPolicyReport _fullReport = new();
    private long _version;
    /// <summary>Why the last read of the file failed; null once a read succeeds.</summary>
    private string? _readFailure;

    public GatewayPolicySource(GatewayPolicyOptions options, IFileAccessCheck access, IGatewayClock clock, TimeZoneInfo zone, ILogger logger)
    {
        _options = options;
        _access = access;
        _clock = clock;
        _zone = zone;
        _logger = logger;
    }

    /// <summary>The policy in force now: what <see cref="Check"/> judges against.</summary>
    public GatewayPolicy InForce
    {
        get { lock (_lock) return _inForce; }
    }

    public IReadOnlyList<string> Start()
    {
        Refresh();
        lock (_lock) return _state == GatewayPolicyStates.Unsafe ? _problems : [];
    }

    public PolicySnapshot Refresh()
    {
        lock (_lock)
        {
            ReadFile();
            var (state, problems, inForce) = Evaluate();
            _state = state;
            _problems = problems;
            _inForce = inForce;

            // Changes are noticed on the whole report; what is sent is cut down to size.
            var full = inForce.ToReport(state, problems);
            var json = JsonSerializer.Serialize(full, ReportJson);
            if (json != _reportJson)
            {
                _reportJson = json;
                _fullReport = full;
                _report = Bounded(full with { Problems = Capped(problems) });
                _version++;
                LogChange(state, problems, inForce);
            }
            return new PolicySnapshot(_report, _version);
        }
    }

    /// <summary>The whole report, every problem and tier included, for the check command customer IT runs; <see cref="Refresh"/> returns the one sent to AL Workbench.</summary>
    public GatewayPolicyReport FullReport
    {
        get { lock (_lock) return _fullReport; }
    }

    /// <summary>The first <see cref="GatewayPolicyReport.MaxProblems"/> problems, and a line counting the rest.</summary>
    public static IReadOnlyList<string> Capped(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count <= GatewayPolicyReport.MaxProblems) return problems;
        return
        [
            .. problems.Take(GatewayPolicyReport.MaxProblems),
            $"And {problems.Count - GatewayPolicyReport.MaxProblems} more; run the gateway's check command on its computer to see them all.",
        ];
    }

    /// <summary>
    /// The report as sent, or a short stand-in when it is over
    /// <see cref="GatewayPolicyReport.MaxJsonBytes"/>, so the heartbeat that carries it
    /// is never refused for its size and the gateway never goes offline over its own
    /// settings.
    /// </summary>
    public static GatewayPolicyReport Bounded(GatewayPolicyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ReportJson).Length;
        if (bytes <= GatewayPolicyReport.MaxJsonBytes) return report;
        return new GatewayPolicyReport
        {
            State = report.State,
            TooLarge = true,
            Problems =
            [
                $"The gateway's settings are too large to show in AL Workbench ({bytes / 1024} KB). The gateway enforces them as written; run its check command on its computer to see them.",
            ],
        };
    }

    public PolicyDecision Check(GatewayCommandMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Refresh();
        GatewayPolicy policy;
        string state;
        lock (_lock)
        {
            if (_state == GatewayPolicyStates.Unsafe)
            {
                return PolicyDecision.Refuse(
                    "The gateway's settings could have been changed by someone other than an administrator, so it refuses every command until customer IT fixes the file permissions. "
                    + string.Join(" ", _problems));
            }
            if (_state == GatewayPolicyStates.Missing)
            {
                return PolicyDecision.Refuse(
                    $"The customer's gateway has no settings file ({GatewayPolicyParser.FileName}) yet, so it refuses every command.");
            }
            policy = _inForce;
            state = _state;
        }
        var request = PolicyRequest.FromCommand(command, out var problem);
        if (request is null) return PolicyDecision.Refuse(problem ?? "The gateway could not read the command's parameters.");
        var decision = PolicyEnforcer.Check(policy, request, _clock.UtcNow, _zone);
        if (!decision.Allowed && state == GatewayPolicyStates.Invalid && request.Verb != GatewayVerb.RefreshInventory)
        {
            return PolicyDecision.Refuse(decision.Reason + " The customer's gateway.toml has mistakes or could not be read, so only reading is allowed until that is fixed.");
        }
        return decision;
    }

    /// <summary>Reads and parses the file when its content changed. Called under the lock.</summary>
    private void ReadFile()
    {
        if (!File.Exists(_options.PolicyFile))
        {
            _fileHash = null;
            _parsed = null;
            _readFailure = null;
            return;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_options.PolicyFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being saved right now, most likely, but possibly with a change that takes
            // a permission away: only reading is allowed until the file reads again
            // (Evaluate), and it is read again on the next round.
            _readFailure = ex.Message;
            return;
        }
        _readFailure = null;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash == _fileHash && _parsed is not null) return;
        _fileHash = hash;
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            _parsed = new PolicyParseResult(null, ["The file is not saved as UTF-8 text. Save it as UTF-8 (Notepad: File, Save As, Encoding UTF-8)."]);
            return;
        }
        _parsed = GatewayPolicyParser.Parse(text, _options.MachineName);
        if (_parsed.IsValid) _lastGood = _parsed.Policy;
    }

    /// <summary>The state, the problems and the policy in force, from the last parse and a fresh look at the files. Called under the lock.</summary>
    private (string State, IReadOnlyList<string> Problems, GatewayPolicy InForce) Evaluate()
    {
        string state;
        var problems = new List<string>();
        GatewayPolicy inForce;
        if (_readFailure is not null)
        {
            // The file is there but could not be read: what it says now is unknown, so
            // the tiers last read stay readable and nothing changes until it reads.
            state = GatewayPolicyStates.Invalid;
            problems.Add($"The gateway could not read {_options.PolicyFile} ({_readFailure}). Until it can, it only reads from the tiers the file listed before.");
            inForce = (_parsed?.Policy ?? _lastGood)?.ReadOnly() ?? GatewayPolicy.Empty;
        }
        else if (_parsed is null)
        {
            state = GatewayPolicyStates.Missing;
            problems.Add($"There is no {GatewayPolicyParser.FileName} in {_options.ProgramFolder}, so the gateway refuses every command.");
            inForce = GatewayPolicy.Empty;
        }
        else if (!_parsed.IsValid)
        {
            state = GatewayPolicyStates.Invalid;
            problems.AddRange(_parsed.Errors);
            inForce = _lastGood?.ReadOnly() ?? GatewayPolicy.Empty;
        }
        else
        {
            var missing = _parsed.Policy!.Scripts.Where(s => !_access.Exists(s)).Select(s => $"The script {s} does not exist.").ToList();
            state = missing.Count > 0 ? GatewayPolicyStates.Invalid : GatewayPolicyStates.Valid;
            problems.AddRange(missing);
            inForce = missing.Count > 0 ? _parsed.Policy.ReadOnly() : _parsed.Policy;
        }

        var unsafeFiles = AccessProblems(_parsed?.Policy ?? _lastGood);
        if (unsafeFiles.Count > 0)
        {
            return (GatewayPolicyStates.Unsafe, unsafeFiles, inForce);
        }
        return (state, problems, inForce);
    }

    /// <summary>
    /// The program folder with everything in it (the program, its DLLs and the policy
    /// file), and each script with its folder: who else can change them. The whole
    /// program folder, because replacing one DLL the service loads is as good as
    /// editing the policy.
    /// </summary>
    private List<string> AccessProblems(GatewayPolicy? policy)
    {
        var problems = FileAccessChecks.FolderAndContents(_access, _options.ProgramFolder);
        var paths = new List<string>();
        if (File.Exists(_options.PolicyFile)) paths.Add(_options.PolicyFile);
        foreach (var script in policy?.Scripts ?? [])
        {
            if (!_access.Exists(script)) continue;
            paths.Add(script);
            var folder = WindowsFolderOf(script);
            if (folder is not null) paths.Add(folder);
        }
        problems.AddRange(paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(_access.Problem)
            .OfType<string>());
        return problems.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The folder of a Windows path, worked out by hand so it behaves the same when tests run on Linux.</summary>
    public static string? WindowsFolderOf(string path)
    {
        var cut = path.LastIndexOf('\\');
        if (cut <= 0) return null;
        var folder = path[..cut];
        return folder.EndsWith(':') ? folder + "\\" : folder;
    }

    private void LogChange(string state, IReadOnlyList<string> problems, GatewayPolicy inForce)
    {
        var listed = string.Join(" ", problems);
        switch (state)
        {
            case GatewayPolicyStates.Valid:
                _logger.PolicyLoaded(_options.PolicyFile, inForce.Instances.Count);
                break;
            case GatewayPolicyStates.Invalid:
                _logger.PolicyInvalid(_options.PolicyFile, listed);
                break;
            case GatewayPolicyStates.Missing:
                _logger.PolicyMissing(_options.PolicyFile);
                break;
            default:
                _logger.PolicyUnsafe(listed);
                break;
        }
    }
}
