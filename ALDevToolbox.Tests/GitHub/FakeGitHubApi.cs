using System.Net;
using System.Text;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// A stand-in for GitHub's HTTP surface.
///
/// <para><c>api.github.com</c> is not reachable from the build environment, and
/// would be the wrong thing to depend on if it were: these tests are about how
/// the workbench reacts to GitHub's documented answers - a 404 for a repository
/// you cannot see, a 302 for an organisation you are not in, a rotated refresh
/// token - not about GitHub itself. Each route is registered by the method and
/// path prefix the production code calls, so a change to either shows up as a
/// missed route rather than a silently different test.</para>
/// </summary>
public sealed class FakeGitHubApi : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = new();

    /// <summary>Repositories that still have no commits. See <see cref="EmptyRepository"/>.</summary>
    private readonly HashSet<string> _emptyRepositories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request the handler saw, as "METHOD absolute-uri", in order.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Bodies of the form posts to the OAuth token endpoint, in order.</summary>
    public List<string> OAuthBodies { get; } = new();

    /// <summary>
    /// Every request body the handler saw, keyed by "METHOD absolute-uri" - so
    /// a test can assert what went <em>into</em> a commit, not only that one was
    /// attempted.
    /// </summary>
    public List<(string Call, string Body)> Bodies { get; } = new();

    /// <summary>
    /// The bearer token each request carried, keyed by "METHOD absolute-uri" -
    /// so a test can assert <em>which credential</em> made a call, which is the
    /// security decision this milestone turns on rather than an implementation
    /// detail.
    /// </summary>
    public List<(string Call, string? Token)> Credentials { get; } = new();

    /// <summary>
    /// The "status" that means GitHub did not answer at all: the handler throws
    /// <see cref="HttpRequestException"/> instead of replying, which is how a
    /// dropped connection, a DNS failure or a timeout reaches the client. It is
    /// a different case from a 503 - there GitHub answered, and said so - and it
    /// is the one the access questions must never remember as a definite no.
    /// </summary>
    public const HttpStatusCode Unreachable = 0;

    /// <summary>
    /// Registers a reply for requests whose path starts with
    /// <paramref name="path"/>. When several routes match, the longest wins, so
    /// <c>/user/installations</c> is not swallowed by <c>/user</c>; between two
    /// routes of the same length the later registration wins, so a helper's
    /// default answer can be overridden by the test that needs a different one.
    /// </summary>
    public FakeGitHubApi On(HttpMethod method, string path, HttpStatusCode status, string? json = null)
    {
        _routes.Add((method.Method, Normalise(path), _ => Respond(status, json)));
        return this;
    }

    /// <summary>
    /// Registers a <c>302</c> pointing at <paramref name="location"/>, which is how
    /// GitHub answers a request for a Release asset's bytes: the file itself lives on
    /// storage behind a signed URL, and the client has to follow the hop by hand
    /// because it does not auto-redirect.
    /// </summary>
    public FakeGitHubApi OnRedirect(HttpMethod method, string path, string location)
    {
        _routes.Add((method.Method, Normalise(path), _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            };
            response.Headers.Location = new Uri(location);
            return response;
        }));
        return this;
    }

    /// <summary>
    /// Registers a reply computed from the request that may also carry headers - a
    /// <c>Link</c> header, say, which is how GitHub's cursor-paged routes name the
    /// next page.
    /// </summary>
    public FakeGitHubApi OnWithHeaders(
        HttpMethod method, string path,
        Func<HttpRequestMessage, (HttpStatusCode Status, string Json, (string Name, string Value)[] Headers)> reply)
    {
        _routes.Add((method.Method, Normalise(path), request =>
        {
            var (status, json, headers) = reply(request);
            var response = Respond(status, json);
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }));
        return this;
    }

    /// <summary>Registers a reply carrying raw bytes - a Release asset, say, rather than JSON.</summary>
    public FakeGitHubApi OnBytes(HttpMethod method, string path, byte[] content)
    {
        _routes.Add((method.Method, Normalise(path), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        }));
        return this;
    }

    /// <summary>
    /// Registers a reply computed from the request itself - for the routes whose
    /// answer depends on <em>which credential</em> asked, as GitHub's own do
    /// when two installations call the same path.
    /// </summary>
    public FakeGitHubApi On(
        HttpMethod method, string path, Func<HttpRequestMessage, (HttpStatusCode Status, string? Json)> reply)
    {
        _routes.Add((method.Method, Normalise(path), request =>
        {
            var (status, json) = reply(request);
            return Respond(status, json);
        }));
        return this;
    }

    /// <summary>
    /// Registers a reply that changes with each call - the sequence is consumed
    /// in order and the last entry repeats, which is how a refresh test says
    /// "expired, then good".
    /// </summary>
    public FakeGitHubApi OnSequence(HttpMethod method, string path, params (HttpStatusCode Status, string? Json)[] replies)
    {
        var index = 0;
        _routes.Add((method.Method, Normalise(path), _ =>
        {
            var reply = replies[Math.Min(index, replies.Length - 1)];
            index++;
            return Respond(reply.Status, reply.Json);
        }));
        return this;
    }

    /// <summary>
    /// Makes <paramref name="fullName"/> behave like a repository that has just
    /// been created with <c>auto_init: false</c>: it has no commits, so every
    /// Git Data call on it answers <c>409 Conflict: Git Repository is empty.</c>
    /// exactly as GitHub's does, until something writes the first file through
    /// the Contents API.
    ///
    /// <para>Without this the fake answers 201 to a blob in a repository that
    /// could not have accepted one, which is how the whole "create it on GitHub"
    /// flow passed its tests while failing on the first call in production.</para>
    /// </summary>
    public FakeGitHubApi EmptyRepository(string fullName)
    {
        _emptyRepositories.Add(fullName);
        return this;
    }

    /// <summary>A user-to-server token response, in the shape GitHub's OAuth endpoint returns.</summary>
    public static string TokenJson(
        string accessToken = "ghu_access",
        int? expiresIn = 28800,
        string? refreshToken = "ghr_refresh",
        int? refreshExpiresIn = 15811200)
    {
        var parts = new List<string> { $"\"access_token\":\"{accessToken}\"", "\"token_type\":\"bearer\"" };
        if (expiresIn is not null) parts.Add($"\"expires_in\":{expiresIn}");
        if (refreshToken is not null) parts.Add($"\"refresh_token\":\"{refreshToken}\"");
        if (refreshExpiresIn is not null) parts.Add($"\"refresh_token_expires_in\":{refreshExpiresIn}");
        return "{" + string.Join(',', parts) + "}";
    }

    /// <summary>The <c>GET /user</c> body.</summary>
    public static string UserJson(long id = 4711, string login = "cronus-dev") =>
        $"{{\"id\":{id},\"login\":\"{login}\"}}";

    /// <summary>One repository, in the shape every repository route returns.</summary>
    public static string RepositoryJson(
        string fullName,
        string defaultBranch = "main",
        bool isPrivate = true,
        string? description = null)
    {
        var name = fullName.Split('/').Last();
        var describe = description is null ? "null" : $"\"{description}\"";
        return $$"""
            {"full_name":"{{fullName}}","name":"{{name}}","default_branch":"{{defaultBranch}}",
             "private":{{(isPrivate ? "true" : "false")}},"description":{{describe}},
             "html_url":"https://github.com/{{fullName}}","clone_url":"https://github.com/{{fullName}}.git"}
            """;
    }

    /// <summary>The <c>GET /installation/repositories</c> body.</summary>
    public static string InstallationRepositoriesJson(params string[] fullNames) =>
        $"{{\"total_count\":{fullNames.Length},\"repositories\":[{string.Join(',', fullNames.Select(f => RepositoryJson(f)))}]}}";

    /// <summary>The <c>POST /app/installations/{id}/access_tokens</c> body.</summary>
    public static string InstallationTokenJson(string token = "ghs_installation") =>
        $"{{\"token\":\"{token}\",\"expires_at\":\"{DateTimeOffset.UtcNow.AddHours(1):O}\"}}";

    /// <summary>The Contents API's body for one file.</summary>
    public static string FileContentsJson(string path, string text, string sha = "blob-sha")
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return $"{{\"path\":\"{path}\",\"sha\":\"{sha}\",\"encoding\":\"base64\",\"content\":\"{encoded}\"}}";
    }

    /// <summary>A <c>{"sha": …}</c> body, which most Git Data writes answer with.</summary>
    public static string ShaJson(string sha) => $"{{\"sha\":\"{sha}\"}}";

    /// <summary>The <c>PUT /repos/{owner}/{repo}/contents/{path}</c> body: the new file, and the commit it landed in.</summary>
    public static string FileWriteJson(string contentSha = "written-blob-sha", string commitSha = "seed-commit-sha") =>
        $"{{\"content\":{{\"sha\":\"{contentSha}\"}},\"commit\":{{\"sha\":\"{commitSha}\"}}}}";

    /// <summary>
    /// The <c>GET /user/installations</c> body, every entry sitting on the same
    /// organisation. The <c>account</c> object is part of GitHub's shape and is
    /// what the install gate reads: the list says which installations a person
    /// can reach, and the account is what their role is then checked in.
    /// </summary>
    public static string InstallationsJson(params long[] ids) => InstallationsJson("cronus-dk", ids);

    /// <summary>The same, on a named account.</summary>
    public static string InstallationsJson(string accountLogin, params long[] ids) =>
        InstallationsJson(accountLogin, "Organization", ids);

    /// <summary>The same, on a named account of a given type (<c>Organization</c> or <c>User</c>).</summary>
    public static string InstallationsJson(string accountLogin, string accountType, params long[] ids) =>
        $"{{\"total_count\":{ids.Length},\"installations\":[{string.Join(',', ids.Select(i =>
            $"{{\"id\":{i},\"account\":{{\"login\":\"{accountLogin}\",\"type\":\"{accountType}\"}}}}"))}]}}";

    /// <summary>
    /// The <c>GET /user/memberships/orgs/{org}</c> body. <c>admin</c> is what
    /// GitHub calls an owner; everyone else is <c>member</c>, and a state other
    /// than <c>active</c> is an invitation nobody has accepted.
    /// </summary>
    public static string OrgMembershipJson(string role = "admin", string state = "active") =>
        $"{{\"state\":\"{state}\",\"role\":\"{role}\"}}";

    /// <summary>
    /// One Release, in the shape every Releases route returns. <c>upload_url</c> is
    /// GitHub's URI template on the separate uploads host, whose <c>{?name,label}</c>
    /// suffix the client has to strip.
    /// </summary>
    public static string ReleaseJson(
        string fullName, string tag, long id = 900, string? name = null,
        string publishedAt = "2026-09-01T10:00:00Z",
        params (long Id, string Name)[] assets)
    {
        var assetJson = string.Join(',', assets.Select(a =>
            "{\"id\":" + a.Id + ",\"name\":\"" + a.Name + "\",\"size\":3}"));
        return "{\"id\":" + id
            + ",\"tag_name\":\"" + tag + "\""
            + ",\"name\":\"" + (name ?? tag) + "\""
            + ",\"published_at\":\"" + publishedAt + "\""
            + ",\"html_url\":\"https://github.com/" + fullName + "/releases/tag/" + tag + "\""
            + ",\"upload_url\":\"https://uploads.github.com/repos/" + fullName + "/releases/" + id + "/assets{?name,label}\""
            + ",\"assets\":[" + assetJson + "]}";
    }

    /// <summary>
    /// GitHub's 422 when a ruleset refuses a write - what a new repository's
    /// default branch answers when the GitHub App is not on the organisation
    /// ruleset's bypass list. The rules that fired are appended to the message,
    /// which is why the workbench matches on the first two words and not the
    /// whole sentence.
    /// </summary>
    public static string RuleViolationJson(string rule = "Changes must be made through a pull request.") =>
        $"{{\"message\":\"Repository rule violations found {rule}\","
        + "\"documentation_url\":\"https://docs.github.com/rest/git/refs\",\"status\":\"422\"}";

    /// <summary>The <c>GET /repos/{owner}/{repo}/releases</c> body.</summary>
    public static string ReleasesJson(params string[] releases) => "[" + string.Join(',', releases) + "]";

    /// <summary>
    /// Holds every request to <paramref name="path"/> until the returned source
    /// is completed - so a test can look at the page <em>while</em> a call is in
    /// flight, which is the only way to see a busy state at all. The route's own
    /// reply is used once the gate opens.
    /// </summary>
    public TaskCompletionSource PauseUntilReleased(HttpMethod method, string path)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Add((method.Method, Normalise(path), gate.Task));
        return gate;
    }

    private readonly List<(string Method, string Path, Task Gate)> _gates = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var gate = _gates.FirstOrDefault(g => g.Method == request.Method.Method
            && request.RequestUri!.AbsolutePath.StartsWith(g.Path, StringComparison.Ordinal));
        if (gate.Gate is not null)
        {
            await gate.Gate.WaitAsync(cancellationToken);
        }

        return await Dispatch(request, cancellationToken);
    }

    private Task<HttpResponseMessage> Dispatch(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.ToString();
        Calls.Add($"{request.Method.Method} {uri}");
        Credentials.Add(($"{request.Method.Method} {uri}", request.Headers.Authorization?.Parameter));
        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Bodies.Add(($"{request.Method.Method} {uri}", body));
            if (uri.Contains("login/oauth/access_token")) OAuthBodies.Add(body);
        }

        var path = request.RequestUri.AbsolutePath;

        // An empty repository refuses the Git Data API and nothing else, and
        // stops being empty the moment the Contents API writes into it.
        var empty = _emptyRepositories.FirstOrDefault(
            r => path.StartsWith($"/repos/{r}/", StringComparison.OrdinalIgnoreCase));
        if (empty is not null)
        {
            if (path.StartsWith($"/repos/{empty}/git/", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Respond(HttpStatusCode.Conflict,
                    "{\"message\":\"Git Repository is empty.\","
                    + "\"documentation_url\":\"https://docs.github.com/rest/git\",\"status\":\"409\"}"));
            }
        }

        var match = _routes
            .Select((Route, Index) => (Route, Index))
            .Where(r => r.Route.Method == request.Method.Method
                && path.StartsWith(r.Route.Path, StringComparison.Ordinal))
            .OrderByDescending(r => r.Route.Path.Length)
            .ThenByDescending(r => r.Index)
            .Select(r => (r.Route, Found: true))
            .FirstOrDefault();
        if (match.Found)
        {
            var response = match.Route.Reply(request);
            // The write that gave it a history: from here on the Git Data API
            // works, as it does on any repository with a commit in it.
            if (empty is not null
                && response.IsSuccessStatusCode
                && request.Method == HttpMethod.Put
                && path.StartsWith($"/repos/{empty}/contents/", StringComparison.OrdinalIgnoreCase))
            {
                _emptyRepositories.Remove(empty);
            }
            return Task.FromResult(response);
        }

        // An unregistered route is a test that asked GitHub something it did not
        // mean to; say which call, rather than failing later on an empty body.
        return Task.FromResult(Respond(HttpStatusCode.NotImplemented,
            $"{{\"message\":\"FakeGitHubApi has no route for {request.Method} {uri}\"}}"));
    }

    /// <summary>Routes are written without a leading slash for readability; the paths they match have one.</summary>
    private static string Normalise(string path) => path.StartsWith('/') ? path : "/" + path;

    private static HttpResponseMessage Respond(HttpStatusCode status, string? json)
    {
        if (status == Unreachable)
        {
            throw new HttpRequestException("FakeGitHubApi: GitHub could not be reached.");
        }
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        };
    }
}
