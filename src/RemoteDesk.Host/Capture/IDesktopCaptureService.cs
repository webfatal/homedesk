using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Capture;

/// <summary>
/// Captures the desktop screen via DXGI Desktop Duplication and delivers
/// raw BGRA32 frames at a configurable frame rate.
/// </summary>
public interface IDesktopCaptureService : IDisposable
{
    /// <summary>
    /// Enumerates all monitors currently attached to the system.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetAvailableMonitors();

    /// <summary>
    /// Starts capturing the specified monitor at the target FPS.
    /// Captured frames are delivered via the <paramref name="onFrame"/> callback.
    /// </summary>
    void StartCapture(int monitorIndex, int targetFps, Action<CapturedFrame> onFrame);

    /// <summary>
    /// Stops the capture loop and releases DXGI resources.
    /// </summary>
    void StopCapture();

    /// <summary>
    /// Updates the target frame rate of a running capture loop without tearing it down.
    /// No-op if capture is not currently running.
    /// </summary>
    void UpdateTargetFps(int targetFps);

    /// <summary>
    /// Whether a capture session is currently running.
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// Raised when the monitor configuration changes (e.g. monitor plugged/unplugged).
    /// </summary>
    event EventHandler<MonitorInfo>? MonitorConfigurationChanged;
}
