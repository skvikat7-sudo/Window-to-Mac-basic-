using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.Interop;
using ProjectCrystal.Native.System;
using ProjectCrystal.Rendering.Core;
using Vortice.Direct2D1;

namespace ProjectCrystal.Rendering.UI;

internal sealed class HiddenShellOwnerForm : Form
{
    public HiddenShellOwnerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Size = new Size(1, 1);
        Location = new Point(-32000, -32000);
        Opacity = 0;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE;
            cp.ExStyle &= ~Win32Interop.WS_EX_APPWINDOW;
            return cp;
        }
    }
}

public abstract class GlassWindowBase : Form
{
    private static HiddenShellOwnerForm? _sharedShellOwner;

    private readonly GpuDeviceContext _gpu;
    private readonly ConfigManager _configMgr;
    private readonly IDesktopBackdropSource? _backdrop;
    private readonly bool _noActivate;
    private readonly bool _topMostWindow;
    private readonly bool _useShellOwner;
    private LiquidGlassSurface? _surface;

    private long _lastBackdropSeq = -1;
    private bool _needsRender = true;
    private PointF _mouseLocal = new(-999f, -999f);
    private DateTime _startUtc = DateTime.UtcNow;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private DateTime _lastBackdropRenderUtc = DateTime.MinValue;
    private bool _isMouseInside;

    protected AnimatedPanelMotion PanelMotion { get; } = new();
    protected GpuDeviceContext Gpu => _gpu;
    protected ConfigManager ConfigMgr => _configMgr;
    protected CrystalConfig Config => _configMgr.Current;
    protected PointF MouseLocal => _mouseLocal;
    protected bool IsMouseInside => _isMouseInside;

    protected override bool ShowWithoutActivation => _noActivate;

    protected GlassWindowBase(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        bool noActivate = true,
        bool topMost = true,
        bool useShellOwner = false)
    {
        _gpu = gpu;
        _configMgr = configMgr;
        _backdrop = backdrop;
        _noActivate = noActivate;
        _topMostWindow = topMost;
        _useShellOwner = useShellOwner;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = topMost;
        DoubleBuffered = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);

        if (_useShellOwner)
        {
            if (_sharedShellOwner == null || _sharedShellOwner.IsDisposed)
            {
                _sharedShellOwner = new HiddenShellOwnerForm();
                _ = _sharedShellOwner.Handle;
            }
            Owner = _sharedShellOwner;
        }
        PanelMotion.SnapOpen();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32Interop.WS_EX_NOREDIRECTIONBITMAP | Win32Interop.WS_EX_TOOLWINDOW;
            cp.ExStyle &= ~Win32Interop.WS_EX_APPWINDOW;
            if (_noActivate) cp.ExStyle |= Win32Interop.WS_EX_NOACTIVATE;
            if (_topMostWindow) cp.ExStyle |= Win32Interop.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int ex = Win32Interop.GetWindowLong(Handle, Win32Interop.GWL_EXSTYLE);
        ex |= Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOREDIRECTIONBITMAP;
        ex &= ~Win32Interop.WS_EX_APPWINDOW;
        Win32Interop.SetWindowLong(Handle, Win32Interop.GWL_EXSTYLE, ex);

        int excludePeek = 1;
        Win32Interop.DwmSetWindowAttribute(Handle, Win32Interop.DWMWA_EXCLUDED_FROM_PEEK, ref excludePeek, sizeof(int));

