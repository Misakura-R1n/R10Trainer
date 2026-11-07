using System.Text;

namespace R10.Bridge.Client;

public static class BoundedJsonLine
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string?> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 4096));
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (buffer.Length == 0) return null;
                throw new InvalidDataException("JSON line ended before its newline delimiter.");
            }
            if (one[0] == (byte)'\n')
            {
                var length = checked((int)buffer.Length);
                if (length > 0 && buffer.GetBuffer()[length - 1] == (byte)'\r') length--;
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, length);
            }
            if (buffer.Length >= maximumBytes) throw new InvalidDataException("JSON line exceeds its byte limit.");
            buffer.WriteByte(one[0]);
        }
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
