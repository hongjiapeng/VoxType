using System.Runtime.CompilerServices;
using System.Text;

namespace VoxType.Streaming;

// Shared source: the adapter and Windows client use the same bounded SSE framing.
internal static class ServerSentEvents
{
    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, int maximumBytes,
        [EnumeratorCancellation] CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        var line = new List<byte>();
        var data = new StringBuilder();
        var utf8 = new UTF8Encoding(false, true);
        int total = 0;
        bool afterCr = false, firstLine = true;
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > maximumBytes) throw new SseLimitException();
            for (int i = 0; i < count; i++)
            {
                byte value = buffer[i];
                if (afterCr && value == '\n') { afterCr = false; continue; }
                afterCr = value == '\r';
                if (value is not ((byte)'\r' or (byte)'\n')) { line.Add(value); continue; }
                string field = utf8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
                line.Clear();
                if (firstLine) { field = field.TrimStart('\uFEFF'); firstLine = false; }
                if (field.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        string message = data.ToString(0, data.Length - 1);
                        data.Clear();
                        yield return message;
                    }
                }
                else if (field == "data") data.Append('\n');
                else if (field.StartsWith("data:", StringComparison.Ordinal))
                {
                    string payload = field[5..];
                    if (payload.StartsWith(' ')) payload = payload[1..];
                    data.Append(payload).Append('\n');
                }
            }
        }
        // An unterminated event is incomplete, never a successful final result.
    }
}

internal sealed class SseLimitException : IOException;
