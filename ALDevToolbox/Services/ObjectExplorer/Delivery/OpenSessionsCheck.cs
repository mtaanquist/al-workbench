using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Who is signed in to a deployment's target environment, asked the moment before a
/// person sends a build that installs right away, so they can hold off while customers
/// are working. Counted, never listed: the names are read and dropped here, same as the
/// Sessions tab (see <c>.design/saas-delivery.md</c>, "Checking who is online before a
/// deployment").
/// <para>
/// Two counts, because they mean different things to the person deciding. A delegated
/// user is a partner signed in through delegated admin access, which Business Central
/// reports under a generated name starting with <c>USER_</c>; everyone else is the
/// customer's own people.
/// </para>
/// </summary>
/// <param name="EnvironmentName">The environment that was asked.</param>
/// <param name="EndUsers">Distinct people signed in who are not delegated users.</param>
/// <param name="DelegatedUsers">Distinct delegated users signed in.</param>
/// <param name="Failure">Why Business Central could not be asked, when it could not; the counts are then zero and mean nothing.</param>
public sealed record OpenSessionsCheck(string EnvironmentName, int EndUsers, int DelegatedUsers, string? Failure = null)
{
    /// <summary>The prefix Business Central gives a delegated admin's user name.</summary>
    public const string DelegatedUserPrefix = "USER_";

    /// <summary>True when the person should be asked before the deployment goes ahead: somebody is signed in, or we could not tell.</summary>
    public bool NeedsConfirmation => Failure is not null || EndUsers + DelegatedUsers > 0;

    /// <summary>
    /// Counts the people in <paramref name="sessions"/>. One person with three tabs open is
    /// one person, so users are counted once each. Web service calls and background, child
    /// and job queue sessions are left out (<see cref="BcSessionDisplay.IsPerson"/>): an
    /// integration or a job runs under somebody's name without that person being online.
    /// </summary>
    public static OpenSessionsCheck From(string environmentName, IEnumerable<BcSession> sessions)
    {
        var users = sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.UserId)
                && BcSessionDisplay.IsPerson(s.ClientType))
            .Select(s => s.UserId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var delegated = users.Count(u => u.StartsWith(DelegatedUserPrefix, StringComparison.OrdinalIgnoreCase));
        return new OpenSessionsCheck(environmentName, users.Count - delegated, delegated);
    }

    /// <summary>A check that could not be made, with Business Central's reason.</summary>
    public static OpenSessionsCheck Unknown(string environmentName, string reason) =>
        new(environmentName, 0, 0, string.IsNullOrWhiteSpace(reason) ? "Business Central did not answer." : reason.Trim());

    /// <summary>
    /// The question put to the person: "There are 3 end-users online and 1 delegated user
    /// online in Production. Are you sure you want to deploy the build?", or why we could
    /// not tell and the same question.
    /// </summary>
    public string Question
    {
        get
        {
            const string ask = "Are you sure you want to deploy the build?";
            if (Failure is { } failure)
            {
                var reason = failure.EndsWith('.') || failure.EndsWith('!') || failure.EndsWith('?') ? failure : failure + ".";
                return $"Couldn't check who is signed in to {EnvironmentName}. {reason} {ask}";
            }
            return $"There {(EndUsers == 1 ? "is" : "are")} {Count(EndUsers, "end-user", "end-users")} online and "
                + $"{Count(DelegatedUsers, "delegated user", "delegated users")} online in {EnvironmentName}. {ask}";
        }
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
