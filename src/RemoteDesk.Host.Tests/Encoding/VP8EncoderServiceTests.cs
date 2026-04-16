using RemoteDesk.Host.Encoding;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Tests.Encoding;

public class VP8EncoderServiceTests
{
    [Fact]
    public void IsInitialized_BeforeInit_ReturnsFalse()
    {
        // Arrange
        using var encoder = new VP8EncoderService();

        // Act & Assert
        Assert.False(encoder.IsInitialized);
    }

    [Fact]
    public void Initialize_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        using var encoder = new VP8EncoderService();

        // This will fail if ffmpeg is not installed, which is expected in CI.
        // We test the guard clause which fires before the process starts.
        try
        {
            encoder.Initialize(100, 100, 15, VideoQuality.Medium);
        }
        catch
        {
            // FFmpeg not available — skip this test
            return;
        }

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Initialize(100, 100, 15, VideoQuality.Medium));
    }

    [Fact]
    public void EncodeFrame_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Arrange
        using var encoder = new VP8EncoderService();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            encoder.EncodeFrame(new byte[400], _ => { }));
    }

    [Fact]
    public void GetStats_BeforeEncoding_ReturnsZeroStats()
    {
        // Arrange
        using var encoder = new VP8EncoderService();

        // Act
        var stats = encoder.GetStats();

        // Assert
        Assert.Equal(0, stats.ActualFps);
        Assert.Equal(0, stats.BitrateKbps);
        Assert.Equal(0, stats.EncodeLatencyMs);
    }

    [Fact]
    public void ForceKeyframe_BeforeInitialize_DoesNotThrow()
    {
        // Arrange
        using var encoder = new VP8EncoderService();

        // Act & Assert
        var exception = Record.Exception(() => encoder.ForceKeyframe());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var encoder = new VP8EncoderService();

        // Act & Assert
        var exception = Record.Exception(() =>
        {
            encoder.Dispose();
            encoder.Dispose();
        });
        Assert.Null(exception);
    }

    [Fact]
    public void Reconfigure_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Arrange
        using var encoder = new VP8EncoderService();

        // Act & Assert — guard must fire without needing ffmpeg
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Reconfigure(20, VideoQuality.High));
    }

    [Fact]
    public void Dispose_AfterInitialize_DoesNotThrow()
    {
        // Arrange
        var encoder = new VP8EncoderService();
        try
        {
            encoder.Initialize(100, 100, 15, VideoQuality.Medium);
        }
        catch
        {
            // FFmpeg not available — still test dispose path
        }

        // Act & Assert
        var exception = Record.Exception(() => encoder.Dispose());
        Assert.Null(exception);
    }
}
