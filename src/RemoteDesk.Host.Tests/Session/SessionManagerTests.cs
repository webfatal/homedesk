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
        var vp8Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        using var manager = CreateSessionManager(
            encoders: new Dictionary<string, IVideoEncoderService>
            {
                ["vp8"] = vp8Mock.Object,
                ["av1"] = new Mock<IVideoEncoderService>().Object
            },
            jpegMock: jpegMock);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Act
        manager.UpdateFps(fps);

        // Assert
        Assert.Equal(fps, manager.CurrentFps);
    }

    [Fact]
    public void UpdateFps_OutOfRange_ClampedToValidRange()
    {
        // Arrange
        var vp8Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        using var manager = CreateSessionManager(
            encoders: new Dictionary<string, IVideoEncoderService>
            {
                ["vp8"] = vp8Mock.Object,
                ["av1"] = new Mock<IVideoEncoderService>().Object
            },
            jpegMock: jpegMock);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

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

    [Fact]
    public void ApplySettings_FpsOnly_CallsCaptureUpdateAndReconfigureWithoutRestart()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo> { new(0, "TestMonitor", 1920, 1080, true, 0, 0) });
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = new Mock<IVideoEncoderService>().Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Act
        manager.ApplySettings(new SessionSettings(0, 24, VideoQuality.Medium, VideoCodec.Vp8));

        // Assert
        captureMock.Verify(c => c.UpdateTargetFps(24), Times.Once);
        captureMock.Verify(c => c.StopCapture(), Times.Never);
        vp8Mock.Verify(e => e.Reconfigure(24, VideoQuality.Medium), Times.Once);
        jpegMock.Verify(e => e.Reconfigure(24, VideoQuality.Medium), Times.Once);
        // Reconfigure restarts the ffmpeg pipeline, which inherently produces
        // a keyframe as the first output frame. Calling ForceKeyframe on top
        // would cause a redundant second restart — we assert it is NOT called.
        vp8Mock.Verify(e => e.ForceKeyframe(), Times.Never);
        Assert.Equal(24, manager.CurrentFps);
    }

    [Fact]
    public void ApplySettings_QualityOnly_ReconfiguresWithoutTouchingCaptureLoop()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo> { new(0, "TestMonitor", 1920, 1080, true, 0, 0) });
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = new Mock<IVideoEncoderService>().Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Act
        manager.ApplySettings(new SessionSettings(0, 15, VideoQuality.High, VideoCodec.Vp8));

        // Assert
        captureMock.Verify(c => c.UpdateTargetFps(It.IsAny<int>()), Times.Never);
        captureMock.Verify(c => c.StopCapture(), Times.Never);
        vp8Mock.Verify(e => e.Reconfigure(15, VideoQuality.High), Times.Once);
        Assert.Equal(VideoQuality.High, manager.CurrentQuality);
    }

    [Fact]
    public void ApplySettings_CodecSwitch_InitializesNewEncoderAndKeepsCaptureRunning()
    {
        // Arrange — start with VP8, then switch to AV1
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var av1Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo> { new(0, "TestMonitor", 1920, 1080, true, 0, 0) });
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        av1Mock.SetupGet(e => e.IsInitialized).Returns(false); // not yet initialised
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = av1Mock.Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // After StartStreaming, let IsInitialized reflect reality for subsequent Reconfigure
        av1Mock.Setup(e => e.Initialize(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<VideoQuality>()))
            .Callback(() => av1Mock.SetupGet(e => e.IsInitialized).Returns(true));

        // Act
        manager.ApplySettings(new SessionSettings(0, 15, VideoQuality.Medium, VideoCodec.Av1));

        // Assert
        av1Mock.Verify(e => e.Initialize(1920, 1080, 15, VideoQuality.Medium), Times.Once);
        av1Mock.Verify(e => e.ForceKeyframe(), Times.Once);
        captureMock.Verify(c => c.StopCapture(), Times.Never);
        Assert.Equal(VideoCodec.Av1, manager.CurrentCodec);
    }

    [Fact]
    public void ApplySettings_CodecSwitch_DisposesPreviousCodecEncoder()
    {
        // Arrange — start with VP8 (initialised), switch to AV1; afterwards the
        // VP8 encoder instance must have been Disposed so its ffmpeg process
        // stops consuming resources.
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var av1Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo> { new(0, "TestMonitor", 1920, 1080, true, 0, 0) });
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        av1Mock.SetupGet(e => e.IsInitialized).Returns(false);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        av1Mock.Setup(e => e.Initialize(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<VideoQuality>()))
            .Callback(() => av1Mock.SetupGet(e => e.IsInitialized).Returns(true));

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = av1Mock.Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Act
        manager.ApplySettings(new SessionSettings(0, 15, VideoQuality.Medium, VideoCodec.Av1));

        // Assert — the old VP8 encoder instance must be disposed
        vp8Mock.Verify(e => e.Dispose(), Times.Once);

        // And the dictionary entry must have been replaced by a fresh, NOT-disposed
        // instance so that a later switch back can re-initialize it cleanly.
        // Access via reflection on the public API: re-apply VP8 and verify the
        // replacement instance (not the disposed one) is initialised.
        vp8Mock.Invocations.Clear();
        manager.ApplySettings(new SessionSettings(0, 15, VideoQuality.Medium, VideoCodec.Vp8));
        // The old (disposed) mock must NOT receive new calls — the entry has
        // been swapped for a real VP8EncoderService instance.
        vp8Mock.Verify(e => e.Initialize(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<VideoQuality>()),
            Times.Never);
    }

    [Fact]
    public void ApplySettings_NoChange_IsNoOp()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo> { new(0, "TestMonitor", 1920, 1080, true, 0, 0) });
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = new Mock<IVideoEncoderService>().Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Act — apply identical settings
        manager.ApplySettings(manager.CurrentSettings);

        // Assert — nothing should have been touched
        captureMock.Verify(c => c.UpdateTargetFps(It.IsAny<int>()), Times.Never);
        vp8Mock.Verify(e => e.Reconfigure(It.IsAny<int>(), It.IsAny<VideoQuality>()), Times.Never);
        vp8Mock.Verify(e => e.ForceKeyframe(), Times.Never);
    }

    [Fact]
    public void ApplySettings_NullSettings_ThrowsArgumentNullException()
    {
        // Arrange
        using var manager = CreateSessionManager();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => manager.ApplySettings(null!));
    }

    [Fact]
    public void CurrentSettings_AfterStartStreaming_ReflectsAllFields()
    {
        // Arrange
        using var manager = CreateSessionManager();

        // Act
        manager.StartStreaming(0, 20, VideoQuality.High, VideoCodec.Av1);

        // Assert
        var settings = manager.CurrentSettings;
        Assert.Equal(0, settings.MonitorIndex);
        Assert.Equal(20, settings.Fps);
        Assert.Equal(VideoQuality.High, settings.Quality);
        Assert.Equal(VideoCodec.Av1, settings.Codec);
    }

    [Fact]
    public void UpdateFps_DelegatesThroughApplySettings_AndCallsCapture()
    {
        // Arrange
        var captureMock = new Mock<IDesktopCaptureService>();
        var vp8Mock = new Mock<IVideoEncoderService>();
        var jpegMock = new Mock<IVideoEncoderService>();

        captureMock.Setup(c => c.GetAvailableMonitors())
            .Returns(new List<MonitorInfo> { new(0, "TestMonitor", 1920, 1080, true, 0, 0) });
        vp8Mock.SetupGet(e => e.IsInitialized).Returns(true);
        jpegMock.SetupGet(e => e.IsInitialized).Returns(true);

        var encoders = new Dictionary<string, IVideoEncoderService>
        {
            ["vp8"] = vp8Mock.Object,
            ["av1"] = new Mock<IVideoEncoderService>().Object
        };

        using var manager = new Host.Session.SessionManager(
            captureMock.Object, encoders, jpegMock.Object);
        manager.StartStreaming(0, 15, VideoQuality.Medium, VideoCodec.Vp8);

        // Act
        manager.UpdateFps(25);

        // Assert — the fix: previously UpdateFps only set a field and nothing else;
        // now it must go through the capture service and encoders.
        captureMock.Verify(c => c.UpdateTargetFps(25), Times.Once);
        vp8Mock.Verify(e => e.Reconfigure(25, VideoQuality.Medium), Times.Once);
        Assert.Equal(25, manager.CurrentFps);
    }
}
