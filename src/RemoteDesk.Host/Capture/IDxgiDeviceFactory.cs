using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace RemoteDesk.Host.Capture;

/// <summary>
/// Abstracts DXGI device and output creation to allow testing without real hardware.
/// Single Responsibility: only handles device/output lifecycle, not capture logic.
/// </summary>
public interface IDxgiDeviceFactory : IDisposable
{
    /// <summary>
    /// Creates a D3D11 device and enumerates available outputs (monitors).
    /// </summary>
    DxgiResources CreateResources();

    /// <summary>
    /// Creates a Desktop Duplication session for the given output.
    /// </summary>
    OutputDuplication DuplicateOutput(SharpDX.Direct3D11.Device device, Output1 output);
}
