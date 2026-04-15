using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Encoding;

/// <summary>
/// Fallback encoder that produces JPEG frames for browsers without WebCodecs VP8 support.
/// </summary>
public sealed class JpegEncoderService : IVideoEncoderService
{
    private int _width;
    private int _height;
    private long _jpegQuality;
    private bool _initialized;
    private bool _disposed;

    private ImageCodecInfo? _jpegCodec;
    private EncoderParameters? _encoderParams;

    // Stats
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

        _jpegQuality = quality switch
        {
            VideoQuality.Low => 40L,
            VideoQuality.Medium => 65L,
            VideoQuality.High => 85L,
            _ => 65L
        };

        _jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        _encoderParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _jpegQuality) }
        };

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

        var sw = Stopwatch.StartNew();

        using var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, _width, _height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            Marshal.Copy(bgraData, 0, bitmapData.Scan0, bgraData.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, _jpegCodec!, _encoderParams);
        var jpegBytes = ms.ToArray();

        sw.Stop();
        _lastEncodeLatencyMs = sw.ElapsedMilliseconds;
        _frameCount++;
        _totalBytes += jpegBytes.Length;

        onEncodedChunk(jpegBytes);
    }

    public void ForceKeyframe()
    {
        // JPEG is always a full frame — no concept of keyframes
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoderParams?.Dispose();
        _statsStopwatch.Stop();
    }
}
