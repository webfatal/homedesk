using System.Net.WebSockets;

namespace RemoteDesk.Host.Server;

/// <summary>
/// Represents a single connected viewer's WebSocket session.
/// </summary>
public sealed class WebSocketSession : IDisposable
{
    private readonly WebSocket _webSocket;
    private readonly CancellationTokenSource _cts = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string PreferredCodec { get; set; } = "jpeg";
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketSession(WebSocket webSocket)
    {
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
    }

    /// <summary>
    /// Sends a binary frame to this viewer.
    /// </summary>
    public async Task SendBinaryAsync(byte[] data)
    {
        if (!IsConnected) return;

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                _cts.Token);
        }
        catch (WebSocketException)
        {
            // Connection lost — will be cleaned up by session manager
        }
    }

    /// <summary>
    /// Sends a JSON text message to this viewer.
    /// </summary>
    public async Task SendTextAsync(string json)
    {
        if (!IsConnected) return;

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                _cts.Token);
        }
        catch (WebSocketException)
        {
            // Connection lost
        }
    }

    /// <summary>
    /// Receives the next WebSocket message. Returns null on close.
    /// </summary>
    public async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveAsync()
    {
        var buffer = new byte[4096];
        using var ms = new System.IO.MemoryStream();

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return (result.MessageType, ms.ToArray());
        }
        catch (WebSocketException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gracefully closes the WebSocket connection.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Session ended",
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Already closed
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _webSocket.Dispose();
    }
}
