namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// Immutable snapshot of runtime-configurable session parameters.
/// Used to diff current state against user-requested changes so that the
/// session can apply only what actually changed without forcing a reconnect.
/// </summary>
public sealed record SessionSettings(
    int MonitorIndex,
    int Fps,
    VideoQuality Quality,
    VideoCodec Codec);
