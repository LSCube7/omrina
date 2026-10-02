using System.Net.WebSockets;
using System.Threading.Channels;
using Omrina.Protocol;

namespace Omrina.Server;

/// <summary>Bounds one event subscriber and distinguishes backpressure from lost authorization.</summary>
internal sealed class EventSubscription
{
    private readonly Channel<AgentEvent> _channel = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    public ChannelReader<AgentEvent> Reader => _channel.Reader;
    public WebSocketCloseStatus CloseStatus { get; private set; } = WebSocketCloseStatus.NormalClosure;

    // The host serializes publishing and completion under its state lock.
    public bool TryPublish(AgentEvent evt)
    {
        if (_channel.Writer.TryWrite(evt)) return true;
        CloseStatus = (WebSocketCloseStatus)1013; // Try Again Later: the grant remains valid.
        _channel.Writer.TryComplete();
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
