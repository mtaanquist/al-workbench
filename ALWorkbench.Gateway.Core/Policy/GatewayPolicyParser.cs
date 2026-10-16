using System.Text.RegularExpressions;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>What reading the policy file gave: the policy, or every mistake found in it.</summary>
public sealed record PolicyParseResult(GatewayPolicy? Policy, IReadOnlyList<string> Errors)
{
    public bool IsValid => Policy is not null && Errors.Count == 0;
}

/// <summary>
/// Reads <c>gateway.toml</c>. Strict on purpose: a key it does not know, a value
/// that is not one of the listed words, or a reference to a tier that is not listed
/// is a mistake reported with its line number, never ignored or guessed at, because a
/// typo that silently dropped a setting could widen or narrow what the gateway may
/// do without anyone noticing. Every mistake is reported at once, so customer IT can
/// fix the file in one go. The messages are written for a Windows administrator who
/// knows nothing about AL Workbench's code. See <c>.design/on-prem-gateway.md</c>,
/// "The local policy file".
///
/// <para>Walks Tomlyn's syntax tree rather than its object model, because only the
/// tree knows which line each key is on.</para>
/// </summary>
public static partial class GatewayPolicyParser
{
    public const string FileName = "gateway.toml";

    /// <summary>Limits that keep the report sent to AL Workbench small. Far above any real network.</summary>
    public const int MaxInstances = 200;
    public const int MaxDatabases = 100;
    public const int MaxMachines = 100;
    public const int MaxListItems = 100;
    public const int MaxTextLength = 260;

    private static readonly string[] InstanceKeys =
        ["machine", "name", "environment", "allow", "change_hours", "tenants", "publishers", "allow_unsigned", "signers", "allow_force_sync", "scope", "powershell"];
    private static readonly string[] DatabaseKeys =
        ["name", "publish_on", "restart", "restart_mode", "shared_restart", "licence_target", "before", "after"];
    private static readonly string[] MachineKeys = ["name", "method"];
    private static readonly string[] DefaultsKeys = ["allow", "change_hours"];
    private static readonly string[] TierKeys = ["machine", "instance"];

    // No [ or ] (PowerShell reads them as a pattern, which could pick another file than
    // the one checked) and no : after the drive (an alternate data stream).
    [GeneratedRegex(@"^[A-Za-z]:\\[^""<>|?*\[\]:]+\.ps1$")]
    private static partial Regex LocalScriptPath();

    /// <summary>
    /// Reads the file's text. <paramref name="localMachine"/> is this computer's name:
    /// tiers on it need no <c>[[machine]]</c> section, and tiers anywhere else do.
    /// </summary>
    public static PolicyParseResult Parse(string text, string localMachine)
    {
        var reader = new Reader(localMachine);
        return reader.Read(text);
    }

    private sealed class Reader
    {
        private readonly string _localMachine;
        private readonly List<(int Line, string Message)> _errors = [];

        private readonly Dictionary<PolicyEnvironment, (IReadOnlySet<PolicyPermission>? Allow, ChangeWindow? Hours)> _defaults = [];
        private readonly List<RawInstance> _instances = [];
        private readonly List<RawDatabase> _databases = [];
        private readonly List<(int Line, PolicyMachine Machine)> _machines = [];

        public Reader(string localMachine) => _localMachine = localMachine;

        public PolicyParseResult Read(string text)
        {
            var document = SyntaxParser.Parse(text, GatewayPolicyParser.FileName, true);
            if (document.HasErrors)
            {
                // The file is not TOML: report what the TOML reader found and stop,
                // because everything after the first syntax error is guesswork.
                var syntax = new List<string>();
                foreach (var diagnostic in document.Diagnostics)
                {
                    if (diagnostic.Kind != DiagnosticMessageKind.Error) continue;
                    syntax.Add($"Line {diagnostic.Span.Start.Line + 1}: {diagnostic.Message}");
                }
                return new PolicyParseResult(null, syntax.Distinct().ToList());
            }

            foreach (var kv in document.KeyValues)
            {
                Error(kv, $"'{KeyName(kv.Key)}' is outside any section. Every setting belongs under a section such as [[instance]], [[database]], [[machine]] or [defaults.production].");
            }
            foreach (var table in document.Tables)
            {
                ReadSection(table);
            }

            var policy = Build();
            var errors = _errors.OrderBy(e => e.Line).Select(e => e.Line > 0 ? $"Line {e.Line}: {e.Message}" : e.Message).Distinct().ToList();
            return errors.Count > 0 ? new PolicyParseResult(null, errors) : new PolicyParseResult(policy, []);
        }

