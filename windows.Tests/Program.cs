using System.IO;
using AboEzzatReceiver.Windows;

const int limit = MediaHeaderProxy.MaxPlaylistBytes;

var exact = new GeneratedStream(limit);
byte[] accepted = await MediaHeaderProxy.ReadBoundedAsync(exact, limit, CancellationToken.None);
if (accepted.Length != limit || exact.BytesRead != limit)
    throw new InvalidOperationException("A playlist exactly at the configured limit was not accepted.");

var oversized = new GeneratedStream(limit + 1024);
try
{
    _ = await MediaHeaderProxy.ReadBoundedAsync(oversized, limit, CancellationToken.None);
    throw new InvalidOperationException("An oversized playlist was unexpectedly accepted.");
}
catch (InvalidDataException)
{
    if (oversized.BytesRead != limit + 1)
        throw new InvalidOperationException("The reader consumed more than one byte beyond the configured limit.");
}

Console.WriteLine("PASS: exact-limit HLS playlist accepted.");
Console.WriteLine("PASS: oversized HLS playlist rejected after reading only limit + 1 bytes.");

sealed class GeneratedStream : Stream
{
    private readonly long _length;
    private long _position;

    public long BytesRead => _position;
    public GeneratedStream(long length) => _length = length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = (int)Math.Min(buffer.Length, _length - _position);
        if (count > 0)
        {
            buffer.Span[..count].Fill(0x41);
            _position += count;
        }
        return ValueTask.FromResult(count);
    }
}
