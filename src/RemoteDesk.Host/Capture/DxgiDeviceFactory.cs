using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace RemoteDesk.Host.Capture;

/// <summary>
/// Production implementation that creates real DXGI/D3D11 resources.
/// </summary>
public sealed class DxgiDeviceFactory : IDxgiDeviceFactory
{
    private DxgiResources? _resources;

    public DxgiResources CreateResources()
    {
        var factory = new Factory1();
        var adapter = factory.GetAdapter1(0);
        var device = new SharpDX.Direct3D11.Device(
            adapter,
            DeviceCreationFlags.BgraSupport);

        _resources = new DxgiResources
        {
            Factory = factory,
            Adapter = adapter,
            Device = device
        };

        return _resources;
    }

    public OutputDuplication DuplicateOutput(
        SharpDX.Direct3D11.Device device, Output1 output)
    {
        return output.DuplicateOutput(device);
    }

    public void Dispose()
    {
        _resources?.Dispose();
    }
}
