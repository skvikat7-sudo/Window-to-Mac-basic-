using System.Runtime.InteropServices;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ProjectCrystal.Native.Capture;

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow(nint window, [In] ref Guid iid);
    nint CreateForMonitor(nint monitor, [In] ref Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface([In] ref Guid iid);
}

public sealed class WgcDesktopCaptureService : IDesktopBackdropSource
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly nint _hMonitor;
    private readonly object _deviceLock;
    private ID3D11Texture2D? _sharedTex;
    private ID3D11ShaderResourceView? _sharedSrv;
    private ID3D11Texture2D? _probeStagingTex;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private long _frameSequence;
    private int _width;
    private int _height;
    private bool _disposed;
    private volatile bool _hasValidNonBlackFrame;
    private bool _loggedBlankFrameSkip;
    private DateTime _lastAcceptedFrameUtc = DateTime.MinValue;
    private DateTime _lastBlankCheckUtc = DateTime.MinValue;
    private int _minIntervalMs = 16;

    public object SyncRoot { get; } = new();
    public bool IsLiveCaptureActive { get; private set; }
    public bool HasValidCaptureFrame => _hasValidNonBlackFrame;
    public long FrameSequence => Interlocked.Read(ref _frameSequence);
    public int TextureWidth => _width;
    public int TextureHeight => _height;
    public string BackendStatusText { get; private set; } = "Initializing";
    public ID3D11ShaderResourceView? BackdropSrv => _sharedSrv;
    public ID3D11Texture2D? BackdropTexture => _sharedTex;

    public event Action? FrameUpdated;

    public WgcDesktopCaptureService(ID3D11Device device, ID3D11DeviceContext context, nint hMonitor, object? deviceLock = null)
    {
        _device = device;
        _context = context;
        _hMonitor = hMonitor;
        _deviceLock = deviceLock ?? new object();
    }

    public void SetMaxCaptureFps(int fps)
    {
        fps = Math.Clamp(fps, 10, 144);
        _minIntervalMs = Math.Max(6, 1000 / fps);
    }

    public async Task<bool> StartAsync()
    {
        if (_disposed) return false;
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                BackendStatusText = "WGC unsupported on OS; using wallpaper backdrop";
                CrystalLogger.Warn("Capture", BackendStatusText);
                return false;
            }

            try
            {
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            }
            catch (Exception ex)
            {
                CrystalLogger.Warn("Capture", $"Borderless capture access request note: {ex.Message}");
            }

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            uint hr = Win32Interop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint pWinrtDev);
            if (hr != 0 || pWinrtDev == 0)
            {
                BackendStatusText = $"Failed creating WinRT D3D11 device (hr=0x{hr:X})";
                return false;
            }

            _winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(pWinrtDev);
            Marshal.Release(pWinrtDev);

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            Guid itemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            nint itemPtr = interop.CreateForMonitor(_hMonitor, ref itemGuid);
            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);

            _width = item.Size.Width;
            _height = item.Size.Height;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (Exception ex)
            {
                CrystalLogger.Warn("Capture", $"Could not disable WGC capture border: {ex.Message}");
            }

            _session.StartCapture();
            IsLiveCaptureActive = true;
            BackendStatusText = $"Windows.Graphics.Capture Active ({_width}x{_height}, Borderless)";
            CrystalLogger.Info("Capture", BackendStatusText);
            return true;
        }
        catch (Exception ex)
        {
            BackendStatusText = $"WGC init failed ({ex.Message}); fallback active";
            CrystalLogger.Error("Capture", "WGC start failed", ex);
            IsLiveCaptureActive = false;
            return false;
        }
    }

    public void Stop()
    {
        IsLiveCaptureActive = false;
        try
        {
            if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
        }
        catch { }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        using var frame = sender.TryGetNextFrame();
        if (frame == null) return;

        var now = DateTime.UtcNow;
        if ((now - _lastAcceptedFrameUtc).TotalMilliseconds < _minIntervalMs)
            return;
        _lastAcceptedFrameUtc = now;

        try
        {
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid texGuid = typeof(ID3D11Texture2D).GUID;
            nint texPtr = access.GetInterface(ref texGuid);
            if (texPtr == 0) return;

            using var srcTex = new ID3D11Texture2D(texPtr);
            var srcDesc = srcTex.Description;

            lock (_deviceLock)
            {
                bool shouldProbeBlank = !_hasValidNonBlackFrame || (now - _lastBlankCheckUtc).TotalMilliseconds >= 2000;
                if (shouldProbeBlank)
                {
                    _lastBlankCheckUtc = now;
                    if (IsFrameBlankBlack(srcTex, (int)srcDesc.Width, (int)srcDesc.Height))
                    {
                        if (!_loggedBlankFrameSkip)
                        {
                            _loggedBlankFrameSkip = true;
                            CrystalLogger.Debug("Capture", "Skipped blank all-zero WGC frame to prevent black glass artifact.");
                        }
                        return;
                    }
                    _loggedBlankFrameSkip = false;
                }

                lock (SyncRoot)
                {
                    bool needRecreate = _sharedTex == null || _sharedSrv == null || _width != (int)srcDesc.Width || _height != (int)srcDesc.Height;
                    if (needRecreate)
                    {
                        _sharedSrv?.Dispose();
                        _sharedSrv = null;
                        _sharedTex?.Dispose();
                        _width = (int)srcDesc.Width;
                        _height = (int)srcDesc.Height;

                        var desc = new Texture2DDescription
                        {
                            Width = srcDesc.Width,
                            Height = srcDesc.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.ShaderResource,
                            CPUAccessFlags = CpuAccessFlags.None
                        };
                        _sharedTex = _device.CreateTexture2D(desc);
                    }

                    _context.CopyResource(_sharedTex!, srcTex);
                    _sharedSrv ??= _device.CreateShaderResourceView(_sharedTex!);
                    _hasValidNonBlackFrame = true;
                    Interlocked.Increment(ref _frameSequence);
                }
            }

            FrameUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Capture", $"Frame copy skipped: {ex.Message}");
        }
    }

    private unsafe bool IsFrameBlankBlack(ID3D11Texture2D srcTex, int w, int h)
    {
        if (w < 16 || h < 16) return true;
        _probeStagingTex ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = 9,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });

        ReadOnlySpan<(int x, int y)> pts = stackalloc (int, int)[]
        {
            (w / 6, h / 6),
            (w / 2, h / 6),
            ((w * 5) / 6, h / 6),
            (w / 6, h / 2),
            (w / 2, h / 2),
            ((w * 5) / 6, h / 2),
            (w / 6, (h * 5) / 6),
            (w / 2, (h * 5) / 6),
            ((w * 5) / 6, (h * 5) / 6)
        };

        for (int i = 0; i < pts.Length; i++)
        {
            var box = new Vortice.Mathematics.Box(pts[i].x, pts[i].y, 0, pts[i].x + 1, pts[i].y + 1, 1);
            _context.CopySubresourceRegion(_probeStagingTex, 0, i, 0, 0, srcTex, 0, box);
        }

        var map = _context.Map(_probeStagingTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* p = (byte*)map.DataPointer;
            for (int i = 0; i < 9; i++)
            {
                byte b = p[i * 4];
                byte g = p[i * 4 + 1];
                byte r = p[i * 4 + 2];
                if (b > 0 || g > 0 || r > 0)
                    return false;
            }
            return true;
        }
        finally
        {
            _context.Unmap(_probeStagingTex, 0);
        }
    }

    public void UploadStaticBgraBackdrop(byte[] bgraPixels, int width, int height)
    {
        lock (_deviceLock)
        lock (SyncRoot)
        {
            _sharedSrv?.Dispose();
            _sharedTex?.Dispose();
            _width = width;
            _height = height;

            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            };
            unsafe
            {
                fixed (byte* ptr = bgraPixels)
                {
                    var sub = new SubresourceData((nint)ptr, width * 4);
                    _sharedTex = _device.CreateTexture2D(desc, sub);
                }
            }
            _sharedSrv = _device.CreateShaderResourceView(_sharedTex);
            _hasValidNonBlackFrame = true;
            Interlocked.Increment(ref _frameSequence);
        }
        FrameUpdated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        lock (_deviceLock)
        lock (SyncRoot)
        {
            _probeStagingTex?.Dispose();
            _probeStagingTex = null;
            _sharedSrv?.Dispose();
            _sharedTex?.Dispose();
            _sharedSrv = null;
            _sharedTex = null;
        }
        _winrtDevice?.Dispose();
    }
}
