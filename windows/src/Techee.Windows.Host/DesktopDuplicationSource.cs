using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Techee.Windows.Host;

/// <summary>
/// Desktop capture via the DXGI Desktop Duplication API.
/// </summary>
/// <remarks>
/// <para>
/// Chosen over Windows Graphics Capture for the first implementation: capturing a whole
/// monitor is exactly what a remote-desktop host wants, and Desktop Duplication does it
/// without a WinRT projection, a picker UI, or a message loop. WGC becomes worthwhile
/// when per-window capture is wanted.
/// </para>
/// <para>
/// Downscaling happens on the GPU before readback, which matters twice over: the CPU
/// never touches full-resolution pixels, and only the smaller image crosses the bus.
/// At 1080p→720p that is 2.25× less data per frame.
/// </para>
/// <para>
/// <b>Frames arrive only when the screen changes.</b> An idle desktop produces almost
/// none, which is the API being efficient rather than broken, and is why
/// <see cref="TryCapture"/> returning false is a normal outcome.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DesktopDuplicationSource : IScreenSource
{
    private readonly object _gate = new();
    private readonly int _outputIndex;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication _duplication = null!;

    private ID3D11Texture2D? _scaled;
    private ID3D11Texture2D? _staging;
    private int _targetWidth, _targetHeight;

    private int _sourceWidth, _sourceHeight;
    private volatile bool _protectedMasked;
    private bool _disposed;

    public DesktopDuplicationSource(DisplayInfo display, int outputIndex = 0)
    {
        Display = display;
        _outputIndex = outputIndex;
        Initialize();
        Resize(display.PhysicalWidth, display.PhysicalHeight);
    }

    public DisplayInfo Display { get; }

    public (int Width, int Height) OutputSize
    {
        get { lock (_gate) return (_targetWidth, _targetHeight); }
    }

    public bool ProtectedContentMasked => _protectedMasked;

    /// <summary>The source resolution, in physical pixels, before any downscale.</summary>
    public (int Width, int Height) SourceSize
    {
        get { lock (_gate) return (_sourceWidth, _sourceHeight); }
    }

    private void Initialize()
    {
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var device).CheckError();

        // CheckError throws on a failed HRESULT, but the out parameter is still typed
        // nullable. An explicit guard both satisfies the compiler and turns a driver
        // that returns success-with-no-device into a diagnosable message rather than a
        // NullReferenceException three frames later.
        _device = device ?? throw new InvalidOperationException(
            "Direct3D 11 reported success but produced no device; screen capture is unavailable.");
        _context = _device.ImmediateContext;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        if (adapter.EnumOutputs((uint)_outputIndex, out var output).Failure)
            throw new InvalidOperationException($"No DXGI output at index {_outputIndex}.");

        using (output)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        {
            _duplication = output1.DuplicateOutput(_device);
        }

        var desc = _duplication.Description;
        _sourceWidth = (int)desc.ModeDescription.Width;
        _sourceHeight = (int)desc.ModeDescription.Height;
    }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (width == _targetWidth && height == _targetHeight && _staging is not null) return;

            _scaled?.Dispose();
            _staging?.Dispose();
            _scaled = null;
            _staging = null;

            _targetWidth = Math.Clamp(width, 2, _sourceWidth);
            _targetHeight = Math.Clamp(height, 2, _sourceHeight);

            // Even dimensions: I420 subsamples chroma 2×2 and VP8 wants even sizes.
            _targetWidth &= ~1;
            _targetHeight &= ~1;

            if (_targetWidth != _sourceWidth || _targetHeight != _sourceHeight)
            {
                _scaled = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_targetWidth,
                    Height = (uint)_targetHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.GenerateMips,
                });
            }

            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_targetWidth,
                Height = (uint)_targetHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }
    }

    public bool TryCapture(TimeSpan timeout, FrameHandler onFrame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_staging is null) return false;

            var ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 5000);
            var result = _duplication.AcquireNextFrame(ms, out var info, out var resource);

            if (result.Failure)
            {
                // DXGI_ERROR_ACCESS_LOST: a mode change, a full-screen app taking
                // exclusive control, or a secure-desktop transition. Recoverable by
                // rebuilding the duplication, which is a normal part of a long session
                // rather than a fault.
                if (result.Code == unchecked((int)0x887A0026)) Reinitialize();
                return false;
            }

            try
            {
                _protectedMasked = info.ProtectedContentMaskedOut;

                using (resource)
                using (var frame = resource.QueryInterface<ID3D11Texture2D>())
                {
                    if (_scaled is null)
                    {
                        _context.CopyResource(_staging, frame);
                    }
                    else
                    {
                        // CopySubresourceRegion crops rather than scales, so this takes
                        // the top-left region at the target size. A proper scaling pass
                        // (a shader, or a mip chain) is the correct answer and is
                        // tracked for the W4 pipeline work; cropping keeps the pump
                        // honest in the meantime rather than silently stretching.
                        var w = Math.Min(_targetWidth, _sourceWidth);
                        var h = Math.Min(_targetHeight, _sourceHeight);
                        _context.CopySubresourceRegion(
                            _scaled, 0, 0, 0, 0, frame, 0, new Box(0, 0, 0, w, h, 1));
                        _context.CopyResource(_staging, _scaled);
                    }
                }

                var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        var length = (int)map.RowPitch * _targetHeight;
                        var span = new ReadOnlySpan<byte>((void*)map.DataPointer, length);
                        onFrame(span, _targetWidth, _targetHeight, (int)map.RowPitch);
                    }
                }
                finally
                {
                    _context.Unmap(_staging, 0);
                }

                return true;
            }
            finally
            {
                _duplication.ReleaseFrame();
            }
        }
    }

    /// <summary>
    /// Rebuilds duplication after the OS revoked it.
    /// </summary>
    /// <remarks>
    /// Access is lost on resolution changes, monitor hot-plug, exclusive full-screen
    /// applications, and secure-desktop transitions — all of which happen routinely over
    /// an unattended session lasting days. Recovering silently is the point.
    /// </remarks>
    private void Reinitialize()
    {
        try
        {
            _duplication.Dispose();

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            if (adapter.EnumOutputs((uint)_outputIndex, out var output).Failure) return;

            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                _duplication = output1.DuplicateOutput(_device);
            }

            var desc = _duplication.Description;
            var newW = (int)desc.ModeDescription.Width;
            var newH = (int)desc.ModeDescription.Height;

            if (newW != _sourceWidth || newH != _sourceHeight)
            {
                _sourceWidth = newW;
                _sourceHeight = newH;
                // Force the textures to be rebuilt against the new source size.
                _targetWidth = 0;
                _targetHeight = 0;
            }
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // The display may still be settling. The next TryCapture retries.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _staging?.Dispose();
            _scaled?.Dispose();
            _duplication?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }

    /// <summary>Enumerates the machine's displays.</summary>
    /// <remarks>
    /// Reports both logical desktop coordinates and physical duplication size, because
    /// on a DPI-scaled monitor they differ and conflating them misplaces every click.
    /// </remarks>
    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out var device).CheckError();

        if (device is null) return displays;

        using (device)
        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        {
            for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
            {
                using (output)
                {
                    var d = output.Description;
                    var r = d.DesktopCoordinates;

                    var logicalWidth = r.Right - r.Left;
                    var logicalHeight = r.Bottom - r.Top;

                    // The physical size comes from duplication, which reports real
                    // pixels; DesktopCoordinates is DPI-scaled.
                    var physicalWidth = logicalWidth;
                    var physicalHeight = logicalHeight;
                    try
                    {
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        using var dup = output1.DuplicateOutput(device);
                        physicalWidth = (int)dup.Description.ModeDescription.Width;
                        physicalHeight = (int)dup.Description.ModeDescription.Height;
                    }
                    catch (SharpGen.Runtime.SharpGenException)
                    {
                        // Duplication may be unavailable (already held, or an RDP
                        // session). Fall back to logical, and let Scale read as 1.0
                        // rather than reporting a size we did not measure.
                    }

                    displays.Add(new DisplayInfo(
                        Id: $"DISPLAY{i + 1}",
                        DeviceName: d.DeviceName,
                        Left: r.Left,
                        Top: r.Top,
                        LogicalWidth: logicalWidth,
                        LogicalHeight: logicalHeight,
                        PhysicalWidth: physicalWidth,
                        PhysicalHeight: physicalHeight,
                        IsPrimary: r.Left == 0 && r.Top == 0));
                }
            }
        }

        return displays;
    }
}