        ApplyCaptureExclusionAffinity();
        _surface = new LiquidGlassSurface(_gpu, Handle, Math.Max(16, Width), Math.Max(16, Height));
        MarkDirty();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _surface?.Resize(Math.Max(16, Width), Math.Max(16, Height));
        MarkDirty();
    }

    public void ShowAnimated(float startSlideY = -12f)
    {
        if (!IsHandleCreated)
            _ = Handle;

        if (ShouldReduceMotion())
            PanelMotion.SnapOpen();
        else
            PanelMotion.BeginOpen(startSlideY);

        _needsRender = true;
        // Render initial frame BEFORE showing HWND so SwapChain/DComp never flashes stale or blank frames
        RenderFrameImmediate(commitDComp: true);

        if (!Visible) Show();
        if (!_noActivate) Activate();
        MarkDirty();
    }

    private void RenderFrameImmediate(bool commitDComp)
    {
        if (_surface is null || IsDisposed) return;
        try
        {
            var rawShapes = BuildGlassShapes();
            var shapes = ApplyPanelMotionToShapes(rawShapes);
            var now = DateTime.UtcNow;
            float timeSec = (float)(now - _startUtc).TotalSeconds;
            _lastBackdropSeq = _backdrop?.FrameSequence ?? 0;
            _lastBackdropRenderUtc = now;
            _needsRender = false;

            _surface.RenderFrame(
                _backdrop,
                Config,
                new Point(Left, Top),
                _mouseLocal,
                shapes,
                timeSec,
                IsDarkModeActive(),
                d2d => DrawForegroundWithMotion(d2d),
                commitDComp);
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Render", $"{GetType().Name} immediate reveal skipped: {ex.Message}");
        }
    }

    private void DrawForegroundWithMotion(ID2D1DeviceContext d2d)
    {
        float op = PanelMotion.Opacity;
        if (op <= 0.02f) return;

        bool pushedLayer = false;
        if (op < 0.99f)
        {
            CrystalUIHelpers.PushOpacityLayer(d2d, op);
            pushedLayer = true;
        }

        try
        {
            float sc = PanelMotion.Scale;
            float oy = PanelMotion.OffsetY;
            if (MathF.Abs(sc - 1f) > 0.001f || MathF.Abs(oy) > 0.1f)
            {
                var center = new Vector2(Width * 0.5f, Height * 0.5f);
                d2d.Transform = Matrix3x2.CreateScale(sc, sc, center) * Matrix3x2.CreateTranslation(0f, oy);
            }
            DrawForeground2D(d2d);
            d2d.Transform = Matrix3x2.Identity;
        }
        finally
        {
            if (pushedLayer)
                d2d.PopLayer();
        }
    }

    public void HideAnimated(float endSlideY = -10f)
    {
        if (!Visible) return;
        if (ShouldReduceMotion())
        {
            Hide();
            return;
        }
        PanelMotion.BeginClose(Hide, endSlideY);
        MarkDirty();
    }

    public void ApplyCaptureExclusionAffinity()
    {
        if (!IsHandleCreated) return;
        uint affinity = Config.Glass.CaptureBackend == CaptureBackendMode.LiveDesktopWgc
            ? Win32Interop.WDA_EXCLUDEFROMCAPTURE
            : Win32Interop.WDA_NONE;
        Win32Interop.SetWindowDisplayAffinity(Handle, affinity);
    }

    public void MarkDirty() => _needsRender = true;

    public bool IsDarkModeActive()
    {
        return Config.Appearance.Theme switch
        {
            ThemeAppearance.Dark => true,
            ThemeAppearance.Light => false,
            _ => DisplayBrightnessService.IsWindowsDarkMode()
        };
    }

    public bool ShouldReduceMotion()
    {
        return Config.Accessibility.MotionMode switch
        {
            ReducedMotionMode.AlwaysReduce => true,
            ReducedMotionMode.AlwaysFull => false,
            _ => Win32Interop.IsReducedMotionEnabledByWindows()
        };
    }

    public bool TickAndRenderIfNeeded(bool commitDComp = true)
    {
        if (!Visible || _surface is null || IsDisposed) return false;

        var now = DateTime.UtcNow;
        float dt = Math.Clamp((float)(now - _lastTickUtc).TotalSeconds, 0.001f, 0.05f);
        _lastTickUtc = now;

        if (Win32Interop.GetCursorPos(out var pt))
        {
            PointF local = new(pt.X - Left, pt.Y - Top);
            bool inside = local.X >= 0 && local.Y >= 0 && local.X <= Width && local.Y <= Height;
            if (inside != _isMouseInside || (inside && (Math.Abs(local.X - _mouseLocal.X) > 0.9f || Math.Abs(local.Y - _mouseLocal.Y) > 0.9f)))
            {
                _isMouseInside = inside;
                _mouseLocal = local;
                _needsRender = true;
            }
        }

        bool animActive = PanelMotion.Step(dt, Config.Glass.AnimationSpeed, ShouldReduceMotion());
        animActive |= StepAnimations(dt);
        long seq = _backdrop?.FrameSequence ?? 0;
        bool backdropChanged = seq != _lastBackdropSeq;

        if (backdropChanged && !animActive && !_needsRender)
        {
            // Throttle pure passive backdrop updates so idle widgets/bars do not burn 60-96 FPS on integrated GPUs
            double minMs = _isMouseInside ? 28.0 : (Config.Performance.Quality == QualityPreset.Eco ? 66.0 : 42.0);
            if ((now - _lastBackdropRenderUtc).TotalMilliseconds < minMs)
                backdropChanged = false;
        }

        if (!animActive && !backdropChanged && !_needsRender && Config.Performance.PauseWhenIdle)
            return false;

        _lastBackdropSeq = seq;
        _needsRender = false;
        _lastBackdropRenderUtc = now;

        try
        {
            var rawShapes = BuildGlassShapes();
            var shapes = ApplyPanelMotionToShapes(rawShapes);
            float timeSec = (float)(now - _startUtc).TotalSeconds;
            _surface.RenderFrame(
                _backdrop,
                Config,
                new Point(Left, Top),
                _mouseLocal,
                shapes,
                timeSec,
                IsDarkModeActive(),
                DrawForegroundWithMotion,
                commitDComp);
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Render", $"{GetType().Name} frame skipped: {ex.Message}");
            return false;
        }
    }

    private IReadOnlyList<GlassShapeItem> ApplyPanelMotionToShapes(IReadOnlyList<GlassShapeItem> source)
    {
        float op = PanelMotion.Opacity;
        float sc = PanelMotion.Scale;
        float oy = PanelMotion.OffsetY;
        if (op >= 0.999f && MathF.Abs(sc - 1f) < 0.001f && MathF.Abs(oy) < 0.1f)
            return source;

        float cx = Width * 0.5f;
        float cy = Height * 0.5f;
        var transformed = new List<GlassShapeItem>(source.Count);
        foreach (var s in source)
        {
            float nx = cx + (s.X - cx) * sc;
            float ny = cy + (s.Y - cy) * sc + oy;
            transformed.Add(new GlassShapeItem
            {
                X = nx,
                Y = ny,
                Width = s.Width * sc,
                Height = s.Height * sc,
                CornerRadius = s.CornerRadius * sc,
                SquirclePower = s.SquirclePower,
                HoverGlow = s.HoverGlow * op,
                BevelScale = s.BevelScale,
                TintColorAndOpacity = new Vector4(s.TintColorAndOpacity.X, s.TintColorAndOpacity.Y, s.TintColorAndOpacity.Z, s.TintColorAndOpacity.W * op)
            });
        }
        return transformed;
    }

    protected abstract IReadOnlyList<GlassShapeItem> BuildGlassShapes();
    protected abstract void DrawForeground2D(ID2D1DeviceContext d2d);
    protected virtual bool StepAnimations(float dtSeconds) => false;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32Interop.WM_NCHITTEST && HitTestTransparent(PointToClient(Cursor.Position)))
        {
            m.Result = (nint)Win32Interop.HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected virtual bool HitTestTransparent(Point localPoint) => false;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surface?.Dispose();
            _surface = null;
        }
        base.Dispose(disposing);
    }
}
