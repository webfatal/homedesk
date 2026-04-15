using System.Buffers.Binary;

namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// Serializes and deserializes binary frame packets for WebSocket transport.
/// Format: [type:1 byte][timestamp:4 bytes BE][length:4 bytes BE][payload:N bytes]
/// </summary>
public static class FramePacketSerializer
{
    public const int HeaderSize = 9; // 1 + 4 + 4

    /// <summary>
    /// Serializes a frame packet into a byte array ready for WebSocket transmission.
    /// </summary>
    public static byte[] Serialize(FramePacket packet)
    {
        var buffer = new byte[HeaderSize + packet.Payload.Length];

        buffer[0] = packet.Type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1, 4), packet.TimestampMs);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5, 4), (uint)packet.Payload.Length);
        packet.Payload.CopyTo(buffer, HeaderSize);

        return buffer;
    }

    /// <summary>
    /// Deserializes a binary WebSocket message into a frame packet.
    /// Returns null if the data is too short or the length field is inconsistent.
    /// </summary>
    public static FramePacket? Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return null;

        var type = data[0];
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(5, 4));

        if (data.Length < HeaderSize + (int)payloadLength)
            return null;

        var payload = data.Slice(HeaderSize, (int)payloadLength).ToArray();

        return new FramePacket(type, timestamp, payload);
    }
}
