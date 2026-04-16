using System.Runtime.InteropServices;
using RemoteDesk.Shared.Protocol;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace RemoteDesk.Host.Capture;

/// <summary>
/// Captures the desktop via DXGI Desktop Duplication API on a dedicated background thread.
/// Delivers raw BGRA32 frames at a configurable frame rate.
/// </summary>
public sealed class DesktopCaptureService : IDesktopCaptureService
{
    public const int MinFps = 5;
    public const int MaxFps = 30;
    private const int AcquireTimeoutMs = 100;

    private readonly IDxgiDeviceFactory _deviceFactory;
    private Thread? _captureThread;
    private volatile bool _running;
    private volatile int _targetFps;
    private bool _disposed;

    public bool IsCapturing => _running;
    public event EventHandler<MonitorInfo>? MonitorConfigurationChanged;

    public DesktopCaptureService(IDxgiDeviceFactory deviceFactory)
    {
        _deviceFactory = deviceFactory ?? throw new ArgumentNullException(nameof(deviceFactory));
    }

    public IReadOnlyList<MonitorInfo> GetAvailableMonitors()
    {
        var monitors = new List<MonitorInfo>();
        using var factory = new Factory1();
        using var adapter = factory.GetAdapter1(0);

        for (var i = 0; i < adapter.GetOutputCount(); i++)
        {
            using var output = adapter.GetOutput(i);
            var desc = output.Description;
            var bounds = desc.DesktopBounds;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;

            monitors.Add(new MonitorInfo(
                Index: i,
                Name: desc.DeviceName,
                Width: width,
                Height: height,
                IsPrimary: i == 0,
                OffsetX: bounds.Left,
                OffsetY: bounds.Top
            ));
        }

        return monitors;
    }

    public void StartCapture(int monitorIndex, int targetFps, Action<CapturedFrame> onFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
            throw new InvalidOperationException("Capture is already running. Call StopCapture first.");

        ArgumentNullException.ThrowIfNull(onFrame);

        if (monitorIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(monitorIndex), "Monitor index must be >= 0.");

        _targetFps = Math.Clamp(targetFps, MinFps, MaxFps);
        _running = true;

        _captureThread = new Thread(() => CaptureLoop(monitorIndex, onFrame))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DesktopCapture"
        };
        _captureThread.Start();
    }

    public void StopCapture()
    {
        _running = false;
        _captureThread?.Join(TimeSpan.FromSeconds(3));
        _captureThread = null;
    }

    public void UpdateTargetFps(int targetFps)
    {
        // The capture loop reads _targetFps on every iteration, so a simple
        // volatile write is enough to adjust the frame interval live.
        _targetFps = Math.Clamp(targetFps, MinFps, MaxFps);
    }

    private void CaptureLoop(int monitorIndex, Action<CapturedFrame> onFrame)
    {
        DxgiResources? resources = null;
        OutputDuplication? duplication = null;
        Output1? output1 = null;

        try
        {
            resources = _deviceFactory.CreateResources();
            using var output = resources.Adapter.GetOutput(monitorIndex);
            output1 = output.QueryInterface<Output1>();
            duplication = _deviceFactory.DuplicateOutput(resources.Device, output1);

            var outputDesc = output.Description;
            var frameWidth = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            var frameHeight = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;

            while (_running)
            {
                var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
                var frameStart = DateTime.UtcNow;

                try
                {
                    var result = duplication.TryAcquireNextFrame(
                        AcquireTimeoutMs,
                        out var frameInfo,
                        out var desktopResource);

                    if (result.Success && desktopResource != null)
                    {
                        try
                        {
                            if (frameInfo.TotalMetadataBufferSize > 0)
                            {
                                using var texture = desktopResource
                                    .QueryInterface<Texture2D>();
                                var bgraData = ExtractBgraData(
                                    resources.Device, texture, frameWidth, frameHeight);

                                onFrame(new CapturedFrame(
                                    BgraData: bgraData,
                                    Width: frameWidth,
                                    Height: frameHeight,
                                    MonitorIndex: monitorIndex,
                                    Timestamp: DateTime.UtcNow,
                                    IsKeyframeRequired: false));
                            }
                        }
                        finally
                        {
                            desktopResource.Dispose();
                            duplication.ReleaseFrame();
                        }
                    }
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Code)
                {
                    // No new frame available within timeout — normal, just continue
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x887A0027))
                {
                    // DXGI_ERROR_ACCESS_LOST — e.g. after UAC dialog, lock screen, desktop switch
                    duplication.Dispose();
                    duplication = _deviceFactory.DuplicateOutput(resources.Device, output1);
                }

                // Maintain target frame rate
                var elapsed = DateTime.UtcNow - frameStart;
                if (elapsed < frameInterval)
                {
                    Thread.Sleep(frameInterval - elapsed);
                }
            }
        }
        catch (SharpDXException ex)
        {
            // Monitor disconnected or other fatal DXGI error
            var fallback = new MonitorInfo(0, "Primary", 0, 0, true, 0, 0);
            MonitorConfigurationChanged?.Invoke(this, fallback);
            System.Diagnostics.Debug.WriteLine(
                $"DXGI capture terminated: {ex.Message} (0x{ex.ResultCode.Code:X8})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Capture loop terminated unexpectedly: {ex.Message}");
        }
        finally
        {
            duplication?.Dispose();
            output1?.Dispose();
            resources?.Dispose();
            _running = false;
        }
    }

    private static byte[] ExtractBgraData(
        SharpDX.Direct3D11.Device device,
        Texture2D sourceTexture,
        int width,
        int height)
    {
        var stagingDesc = sourceTexture.Description;
        stagingDesc.CpuAccessFlags = CpuAccessFlags.Read;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.OptionFlags = ResourceOptionFlags.None;

        using var staging = new Texture2D(device, stagingDesc);
        device.ImmediateContext.CopyResource(sourceTexture, staging);

        var mapped = device.ImmediateContext.MapSubresource(
            staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var size = width * height * 4; // BGRA32 = 4 bytes per pixel
            var data = new byte[size];

            // Handle row pitch (may differ from width * 4 due to alignment)
            if (mapped.RowPitch == width * 4)
            {
                Marshal.Copy(mapped.DataPointer, data, 0, size);
            }
            else
            {
                var sourcePtr = mapped.DataPointer;
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        sourcePtr + row * mapped.RowPitch,
                        data,
                        row * width * 4,
                        width * 4);
                }
            }

            return data;
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(staging, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCapture();
        _deviceFactory.Dispose();
    }
}
