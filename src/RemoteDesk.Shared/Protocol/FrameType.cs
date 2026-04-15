namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// Binary frame header type identifiers for the WebSocket protocol.
/// </summary>
public static class FrameType
{
    public const byte Vp8 = 0x01;
    public const byte Jpeg = 0x02;
    public const byte Keyframe = 0x03;
    public const byte Av1 = 0x04;
}