        // ------------------------------------------------------------ sections

        private void ReadSection(TableSyntaxBase table)
        {
            var path = KeyPath(table.Name);
            var name = string.Join('.', path);
            var isArray = table is TableArraySyntax;
            switch (path)
            {
                case ["instance"] when isArray:
                    ReadInstance(table);
                    return;
                case ["database"] when isArray:
                    ReadDatabase(table);
                    return;
                case ["machine"] when isArray:
                    ReadMachine(table);
                    return;
                case ["instance" or "database" or "machine"]:
                    Error(table, $"Write [[{name}]] with double brackets: each one is its own [[{name}]] section.");
                    return;
                case ["defaults", var kind] when !isArray:
                    ReadDefaults(table, kind);
                    return;
                case ["defaults"] when !isArray:
                    foreach (var kv in table.Items)
                    {
                        Error(kv, $"'{KeyName(kv.Key)}' does not belong directly under [defaults]. Defaults are per kind of environment, for example [defaults.production].");
                    }
                    return;
                default:
                    Error(table, $"[{(isArray ? "[" : "")}{name}{(isArray ? "]" : "")}] is not a section this file knows. The sections are [[instance]], [[database]], [[machine]] and [defaults.production], [defaults.test] and [defaults.development].");
                    return;
            }
        }

        private void ReadDefaults(TableSyntaxBase table, string kind)
        {
            if (!PolicyWords.TryParse(PolicyWords.Environments, kind, out var environment))
            {
                Error(table, $"[defaults.{kind}] is not a kind of environment. Use [defaults.production], [defaults.test] or [defaults.development].");
                return;
            }
            if (_defaults.ContainsKey(environment))
            {
                Error(table, $"[defaults.{kind}] appears twice. Keep one.");
                return;
            }
            var values = Settings(table, DefaultsKeys, $"[defaults.{kind}]");
            _defaults[environment] = (ReadAllow(values), ReadHours(values));
        }

        private void ReadInstance(TableSyntaxBase table)
        {
            const string section = "[[instance]]";
            var values = Settings(table, InstanceKeys, section);
            var machine = RequiredText(table, values, "machine", section);
            var name = RequiredText(table, values, "name", section);
            var environment = ReadWord(values, "environment", PolicyWords.Environments) ?? PolicyEnvironment.Production;
            var tenants = ReadTextList(values, "tenants", allowEmpty: false);
            var publishers = ReadTextList(values, "publishers", allowEmpty: false);
            _instances.Add(new RawInstance(
                Line(table),
                machine is null || name is null ? null : new TierRef(machine, name),
                environment,
                ReadAllow(values),
                ReadHours(values),
                tenants,
                publishers,
                ReadBool(values, "allow_unsigned") ?? false,
                ReadWord(values, "scope", PolicyWords.Scopes) ?? PublishScope.Tenant,
                ReadWord(values, "powershell", PolicyWords.PowerShellEditions),
                ReadTextList(values, "signers", allowEmpty: false),
                ReadBool(values, "allow_force_sync") ?? false));
        }

        private void ReadDatabase(TableSyntaxBase table)
        {
            const string section = "[[database]]";
            var values = Settings(table, DatabaseKeys, section);
            var name = RequiredText(table, values, "name", section);
            TierRef? publishOn = null;
            if (values.TryGetValue("publish_on", out var publishValue))
            {
                publishOn = ReadTier(publishValue.Value, "publish_on");
            }
            else
            {
                Error(table, $"This {section} has no publish_on. Name the tier that publishes for it, for example publish_on = {{ machine = \"CRONUS-BC01\", instance = \"BC140\" }}.");
            }
            List<TierRef>? restart = null;
            if (values.TryGetValue("restart", out var restartValue))
            {
                restart = ReadTierList(restartValue, "restart");
            }
            _databases.Add(new RawDatabase(
                Line(table),
                name,
                publishOn,
                restart,
                ReadWord(values, "restart_mode", PolicyWords.RestartModes) ?? RestartMode.StopAllThenStartAll,
                ReadBool(values, "shared_restart") ?? false,
                ReadWord(values, "licence_target", PolicyWords.LicenceTargets),
                ReadScript(values, "before"),
                ReadScript(values, "after")));
        }

