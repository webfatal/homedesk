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

    private VideoCodec _selectedCodec = VideoCodec.Av1;
    private int _currentFps = 15;
    private int _currentWidth;
    private int _currentHeight;
    private int _currentMonitorIndex;
    private VideoQuality _currentQuality = VideoQuality.Medium;
    private DateTime _sessionStartUtc;
    private bool _disposed;
    private readonly object _applyLock = new();

    public int ViewerCount => _sessions.Count;
    public int CurrentFps => _currentFps;
    public VideoQuality CurrentQuality => _currentQuality;
    public VideoCodec CurrentCodec => _selectedCodec;

    public SessionSettings CurrentSettings => new(
        _currentMonitorIndex, _currentFps, _currentQuality, _selectedCodec);

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
        _currentMonitorIndex = monitorIndex;
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
    /// Applies new session settings in-flight. Only the actually changed aspects
    /// are re-configured; viewers keep their WebSocket connections and are
    /// informed about codec/resolution changes via a fresh <c>config</c> message
    /// followed by a forced keyframe.
    /// </summary>
    public void ApplySettings(SessionSettings newSettings)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_applyLock)
        {
            var fpsChanged = newSettings.Fps != _currentFps;
            var qualityChanged = newSettings.Quality != _currentQuality;
            var codecChanged = newSettings.Codec != _selectedCodec;
            var monitorChanged = newSettings.MonitorIndex != _currentMonitorIndex;

            if (!fpsChanged && !qualityChanged && !codecChanged && !monitorChanged)
                return;

            if (monitorChanged)
            {
                // Monitor switch changes dimensions — a full capture restart is
                // unavoidable, but viewers still keep their sockets.
                RestartCaptureForMonitor(newSettings);
            }
            else if (fpsChanged)
            {
                _captureService.UpdateTargetFps(newSettings.Fps);
            }

            // Remember the previous codec so we can shut its encoder down
            // *after* the switch is complete.
            var previousCodecName = CodecToName(_selectedCodec);

            if (codecChanged)
            {
                var newCodecName = CodecToName(newSettings.Codec);
                if (_encoders.TryGetValue(newCodecName, out var newEncoder) && !newEncoder.IsInitialized)
                {
                    newEncoder.Initialize(
                        _currentWidth, _currentHeight, newSettings.Fps, newSettings.Quality);
                }
            }

            _currentFps = newSettings.Fps;
            _currentQuality = newSettings.Quality;
            _selectedCodec = newSettings.Codec;
            _currentMonitorIndex = newSettings.MonitorIndex;

            // ORDER MATTERS: tell the clients BEFORE we produce new encoded frames.
            // The browser disposes its old decoder and creates a fresh one on
            // receiving `config`; that fresh decoder requires a keyframe as the
            // first frame. If we restarted the encoder first, a keyframe of the
            // new codec would arrive before the client has swapped its decoder —
            // the old decoder would reject it (AV1 data into a VP8 decoder etc.).
            BroadcastConfigUpdate();

            // JPEG mirrors quality. No pipeline restart required.
            if (_jpegEncoder.IsInitialized)
                _jpegEncoder.Reconfigure(newSettings.Fps, newSettings.Quality);

            // Target encoder: Reconfigure restarts the ffmpeg pipeline when
            // fps/quality actually changed — the next emitted frame is then a
            // keyframe. On a codec-only switch the target encoder may either
            // have been freshly Initialized above (fresh stream → first frame
            // is a keyframe) or already be running mid-GOP; ForceKeyframe
            // covers the latter case by restarting unconditionally.
            var targetCodecName = CodecToName(newSettings.Codec);
            if (_encoders.TryGetValue(targetCodecName, out var targetEncoder) && targetEncoder.IsInitialized)
            {
                if (fpsChanged || qualityChanged)
                {
                    targetEncoder.Reconfigure(newSettings.Fps, newSettings.Quality);
                }
                else if (codecChanged)
                {
                    // Codec switch with identical fps/quality: guarantee a fresh
                    // GOP so the new client-side decoder gets its required keyframe.
                    targetEncoder.ForceKeyframe();
                }
            }

            // Finally, shut down the previous codec's encoder so its ffmpeg
            // process stops consuming resources. OnFrameCaptured no longer
            // routes frames to it (because _selectedCodec has been updated),
            // so it is safe to dispose. A later switch back will hit the
            // `!IsInitialized` branch above and bring up a fresh pipeline.
            if (codecChanged &&
                _encoders.TryGetValue(previousCodecName, out var previousEncoder) &&
                previousEncoder.IsInitialized)
            {
                previousEncoder.Dispose();
                _encoders[previousCodecName] = CreateReplacementEncoder(previousCodecName);
            }
        }
    }

    private void RestartCaptureForMonitor(SessionSettings newSettings)
    {
        _captureService.StopCapture();

        var monitors = _captureService.GetAvailableMonitors();
        var monitor = monitors.Count > newSettings.MonitorIndex
            ? monitors[newSettings.MonitorIndex]
            : monitors[0];
        _currentWidth = monitor.Width;
        _currentHeight = monitor.Height;

        // Encoders are pinned to width/height at Initialize time. Because the
        // pipe-based encoders cannot change resolution on the fly, we dispose
        // and re-create the active encoder entry to match the new monitor.
        var codecName = CodecToName(newSettings.Codec);
        if (_encoders.TryGetValue(codecName, out var encoder))
        {
            encoder.Dispose();
            var replacement = CreateReplacementEncoder(codecName);
            _encoders[codecName] = replacement;
            replacement.Initialize(_currentWidth, _currentHeight, newSettings.Fps, newSettings.Quality);
        }

        _captureService.StartCapture(newSettings.MonitorIndex, newSettings.Fps, OnFrameCaptured);
    }

    private static IVideoEncoderService CreateReplacementEncoder(string codecName) => codecName switch
    {
        "vp8" => new VP8EncoderService(),
        "av1" => new Av1EncoderService(),
        _ => throw new ArgumentException($"Unknown codec: {codecName}")
    };

    private void BroadcastConfigUpdate()
    {
        var serverCodecName = CodecToName(_selectedCodec);

        foreach (var session in _sessions.Values)
        {
            if (!session.IsConnected) continue;

            // Viewer stays on its previously negotiated codec if possible; if
            // the server switched to a codec it does not support, fall back to jpeg.
            var newCodec = session.Capabilities.Contains(serverCodecName)
                ? serverCodecName
                : "jpeg";

            session.PreferredCodec = newCodec;

            var config = JsonSerializer.Serialize(new
            {
                type = MessageType.Config,
                codec = newCodec,
                fps = _currentFps,
                width = _currentWidth,
                height = _currentHeight
            });
            _ = session.SendTextAsync(config);
        }
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
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();

            // Pick the server-configured codec if the browser supports it, else JPEG fallback
            var codecName = CodecToName(_selectedCodec);
            session.Capabilities = capabilities;
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

            // Force a keyframe so the new viewer gets an immediate full frame.
            // Note: this restarts the pipe-based ffmpeg pipeline, so any other
            // already connected viewers will briefly lose a few frames. That is
            // the price for making new viewers usable within < 100ms instead of
            // waiting up to a full GOP (~2s).
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
    /// Updates the FPS setting on the running capture session. Delegates to
    /// <see cref="ApplySettings"/> so the change is actually propagated to the
    /// capture loop and encoders.
    /// </summary>
    public void UpdateFps(int fps)
    {
        var clamped = Math.Clamp(fps, DesktopCaptureService.MinFps, DesktopCaptureService.MaxFps);
        ApplySettings(CurrentSettings with { Fps = clamped });
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

        if (hasCodecViewers &&
            _encoders.TryGetValue(codecName, out var encoder) &&
            encoder.IsInitialized)
        {
            try
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
            catch (ObjectDisposedException)
            {
                // The encoder was swapped out by a concurrent ApplySettings
                // (codec change) between the TryGetValue and EncodeFrame calls.
                // Dropping this single frame is harmless — the next capture
                // iteration will see the replacement encoder.
            }
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
