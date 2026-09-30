using System.Net;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Drives <see cref="BcArtifactService"/> against the shared <see cref="TestDb"/>
/// Postgres fixture with a stubbed artifact index (no real Azure call): the
/// <c>oe_artifact_versions</c> cache upsert / remove-absent behaviour and the
/// resolve-to-URL path. The pure index parsing lives in
/// <see cref="BcArtifactIndexTests"/>.
/// </summary>
public sealed class BcArtifactServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private BcArtifactService NewService(StubHttpClientFactory factory, Data.AppDbContext ctx) =>
        new(factory, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance);

    private static StubHttpClientFactory FactoryFor(string countryJson, string platformJson)
    {
        var responses = new Dictionary<string, string>
        {
            ["/indexes/dk.json"] = countryJson,
            ["/indexes/platform.json"] = platformJson,
        };
        return new StubHttpClientFactory(responses);
    }

    private const string PlatformJson = """
    [ { "Version": "28.2.50931.51727" }, { "Version": "28.1.49000.50000" }, { "Version": "28.2.50000.50100" } ]
    """;

    [Fact]
    public async Task RefreshIndexAsync_populates_the_cache_newest_first()
    {
        const string country = """
        [ { "Version": "28.1.49000.50000" }, { "Version": "28.2.50931.51727" }, { "Version": "28.2.50000.50100" } ]
        """;
        await using var ctx = _db.NewContext();
        var svc = NewService(FactoryFor(country, PlatformJson), ctx);

        var rows = await svc.RefreshIndexAsync("dk");

        rows.Select(r => r.Version).Should().ContainInOrder(
            "28.2.50931.51727", "28.2.50000.50100", "28.1.49000.50000");
        rows[0].MajorMinor.Should().Be("28.2");
        rows[0].Country.Should().Be("dk");
        rows[0].ApplicationUrl.Should().Be(
            $"https://{BcArtifactIndex.CdnHost}/onprem/28.2.50931.51727/dk");

        await using var read = _db.NewContext();
        (await read.OeArtifactVersions.CountAsync(a => a.Country == "dk")).Should().Be(3);
    }

    [Fact]
    public async Task RefreshIndexAsync_upserts_without_duplicating_and_drops_absent_versions()
    {
        const string first = """
        [ { "Version": "28.1.49000.50000" }, { "Version": "28.2.50000.50100" } ]
        """;
        await using (var ctx = _db.NewContext())
        {
            await NewService(FactoryFor(first, PlatformJson), ctx).RefreshIndexAsync("dk");
        }

        DateTime firstRefreshedAt;
        await using (var read = _db.NewContext())
        {
            firstRefreshedAt = await read.OeArtifactVersions
                .Where(a => a.Version == "28.2.50000.50100").Select(a => a.RefreshedAt).SingleAsync();
        }

        // Second index: 28.1 is gone, a newer 28.2 build appears, 28.2.50000 stays.
        const string second = """
        [ { "Version": "28.2.50000.50100" }, { "Version": "28.2.50931.51727" } ]
        """;
        await using (var ctx = _db.NewContext())
        {
            await NewService(FactoryFor(second, PlatformJson), ctx).RefreshIndexAsync("dk");
        }

        await using var verify = _db.NewContext();
        var versions = await verify.OeArtifactVersions
            .Where(a => a.Country == "dk").Select(a => a.Version).ToListAsync();
        versions.Should().BeEquivalentTo(new[] { "28.2.50000.50100", "28.2.50931.51727" });
        // The surviving row was upserted, not duplicated, and its timestamp moved.
        var survivor = await verify.OeArtifactVersions.SingleAsync(a => a.Version == "28.2.50000.50100");
        survivor.RefreshedAt.Should().BeOnOrAfter(firstRefreshedAt);
    }

    [Fact]
    public async Task ResolveOnPremAsync_newest_and_exact_resolve_to_url_and_label()
    {
        const string country = """
        [ { "Version": "28.2.50931.51727" }, { "Version": "28.1.49000.50000" } ]
        """;
        await using var ctx = _db.NewContext();
        var svc = NewService(FactoryFor(country, PlatformJson), ctx);

        var newest = await svc.ResolveOnPremAsync("dk", version: null);
        newest.Should().NotBeNull();
        newest!.Version.Should().Be("28.2.50931.51727");
        newest.Label.Should().Be("Business Central 28.2 (DK)");
        newest.ApplicationUrl.Should().Be($"https://{BcArtifactIndex.CdnHost}/onprem/28.2.50931.51727/dk");

        var exact = await svc.ResolveOnPremAsync("dk", "28.1.49000.50000");
        exact!.Label.Should().Be("Business Central 28.1 (DK)");
    }

    [Fact]
    public async Task ResolveTargetAsync_resolves_an_insider_build_under_the_preview_key()
    {
        var factory = new StubHttpClientFactory(new Dictionary<string, string>
        {
            ["/onprem/indexes/dk.json"] = """[ { "Version": "28.5.54151.54365" } ]""",
            ["/onprem/indexes/platform.json"] = """[ { "Version": "28.5.54151.54365" } ]""",
            ["/sandbox/indexes/dk.json"] = """[ { "Version": "30.0.55227.0" }, { "Version": "29.0.52914.0" }, { "Version": "28.6.55100.0" } ]""",
            ["/sandbox/indexes/platform.json"] = """[ { "Version": "30.0.55227.0" }, { "Version": "29.0.52914.0" }, { "Version": "28.6.55100.0" } ]""",
        });
        await using var ctx = _db.NewContext();
        var svc = NewService(factory, ctx);

        var major = await svc.ResolveTargetAsync("dk", BcBuildTarget.NextMajor);
        major.Should().NotBeNull();
        major!.Version.Should().Be("29.0.52914.0");
        major.IsPrerelease.Should().BeTrue();
        major.MajorMinor.Should().Be("29.0");
        major.DedupKey.Should().Be("bc-insider:29.0:dk");
        major.ApplicationUrl.Should().Be($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/29.0.52914.0/dk");

        (await svc.ResolveTargetAsync("dk", BcBuildTarget.NextMinor))!.Version.Should().Be("28.6.55100.0");
    }

    [Fact]
    public async Task ResolveTargetAsync_is_null_when_the_insider_storage_has_no_index_for_the_country()
    {
        await using var ctx = _db.NewContext();
        // Only the public storage answers; the insider one 404s.
        var factory = new StubHttpClientFactory(new Dictionary<string, string>
        {
            ["/onprem/indexes/dk.json"] = """[ { "Version": "28.2.50931.51727" } ]""",
            ["/onprem/indexes/platform.json"] = PlatformJson,
        });
        (await NewService(factory, ctx).ResolveTargetAsync("dk", BcBuildTarget.NextMajor)).Should().BeNull();
    }
}

/// <summary>Maps a request URL substring to a canned JSON body; 404 for anything unmatched.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly IReadOnlyDictionary<string, string> _responses;
    public StubHttpClientFactory(IReadOnlyDictionary<string, string> responses) => _responses = responses;

    public HttpClient CreateClient(string name) => new(new StubHandler(_responses));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;
        public StubHandler(IReadOnlyDictionary<string, string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            foreach (var (needle, body) in _responses)
            {
                if (url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
