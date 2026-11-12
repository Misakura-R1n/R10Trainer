using System.Text;

namespace R10.Bridge.Client;

public sealed class BoundedJsonLine
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _stream;
    private readonly int _maximumBytes;
    private readonly byte[] _buffer;
    private int _start, _end;

    public BoundedJsonLine(Stream stream, int maximumBytes)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        _buffer = new byte[Math.Min(maximumBytes, 4096)];
    }

    // Keep bytes following a delimiter for the next read on the same stream.
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        MemoryStream? spill = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_start == _end)
                {
                    _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _start = 0;
                    if (_end == 0)
                    {
                        if (spill is null) return null;
                        throw new InvalidDataException("JSON line ended before its newline delimiter.");
                    }
                }

                int start = _start;
                int newline = Array.IndexOf(_buffer, (byte)'\n', start, _end - start);
                int count = (newline >= 0 ? newline : _end) - start;
                if (count > _maximumBytes - (spill?.Length ?? 0))
                    throw new InvalidDataException("JSON line exceeds its byte limit.");
                _start = newline >= 0 ? newline + 1 : _end;
                if (newline >= 0 && spill is null) return Decode(_buffer, start, count);

                (spill ??= new MemoryStream(_buffer.Length)).Write(_buffer, start, count);
                if (newline >= 0) return Decode(spill.GetBuffer(), 0, checked((int)spill.Length));
            }
        }
        finally { spill?.Dispose(); }
    }

    private static string Decode(byte[] bytes, int offset, int length)
    {
        if (length > 0 && bytes[offset + length - 1] == (byte)'\r') length--;
        return StrictUtf8.GetString(bytes, offset, length);
    }

    public static async Task WriteAsync(Stream stream, string line, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (line.Contains('\n') || line.Contains('\r')) throw new InvalidDataException("JSON messages must occupy one line.");
        var bytes = StrictUtf8.GetBytes(line + "\n");
        if (bytes.Length - 1 > maximumBytes) throw new InvalidDataException("JSON line exceeds its byte limit.");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
