using System.Net.WebSockets;
using System.Text;

namespace ContentCoHostStudio;

public interface ILiveTransport : IAsyncDisposable
{
    Task ConnectAsync(string apiKey, CancellationToken cancellationToken);
    Task SendAsync(string json, CancellationToken cancellationToken);
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    void Abort();
}

public sealed class LiveWebSocketTransport : ILiveTransport
{
    private readonly ClientWebSocket socket = new();

    public Task ConnectAsync(string apiKey, CancellationToken cancellationToken)
    {
        // The documented WebSocket authentication uses a query string. Never log this URI.
        var endpoint = new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + Uri.EscapeDataString(apiKey));
        return socket.ConnectAsync(endpoint, cancellationToken);
    }

    public Task SendAsync(string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, cancellationToken).AsTask();

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => socket.ReceiveAsync(buffer, cancellationToken);
    public Task CloseAsync(CancellationToken cancellationToken) => socket.State is WebSocketState.Open or WebSocketState.CloseReceived ?
        socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client stopped", cancellationToken) : Task.CompletedTask;
    public void Abort() => socket.Abort();
    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
