using System.IO.Compression;
using System.Text;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Download endpoints for the Artifacts tool: one compiled <c>.app</c>, a build's
/// "Download all" zip, and the raw build log. Any signed-in user who can see the
/// owning project may download its builds, so these require authentication but not
/// project ownership. Two fences are re-checked server-side by
/// <see cref="ArtifactService"/>, which runs on the request's org-filtered
/// <c>AppDbContext</c>: a build belonging to another org returns 404, and a build
/// of a Private project the caller has no grant on throws
/// <see cref="ProjectAccessDeniedException"/>, which each handler turns into the
/// same 404 — a refusal that told the two apart would confirm the project exists.
/// GETs carry no antiforgery token (they're plain download links, no state change).
/// See <c>.design/artifacts.md</c> and <c>.design/teams-and-visibility.md</c>.
/// </summary>
internal static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        // One deliverable.
        app.MapGet("/artifacts/build/{buildId:int}/app/{artifactId:int}",
            async (int buildId, int artifactId, ArtifactService artifacts, CancellationToken ct) =>
            {
                DownloadFile? file;
                try
                {
                    file = await artifacts.GetArtifactBytesAsync(buildId, artifactId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (file is null) return Results.NotFound();
                return Results.File(file.Content, "application/octet-stream", SanitiseFileName(file.FileName));
            })
            .RequireAuthorization();

        // Every deliverable of a build, zipped. Streamed straight to the response, one
        // .app read at a time: a build with many large apps used to sit in memory three
        // times over - the blobs, the growing zip, and its copy (#1263).
        app.MapGet("/artifacts/build/{buildId:int}/all",
            async (int buildId, ArtifactService artifacts, CancellationToken ct) =>
            {
                List<ArtifactRow> rows;
                try
                {
                    rows = await artifacts.ListArtifactRowsAsync(buildId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (rows.Count == 0) return Results.NotFound();

                return Results.Stream(async body =>
                {
                    // ZipArchive writes synchronously, which the response body refuses
                    // (#1212), so each entry lands in a buffer that is sent on
                    // asynchronously before the next app is read.
                    using var buffer = new EntryBuffer(body);
                    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var row in rows)
                        {
                            // Re-read through the same gate; one deleted since the listing is skipped.
                            var file = await artifacts.GetArtifactBytesAsync(buildId, row.Id, ct);
                            if (file is null) continue;
                            var entry = zip.CreateEntry(SanitiseFileName(file.FileName), CompressionLevel.Fastest);
                            using (var es = entry.Open()) es.Write(file.Content);
                            await buffer.SendAsync(ct);
                        }
                    }
                    // The archive's closing directory, written as it was disposed.
                    await buffer.SendAsync(ct);
                }, "application/zip", $"build-{buildId}-apps.zip");
            })
            .RequireAuthorization();

        // The raw build log.
        app.MapGet("/artifacts/build/{buildId:int}/log",
            async (int buildId, ArtifactService artifacts, CancellationToken ct) =>
            {
                RawLog? log;
                try
                {
                    log = await artifacts.GetRawLogAsync(buildId, ct);
                }
                catch (ProjectAccessDeniedException)
                {
                    return Results.NotFound();
                }
                if (log is null) return Results.NotFound();
                return Results.File(Encoding.UTF8.GetBytes(log.Content), "text/plain; charset=utf-8", log.FileName);
            })
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// A write-only stream that holds what is written to it until <see cref="SendAsync"/>
    /// copies it to the response and empties it. It lets a synchronous writer
    /// (<see cref="ZipArchive"/>) feed a response body that only accepts asynchronous
    /// writes, while holding no more than the entry being written.
    /// </summary>
    private sealed class EntryBuffer : Stream
    {
        private readonly Stream _response;
        private readonly MemoryStream _pending = new();
        private long _written;

        public EntryBuffer(Stream response) => _response = response;

        public async Task SendAsync(CancellationToken ct)
        {
            if (_pending.Length == 0) return;
            await _response.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            await _response.FlushAsync(ct);
            _pending.SetLength(0);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _pending.Write(buffer);
            _written += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _pending.Dispose();
            base.Dispose(disposing);
        }
    }
}
