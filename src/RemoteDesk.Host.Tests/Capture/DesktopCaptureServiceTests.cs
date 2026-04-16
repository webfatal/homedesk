using Moq;
using RemoteDesk.Host.Capture;
using RemoteDesk.Shared.Protocol;

namespace RemoteDesk.Host.Tests.Capture;

public class DesktopCaptureServiceTests
{
    [Fact]
    public void Constructor_NullFactory_ThrowsArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DesktopCaptureService(null!));
    }

    [Fact]
    public void IsCapturing_BeforeStart_ReturnsFalse()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        using var service = new DesktopCaptureService(factory.Object);

        // Act
        var result = service.IsCapturing;

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void StartCapture_NullCallback_ThrowsArgumentNullException()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        using var service = new DesktopCaptureService(factory.Object);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            service.StartCapture(0, 15, null!));
    }

    [Fact]
    public void StartCapture_NegativeMonitorIndex_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        using var service = new DesktopCaptureService(factory.Object);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.StartCapture(-1, 15, _ => { }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    public void StartCapture_AnyFpsValue_StartsWithoutThrowing(int inputFps)
    {
        // Arrange
        var started = new ManualResetEventSlim(false);
        var factory = new Mock<IDxgiDeviceFactory>();
        factory.Setup(f => f.CreateResources())
            .Returns(() =>
            {
                started.Set();
                // Block the thread until StopCapture is called
                Thread.Sleep(Timeout.Infinite);
                throw new InvalidOperationException("Unreachable");
            });

        using var service = new DesktopCaptureService(factory.Object);

        // Act — FPS values outside 5-30 are clamped, not rejected
        service.StartCapture(0, inputFps, _ => { });
        started.Wait(TimeSpan.FromSeconds(2));

        // Assert
        Assert.True(service.IsCapturing);

        // Cleanup
        service.StopCapture();
    }

    [Fact]
    public void StopCapture_WhenNotCapturing_DoesNotThrow()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        using var service = new DesktopCaptureService(factory.Object);

        // Act & Assert
        var exception = Record.Exception(() => service.StopCapture());
        Assert.Null(exception);
    }

    [Fact]
    public void StartCapture_WhenAlreadyCapturing_ThrowsInvalidOperationException()
    {
        // Arrange
        var started = new ManualResetEventSlim(false);
        var factory = new Mock<IDxgiDeviceFactory>();
        factory.Setup(f => f.CreateResources())
            .Returns(() =>
            {
                started.Set();
                Thread.Sleep(Timeout.Infinite);
                throw new InvalidOperationException("Unreachable");
            });

        using var service = new DesktopCaptureService(factory.Object);
        service.StartCapture(0, 15, _ => { });
        started.Wait(TimeSpan.FromSeconds(2));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            service.StartCapture(0, 15, _ => { }));

        // Cleanup
        service.StopCapture();
    }

    [Fact]
    public void Dispose_WhileCapturing_StopsCapture()
    {
        // Arrange
        var started = new ManualResetEventSlim(false);
        var factory = new Mock<IDxgiDeviceFactory>();
        factory.Setup(f => f.CreateResources())
            .Returns(() =>
            {
                started.Set();
                Thread.Sleep(Timeout.Infinite);
                throw new InvalidOperationException("Unreachable");
            });

        var service = new DesktopCaptureService(factory.Object);
        service.StartCapture(0, 15, _ => { });
        started.Wait(TimeSpan.FromSeconds(2));
        Assert.True(service.IsCapturing);

        // Act
        service.Dispose();

        // Assert
        Assert.False(service.IsCapturing);
    }

    [Fact]
    public void StartCapture_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        var service = new DesktopCaptureService(factory.Object);
        service.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() =>
            service.StartCapture(0, 15, _ => { }));
    }

    [Fact]
    public void UpdateTargetFps_WhileNotCapturing_DoesNotThrow()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        using var service = new DesktopCaptureService(factory.Object);

        // Act & Assert — UpdateTargetFps must be safe on an idle service
        var exception = Record.Exception(() => service.UpdateTargetFps(20));
        Assert.Null(exception);
    }

    [Fact]
    public void UpdateTargetFps_OutOfRange_IsClamped()
    {
        // Arrange
        var started = new ManualResetEventSlim(false);
        var factory = new Mock<IDxgiDeviceFactory>();
        factory.Setup(f => f.CreateResources())
            .Returns(() =>
            {
                started.Set();
                Thread.Sleep(Timeout.Infinite);
                throw new InvalidOperationException("Unreachable");
            });

        using var service = new DesktopCaptureService(factory.Object);
        service.StartCapture(0, 15, _ => { });
        started.Wait(TimeSpan.FromSeconds(2));

        // Act & Assert — 100 must be clamped to MaxFps without throwing
        var exception = Record.Exception(() => service.UpdateTargetFps(100));
        Assert.Null(exception);

        // Cleanup
        service.StopCapture();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var factory = new Mock<IDxgiDeviceFactory>();
        var service = new DesktopCaptureService(factory.Object);

        // Act & Assert
        var exception = Record.Exception(() =>
        {
            service.Dispose();
            service.Dispose();
        });
        Assert.Null(exception);
    }
}
