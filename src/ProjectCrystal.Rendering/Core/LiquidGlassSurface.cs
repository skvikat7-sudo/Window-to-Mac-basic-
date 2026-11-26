using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Native.Capture;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ProjectCrystal.Rendering.Core;

public sealed class LiquidGlassSurface : IDisposable
{
    private readonly GpuDeviceContext _gpu;
    private readonly nint _hwnd;
    private readonly bool _isOffscreen;

    private IDXGISwapChain1? _swapChain;
    private IDCompositionTarget? _dcompTarget;
    private IDCompositionVisual? _dcompVisual;
    private ID2D1DeviceContext _d2dCtx;

    private ID3D11Texture2D? _offscreenTex;
    private ID3D11RenderTargetView? _offscreenRtv;
    private ID2D1Bitmap1? _offscreenD2dBmp;
    private readonly Dictionary<nint, (ID3D11RenderTargetView Rtv, ID2D1Bitmap1 D2dBmp)> _swapBufferCache = new();
    private readonly ID3D11ShaderResourceView[] _psSrvs = new ID3D11ShaderResourceView[2];
    private readonly ID3D11ShaderResourceView[] _psNullSrvs = new ID3D11ShaderResourceView[2];
    private static readonly ID3D11RenderTargetView[] EmptyRtvs = Array.Empty<ID3D11RenderTargetView>();
    private static readonly BitmapProperties1 DxgiTargetProps = new(
        new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
        96f, 96f,
        BitmapOptions.Target | BitmapOptions.CannotDraw);

    public int Width { get; private set; }
    public int Height { get; private set; }
    private bool _disposed;

    public LiquidGlassSurface(GpuDeviceContext gpu, nint hwnd, int width, int height)
    {
        _gpu = gpu;
        _hwnd = hwnd;
        _isOffscreen = hwnd == 0;
        Width = Math.Max(16, width);
        Height = Math.Max(16, height);

        lock (_gpu.DeviceLock)
        {
            _d2dCtx = _gpu.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
            CreateTargets(Width, Height);
        }
    }

    private void CreateTargets(int width, int height)
    {
        if (_isOffscreen)
        {
            _offscreenRtv?.Dispose();
            _offscreenTex?.Dispose();
            _offscreenTex = _gpu.D3DDevice.CreateTexture2D(new Texture2DDescription
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
            _offscreenRtv = _gpu.D3DDevice.CreateRenderTargetView(_offscreenTex);
            return;
        }

        var scDesc = new SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Premultiplied
        };

        _swapChain = _gpu.DxgiFactory.CreateSwapChainForComposition(_gpu.D3DDevice, scDesc);
        _gpu.DCompDevice.CreateTargetForHwnd(_hwnd, true, out _dcompTarget);
        _dcompVisual = _gpu.DCompDevice.CreateVisual();
        _dcompVisual.SetContent(_swapChain);
        _dcompTarget.SetRoot(_dcompVisual);
        _gpu.DCompDevice.Commit();
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(16, width);
        height = Math.Max(16, height);
        if (width == Width && height == Height) return;

        Width = width;
        Height = height;

        lock (_gpu.DeviceLock)
        {
            ClearCachedTargets();
            if (_isOffscreen)
            {
                CreateTargets(width, height);
            }
            else if (_swapChain != null)
            {
                _swapChain.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            }
        }
    }

    private void ClearCachedTargets()
    {
        _d2dCtx.Target = null;
        _offscreenD2dBmp?.Dispose();
        _offscreenD2dBmp = null;
        foreach (var kv in _swapBufferCache.Values)
        {
            kv.D2dBmp.Dispose();
            kv.Rtv.Dispose();
        }
        _swapBufferCache.Clear();
    }

