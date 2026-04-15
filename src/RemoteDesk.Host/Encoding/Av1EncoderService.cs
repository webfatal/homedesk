using System.Diagnostics;
using System.IO;
using FFMpegCore;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Encoding;

/// <summary>
/// Encodes BGRA32 frames to AV1 using NVENC hardware encoding via a pipe-based ffmpeg pipeline.
/// Requires an NVIDIA GPU with AV1 NVENC support (RTX 40-series or newer).
/// </summary>
public sealed class Av1EncoderService : IVideoEncoderService
{
    private int _width;
    private int _height;
    private int _fps;
    private volatile bool _forceKeyframe;
    private bool _initialized;
    private bool _disposed;

    private Process? _ffmpegProcess;
    private Stream? _ffmpegInput;
    private Stream? _ffmpegOutput;
    private Thread? _readerThread;
    private Action<byte[]>? _currentChunkCallback;
    private readonly object _encodeLock = new();
    private volatile bool _ffmpegExited;

    // Stats tracking
    private readonly Stopwatch _statsStopwatch = new();
    private int _frameCount;
    private long _totalBytes;
    private long _lastEncodeLatencyMs;

    public bool IsInitialized => _initialized;

    public void Initialize(int width, int height, int fps, VideoQuality quality)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
            throw new InvalidOperationException("Encoder is already initialized. Dispose and create a new instance.");

        _width = width;
        _height = height;
        _fps = fps;

        var cq = quality switch
        {
            VideoQuality.Low => 38,
            VideoQuality.Medium => 28,
            VideoQuality.High => 18,
            _ => 28
        };

        var bitrate = quality switch
        {
            VideoQuality.Low => "1500k",
            VideoQuality.Medium => "4000k",
            VideoQuality.High => "10000k",
            _ => "4000k"
        };

        var preset = quality switch
        {
            VideoQuality.Low => "p1",
            VideoQuality.Medium => "p4",
            VideoQuality.High => "p5",
            _ => "p4"
        };

        var ffmpegPath = GlobalFFOptions.Current.BinaryFolder != null
            ? Path.Combine(GlobalFFOptions.Current.BinaryFolder, "ffmpeg")
            : "ffmpeg";

        var args = $"-f rawvideo -pix_fmt bgra -video_size {_width}x{_height} -framerate {_fps} -i pipe:0 " +
                   $"-c:v av1_nvenc -pix_fmt yuv420p -preset {preset} -tune ll -rc vbr -cq {cq} -b:v {bitrate} " +
                   $"-g {_fps * 2} -keyint_min {_fps * 2} " +
                   $"-f ivf pipe:1";

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        _ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                System.Diagnostics.Debug.WriteLine($"[AV1Encoder/ffmpeg] {e.Data}");
        };

        _ffmpegProcess.Exited += (_, _) =>
        {
            _ffmpegExited = true;
            System.Diagnostics.Debug.WriteLine(
                $"[AV1Encoder] ffmpeg process exited with code {_ffmpegProcess?.ExitCode}");
        };
        _ffmpegProcess.EnableRaisingEvents = true;

        _ffmpegProcess.Start();
        _ffmpegInput = _ffmpegProcess.StandardInput.BaseStream;
        _ffmpegOutput = _ffmpegProcess.StandardOutput.BaseStream;

        _ffmpegProcess.BeginErrorReadLine();

        if (_ffmpegProcess.HasExited)
        {
            throw new InvalidOperationException(
                $"ffmpeg process exited immediately with code {_ffmpegProcess.ExitCode}. " +
                "Ensure ffmpeg is installed and your GPU supports AV1 NVENC (NVIDIA RTX 40-series or newer).");
        }

        _readerThread = new Thread(ReadOutputLoop)
        {
            IsBackground = true,
            Name = "AV1EncoderReader"
        };
        _readerThread.Start();

        _statsStopwatch.Start();
        _initialized = true;
    }

    public void EncodeFrame(byte[] bgraData, Action<byte[]> onEncodedChunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
            throw new InvalidOperationException("Encoder is not initialized. Call Initialize first.");

        var expectedSize = _width * _height * 4;
        if (bgraData.Length != expectedSize)
            throw new ArgumentException(
                $"Expected {expectedSize} bytes for {_width}x{_height} BGRA32, got {bgraData.Length}.");

        if (_ffmpegExited)
        {
            System.Diagnostics.Debug.WriteLine("[AV1Encoder] Skipping frame — ffmpeg process has exited.");
            return;
        }

        lock (_encodeLock)
        {
            var sw = Stopwatch.StartNew();
            _currentChunkCallback = onEncodedChunk;

            try
            {
                _ffmpegInput!.Write(bgraData, 0, bgraData.Length);
                _ffmpegInput.Flush();
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AV1Encoder] Write to ffmpeg failed: {ex.Message}");
                return;
            }

            sw.Stop();
            _lastEncodeLatencyMs = sw.ElapsedMilliseconds;
            _frameCount++;
        }
    }

    public void ForceKeyframe()
    {
        _forceKeyframe = true;
    }

    public VideoStats GetStats()
    {
        var elapsed = _statsStopwatch.Elapsed.TotalSeconds;
        var actualFps = elapsed > 0 ? _frameCount / elapsed : 0;
        var bitrateKbps = elapsed > 0 ? (int)(_totalBytes * 8 / elapsed / 1000) : 0;

        return new VideoStats(
            ActualFps: Math.Round(actualFps, 1),
            BitrateKbps: bitrateKbps,
            EncodeLatencyMs: (int)_lastEncodeLatencyMs);
    }

    private void ReadOutputLoop()
    {
        var headerBuffer = new byte[12]; // IVF frame header: 4 bytes size + 8 bytes timestamp
        try
        {
            // Skip IVF file header (32 bytes)
            var fileHeader = new byte[32];
            var read = ReadExact(_ffmpegOutput!, fileHeader, 0, 32);
            if (read < 32)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AV1Encoder] Failed to read IVF file header (got {read}/32 bytes). " +
                    "ffmpeg may have failed — check GPU support for av1_nvenc.");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[AV1Encoder] IVF header read successfully, waiting for encoded frames...");

            while (!_disposed && _ffmpegOutput != null)
            {
                read = ReadExact(_ffmpegOutput, headerBuffer, 0, 12);
                if (read < 12) break;

                var frameSize = BitConverter.ToInt32(headerBuffer, 0);
                if (frameSize <= 0 || frameSize > 10_000_000)
                {
                    System.Diagnostics.Debug.WriteLine($"[AV1Encoder] Invalid frame size: {frameSize}");
                    break;
                }

                var frameData = new byte[frameSize];
                read = ReadExact(_ffmpegOutput, frameData, 0, frameSize);
                if (read < frameSize) break;

                _totalBytes += frameSize;

                if (_frameCount == 0)
                    System.Diagnostics.Debug.WriteLine($"[AV1Encoder] First encoded frame: {frameSize} bytes");

                _currentChunkCallback?.Invoke(frameData);
            }

            System.Diagnostics.Debug.WriteLine("[AV1Encoder] Reader loop exited.");
        }
        catch (IOException ex)
        {
            if (!_disposed)
                System.Diagnostics.Debug.WriteLine($"[AV1Encoder] Reader IO error: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed during shutdown
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _ffmpegInput?.Close();
        }
        catch { /* ignore */ }

        _readerThread?.Join(TimeSpan.FromSeconds(3));

        if (_ffmpegProcess is { HasExited: false })
        {
            try { _ffmpegProcess.Kill(); } catch { /* ignore */ }
        }

        _ffmpegProcess?.Dispose();
        _statsStopwatch.Stop();
    }
}
