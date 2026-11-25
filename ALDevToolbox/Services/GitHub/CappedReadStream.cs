namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// A read-only pass-through stream that throws <see cref="ArchiveTooLargeException"/>
/// once more than a byte budget has been read through it. Wrapped around a download
/// it caps the bytes fetched; wrapped around a decompressor it caps what a small
/// archive may expand to.
/// </summary>
internal sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _max;
    private long _read;

    public CappedReadStream(Stream inner, long max)
    {
        _inner = inner;
        _max = max;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await _inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int n)
    {
        _read += n;
        if (_read > _max) throw new ArchiveTooLargeException(_max);
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