    public void RenderFrame(
        IDesktopBackdropSource? backdrop,
        CrystalConfig config,
        Point windowScreenOrigin,
        PointF mouseLocalPos,
        IReadOnlyList<GlassShapeItem> shapes,
        float timeSeconds,
        bool isDarkAppearance,
        Action<ID2D1DeviceContext>? drawForeground2D,
        bool commitDComp = true)
    {
        if (_disposed) return;

        lock (_gpu.DeviceLock)
        {
            ID3D11Texture2D? targetTex = null;
            ID3D11RenderTargetView? rtv = null;
            ID2D1Bitmap1? d2dTarget = null;
            bool disposeTex = false;

            try
            {
                if (_isOffscreen)
                {
                    targetTex = _offscreenTex;
                    rtv = _offscreenRtv;
                    if (targetTex != null && _offscreenD2dBmp == null && drawForeground2D != null)
                    {
                        using var dxgiSurf = targetTex.QueryInterface<IDXGISurface>();
                        _offscreenD2dBmp = _d2dCtx.CreateBitmapFromDxgiSurface(dxgiSurf, DxgiTargetProps);
                    }
                    d2dTarget = _offscreenD2dBmp;
                }
                else if (_swapChain != null)
                {
                    targetTex = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                    disposeTex = true;
                    nint key = targetTex.NativePointer;
                    if (!_swapBufferCache.TryGetValue(key, out var cached))
                    {
                        if (_swapBufferCache.Count >= 4)
                            ClearCachedTargets();
                        var newRtv = _gpu.D3DDevice.CreateRenderTargetView(targetTex);
                        using var dxgiSurf = targetTex.QueryInterface<IDXGISurface>();
                        var newBmp = _d2dCtx.CreateBitmapFromDxgiSurface(dxgiSurf, DxgiTargetProps);
                        cached = (newRtv, newBmp);
                        _swapBufferCache[key] = cached;
                    }
                    rtv = cached.Rtv;
                    d2dTarget = cached.D2dBmp;
                }

                if (targetTex is null || rtv is null) return;

                // Resolve active backdrop SRV (Live WGC if enabled & ready, else Wallpaper fallback)
                ID3D11ShaderResourceView? activeSrv = null;
                int desktopW = _gpu.FallbackWidth;
                int desktopH = _gpu.FallbackHeight;
                bool lockedBackdrop = false;

                if (backdrop != null && config.Glass.CaptureBackend == CaptureBackendMode.LiveDesktopWgc && backdrop.BackdropSrv != null)
                {
                    Monitor.Enter(backdrop.SyncRoot, ref lockedBackdrop);
                    if (backdrop.BackdropSrv != null)
                    {
                        activeSrv = backdrop.BackdropSrv;
                        desktopW = Math.Max(1, backdrop.TextureWidth);
                        desktopH = Math.Max(1, backdrop.TextureHeight);
                    }
                }
                else if (backdrop != null && config.Glass.CaptureBackend == CaptureBackendMode.WallpaperComposited && backdrop.BackdropSrv != null && !backdrop.IsLiveCaptureActive)
                {
                    // Allows custom injected test texture in tests
                    Monitor.Enter(backdrop.SyncRoot, ref lockedBackdrop);
                    if (backdrop.BackdropSrv != null)
                    {
                        activeSrv = backdrop.BackdropSrv;
                        desktopW = Math.Max(1, backdrop.TextureWidth);
                        desktopH = Math.Max(1, backdrop.TextureHeight);
                    }
                }

                activeSrv ??= _gpu.GetFallbackSrv();

                try
                {
                    UploadConstants(config, windowScreenOrigin, desktopW, desktopH, mouseLocalPos, shapes, timeSeconds, isDarkAppearance);

                    var ctx = _gpu.D3DContext;
                    ctx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 0f));
                    ctx.OMSetRenderTargets(rtv);
                    ctx.RSSetViewport(new Viewport(0, 0, Width, Height));
                    ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    ctx.VSSetShader(_gpu.GlassVertexShader);
                    ctx.PSSetShader(_gpu.GlassPixelShader);
                    ctx.PSSetConstantBuffer(0, _gpu.GlassConstantBuffer);
                    _psSrvs[0] = activeSrv;
                    _psSrvs[1] = _gpu.GetFallbackSrv();
                    ctx.PSSetShaderResources(0, _psSrvs);
                    ctx.PSSetSampler(0, _gpu.LinearClampSampler);
                    ctx.Draw(3, 0);
                    ctx.OMSetRenderTargets(EmptyRtvs);
                    ctx.PSSetShaderResources(0, _psNullSrvs);
                }
                finally
                {
                    if (lockedBackdrop && backdrop != null)
                        Monitor.Exit(backdrop.SyncRoot);
                }

                // Pass 2: Direct2D 1.1 + DirectWrite Crisp Vector & Typography Overlay
                if (drawForeground2D != null && d2dTarget != null)
                {
                    _d2dCtx.Target = d2dTarget;
                    _d2dCtx.BeginDraw();
                    drawForeground2D(_d2dCtx);
                    _d2dCtx.EndDraw();
                    _d2dCtx.Target = null;
                }

                if (!_isOffscreen && _swapChain != null)
                {
                    _swapChain.Present(0, PresentFlags.None);
                    if (commitDComp)
                        _gpu.DCompDevice.Commit();
                }
            }
            finally
            {
                if (disposeTex)
                    targetTex?.Dispose();
            }
        }
    }

    public byte[] CaptureOffscreenBgra()
    {
        if (!_isOffscreen || _offscreenTex is null)
            throw new InvalidOperationException("CaptureOffscreenBgra requires an offscreen LiquidGlassSurface.");

        lock (_gpu.DeviceLock)
        {
            var desc = _offscreenTex.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = _gpu.D3DDevice.CreateTexture2D(desc);
            _gpu.D3DContext.CopyResource(staging, _offscreenTex);

            var mapped = _gpu.D3DContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte[] bytes = new byte[Width * Height * 4];
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    for (int y = 0; y < Height; y++)
                    {
                        Marshal.Copy((nint)(src + y * mapped.RowPitch), bytes, y * Width * 4, Width * 4);
                    }
                }
                return bytes;
            }
            finally
            {
                _gpu.D3DContext.Unmap(staging, 0);
            }
        }
    }

    private unsafe void UploadConstants(
        CrystalConfig config,
        Point windowOrigin,
        int desktopW,
        int desktopH,
        PointF mouseLocalPos,
        IReadOnlyList<GlassShapeItem> shapes,
        float timeSeconds,
        bool isDarkAppearance)
    {
        var g = config.Glass;
        // Scale quality parameters gently for Eco mode
        float qualityScale = config.Performance.Quality == QualityPreset.Eco ? 0.65f : 1.0f;

        var cb = new GlassConstantsGpu
        {
            WindowSize = new Vector2(Width, Height),
            WindowOrigin = new Vector2(windowOrigin.X, windowOrigin.Y),
            DesktopSize = new Vector2(desktopW, desktopH),
            MousePos = new Vector2(mouseLocalPos.X, mouseLocalPos.Y),

            Transparency = g.Transparency,
            RefractionIntensity = g.RefractionIntensity * qualityScale,
            DistortionStrength = g.DistortionStrength * qualityScale,
            Magnification = g.Magnification,

            BlurRadius = config.Performance.Quality == QualityPreset.Eco ? Math.Min(g.BlurRadius, 1.0f) : g.BlurRadius,
            ChromaticAberration = config.Performance.Quality == QualityPreset.Eco ? 0f : g.ChromaticAberration,
            TintOpacity = g.TintOpacity,
            ReflectionIntensity = g.ReflectionIntensity,

            EdgeLighting = g.EdgeLighting,
            Depth = g.Depth,
            TimeSeconds = timeSeconds,
            IsDarkAppearance = isDarkAppearance ? 1f : 0f,

            GlobalTintColor = GpuDeviceContext.ParseHexColor(g.GlassTintHex, 1f),
            ShapeCount = Math.Min(shapes.Count, 16),
            HighVisibilityEdges = config.Accessibility.HighVisibilityEdges ? 1f : 0f
        };

        for (int i = 0; i < cb.ShapeCount; i++)
        {
            var s = shapes[i];
            int offset = i * 12;
            cb.ShapesRaw[offset + 0] = s.X;
            cb.ShapesRaw[offset + 1] = s.Y;
            cb.ShapesRaw[offset + 2] = s.Width;
            cb.ShapesRaw[offset + 3] = s.Height;

            cb.ShapesRaw[offset + 4] = s.CornerRadius;
            cb.ShapesRaw[offset + 5] = s.SquirclePower;
            cb.ShapesRaw[offset + 6] = s.HoverGlow;
            cb.ShapesRaw[offset + 7] = s.BevelScale;

            cb.ShapesRaw[offset + 8] = s.TintColorAndOpacity.X;
            cb.ShapesRaw[offset + 9] = s.TintColorAndOpacity.Y;
            cb.ShapesRaw[offset + 10] = s.TintColorAndOpacity.Z;
            cb.ShapesRaw[offset + 11] = s.TintColorAndOpacity.W;
        }

        var mapped = _gpu.D3DContext.Map(_gpu.GlassConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            Marshal.StructureToPtr(cb, mapped.DataPointer, false);
        }
        finally
        {
            _gpu.D3DContext.Unmap(_gpu.GlassConstantBuffer, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gpu.DeviceLock)
        {
            ClearCachedTargets();
            UI.CrystalUIHelpers.ReleaseContextCache(_d2dCtx);
            _d2dCtx.Dispose();
            _offscreenRtv?.Dispose();
            _offscreenTex?.Dispose();
            _dcompVisual?.Dispose();
            _dcompTarget?.Dispose();
            _swapChain?.Dispose();
        }
    }
}
