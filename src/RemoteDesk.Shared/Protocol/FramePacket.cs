namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// Represents a serialized video frame packet sent over WebSocket.
/// Binary format: [type:1][timestamp:4][length:4][payload:N]
/// </summary>
public record FramePacket(
    byte Type,
    uint TimestampMs,
    byte[] Payload
);
