using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Encoding;

/// <summary>
/// Encodes raw BGRA32 frames into a compressed video format (VP8 or JPEG).
/// </summary>
public interface IVideoEncoderService : IDisposable
{
    /// <summary>
    /// Initializes the encoder with the given frame dimensions, target FPS, and quality preset.
    /// Must be called before <see cref="EncodeFrame"/>.
    /// </summary>
    void Initialize(int width, int height, int fps, VideoQuality quality);

    /// <summary>
    /// Applies a new FPS / quality pair to an already initialized encoder without
    /// forcing connected viewers to reconnect. Implementations may restart an
    /// underlying pipeline (e.g. ffmpeg) but must keep the encoder instance reusable.
    /// </summary>
    void Reconfigure(int fps, VideoQuality quality);

    /// <summary>
    /// Encodes a single BGRA32 frame. The encoded chunk is delivered via <paramref name="onEncodedChunk"/>.
    /// </summary>
    void EncodeFrame(byte[] bgraData, Action<byte[]> onEncodedChunk);

    /// <summary>
    /// Forces the next frame to be a keyframe (e.g. when a new viewer connects).
    /// </summary>
    void ForceKeyframe();

    /// <summary>
    /// Returns current encoding statistics.
    /// </summary>
    VideoStats GetStats();

    /// <summary>
    /// Whether the encoder has been initialized and is ready to encode frames.
    /// </summary>
    bool IsInitialized { get; }
}
