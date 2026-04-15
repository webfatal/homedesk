namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// A single captured desktop frame in raw BGRA32 format.
/// </summary>
public record CapturedFrame(
    byte[] BgraData,
    int Width,
    int Height,
    int MonitorIndex,
    DateTime Timestamp,
    bool IsKeyframeRequired
);
