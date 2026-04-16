using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using RemoteDesk.Host.Capture;
using RemoteDesk.Host.Encoding;
using RemoteDesk.Host.Server;
using RemoteDesk.Host.Session;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host;

/// <summary>
/// Application entry point. Initializes services and starts the WebSocket server.
/// </summary>
public partial class App : Application
{
    private LocalWebSocketServer? _server;
    private SessionManager? _sessionManager;

    /// <summary>
    /// Active session manager. Exposed so the settings window can invoke
    /// <see cref="SessionManager.ApplySettings"/> on runtime config changes.
    /// </summary>
    public SessionManager? SessionManager => _sessionManager;

    /// <summary>
    /// Current server port. Used by the settings window to display the URL.
    /// </summary>
    public int ServerPort => _server?.Port ?? 0;

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        AllocConsole();
        Trace.Listeners.Add(new ConsoleTraceListener());
        Debug.WriteLine("[App] RemoteDesk starting...");
#endif

        var captureService = new DesktopCaptureService(new DxgiDeviceFactory());
        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = new VP8EncoderService(),
            ["av1"] = new Av1EncoderService()
        };
        var jpegEncoder = new JpegEncoderService();

        _sessionManager = new SessionManager(captureService, encoders, jpegEncoder);
        _sessionManager.StartStreaming(
            monitorIndex: 0,
            fps: 15,
            quality: VideoQuality.Medium,
            codec: VideoCodec.Vp8);

        Debug.WriteLine("[App] Streaming started on monitor 0, 15fps, Medium quality");

        _server = new LocalWebSocketServer(_sessionManager);
        await _server.StartAsync(port: 8443);

        Debug.WriteLine("[App] WebSocket server started on port 8443");
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _sessionManager?.StopStreaming();

        if (_server != null)
            await _server.DisposeAsync();

        _sessionManager?.Dispose();
        base.OnExit(e);
    }
}
