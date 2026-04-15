using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteDesk.Host.Capture;
using RemoteDesk.Host.Encoding;
using RemoteDesk.Host.Server;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Session;

/// <summary>
/// Manages active viewer sessions, codec negotiation, and frame broadcasting.
/// </summary>
public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, WebSocketSession> _sessions = new();
    private readonly IDesktopCaptureService _captureService;
    private readonly Dictionary<string, IVideoEncoderService> _encoders;
    private readonly IVideoEncoderService _jpegEncoder;

    private VideoCodec _selectedCodec = VideoCodec.Vp8;
    private int _currentFps = 15;
    private int _currentWidth;
    private int _currentHeight;
    private VideoQuality _currentQuality = VideoQuality.Medium;
    private DateTime _sessionStartUtc;
    private bool _disposed;

    public int ViewerCount => _sessions.Count;
    public int CurrentFps => _currentFps;
    public VideoQuality CurrentQuality => _currentQuality;
    public VideoCodec CurrentCodec => _selectedCodec;

    public SessionManager(
        IDesktopCaptureService captureService,
        Dictionary<string, IVideoEncoderService> encoders,
        IVideoEncoderService jpegEncoder)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _jpegEncoder = jpegEncoder ?? throw new ArgumentNullException(nameof(jpegEncoder));
    }

    /// <summary>
    /// Starts capturing and encoding. Call before accepting connections.
    /// </summary>
    public void StartStreaming(int monitorIndex, int fps, VideoQuality quality, VideoCodec codec = VideoCodec.Vp8)
    {
        _currentFps = fps;
        _currentQuality = quality;
        _selectedCodec = codec;
        _sessionStartUtc = DateTime.UtcNow;

        var monitors = _captureService.GetAvailableMonitors();
        var monitor = monitors.Count > monitorIndex ? monitors[monitorIndex] : monitors[0];
        _currentWidth = monitor.Width;
        _currentHeight = monitor.Height;

        var codecName = CodecToName(_selectedCodec);
        if (_encoders.TryGetValue(codecName, out var encoder))
            encoder.Initialize(_currentWidth, _currentHeight, _currentFps, _currentQuality);

        _jpegEncoder.Initialize(_currentWidth, _currentHeight, _currentFps, _currentQuality);

        _captureService.StartCapture(monitorIndex, _currentFps, OnFrameCaptured);
    }

    /// <summary>
    /// Handles a new WebSocket connection: performs handshake then enters receive loop.
    /// </summary>
    public async Task HandleNewConnectionAsync(WebSocket webSocket)
    {
        using var session = new WebSocketSession(webSocket);
        _sessions.TryAdd(session.Id, session);

        try
        {
            // Wait for hello message
            var msg = await session.ReceiveAsync();
            if (msg == null) return;

            var hello = JsonSerializer.Deserialize<JsonElement>(msg.Value.Data);
            var capabilities = hello.GetProperty("capabilities")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToList();

            // Pick the server-configured codec if the browser supports it, else JPEG fallback
            var codecName = CodecToName(_selectedCodec);
            session.PreferredCodec = capabilities.Contains(codecName) ? codecName : "jpeg";

            // Send config response
            var config = JsonSerializer.Serialize(new
            {
                type = MessageType.Config,
                codec = session.PreferredCodec,
                fps = _currentFps,
                width = _currentWidth,
                height = _currentHeight
            });
            await session.SendTextAsync(config);

            // Force a keyframe so the new viewer gets an immediate full frame
            if (_encoders.TryGetValue(codecName, out var encoder))
                encoder.ForceKeyframe();

            // Enter receive loop (for future input handling in Phase 4)
            while (session.IsConnected)
            {
                var received = await session.ReceiveAsync();
                if (received == null) break;
            }
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            await session.CloseAsync();
        }
    }

    /// <summary>
    /// Updates the FPS setting on the running capture session.
    /// </summary>
    public void UpdateFps(int fps)
    {
        _currentFps = Math.Clamp(fps, DesktopCaptureService.MinFps, DesktopCaptureService.MaxFps);
    }

    /// <summary>
    /// Stops all streaming activity and disconnects all viewers.
    /// </summary>
    public void StopStreaming()
    {
        _captureService.StopCapture();

        foreach (var session in _sessions.Values)
        {
            _ = session.CloseAsync();
        }
        _sessions.Clear();
    }

    private int _capturedFrameCount;

    private void OnFrameCaptured(CapturedFrame frame)
    {
        if (_sessions.IsEmpty) return;

        _capturedFrameCount++;
        if (_capturedFrameCount == 1)
            System.Diagnostics.Debug.WriteLine(
                $"[SessionManager] First captured frame: {frame.Width}x{frame.Height}, {frame.BgraData.Length} bytes, {_sessions.Count} viewer(s)");

        var timestampMs = (uint)(DateTime.UtcNow - _sessionStartUtc).TotalMilliseconds;
        var codecName = CodecToName(_selectedCodec);

        var hasCodecViewers = _sessions.Values.Any(s => s.PreferredCodec == codecName);
        var hasJpegViewers = _sessions.Values.Any(s => s.PreferredCodec == "jpeg");

        if (hasCodecViewers && _encoders.TryGetValue(codecName, out var encoder))
        {
            encoder.EncodeFrame(frame.BgraData, encodedChunk =>
            {
                var isKeyframe = DetectKeyframe(encodedChunk, _selectedCodec);
                var frameType = isKeyframe
                    ? FrameType.Keyframe
                    : CodecToFrameType(_selectedCodec);

                var packet = FramePacketSerializer.Serialize(
                    new FramePacket(frameType, timestampMs, encodedChunk));

                BroadcastToViewers(packet, codecName);
            });
        }

        if (hasJpegViewers)
        {
            _jpegEncoder.EncodeFrame(frame.BgraData, encodedChunk =>
            {
                var packet = FramePacketSerializer.Serialize(
                    new FramePacket(FrameType.Jpeg, timestampMs, encodedChunk));

                BroadcastToViewers(packet, "jpeg");
            });
        }
    }

    private int _broadcastCount;

    private void BroadcastToViewers(byte[] packet, string codec)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.PreferredCodec == codec && session.IsConnected)
            {
                _broadcastCount++;
                if (_broadcastCount <= 3)
                    System.Diagnostics.Debug.WriteLine(
                        $"[SessionManager] Broadcasting {codec} frame #{_broadcastCount}: {packet.Length} bytes to session {session.Id}");

                _ = session.SendBinaryAsync(packet);
            }
        }
    }

    private static string CodecToName(VideoCodec codec) => codec switch
    {
        VideoCodec.Vp8 => "vp8",
        VideoCodec.Av1 => "av1",
        _ => "vp8"
    };

    private static byte CodecToFrameType(VideoCodec codec) => codec switch
    {
        VideoCodec.Vp8 => FrameType.Vp8,
        VideoCodec.Av1 => FrameType.Av1,
        _ => FrameType.Vp8
    };

    private static bool DetectKeyframe(byte[] data, VideoCodec codec)
    {
        if (data.Length == 0) return false;

        return codec switch
        {
            // VP8: bit 0 of first byte is 0 for keyframes
            VideoCodec.Vp8 => (data[0] & 0x01) == 0,
            // AV1 OBU: check if first OBU is a sequence header (type 1, bits 3-6 of first byte)
            VideoCodec.Av1 => data.Length >= 2 && ((data[0] >> 3) & 0x0F) == 1,
            _ => false
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopStreaming();
    }
}
