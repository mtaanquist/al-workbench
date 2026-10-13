using System.Net;
using System.Text;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Offsite;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Drives <see cref="S3Provider.ListAsync"/> against a loopback HTTP endpoint
/// serving canned ListObjectsV2 XML, so the response-shape handling is covered
/// without a live bucket or a container runtime.
///
/// <para>
/// These exist because AWS SDK v4 changed response shapes in ways the compiler
/// cannot catch: collections come back <c>null</c> instead of empty, and the
/// scalar properties on <c>S3Object</c> became nullable. The empty-listing case
/// below is the one that actually bit — a prefix with no objects hands back a
/// null <c>S3Objects</c>, which a bare <c>foreach</c> dereferences.
/// </para>
/// </summary>
public sealed class S3ProviderListTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _endpoint;
    private string _responseXml = string.Empty;
    private volatile string? _lastQuery;

    public S3ProviderListTests()
    {
        // Port 0 asks the OS for a free port, but HttpListener needs a concrete
        // prefix — grab one via a throwaway socket first.
        var port = FreePort();
        _endpoint = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_endpoint);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _lastQuery = ctx.Request.Url?.Query;
            var bytes = Encoding.UTF8.GetBytes(_responseXml);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/xml";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private S3Provider NewProvider() => new(
        new ResolvedOffsiteSettings(
            Provider: "s3",
            Endpoint: _endpoint,
            Region: "eu-west-1",
            Bucket: "backups",
            Prefix: "aldevtoolbox/",
            AccessKey: "test-access-key",
            SecretKey: "test-secret-key",
            // Path style keeps the bucket out of the hostname, so requests land
            // on the loopback prefix above rather than backups.localhost.
            ForcePathStyle: true,
            RetentionDays: 90),
        NullLogger<S3Provider>.Instance);

    [Fact]
    public async Task A_listing_maps_key_size_and_last_modified()
    {
        _responseXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <Name>backups</Name>
          <IsTruncated>false</IsTruncated>
          <Contents>
            <Key>aldevtoolbox/2026-08-01.dump</Key>
            <LastModified>2026-08-01T09:30:00.000Z</LastModified>
            <Size>2048</Size>
          </Contents>
          <Contents>
            <Key>aldevtoolbox/2026-08-02.dump</Key>
            <LastModified>2026-08-02T09:30:00.000Z</LastModified>
            <Size>4096</Size>
          </Contents>
        </ListBucketResult>
        """;

        using var provider = NewProvider();
        var objects = await provider.ListAsync("aldevtoolbox/", maxObjects: 100, topLevelOnly: false, CancellationToken.None);

        objects.Should().HaveCount(2);
        objects[0].Key.Should().Be("aldevtoolbox/2026-08-01.dump");
        objects[0].Size.Should().Be(2048);
        objects[0].LastModifiedUtc.Should().Be(new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc));
        objects[1].Size.Should().Be(4096);
    }

    /// <summary>
    /// The AWS SDK v4 regression guard: an empty prefix comes back with no
    /// Contents elements, which v4 surfaces as a null S3Objects rather than an
    /// empty list. Before the null-coalesce in ListAsync this threw a
    /// NullReferenceException instead of returning nothing.
    /// </summary>
    [Fact]
    public async Task An_empty_prefix_returns_no_objects_rather_than_throwing()
    {
        _responseXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <Name>backups</Name>
          <IsTruncated>false</IsTruncated>
        </ListBucketResult>
        """;

        using var provider = NewProvider();
        var objects = await provider.ListAsync("aldevtoolbox/", maxObjects: 100, topLevelOnly: false, CancellationToken.None);

        objects.Should().BeEmpty();
    }

    /// <summary>
    /// The off-site prune lists the whole-database dumps with a delimiter so S3
    /// does not page through the per-tenant subtree underneath them (#1258).
    /// </summary>
    [Fact]
    public async Task A_top_level_listing_asks_for_a_delimiter_and_a_full_one_does_not()
    {
        _responseXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
          <Name>backups</Name>
          <IsTruncated>false</IsTruncated>
        </ListBucketResult>
        """;

        using var provider = NewProvider();
        await provider.ListAsync("aldevtoolbox/", maxObjects: 100, topLevelOnly: true, CancellationToken.None);
        _lastQuery.Should().Contain("delimiter=%2F");

        await provider.ListAsync("aldevtoolbox/", maxObjects: 100, topLevelOnly: false, CancellationToken.None);
        _lastQuery.Should().NotContain("delimiter");
    }

    public void Dispose()
    {
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
    }
}
