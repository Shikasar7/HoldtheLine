using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace HoldTheLine.Net;

/// <summary>
/// UTF-8 text framing over a <see cref="WebSocket"/>, shared by the client transport and the
/// server-side connection so both halves reassemble fragmented frames identically.
/// </summary>
public static class WebSocketText
{
    public static async ValueTask SendAsync(WebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>Awaits one complete text message; returns null on a clean close or an abrupt drop.</summary>
    public static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var frame = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                frame.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
