namespace RemoteDesk.Shared.Protocol;

public record VideoStats(
    double ActualFps,
    int BitrateKbps,
    int EncodeLatencyMs
);
