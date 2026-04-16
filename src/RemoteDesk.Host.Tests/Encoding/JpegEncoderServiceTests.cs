using RemoteDesk.Host.Encoding;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Tests.Encoding;

public class JpegEncoderServiceTests
{
    [Fact]
    public void Initialize_ValidParams_SetsIsInitialized()
    {
        // Arrange
        using var encoder = new JpegEncoderService();

        // Act
        encoder.Initialize(100, 100, 15, VideoQuality.Medium);

        // Assert
        Assert.True(encoder.IsInitialized);
    }

    [Fact]
    public void Initialize_CalledTwice_ThrowsInvalidOperationException()
    {
        // Arrange
        using var encoder = new JpegEncoderService();
        encoder.Initialize(100, 100, 15, VideoQuality.Medium);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Initialize(100, 100, 15, VideoQuality.Medium));
    }

    [Fact]
    public void EncodeFrame_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // Arrange
        using var encoder = new JpegEncoderService();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            encoder.EncodeFrame(new byte[400], _ => { }));
    }

    [Fact]
    public void EncodeFrame_WrongDataSize_ThrowsArgumentException()
    {
        // Arrange
        using var encoder = new JpegEncoderService();
        encoder.Initialize(10, 10, 15, VideoQuality.Medium);

        // Act & Assert — expects 10*10*4=400 bytes, give 100
        Assert.Throws<ArgumentException>(() =>
            encoder.EncodeFrame(new byte[100], _ => { }));
    }

    [Fact]
    public void EncodeFrame_ValidData_ProducesJpegOutput()
    {
        // Arrange
        const int width = 10;
        const int height = 10;
        using var encoder = new JpegEncoderService();
        encoder.Initialize(width, height, 15, VideoQuality.Medium);
        var bgraData = new byte[width * height * 4]; // All black
        byte[]? result = null;

        // Act
        encoder.EncodeFrame(bgraData, chunk => result = chunk);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
        // JPEG files start with FF D8
        Assert.Equal(0xFF, result[0]);
        Assert.Equal(0xD8, result[1]);
    }

    [Fact]
    public void GetStats_AfterEncoding_ReturnsNonZeroStats()
    {
        // Arrange
        const int width = 10;
        const int height = 10;
        using var encoder = new JpegEncoderService();
        encoder.Initialize(width, height, 15, VideoQuality.Medium);
        var bgraData = new byte[width * height * 4];
        encoder.EncodeFrame(bgraData, _ => { });

        // Act
        var stats = encoder.GetStats();

        // Assert
        Assert.True(stats.ActualFps >= 0);
        Assert.True(stats.BitrateKbps >= 0);
        Assert.True(stats.EncodeLatencyMs >= 0);
    }

    [Fact]
    public void ForceKeyframe_DoesNotThrow()
    {
        // Arrange
        using var encoder = new JpegEncoderService();

        // Act & Assert — JPEG has no keyframe concept, should be a no-op
        var exception = Record.Exception(() => encoder.ForceKeyframe());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var encoder = new JpegEncoderService();
        encoder.Initialize(10, 10, 15, VideoQuality.Medium);

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
        using var encoder = new JpegEncoderService();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Reconfigure(20, VideoQuality.High));
    }

    [Fact]
    public void Reconfigure_ChangesQuality_ProducesDifferentOutputSize()
    {
        // Arrange — a natural colour gradient so quality differences become visible
        const int width = 32;
        const int height = 32;
        using var encoder = new JpegEncoderService();
        encoder.Initialize(width, height, 15, VideoQuality.High);
        var bgraData = new byte[width * height * 4];
        for (var i = 0; i < bgraData.Length; i++) bgraData[i] = (byte)(i % 256);

        // Act
        byte[]? highOutput = null;
        encoder.EncodeFrame(bgraData, chunk => highOutput = chunk);

        encoder.Reconfigure(15, VideoQuality.Low);
        byte[]? lowOutput = null;
        encoder.EncodeFrame(bgraData, chunk => lowOutput = chunk);

        // Assert — low quality should be smaller than high for the same payload
        Assert.NotNull(highOutput);
        Assert.NotNull(lowOutput);
        Assert.True(lowOutput.Length < highOutput.Length,
            $"Expected Low ({lowOutput.Length}) < High ({highOutput.Length})");
    }

    [Fact]
    public void Reconfigure_SameQuality_IsIdempotent()
    {
        // Arrange
        using var encoder = new JpegEncoderService();
        encoder.Initialize(10, 10, 15, VideoQuality.Medium);

        // Act & Assert
        var exception = Record.Exception(() =>
            encoder.Reconfigure(20, VideoQuality.Medium));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(VideoQuality.Low)]
    [InlineData(VideoQuality.Medium)]
    [InlineData(VideoQuality.High)]
    public void EncodeFrame_AllQualities_ProducesOutput(VideoQuality quality)
    {
        // Arrange
        const int width = 8;
        const int height = 8;
        using var encoder = new JpegEncoderService();
        encoder.Initialize(width, height, 15, quality);
        var bgraData = new byte[width * height * 4];
        byte[]? result = null;

        // Act
        encoder.EncodeFrame(bgraData, chunk => result = chunk);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
    }
}
