namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// A BC environment as returned by the Admin Center API. <see cref="Name"/> and
/// <see cref="Type"/> are the two fields every response carries; everything else is
/// optional and stays <c>null</c> when the payload omits it (the by-name response
/// omits <see cref="GeoName"/>, for instance). Enum-ish values are kept
/// <strong>verbatim</strong> — Microsoft's casing is inconsistent across endpoints
/// (<c>productFamily: "BusinessCentral"</c> beside <c>creatorPrincipalType: "app"</c>),
/// so comparisons are case-insensitive and the stored string is whatever the API said.
/// See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed record BcEnvironment(string Name, string Type)
{
    /// <summary>The display name a BC admin sees in the admin center; often equals <see cref="Name"/>.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>The application family the environment belongs to (e.g. <c>BusinessCentral</c>). Needed to address the environment in later admin-center calls.</summary>
    public string? ApplicationFamily { get; init; }

    /// <summary>Lifecycle status (<c>Active</c>, <c>Upgrading</c>, <c>SoftDeleted</c>, ...). The delivery gate reads this.</summary>
    public string? Status { get; init; }

    public string? CountryCode { get; init; }
    public Guid? AadTenantId { get; init; }

    /// <summary>Deep link to the environment's web client, for an "Open in Business Central" action.</summary>
    public string? WebClientLoginUrl { get; init; }

    public string? LocationName { get; init; }

    /// <summary>Azure geography. Present in the list response only — the by-name response omits it.</summary>
    public string? GeoName { get; init; }

    public string? RingName { get; init; }

    /// <summary>How AppSource app updates are applied to this environment.</summary>
    public string? AppSourceAppsUpdateCadence { get; init; }

    /// <summary>The platform/application version, from <c>versionDetails</c>.</summary>
    public string? Version { get; init; }

    public DateTime? GracePeriodStartDate { get; init; }
    public DateTime? EnforcedUpdatePeriodStartDate { get; init; }

    /// <summary>Set once the customer soft-deletes the environment; it still returns from the API until hard-deleted.</summary>
    public DateTime? SoftDeletedOn { get; init; }

    public DateTime? HardDeletePendingOn { get; init; }
    public string? DeleteReason { get; init; }
}

/// <summary>
/// Classification of a "Test connection" outcome, so the UI can name the step that
/// actually needs fixing. The two denial cases are deliberately separate: Entra
/// issuing a token says nothing about whether Business Central will accept the app,
/// and the two failures have different remedies in different portals.
/// </summary>
public enum BcConnectionResult
{
    /// <summary>Token acquired and environments listed.</summary>
    Success,

    /// <summary>The credentials themselves were rejected (bad tenant/client/secret, or the key ring can't decrypt the stored secret).</summary>
    AuthFailed,

    /// <summary>
    /// 401 from the Admin Center API: Entra issued the token, but Business Central
    /// won't accept the app at all. Almost always the app is missing from the admin
    /// center's "Authorized Microsoft Entra apps" list — a registration that lives in
    /// BC, not Entra, so the Entra portal looks complete while every call fails.
    /// </summary>
    AppNotAuthorized,

    /// <summary>
    /// 403 from the Admin Center API: the app is known to Business Central but isn't
    /// allowed to list environments — a missing/unconsented <c>AdminCenter.ReadWrite.All</c>,
    /// or, when acting on a customer's tenant as a partner, a missing delegated admin
    /// (GDAP) relationship.
    /// </summary>
    AccessDenied,

    /// <summary>Any other failure (network, unexpected status, malformed response).</summary>
    Error,
}

/// <summary>
/// The outcome of a "Test connection" / "Refresh environments" run: the
/// classification, the number of environments fetched on success, and a
/// user-facing message. Never carries the secret.
/// </summary>
public sealed record BcConnectionTestResult(BcConnectionResult Result, int EnvironmentCount, string Message)
{
    public bool IsSuccess => Result == BcConnectionResult.Success;
}

/// <summary>
/// Raised by the BC HTTP clients when the API returns a non-success status, so the
/// orchestrating service can classify it (e.g. 401/403 on the admin call → GDAP
/// missing). Carries the status code and a short, secret-free detail.
/// </summary>
public sealed class BcApiException : Exception
{
    public System.Net.HttpStatusCode? StatusCode { get; }

