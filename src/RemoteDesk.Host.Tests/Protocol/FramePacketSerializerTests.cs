using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Tests.Protocol;

public class FramePacketSerializerTests
{
    [Fact]
    public void Serialize_ValidPacket_ProducesCorrectHeaderAndPayload()
    {
        // Arrange
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var packet = new FramePacket(FrameType.Vp8, 12345, payload);

        // Act
        var bytes = FramePacketSerializer.Serialize(packet);

        // Assert
        Assert.Equal(FramePacketSerializer.HeaderSize + payload.Length, bytes.Length);
        Assert.Equal(FrameType.Vp8, bytes[0]);
        // Timestamp 12345 in big-endian: 0x00 0x00 0x30 0x39
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x30, bytes[3]);
        Assert.Equal(0x39, bytes[4]);
        // Payload length 4 in big-endian
        Assert.Equal(0, bytes[5]);
        Assert.Equal(0, bytes[6]);
        Assert.Equal(0, bytes[7]);
        Assert.Equal(4, bytes[8]);
        // Payload
        Assert.Equal(payload, bytes[9..]);
    }

    [Fact]
    public void Deserialize_ValidData_ReturnsCorrectPacket()
    {
        // Arrange
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var original = new FramePacket(FrameType.Jpeg, 42000, payload);
        var serialized = FramePacketSerializer.Serialize(original);

        // Act
        var deserialized = FramePacketSerializer.Deserialize(serialized);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(FrameType.Jpeg, deserialized.Type);
        Assert.Equal(42000u, deserialized.TimestampMs);
        Assert.Equal(payload, deserialized.Payload);
    }

    [Fact]
    public void Deserialize_TooShortData_ReturnsNull()
    {
        // Arrange
        var data = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        var result = FramePacketSerializer.Deserialize(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_InconsistentLength_ReturnsNull()
    {
        // Arrange — header claims 100 bytes of payload but only 2 are present
        var data = new byte[FramePacketSerializer.HeaderSize + 2];
        data[0] = FrameType.Vp8;
        // Set payload length to 100 (big-endian)
        data[8] = 100;

        // Act
        var result = FramePacketSerializer.Deserialize(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Serialize_EmptyPayload_ProducesHeaderOnly()
    {
        // Arrange
        var packet = new FramePacket(FrameType.Keyframe, 0, Array.Empty<byte>());

        // Act
        var bytes = FramePacketSerializer.Serialize(packet);

        // Assert
        Assert.Equal(FramePacketSerializer.HeaderSize, bytes.Length);
        Assert.Equal(FrameType.Keyframe, bytes[0]);
    }

    [Fact]
    public void RoundTrip_LargePayload_PreservesData()
    {
        // Arrange
        var payload = new byte[65536];
        Random.Shared.NextBytes(payload);
        var original = new FramePacket(FrameType.Vp8, uint.MaxValue, payload);

        // Act
        var serialized = FramePacketSerializer.Serialize(original);
        var deserialized = FramePacketSerializer.Deserialize(serialized);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Type, deserialized.Type);
        Assert.Equal(original.TimestampMs, deserialized.TimestampMs);
        Assert.Equal(original.Payload, deserialized.Payload);
    }

    [Theory]
    [InlineData(FrameType.Vp8)]
    [InlineData(FrameType.Jpeg)]
    [InlineData(FrameType.Keyframe)]
    public void RoundTrip_AllFrameTypes_PreservesType(byte frameType)
    {
        // Arrange
        var packet = new FramePacket(frameType, 1000, new byte[] { 0xFF });

        // Act
        var result = FramePacketSerializer.Deserialize(
            FramePacketSerializer.Serialize(packet));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(frameType, result.Type);
    }
}
