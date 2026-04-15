namespace RemoteDesk.Shared.Protocol;

/// <summary>
/// Describes a physical monitor attached to the host system.
/// </summary>
public record MonitorInfo(
    int Index,
    string Name,
    int Width,
    int Height,
    bool IsPrimary,
    int OffsetX,
    int OffsetY
);