    public BcApiException(System.Net.HttpStatusCode? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// An environment's <em>Microsoft platform-update window</em>, from
/// <c>settings/upgrade</c> — when Microsoft's own updates run against that environment.
/// <para>
/// <b>This is not the workbench's delivery slot.</b> The delivery slot lives on
/// <c>ProjectEnvironment.UpdateWindowStart/End</c>, is a commercial arrangement with the
/// customer, and is enforced by our own worker. This record is mirrored context so a
/// consultant can see Microsoft's maintenance hours before choosing that slot. The two
/// are never sourced from each other.
/// </para>
/// <para>
/// <see cref="StartTime"/>/<see cref="EndTime"/> plus <see cref="WindowsTimeZoneId"/>
/// are the stable definition of the window; the UTC pair the API also returns names only
/// the next occurrence and drifts, so it is not persisted.
/// </para>
/// </summary>
/// <param name="StartTime">Wall-clock start in <see cref="WindowsTimeZoneId"/>, or null when the environment has no window.</param>
/// <param name="EndTime">Wall-clock end, or null.</param>
/// <param name="WindowsTimeZoneId">A <em>Windows</em> time-zone id (e.g. <c>Romance Standard Time</c>) — the only form this API accepts or returns.</param>
public sealed record BcUpdateSettings(TimeOnly? StartTime, TimeOnly? EndTime, string? WindowsTimeZoneId)
{
    /// <summary>True when Microsoft has a real window for this environment (both bounds set).</summary>
    public bool IsConfigured => StartTime is not null && EndTime is not null;
}

/// <summary>
/// One platform target version for an environment, from <c>GET .../environments/{env}/updates</c>
/// — how "when does this customer get 27.6?" is answered without opening the admin center.
/// <para>
/// A version may be released or not yet (<see cref="Available"/>), and at most one is
/// <see cref="Selected"/> as the environment's next update. An unreleased version carries
/// only a rough expected month/year; a released one carries the scheduling detail.
/// </para>
/// </summary>
public sealed record BcEnvironmentUpdate(
    string TargetVersion,
    bool Available,
    bool Selected,
    string UpdateStatus,
    string TargetVersionType,
    DateTimeOffset? SelectedDateTime,
    DateTimeOffset? LatestSelectableDateTime,
    bool IgnoreUpdateWindow,
    string RolloutStatus,
    int? ExpectedMonth,
    int? ExpectedYear)
{
    /// <summary>"August 2025" for a version Microsoft hasn't released yet, else null.</summary>
    public string? ExpectedAvailability =>
        ExpectedYear is { } y && ExpectedMonth is >= 1 and <= 12
            ? $"{System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(ExpectedMonth.Value)} {y}"
            : null;
}

/// <summary>
/// A Windows time zone Business Central will accept for an update window, from
/// <c>GET applications/settings/timezones</c> — the only ids the update-window write
/// takes, so the picker is populated from here rather than from the host's own list.
/// </summary>
public sealed record BcTimeZone(string Id, string DisplayName, string CurrentUtcOffset);

/// <summary>
/// How often Marketplace (AppSource) apps on an environment are updated. Wire values,
/// sent verbatim.
/// </summary>
public static class BcAppUpdateCadence
{
    /// <summary>Microsoft's own default cadence for the environment.</summary>
    public const string Default = "Default";

    /// <summary>Only when the environment takes a major update.</summary>
    public const string DuringMajorUpgrade = "DuringMajorUpgrade";

    /// <summary>With every major and minor update.</summary>
    public const string DuringMajorMinorUpgrade = "DuringMajorMinorUpgrade";

    /// <summary>Every accepted value, in the order the picker offers them.</summary>
    public static readonly IReadOnlyList<string> All = [Default, DuringMajorUpgrade, DuringMajorMinorUpgrade];

    /// <summary>Canonical spelling of a stored value, case-insensitively; null when unknown.</summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How a cadence reads on screen. Every arm is a full-cased phrase, and an unknown
    /// value falls back to a phrase too rather than to the wire token — a value Microsoft
    /// adds later would otherwise reach a consultant as <c>DuringMajorMinorUpgrade</c>.
    /// </summary>
    public static string Display(string? value) => Normalize(value) switch
    {
        Default => "Microsoft's default",
        DuringMajorUpgrade => "Only with major updates",
        DuringMajorMinorUpgrade => "With major and minor updates",
        _ => "Not set",
    };

    /// <summary>
    /// The whole sentence a confirm shows for a cadence, naming the environment. Written
    /// out per option because "will update: only with major updates" is not a sentence,
    /// and lower-casing a display string to force one is how wire values leak into copy.
    /// </summary>
    public static string ConfirmSentence(string? value, string environmentName) => Normalize(value) switch
    {
        Default => $"AppSource apps on {environmentName} will update on Microsoft's default schedule.",
        DuringMajorUpgrade => $"AppSource apps on {environmentName} will only update when the environment takes a major Business Central update.",
        DuringMajorMinorUpgrade => $"AppSource apps on {environmentName} will update when the environment takes a major or a minor Business Central update.",
        _ => $"The way AppSource apps update on {environmentName} will change.",
    };
}

/// <summary>
/// What an environment is: the two types Business Central offers, as the copy write
/// sends them. Wire values, so they are sent verbatim.
/// </summary>
public static class BcEnvironmentTypes
{
    public const string Sandbox = "Sandbox";
    public const string Production = "Production";

    /// <summary>Every accepted value, in the order a picker offers them.</summary>
    public static readonly IReadOnlyList<string> All = [Sandbox, Production];

    /// <summary>Canonical spelling of a stored value, case-insensitively; null when unknown.</summary>
    public static string? Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when Business Central calls this environment a production one.</summary>
    public static bool IsProduction(string? value) =>
        string.Equals(value?.Trim(), Production, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Business Central's rules for what an environment may be called, in one place so the
/// service (the source of truth) and the dialog's inline hint cannot drift apart. The
/// rules are Microsoft's, reported as <c>environmentNameNotValid</c> when broken.
/// </summary>
public static class BcEnvironmentName
{
    /// <summary>
    /// Business Central takes "fewer than 30 characters", so 29 is the longest name that
    /// is accepted. Mirrored on the input's <c>maxlength</c>.
    /// </summary>
    public const int MaxLength = 29;

    /// <summary>
    /// The same rules as <see cref="Validate"/>, for the input's <c>pattern</c> attribute
    /// so the browser answers before the server has to.
    /// </summary>
    public const string Pattern = "[A-Za-z][A-Za-z0-9_-]{0,28}";

    /// <summary>The rules in one sentence, shown under the field and repeated in a refusal.</summary>
    public const string Rule =
        "Start with a letter, then letters, numbers, dashes or underscores - up to 29 characters.";

    /// <summary>
    /// Why this name won't do, or null when it will. One sentence, naming the rule that
    /// was broken rather than restating all of them.
    /// </summary>
    public static string? Validate(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0) return "Enter a name for the new environment.";
        if (value.Length > MaxLength) return $"That name is too long. Business Central allows up to {MaxLength} characters.";
        if (!char.IsAsciiLetter(value[0])) return "An environment name has to start with a letter.";
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            ? null
            : "An environment name can only hold letters, numbers, dashes and underscores.";
    }

    /// <summary>
    /// The name to offer first: the source with <c>-Copy</c> on the end, shortened from
    /// the source's side so the suffix - the part that says what this environment is -
    /// always survives.
    /// </summary>
    public static string Suggest(string sourceName)
    {
        const string suffix = "-Copy";
        var stem = (sourceName ?? string.Empty).Trim();
        if (stem.Length == 0) return "Copy";
        if (stem.Length + suffix.Length > MaxLength) stem = stem[..(MaxLength - suffix.Length)];
        return stem.TrimEnd('-', '_') + suffix;
    }
}

/// <summary>
/// What Business Central answered when asked to copy an environment: the long-running
/// operation it scheduled, not the new environment. Both fields are best-effort - the
/// copy has been accepted by the time this is returned, and a body we couldn't read is
/// not a reason to tell anyone it failed.
/// </summary>
/// <param name="OperationId">Microsoft's id for the copy, for the log and the Operations tab.</param>
/// <param name="Status">Where it had got to when it answered - <c>scheduled</c>, usually.</param>
public sealed record BcEnvironmentCopy(string? OperationId, string? Status);

/// <summary>
/// One thing Business Central did, or is doing, to an environment - an app install or
/// update, a platform update, a rename, a restart, a setting change. Whoever did it:
/// this workbench, the admin centre, or Microsoft.
/// </summary>
public sealed record BcEnvironmentOperation(
    string Id,
    string Type,
    string Status,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? StartedOn,
    DateTimeOffset? CompletedOn,
    string CreatedBy,
    string ErrorMessage,
    IReadOnlyDictionary<string, string> Parameters)
{
    public string? Parameter(string name) => Parameters.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// How an operation reads on screen. Every known type gets a plain phrase, and an
/// unknown one is spaced out rather than shown as the wire token.
/// </summary>
public static class BcEnvironmentOperationDisplay
{
    /// <summary>
    /// What happened, in a line. <paramref name="appName"/> resolves an app id to a name
    /// when the caller knows it; the operations list itself carries only the id.
    /// </summary>
    public static string Headline(BcEnvironmentOperation op, Func<Guid, string?>? appName = null)
    {
        var app = AppLabel(op, appName);
        var version = op.Parameter("targetAppVersion") ?? op.Parameter("targetVersion") ?? op.Parameter("applicationVersion");
        var to = string.IsNullOrWhiteSpace(version) ? string.Empty : $" to {version}";
        return op.Type.ToLowerInvariant() switch
        {
            "environmentappinstall" => $"Installed {app}{(string.IsNullOrWhiteSpace(version) ? string.Empty : $" {version}")}",
            "environmentappupdate" => $"Updated {app}{to}",
            "environmentapphotfix" => $"Hotfixed {app}{to}",
            "environmentappuninstall" => $"Uninstalled {app}",
            "update" => $"Business Central update{to}",
            "modify" => "Environment settings changed",
            "restart" => "Environment restarted",
            "environmentrename" => op.Parameter("newEnvironmentName") is { } renamed
                ? $"Renamed to {renamed}" : "Environment renamed",
            "copy" => op.Parameter("sourceEnvironmentName") is { Length: > 0 } source
                ? $"Copied from {source}" : "Environment copied",
            "create" => "Environment created",
            "softdelete" => "Environment deleted (recoverable)",
            "delete" => "Environment deleted",
            "recover" => "Environment recovered",
            "pitrestore" => "Restored from a backup",
            "movetoanotheraadtenant" => "Moved to another Microsoft Entra organisation",
            _ => SpaceOut(op.Type),
        };
    }

    /// <summary>The status as a word, and the pill tone that goes with it.</summary>
    public static (string Word, string Tone) Status(BcEnvironmentOperation op) => op.Status.ToLowerInvariant() switch
    {
        "succeeded" => ("Succeeded", "success"),
        "failed" => ("Failed", "danger"),
        "running" => ("Running", "running"),
        "queued" => ("Queued", "queued"),
        "scheduled" => ("Scheduled", "queued"),
        "canceled" or "cancelled" => ("Cancelled", "muted"),
        "skipped" => ("Skipped", "muted"),
        _ => (string.IsNullOrWhiteSpace(op.Status) ? "Unknown" : SpaceOut(op.Status), "muted"),
    };

    /// <summary>True while Business Central has not finished with it.</summary>
    public static bool IsOpen(BcEnvironmentOperation op) =>
        op.Status.ToLowerInvariant() is "running" or "queued" or "scheduled";

    private static string AppLabel(BcEnvironmentOperation op, Func<Guid, string?>? appName)
    {
        if (op.Parameter("appName") is { Length: > 0 } named) return named;
        if (Guid.TryParse(op.Parameter("appId"), out var id) && appName?.Invoke(id) is { Length: > 0 } known) return known;
        return "an app";
    }

    private static string SpaceOut(string token)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(token, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}

/// <summary>
/// One person (or one background job) signed in to an environment right now, from
/// <c>GET .../environments/{name}/sessions</c>. Nothing here is ever stored: a user id
/// and what that person is doing is personal data, so the list is shown and forgotten.
/// See <c>.design/environment-updates.md</c>, "Sessions".
/// <para>
/// Every field but <see cref="SessionId"/> is optional as far as this record is
/// concerned - a background session has no current object, and Microsoft's own example
/// shows fields that can come back empty - so an absent one is an empty string or null
/// rather than a reason to drop the row.
/// </para>
/// </summary>
/// <param name="SessionId">Business Central's id for the session, and what a cancel addresses. An integer, not a GUID.</param>
/// <param name="UserId">Who is signed in, as Business Central reports them - usually their email address.</param>
/// <param name="ClientType">The wire word for how they got in (<c>WebClient</c>, <c>Background</c>, <c>WebServiceClient</c>, ...). Worded for the screen by <see cref="BcSessionDisplay"/>.</param>
/// <param name="LogOnDate">When the session started.</param>
/// <param name="CurrentOperationDuration">
/// How long the session has been in the operation it is running now. <b>Microsoft
/// documents the field as a <c>long</c> and names no unit</b>; the parser reads a number
/// as milliseconds and a string as a time span, and this is the one field here that has
/// not been checked against a live tenant.
/// </param>
public sealed record BcSession(
    int SessionId,
    string UserId,
    string ClientType,
    DateTimeOffset? LogOnDate,
    string EntryPointOperation,
    string EntryPointObjectName,
    string EntryPointObjectId,
    string EntryPointObjectType,
    string CurrentObjectName,
    int? CurrentObjectId,
    string CurrentObjectType,
    TimeSpan? CurrentOperationDuration);

/// <summary>
/// How a session reads on screen, in the words a consultant on the phone to a customer
/// would use. The same treatment <see cref="BcEnvironmentOperationDisplay"/> gives an
/// operation: every value Microsoft has today gets a phrase, and one they add tomorrow is
/// spaced out into words rather than reaching anybody as a wire token.
/// </summary>
public static class BcSessionDisplay
{
    /// <summary>
    /// How long a session may sit in one operation before the row is marked.
    /// <para>
    /// The number is ours - Business Central marks nothing - so it is chosen to be
    /// defensible rather than clever. A person clicking through the web client finishes
    /// an operation in well under a second, so anything still running after a minute is
    /// doing work rather than waiting for somebody; five minutes is where a consultant on
    /// the phone would start looking at it, and it is a round figure they can hold in
    /// their head. It marks a row and orders the list. It never hides a row, and it
    /// never decides anything: a nightly job legitimately runs for hours.
    /// </para>
    /// </summary>
    public static readonly TimeSpan LongRunningAfter = TimeSpan.FromMinutes(5);

    /// <summary>True when this session has been in its current operation long enough to be worth a look.</summary>
    public static bool IsLongRunning(BcSession session) =>
        session.CurrentOperationDuration is { } duration && duration >= LongRunningAfter;

    /// <summary>
    /// How somebody got in, as a phrase. Business Central's client types are wire words
    /// (<c>WebServiceClient</c>, <c>ODataV4</c>), and the people reading this say "the web
    /// client" and "a web service".
    /// </summary>
    public static string ClientTypeWord(string? clientType) => Normalise(clientType) switch
    {
        "web" or "webclient" => "Web client",
        "tablet" => "Tablet",
        "phone" => "Phone",
        "desktop" or "windows" or "windowsclient" => "Desktop client",
        "webservice" or "webserviceclient" or "soap" or "soapwebserviceclient" => "Web service (SOAP)",
        "odata" or "odatav4" or "odatav4client" or "odatawebserviceclient" => "Web service (OData)",
        "api" or "apiclient" => "Web service (API)",
        "background" or "backgroundsession" => "Background",
        "nas" or "nasclient" or "jobqueue" => "Job queue",
        "child" or "childsession" => "Background (child session)",
        "management" or "managementclient" => "Management client",
        "" => "Unknown",
        _ => SpaceOut(clientType!),
    };

    /// <summary>
    /// True when the session is a person at a client (web, tablet, phone, desktop, or a
    /// client type we don't know yet), false for web service calls and sessions that run
    /// under somebody's name without them: background, child and job queue. Used to count
    /// who is online before a deployment, where an integration polling OData is not somebody
    /// who would lose their work.
    /// </summary>
    public static bool IsPerson(string? clientType) => Normalise(clientType) switch
    {
        "webservice" or "webserviceclient" or "soap" or "soapwebserviceclient" => false,
        "odata" or "odatav4" or "odatav4client" or "odatawebserviceclient" => false,
        "api" or "apiclient" => false,
        "background" or "backgroundsession" or "child" or "childsession" => false,
        "nas" or "nasclient" or "jobqueue" => false,
        _ => true,
    };

    /// <summary>
    /// The same thing inside a sentence ("ended Ola's <em>web client</em> session"). Written
    /// out per arm rather than lower-cased from <see cref="ClientTypeWord"/>, because
    /// "ended their web service (soap) session" is how a wire value leaks into copy.
    /// </summary>
    public static string ClientTypePhrase(string? clientType) => Normalise(clientType) switch
    {
        "web" or "webclient" => "web client",
        "tablet" => "tablet",
        "phone" => "phone",
        "desktop" or "windows" or "windowsclient" => "desktop client",
        "webservice" or "webserviceclient" or "soap" or "soapwebserviceclient" => "web service",
        "odata" or "odatav4" or "odatav4client" or "odatawebserviceclient" => "web service",
        "api" or "apiclient" => "web service",
        "background" or "backgroundsession" or "child" or "childsession" => "background",
        "nas" or "nasclient" or "jobqueue" => "job queue",
        "management" or "managementclient" => "management client",
        _ => "Business Central",
    };

    /// <summary>
    /// What the session is running now, as something a consultant can repeat to a
    /// customer - the object it is in, with the kind and number in brackets so it can be
    /// found in Business Central. Falls back to what the session came in through, and
    /// then to a plain phrase; never to an empty cell.
    /// </summary>
    /// <summary>A session doing nothing nameable. Compared by the callers that word a sentence around <see cref="Doing"/>.</summary>
    public const string Idle = "Idle";

    public static string Doing(BcSession session)
    {
        if (Describe(session.CurrentObjectName, session.CurrentObjectType, session.CurrentObjectId?.ToString()) is { } now)
        {
            return now;
        }
        if (Describe(session.EntryPointObjectName, session.EntryPointObjectType, session.EntryPointObjectId) is { } entry)
        {
            return entry;
        }
        // One word, because this column is eye-scanned for the row that is busy.
        return string.IsNullOrWhiteSpace(session.EntryPointOperation) ? Idle : session.EntryPointOperation;
    }

    /// <summary>How long, in the same rounded words the Operations tab uses for "Took".</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } took || took < TimeSpan.Zero) return "—";
        if (took.TotalSeconds < 1) return "Under a second";
        if (took.TotalMinutes < 1) return $"{(int)took.TotalSeconds} sec";
        return took.TotalHours < 1 ? $"{(int)took.TotalMinutes} min" : $"{(int)took.TotalHours} h {took.Minutes} min";
    }

    /// <summary>"Sales Order (page 42)", or null when there is no object worth naming.</summary>
    private static string? Describe(string? name, string? type, string? id)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var kind = string.IsNullOrWhiteSpace(type) ? null : SpaceOut(type).ToLowerInvariant();
        var number = string.IsNullOrWhiteSpace(id) || id == "0" ? null : id.Trim();
        return (kind, number) switch
        {
            ({ }, { }) => $"{name.Trim()} ({kind} {number})",
            ({ }, null) => $"{name.Trim()} ({kind})",
            (null, { }) => $"{name.Trim()} ({number})",
            _ => name.Trim(),
        };
    }

    private static string Normalise(string? value) =>
        new string((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string SpaceOut(string token)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(token, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}

/// <summary>
/// How much database each of a tenant's environments uses, and what the tenant is allowed
/// in total. Business Central can go over its allowance, so used may exceed the total.
/// </summary>
public sealed record BcTenantStorage(IReadOnlyDictionary<string, long> DatabaseKilobytesByEnvironment, long? AllowedKilobytes);
