using System.Net;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// Where <see cref="GitHubAppClient.ReadTarballAsync{T}"/> is willing to fetch a
/// repository's archive from, and how much of it (#1415): GitHub's redirect is
/// followed only to its own archive host over HTTPS, and the download is held to
/// the caller's byte budget whether or not GitHub declares a length.
/// </summary>
public sealed class GitHubAppClientArchiveTests : IDisposable
{
    private const string Repo = "contoso/document-capture";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("https://evil.example/contoso/document-capture/legacy.tar.gz/x")]
    [InlineData("http://codeload.github.com/contoso/document-capture/legacy.tar.gz/x")]
    [InlineData("https://codeload.github.com.evil.example/contoso/document-capture/legacy.tar.gz/x")]
    public async Task A_redirect_anywhere_but_the_archive_host_is_not_followed(string location)
    {
        var api = new FakeGitHubApi()
            .OnRedirect(HttpMethod.Get, $"/repos/{Repo}/tarball/{Sha}", location)
            .OnBytes(HttpMethod.Get, "/", [1, 2, 3]);
        var read = false;

        var act = () => ReadAsync(api, maxBytes: 1024, (_, _) => { read = true; return Task.FromResult(0); });

        (await act.Should().ThrowAsync<GitHubApiException>()).WithMessage("*somewhere unexpected*");
        api.Calls.Should().ContainSingle("only GitHub's own answer was asked for; the hop it named was never dialled");
        read.Should().BeFalse();
    }

    [Fact]
    public async Task A_declared_length_over_the_cap_is_refused_before_the_body_is_read()
    {
        var body = new WatchedStream(new byte[16]);
        var api = Archive(() =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = 1025;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var read = false;

        var act = () => ReadAsync(api, maxBytes: 1024, (_, _) => { read = true; return Task.FromResult(0); });

        (await act.Should().ThrowAsync<ArchiveTooLargeException>()).Which.LimitBytes.Should().Be(1024);
        read.Should().BeFalse();
        body.BytesRead.Should().Be(0);
    }

    [Fact]
    public async Task A_body_with_no_declared_length_is_cut_off_once_it_streams_past_the_cap()
    {
        var body = new WatchedStream(new byte[1024 * 1024]);
        var api = Archive(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

        var act = () => ReadAsync(api, maxBytes: 1024, async (stream, ct) =>
        {
            await stream.CopyToAsync(Stream.Null, ct);
            return 0;
        });

        (await act.Should().ThrowAsync<ArchiveTooLargeException>()).Which.LimitBytes.Should().Be(1024);
        body.BytesRead.Should().BeLessThan(1024 * 1024, "the download stops at the cap rather than running to the end");
    }

    [Fact]
    public async Task A_body_exactly_at_the_cap_is_read_whole()
    {
        var api = Archive(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WatchedStream(new byte[1024])) });

        var length = await ReadAsync(api, maxBytes: 1024, async (stream, ct) =>
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.Length;
        });

        length.Should().Be(1024);
    }

    private static FakeGitHubApi Archive(Func<HttpResponseMessage> reply) => new FakeGitHubApi()
        .OnRedirect(HttpMethod.Get, $"/repos/{Repo}/tarball/{Sha}", $"https://codeload.github.com/{Repo}/legacy.tar.gz/{Sha}")
        .OnResponse(HttpMethod.Get, $"/{Repo}/legacy.tar.gz/{Sha}", reply);

    private async Task<T> ReadAsync<T>(FakeGitHubApi api, long maxBytes, Func<Stream, CancellationToken, Task<T>> read)
    {
        await using var ctx = _db.NewContext();
        return await _db.NewGitHubAppClient(ctx, api)
            .ReadTarballAsync(null, "contoso", "document-capture", Sha, maxBytes, read);
    }

    /// <summary>A body that counts what was read from it, and cannot be measured, so nothing declares its length unasked.</summary>
    private sealed class WatchedStream(byte[] content) : MemoryStream(content)
    {
        public long BytesRead { get; private set; }

        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count) => Counted(base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(base.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Counted(await base.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Counted(await base.ReadAsync(buffer, offset, count, cancellationToken));

        private int Counted(int n)
        {
            BytesRead += n;
            return n;
        }
    }
}
