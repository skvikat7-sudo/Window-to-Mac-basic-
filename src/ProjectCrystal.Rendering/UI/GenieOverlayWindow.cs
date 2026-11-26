using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.Interop;
using ProjectCrystal.Rendering.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace ProjectCrystal.Rendering.UI;

[StructLayout(LayoutKind.Sequential)]
internal struct GenieConstantsGpu
{
    public Vector4 WinRect;
    public Vector4 IconRect;
    public Vector4 OverlayParams;
    public Vector4 EffectParams;
}

public sealed class GenieOverlayWindow : Form
{
    private sealed class ActiveGenieItem : IDisposable
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11ShaderResourceView Srv;
        public required Rectangle WinScreenRect;
        public required Rectangle TargetScreenRect; // IconRect for Genie, TargetRect for Morph
        public required int DockEdge;             // 0=Bottom, 1=Left, 2=Right
        public required int Mode;                 // 0=Genie, 1=Morph
        public required bool IsEmerge;
        public float ElapsedSec;
        public float DurationSec;
        public Action? OnComplete;

        public void Dispose()
        {
            Srv.Dispose();
            Texture.Dispose();
        }
    }

    private readonly GpuDeviceContext _gpu;
    private readonly ConfigManager _configMgr;
    private readonly List<ActiveGenieItem> _active = new();
    private readonly Dictionary<nint, ID3D11RenderTargetView> _rtvCache = new();

    private IDXGISwapChain1? _swapChain;
    private IDCompositionTarget? _dcompTarget;
    private IDCompositionVisual? _dcompVisual;
    private DateTime _lastTickUtc = DateTime.UtcNow;

    public bool HasActiveAnimations => _active.Count > 0;

    protected override bool ShowWithoutActivation => true;

    public GenieOverlayWindow(GpuDeviceContext gpu, ConfigManager configMgr)
    {
        _gpu = gpu;
        _configMgr = configMgr;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);

        var bounds = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        Bounds = bounds;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32Interop.WS_EX_NOREDIRECTIONBITMAP
                        | Win32Interop.WS_EX_TOOLWINDOW
                        | Win32Interop.WS_EX_TRANSPARENT
                        | Win32Interop.WS_EX_NOACTIVATE
                        | Win32Interop.WS_EX_TOPMOST;
            cp.ExStyle &= ~Win32Interop.WS_EX_APPWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int excludePeek = 1;
        Win32Interop.DwmSetWindowAttribute(Handle, Win32Interop.DWMWA_EXCLUDED_FROM_PEEK, ref excludePeek, sizeof(int));
        Win32Interop.SetWindowDisplayAffinity(Handle, Win32Interop.WDA_EXCLUDEFROMCAPTURE);
        InitSwapChain();
    }

    private void InitSwapChain()
    {
        lock (_gpu.DeviceLock)
        {
            var scDesc = new SwapChainDescription1
            {
                Width = Math.Max(16, Width),
                Height = Math.Max(16, Height),
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied
            };

            _swapChain = _gpu.DxgiFactory.CreateSwapChainForComposition(_gpu.D3DDevice, scDesc);
            _gpu.DCompDevice.CreateTargetForHwnd(Handle, true, out _dcompTarget);
            _dcompVisual = _gpu.DCompDevice.CreateVisual();
            _dcompVisual.SetContent(_swapChain);
            _dcompTarget.SetRoot(_dcompVisual);
            _gpu.DCompDevice.Commit();
        }
    }

    public void PlayGenieCollapse(WindowSnapshot snapshot, Rectangle dockIconScreenRect, DockPosition dockPos, Action? onComplete = null)
    {
        StartAnimation(snapshot, dockIconScreenRect, dockPos, mode: 0, isEmerge: false, durationSec: 0.48f, onComplete);
    }

    public void PlayGenieEmerge(WindowSnapshot snapshot, Rectangle dockIconScreenRect, DockPosition dockPos, Action? onComplete = null)
    {
        StartAnimation(snapshot, dockIconScreenRect, dockPos, mode: 0, isEmerge: true, durationSec: 0.48f, onComplete);
    }

    public void PlayWindowMorph(WindowSnapshot snapshot, Rectangle targetScreenRect, Action? onComplete = null)
    {
        StartAnimation(snapshot, targetScreenRect, DockPosition.Bottom, mode: 1, isEmerge: false, durationSec: 0.32f, onComplete);
    }

    private void StartAnimation(
        WindowSnapshot snapshot,
        Rectangle targetScreenRect,
        DockPosition dockPos,
        int mode,
        bool isEmerge,
        float durationSec,
        Action? onComplete)
    {
        if (ShouldReduceMotion())
        {
            onComplete?.Invoke();
            return;
        }

        if (!IsHandleCreated)
            _ = Handle;

        var screen = Screen.FromRectangle(snapshot.ScreenBounds);
        if (Bounds != screen.Bounds)
        {
            Bounds = screen.Bounds;
            ResizeSwapChain();
        }

        float speed = Math.Clamp(_configMgr.Current.Glass.AnimationSpeed, 0.4f, 2.0f);
        float scaledDuration = Math.Clamp(durationSec / MathF.Sqrt(speed), 0.30f, 0.85f);
        int edge = dockPos switch
        {
            DockPosition.Left => 1,
            DockPosition.Right => 2,
            _ => 0
        };

        string kind = mode == 1 ? "Morph" : (isEmerge ? "GenieEmerge" : "GenieCollapse");
        CrystalLogger.Info("Genie", $"Starting {kind} ({scaledDuration:F2}s): Win={snapshot.ScreenBounds} -> Target={targetScreenRect}");

        lock (_gpu.DeviceLock)
        {
            var texDesc = new Texture2DDescription
            {
                Width = snapshot.Width,
                Height = snapshot.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource
            };

            ID3D11Texture2D tex;
            unsafe
            {
                fixed (byte* ptr = snapshot.BgraPixels)
                {
                    var subData = new SubresourceData(ptr, snapshot.Width * 4);
                    tex = _gpu.D3DDevice.CreateTexture2D(texDesc, subData);
                }
            }
            var srv = _gpu.D3DDevice.CreateShaderResourceView(tex);

            _active.Add(new ActiveGenieItem
            {
                Texture = tex,
                Srv = srv,
                WinScreenRect = snapshot.ScreenBounds,
                TargetScreenRect = targetScreenRect,
                DockEdge = edge,
                Mode = mode,
                IsEmerge = isEmerge,
                ElapsedSec = 0f,
                DurationSec = scaledDuration,
                OnComplete = onComplete
            });
        }

        _lastTickUtc = DateTime.UtcNow;
        RenderFrame(commitDComp: true);
        if (!Visible)
        {
            Show();
            Win32Interop.SetWindowPos(
                Handle, Win32Interop.HWND_TOPMOST, Left, Top, Width, Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
        }
    }

    public bool TickAndRenderIfNeeded(bool commitDComp = true)
    {
        if (_active.Count == 0 || IsDisposed) return false;

        var now = DateTime.UtcNow;
        float dt = Math.Clamp((float)(now - _lastTickUtc).TotalSeconds, 0.001f, 0.045f);
        _lastTickUtc = now;

        List<Action?> completedCallbacks = new();
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var item = _active[i];
            item.ElapsedSec += dt;
            if (item.ElapsedSec >= item.DurationSec)
            {
                completedCallbacks.Add(item.OnComplete);
                item.Dispose();
                _active.RemoveAt(i);
            }
        }

        RenderFrame(commitDComp);

        foreach (var cb in completedCallbacks)
        {
            try { cb?.Invoke(); }
            catch (Exception ex) { CrystalLogger.Debug("Genie", $"Completion callback error: {ex.Message}"); }
        }

        if (_active.Count == 0 && Visible)
            Hide();

        return true;
    }

    private void RenderFrame(bool commitDComp)
    {
        if (_swapChain == null) return;
        try
        {
            lock (_gpu.DeviceLock)
            {
                using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                if (!_rtvCache.TryGetValue(backBuffer.NativePointer, out var rtv))
                {
                    rtv = _gpu.D3DDevice.CreateRenderTargetView(backBuffer);
                    _rtvCache[backBuffer.NativePointer] = rtv;
                }

                var ctx = _gpu.D3DContext;
                ctx.OMSetRenderTargets(rtv);
                ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));

                if (_active.Count > 0)
                {
                    ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, Width, Height, 0f, 1f));
                    ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    ctx.IASetInputLayout(null);
                    ctx.VSSetShader(_gpu.GenieVertexShader);
                    ctx.PSSetShader(_gpu.GeniePixelShader);
                    ctx.PSSetSampler(0, _gpu.LinearClampSampler);
                    ctx.OMSetBlendState(_gpu.PremultipliedBlendState);

                    foreach (var item in _active)
                    {
                        float t = Math.Clamp(item.ElapsedSec / Math.Max(0.01f, item.DurationSec), 0f, 1f);
                        float progress;
                        if (item.Mode == 1)
                        {
                            // Smooth ease-out quintic for snap/maximize morph
                            progress = EasingCurves.EaseOutQuint(t);
                        }
                        else if (item.IsEmerge)
                        {
                            // Emerge: starts inside dock icon (1.0) -> unfolds to window (0.0) with gentle settle
                            float eased = EasingCurves.EaseOutCubic(t);
                            progress = 1f - eased;
                        }
                        else
                        {
                            // Collapse: smoothly accelerates into dock icon (0.0 -> 1.0)
                            progress = EasingCurves.SmoothStep(0f, 1f, t);
                        }

                        var winLocal = new Rectangle(
                            item.WinScreenRect.Left - Left,
                            item.WinScreenRect.Top - Top,
                            item.WinScreenRect.Width,
                            item.WinScreenRect.Height);

                        var targetLocal = new Rectangle(
                            item.TargetScreenRect.Left - Left,
                            item.TargetScreenRect.Top - Top,
                            item.TargetScreenRect.Width,
                            item.TargetScreenRect.Height);

                        var gpuConst = new GenieConstantsGpu
                        {
                            WinRect = new Vector4(winLocal.X, winLocal.Y, winLocal.Width, winLocal.Height),
                            IconRect = new Vector4(targetLocal.X, targetLocal.Y, targetLocal.Width, targetLocal.Height),
                            OverlayParams = new Vector4(Width, Height, item.DockEdge, progress),
                            EffectParams = new Vector4(item.Mode, 1.0f, 1.0f, 12f)
                        };

                        var mapped = ctx.Map(_gpu.GenieConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                        Marshal.StructureToPtr(gpuConst, mapped.DataPointer, false);
                        ctx.Unmap(_gpu.GenieConstantBuffer, 0);

                        ctx.PSSetConstantBuffer(0, _gpu.GenieConstantBuffer);
                        ctx.PSSetShaderResource(0, item.Srv);
                        ctx.Draw(3, 0);
                    }

                    ctx.OMSetBlendState(null);
                    ctx.PSSetShaderResource(0, null!);
                }

                ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
                _swapChain.Present(0, PresentFlags.None);
                if (commitDComp)
                    _gpu.DCompDevice.Commit();
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Genie", $"RenderFrame failed: {ex.Message}");
        }
    }

    public static byte[] RenderOffscreenSampleBgra(
        GpuDeviceContext gpu,
        int width,
        int height,
        WindowSnapshot snapshot,
        Rectangle targetScreenRect,
        DockPosition dockPos,
        float progress,
        int mode = 0)
    {
        lock (gpu.DeviceLock)
        {
            ID3D11Texture2D winTex;
            var winTexDesc = new Texture2DDescription
            {
                Width = snapshot.Width,
                Height = snapshot.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource
            };
            unsafe
            {
                fixed (byte* ptr = snapshot.BgraPixels)
                    winTex = gpu.D3DDevice.CreateTexture2D(winTexDesc, new SubresourceData(ptr, snapshot.Width * 4));
            }
            using var _winTexScope = winTex;
            using var winSrv = gpu.D3DDevice.CreateShaderResourceView(winTex);

            using var outTex = gpu.D3DDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });
            using var rtv = gpu.D3DDevice.CreateRenderTargetView(outTex);
            using var staging = gpu.D3DDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            });

            var ctx = gpu.D3DContext;
            ctx.OMSetRenderTargets(rtv);
            ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0.08f, 0.12f, 0.22f, 1f));
            ctx.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, width, height, 0f, 1f));
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(gpu.GenieVertexShader);
            ctx.PSSetShader(gpu.GeniePixelShader);
            ctx.PSSetSampler(0, gpu.LinearClampSampler);
            ctx.OMSetBlendState(gpu.PremultipliedBlendState);

            int edge = dockPos switch { DockPosition.Left => 1, DockPosition.Right => 2, _ => 0 };
            var gpuConst = new GenieConstantsGpu
            {
                WinRect = new Vector4(snapshot.ScreenBounds.X, snapshot.ScreenBounds.Y, snapshot.ScreenBounds.Width, snapshot.ScreenBounds.Height),
                IconRect = new Vector4(targetScreenRect.X, targetScreenRect.Y, targetScreenRect.Width, targetScreenRect.Height),
                OverlayParams = new Vector4(width, height, edge, progress),
                EffectParams = new Vector4(mode, 1f, 1f, 12f)
            };
            var mapped = ctx.Map(gpu.GenieConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            Marshal.StructureToPtr(gpuConst, mapped.DataPointer, false);
            ctx.Unmap(gpu.GenieConstantBuffer, 0);

            ctx.PSSetConstantBuffer(0, gpu.GenieConstantBuffer);
            ctx.PSSetShaderResource(0, winSrv);
            ctx.Draw(3, 0);

            ctx.OMSetBlendState(null);
            ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            ctx.PSSetShaderResource(0, null!);
            ctx.CopyResource(staging, outTex);

            var readMap = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte[] bytes = new byte[width * height * 4];
                unsafe
                {
                    byte* src = (byte*)readMap.DataPointer;
                    for (int y = 0; y < height; y++)
                        Marshal.Copy((nint)(src + y * readMap.RowPitch), bytes, y * width * 4, width * 4);
                }
                return bytes;
            }
            finally
            {
                ctx.Unmap(staging, 0);
            }
        }
    }

    private void ResizeSwapChain()
    {
        if (_swapChain == null) return;
        lock (_gpu.DeviceLock)
        {
            foreach (var rtv in _rtvCache.Values) rtv.Dispose();
            _rtvCache.Clear();
            _swapChain.ResizeBuffers(2, Math.Max(16, Width), Math.Max(16, Height), Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        }
    }

    private bool ShouldReduceMotion() => _configMgr.Current.Accessibility.MotionMode switch
    {
        ReducedMotionMode.AlwaysReduce => true,
        ReducedMotionMode.AlwaysFull => false,
        _ => Win32Interop.IsReducedMotionEnabledByWindows()
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gpu.DeviceLock)
            {
                foreach (var item in _active) item.Dispose();
                _active.Clear();
                foreach (var rtv in _rtvCache.Values) rtv.Dispose();
                _rtvCache.Clear();
                _dcompVisual?.Dispose();
                _dcompTarget?.Dispose();
                _swapChain?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
