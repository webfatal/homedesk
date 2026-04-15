using Moq;
using RemoteDesk.Host.Capture;
using RemoteDesk.Host.Encoding;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Tests.Session;

public class SessionManagerTests
{
    private static Host.Session.SessionManager CreateSessionManager(
        Mock<IDesktopCaptureService>? captureMock = null,
        Dictionary<string, IVideoEncoderService>? encoders = null,
        Mock<IVideoEncoderService>? jpegMock = null)
    {
        captureMock ??= new Mock<IDesktopCaptureService>();
        jpegMock ??= new Mock<IVideoEncoderService>();

        encoders ??= new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = new Mock<IVideoEncoderService>().Object,
            ["av1"] = new Mock<IVideoEncoderService>().Object
        };

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo>
            {
                new(0, "TestMonitor", 1920, 1080, true, 0, 0)
            });

        return new Host.Session.SessionManager(
            captureMock.Object,
            encoders,
            jpegMock.Object);
    }

    [Fact]
    public void Constructor_NullCaptureService_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new Host.Session.SessionManager(
                null!,
                new Dictionary<string, IVideoEncoderService>(),
                new Mock<IVideoEncoderService>().Object));
    }

    [Fact]
    public void Constructor_NullEncoders_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new Host.Session.SessionManager(
                new Mock<IDesktopCaptureService>().Object,
                null!,
                new Mock<IVideoEncoderService>().Object));
    }

    [Fact]
    public void Constructor_NullJpegEncoder_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new Host.Session.SessionManager(
                new Mock<IDesktopCaptureService>().Object,
                new Dictionary<string, IVideoEncoderService>(),
                null!));
    }

    [Fact]
    public void ViewerCount_Initially_IsZero()
    {
        // Arrange
        using var manager = CreateSessionManager();

        // Act & Assert
        Assert.Equal(0, manager.ViewerCount);
    }

    [Fact]
    public void StartStreaming_WithVp8_InitializesVp8EncoderAndJpeg()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var av1Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo>
            {
                new(0, "TestMonitor", 1920, 1080, true, 0, 0)
            });

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = av1Mock.Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);

        // Act
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Assert
        vp8Mock.Verify(e => e.Initialize(1920, 1080, 15, VideoQuality.Medium), Times.Once);
        av1Mock.Verify(e => e.Initialize(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<VideoQuality>()), Times.Never);
        jpegMock.Verify(e => e.Initialize(1920, 1080, 15, VideoQuality.Medium), Times.Once);
        captureMock.Verify(c => c.StartCapture(0, 15, It.IsAny<Action<CapturedFrame>>()), Times.Once);
    }

    [Fact]
    public void StartStreaming_WithAv1_InitializesAv1EncoderAndJpeg()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var av1Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo>
            {
                new(0, "TestMonitor", 1920, 1080, true, 0, 0)
            });

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = av1Mock.Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);

        // Act
        manager.StartStreaming(0, 15, VideoQuality.High, VideoCodec.Av1);

        // Assert
        av1Mock.Verify(e => e.Initialize(1920, 1080, 15, VideoQuality.High), Times.Once);
        vp8Mock.Verify(e => e.Initialize(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<VideoQuality>()), Times.Never);
        jpegMock.Verify(e => e.Initialize(1920, 1080, 15, VideoQuality.High), Times.Once);
    }

    [Fact]
    public void CurrentCodec_AfterStartStreaming_ReflectsSelectedCodec()
    {
        // Arrange
        using var manager = CreateSessionManager();

        // Act
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Av1);

        // Assert
        Assert.Equal(VideoCodec.Av1, manager.CurrentCodec);
    }

    [Fact]
    public void StopStreaming_StopsCapture()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo>
            {
                new(0, "TestMonitor", 1920, 1080, true, 0, 0)
            });

        using var manager = new Host.Session.SessionManager(
            captureMock.Object,
            new Dictionary<string, IVideoEncoderService>
            {
                ["vp8"] = new Mock<IVideoEncoderService>().Object
            },
            new Mock<IVideoEncoderService>().Object);

        manager.StartStreaming(0, 15, VideoQuality.Medium);

        // Act
        manager.StopStreaming();

        // Assert
        captureMock.Verify(c => c.StopCapture(), Times.Once);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    public void UpdateFps_SetsCurrentFps(int fps)
    {
        // Arrange
        using var manager = CreateSessionManager();

        // Act
        manager.UpdateFps(fps);

        // Assert
        Assert.Equal(fps, manager.CurrentFps);
    }

    [Fact]
    public void UpdateFps_OutOfRange_ClampedToValidRange()
    {
        // Arrange
        using var manager = CreateSessionManager();

        // Act
        manager.UpdateFps(100);

        // Assert
        Assert.Equal(DesktopCaptureService.MaxFps, manager.CurrentFps);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var manager = CreateSessionManager();

        // Act & Assert
        var exception = Record.Exception(() =>
        {
            manager.Dispose();
            manager.Dispose();
        });
        Assert.Null(exception);
    }
}
