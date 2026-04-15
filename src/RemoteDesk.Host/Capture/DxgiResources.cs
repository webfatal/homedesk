using SharpDX.DXGI;

namespace RemoteDesk.Host.Capture;

/// <summary>
/// Holds the DXGI resources needed for desktop capture.
/// </summary>
public sealed class DxgiResources : IDisposable
{
    public required Factory1 Factory { get; init; }
    public required Adapter1 Adapter { get; init; }
    public required SharpDX.Direct3D11.Device Device { get; init; }

    public void Dispose()
    {
        Device.Dispose();
        Adapter.Dispose();
        Factory.Dispose();
    }
}