        private void ReadMachine(TableSyntaxBase table)
        {
            const string section = "[[machine]]";
            var values = Settings(table, MachineKeys, section);
            var name = RequiredText(table, values, "name", section);
            MachineMethod? method = null;
            if (values.ContainsKey("method"))
            {
                method = ReadWord(values, "method", PolicyWords.MachineMethods);
            }
            else
            {
                Error(table, $"This {section} has no method. Say how to reach the machine: method = {PolicyWords.Choices(PolicyWords.MachineMethods)}.");
            }
            if (name is not null && method is not null) _machines.Add((Line(table), new PolicyMachine(name, method.Value)));
        }

        // ------------------------------------------------------------ the whole file

        private GatewayPolicy Build()
        {
            if (_instances.Count > MaxInstances) Error(0, $"The file lists {_instances.Count} service tiers; a gateway can manage at most {MaxInstances}.");
            if (_databases.Count > MaxDatabases) Error(0, $"The file lists {_databases.Count} databases; at most {MaxDatabases} are allowed.");
            if (_machines.Count > MaxMachines) Error(0, $"The file lists {_machines.Count} machines; at most {MaxMachines} are allowed.");

            var machines = new List<PolicyMachine>();
            foreach (var (line, machine) in _machines)
            {
                if (string.Equals(machine.Name, _localMachine, StringComparison.OrdinalIgnoreCase))
                {
                    Error(line, $"{machine.Name} is this computer, which the gateway reaches directly. Remove this [[machine]] section.");
                }
                else if (machines.Any(m => string.Equals(m.Name, machine.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    Error(line, $"{machine.Name} has two [[machine]] sections. Keep one.");
                }
                else
                {
                    machines.Add(machine);
                }
            }

            var instances = new List<EffectiveInstance>();
            foreach (var raw in _instances)
            {
                if (raw.Tier is null) continue;
                if (instances.Any(i => i.Tier.Equals(raw.Tier)))
                {
                    Error(raw.Line, $"{raw.Tier} is listed twice. Keep one [[instance]] section for each service tier.");
                    continue;
                }
                if (!string.Equals(raw.Tier.Machine, _localMachine, StringComparison.OrdinalIgnoreCase)
                    && !machines.Any(m => string.Equals(m.Name, raw.Tier.Machine, StringComparison.OrdinalIgnoreCase)))
                {
                    Error(raw.Line, $"{raw.Tier.Machine} is not this computer ({_localMachine}). Add a [[machine]] section for {raw.Tier.Machine} saying how to reach it.");
                }
                // Each of the two replaces its default whole; nothing is merged.
                var kind = _defaults.GetValueOrDefault(raw.Environment);
                var allow = raw.Allow ?? kind.Allow ?? new HashSet<PolicyPermission> { PolicyPermission.Inventory };
                var hours = raw.HoursSet ? raw.Hours : kind.Hours;
                instances.Add(new EffectiveInstance(
                    raw.Tier,
                    raw.Environment,
                    allow,
                    hours,
                    raw.Tenants ?? ["default"],
                    raw.Publishers,
                    raw.AllowUnsigned,
                    raw.Scope,
                    raw.PowerShell)
                {
                    Signers = raw.Signers,
                    AllowForceSync = raw.AllowForceSync,
                });
            }

            var databases = new List<PolicyDatabase>();
            var owner = new Dictionary<TierRef, string>();
            foreach (var raw in _databases)
            {
                if (raw.Name is null || raw.PublishOn is null) continue;
                if (databases.Any(d => string.Equals(d.Name, raw.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    Error(raw.Line, $"The database \"{raw.Name}\" is listed twice. Keep one [[database]] section for it.");
                    continue;
                }
                var restart = raw.Restart ?? [raw.PublishOn];
                var database = new PolicyDatabase(raw.Name, raw.PublishOn, restart, raw.RestartMode, raw.SharedRestart, raw.LicenceTarget, raw.Before, raw.After);
                var ok = true;
                foreach (var tier in database.Tiers)
                {
                    if (!_instances.Any(i => tier.Equals(i.Tier)))
                    {
                        Error(raw.Line, $"The database \"{raw.Name}\" names {tier}, which has no [[instance]] section. List every tier as an [[instance]] first.");
                        ok = false;
                    }
                    else if (owner.TryGetValue(tier, out var other))
                    {
                        Error(raw.Line, $"{tier} is in both \"{other}\" and \"{raw.Name}\". A service tier belongs to one database.");
                        ok = false;
                    }
                    else
                    {
                        owner[tier] = raw.Name;
                    }
                }
                if (ok) databases.Add(database);
            }

            return new GatewayPolicy(instances, databases, machines);
        }

        // ------------------------------------------------------------ values

        /// <summary>
        /// The section's settings by key. A key the section does not know, or a dotted
        /// key, is a mistake; Tomlyn has already refused a key given twice.
        /// </summary>
        private Dictionary<string, KeyValueSyntax> Settings(TableSyntaxBase table, string[] known, string section)
        {
            var values = new Dictionary<string, KeyValueSyntax>(StringComparer.Ordinal);
            foreach (var kv in table.Items)
            {
                var path = KeyPath(kv.Key);
                var key = string.Join('.', path);
                if (path.Count != 1)
                {
                    Error(kv, $"'{key}' is not a setting of {section}. Write each setting on its own line, without dots in its name.");
                }
                else if (!known.Contains(key, StringComparer.Ordinal))
                {
                    Error(kv, $"'{key}' is not a setting of {section}. The settings are: {string.Join(", ", known)}.");
                }
                else
                {
                    values[key] = kv;
                }
            }
            return values;
        }

        private string? RequiredText(TableSyntaxBase table, Dictionary<string, KeyValueSyntax> values, string key, string section)
        {
            if (!values.TryGetValue(key, out var kv))
            {
                Error(table, $"This {section} has no {key}.");
                return null;
            }
            return Text(kv.Value, key);
        }

        private string? Text(ValueSyntax? value, string key)
        {
            if (value is not StringValueSyntax { Value: { } text })
            {
                Error(value, $"{key} must be text in quotes.");
                return null;
            }
            text = text.Trim();
            if (text.Length == 0)
            {
                Error(value, $"{key} is empty.");
                return null;
            }
            if (text.Length > MaxTextLength || text.Any(char.IsControl))
            {
                Error(value, $"{key} is longer than {MaxTextLength} characters or contains a line break or tab.");
                return null;
            }
            return text;
        }

        private bool? ReadBool(Dictionary<string, KeyValueSyntax> values, string key)
        {
            if (!values.TryGetValue(key, out var kv)) return null;
            if (kv.Value is BooleanValueSyntax boolean) return boolean.Value;
            Error(kv, $"{key} must be true or false, without quotes.");
            return null;
        }

        private T? ReadWord<T>(Dictionary<string, KeyValueSyntax> values, string key, IReadOnlyDictionary<T, string> words) where T : struct, Enum
        {
            if (!values.TryGetValue(key, out var kv)) return null;
            var text = Text(kv.Value, key);
            if (text is null) return null;
            if (PolicyWords.TryParse(words, text, out var value)) return value;
            Error(kv, $"{key} = \"{text}\" is not one of the choices. Use {PolicyWords.Choices(words)}.");
            return null;
        }

        private List<string>? ReadTextList(Dictionary<string, KeyValueSyntax> values, string key, bool allowEmpty)
        {
            if (!values.TryGetValue(key, out var kv)) return null;
            if (kv.Value is not ArraySyntax array)
            {
                Error(kv, $"{key} must be a list in square brackets, for example {key} = [\"one\", \"two\"].");
                return [];
            }
            var list = new List<string>();
            foreach (var item in array.Items)
            {
                var text = Text(item.Value, key);
                if (text is not null && !list.Contains(text, StringComparer.OrdinalIgnoreCase)) list.Add(text);
            }
            if (list.Count == 0 && !allowEmpty && array.Items.ChildrenCount == 0)
            {
                Error(kv, $"{key} is an empty list. Leave the line out instead, or list at least one.");
            }
            if (list.Count > MaxListItems) Error(kv, $"{key} lists more than {MaxListItems} entries.");
            return list;
        }

        private IReadOnlySet<PolicyPermission>? ReadAllow(Dictionary<string, KeyValueSyntax> values)
        {
            if (!values.TryGetValue("allow", out var kv)) return null;
            if (kv.Value is not ArraySyntax array)
            {
                Error(kv, "allow must be a list in square brackets, for example allow = [\"inventory\", \"restart\"].");
                return new HashSet<PolicyPermission>();
            }
            var allow = new HashSet<PolicyPermission>();
            foreach (var item in array.Items)
            {
                var text = Text(item.Value, "allow");
                if (text is null) continue;
                if (PolicyWords.TryParse(PolicyWords.Permissions, text, out var permission)) allow.Add(permission);
                else Error(item, $"\"{text}\" is not a permission. The permissions are {PolicyWords.Choices(PolicyWords.Permissions)}.");
            }
            return allow;
        }

        private ChangeWindow? ReadHours(Dictionary<string, KeyValueSyntax> values)
        {
            if (!values.TryGetValue("change_hours", out var kv)) return null;
            var text = Text(kv.Value, "change_hours");
            if (text is null) return null;
            if (ChangeWindow.TryParse(text, out var window, out var problem)) return window;
            Error(kv, "change_hours: " + problem);
            return null;
        }

        private string? ReadScript(Dictionary<string, KeyValueSyntax> values, string key)
        {
            if (!values.TryGetValue(key, out var kv)) return null;
            var path = Text(kv.Value, key);
            if (path is null) return null;
            if (path.IndexOfAny(['[', ']']) >= 0)
            {
                Error(kv, $"{key} must not contain [ or ], which PowerShell reads as a pattern rather than a name. Rename the script or its folder.");
                return null;
            }
            if (!LocalScriptPath().IsMatch(path) || path.Contains(@"\..\", StringComparison.Ordinal))
            {
                Error(kv, $"{key} must be the full path of a .ps1 file on this computer, for example 'C:\\ALWorkbench\\before-deploy.ps1'. Use single quotes so the backslashes are kept.");
                return null;
            }
            return path;
        }

        private TierRef? ReadTier(SyntaxNode? value, string key)
        {
            if (value is not InlineTableSyntax table)
            {
                Error(value, $"{key} must name a tier like {{ machine = \"CRONUS-BC01\", instance = \"BC140\" }}.");
                return null;
            }
            string? machine = null, instance = null;
            var given = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in table.Items)
            {
                var kv = item.KeyValue;
                if (kv is null) continue;
                var name = string.Join('.', KeyPath(kv.Key));
                if (!TierKeys.Contains(name, StringComparer.Ordinal))
                {
                    Error(kv, $"'{name}' does not belong in {key}. Name the tier with machine and instance only.");
                    continue;
                }
                given.Add(name);
                var text = Text(kv.Value, name);
                if (name == "machine") machine = text;
                else instance = text;
            }
            if (!given.Contains("machine") || !given.Contains("instance"))
            {
                Error(table, $"{key} must give both machine and instance, like {{ machine = \"CRONUS-BC01\", instance = \"BC140\" }}.");
            }
            if (machine is null || instance is null) return null;
            return new TierRef(machine, instance);
        }

        private List<TierRef>? ReadTierList(KeyValueSyntax kv, string key)
        {
            if (kv.Value is not ArraySyntax array)
            {
                Error(kv, $"{key} must be a list of tiers in square brackets, like [{{ machine = \"CRONUS-BC01\", instance = \"BC140\" }}].");
                return null;
            }
            var tiers = new List<TierRef>();
            foreach (var item in array.Items)
            {
                var tier = ReadTier(item.Value, key);
                if (tier is not null && !tiers.Contains(tier)) tiers.Add(tier);
            }
            if (array.Items.ChildrenCount == 0) Error(kv, $"{key} is an empty list. Leave the line out to restart only the publishing tier.");
            return tiers;
        }

        // ------------------------------------------------------------ helpers

        private static List<string> KeyPath(KeySyntax? key)
        {
            var path = new List<string>();
            if (key is null) return path;
            path.Add(KeyText(key.Key));
            foreach (var dotted in key.DotKeys) path.Add(KeyText(dotted.Key));
            return path;
        }

        private static string KeyName(KeySyntax? key) => string.Join('.', KeyPath(key));

        private static string KeyText(BareKeyOrStringValueSyntax? key) => key switch
        {
            BareKeySyntax bare => bare.Key?.Text ?? string.Empty,
            StringValueSyntax quoted => quoted.Value ?? string.Empty,
            _ => string.Empty,
        };

        private static int Line(SyntaxNode? node) => node is null ? 0 : node.Span.Start.Line + 1;

        private void Error(SyntaxNode? node, string message) => _errors.Add((Line(node), message));

        private void Error(int line, string message) => _errors.Add((line, message));
    }

    private sealed record RawInstance(
        int Line,
        TierRef? Tier,
        PolicyEnvironment Environment,
        IReadOnlySet<PolicyPermission>? Allow,
        ChangeWindow? Hours,
        List<string>? Tenants,
        List<string>? Publishers,
        bool AllowUnsigned,
        PublishScope Scope,
        PowerShellEdition? PowerShell,
        List<string>? Signers,
        bool AllowForceSync)
    {
        /// <summary>Whether the tier set its own change_hours, which then replaces the default.</summary>
        public bool HoursSet => Hours is not null;
    }

    private sealed record RawDatabase(
        int Line,
        string? Name,
        TierRef? PublishOn,
        List<TierRef>? Restart,
        RestartMode RestartMode,
        bool SharedRestart,
        LicenceTarget? LicenceTarget,
        string? Before,
        string? After);
}
