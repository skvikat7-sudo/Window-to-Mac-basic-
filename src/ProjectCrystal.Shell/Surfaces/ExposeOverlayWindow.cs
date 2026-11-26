using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.Interop;
using ProjectCrystal.Native.WindowManagement;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed class ExposeOverlayWindow : GlassWindowBase
{
    private readonly WindowManagerService _windowMgr;
    private readonly IconTextureRegistry _icons;
    private readonly List<WindowInfo> _windows = new();
    private readonly List<nint> _dwmThumbnails = new();
    private readonly List<(WindowInfo Win, RectangleF CardRect, RectangleF CloseRect)> _cards = new();
    private readonly List<(RectangleF Rect, Action OnClick)> _headerButtons = new();
    private int _hoverIdx = -1;

    protected override PanelMotionPreset MotionPreset => PanelMotionPreset.MacMissionControl;

    public event Action<nint>? CloseWindowRequested;
    public event Action<nint>? RestoreWindowRequested;

    public ExposeOverlayWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        WindowManagerService windowMgr,
        IconTextureRegistry icons)
        : base(gpu, configMgr, backdrop, noActivate: false, topMost: true)
    {
        _windowMgr = windowMgr;
        _icons = icons;
        KeyPreview = true;

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) CloseOverlay(); };
        Deactivate += (_, __) => CloseOverlay();
        MouseMove += (_, e) =>
        {
            int idx = _cards.FindIndex(c => c.CardRect.Contains(e.X, e.Y));
            if (idx != _hoverIdx) { _hoverIdx = idx; MarkDirty(); }
        };
        MouseClick += OnExposeMouseClick;
    }

    public void ToggleForMonitor(Rectangle monitorBounds)
    {
        if (Visible && PanelMotion.IsOpenOrOpening) { CloseOverlay(); return; }

        SetBounds(monitorBounds.X, monitorBounds.Y, monitorBounds.Width, monitorBounds.Height);
        RefreshWindowCards();
        ShowAnimated(0f);
        RegisterThumbnails();
    }

    private void RefreshWindowCards()
    {
        UnregisterThumbnails();
        _windows.Clear();
        _windows.AddRange(_windowMgr.GetOpenWindows().Take(12));
        foreach (var w in _windows)
        {
            if (!string.IsNullOrEmpty(w.ExecutablePath))
                _icons.PreloadIcon(w.ExecutablePath);
        }
    }

    private void RegisterThumbnails()
    {
        UnregisterThumbnails();
        if (!IsHandleCreated || _cards.Count == 0)
            ComputeCardLayout();

        foreach (var card in _cards)
        {
            if (card.Win.IsMinimized) continue;
            if (Win32Interop.DwmRegisterThumbnail(Handle, card.Win.Handle, out nint thumbId) == 0 && thumbId != 0)
            {
                var props = new Win32Interop.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Win32Interop.DWM_TNP_RECTDESTINATION | Win32Interop.DWM_TNP_VISIBLE | Win32Interop.DWM_TNP_OPACITY | Win32Interop.DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new Win32Interop.RECT
                    {
                        Left = (int)card.CardRect.X + 12,
                        Top = (int)card.CardRect.Y + 42,
                        Right = (int)card.CardRect.Right - 12,
                        Bottom = (int)card.CardRect.Bottom - 12
                    },
                    opacity = 248,
                    fVisible = true,
                    fSourceClientAreaOnly = false
                };
                Win32Interop.DwmUpdateThumbnailProperties(thumbId, ref props);
                _dwmThumbnails.Add(thumbId);
            }
        }
    }

    private void UnregisterThumbnails()
    {
        foreach (nint t in _dwmThumbnails)
        {
            try { Win32Interop.DwmUnregisterThumbnail(t); } catch { }
        }
        _dwmThumbnails.Clear();
    }

    private void CloseOverlay()
    {
        UnregisterThumbnails();
        HideAnimated(0f);
    }

    private void ComputeCardLayout()
    {
        _cards.Clear();
        int count = _windows.Count;
        if (count == 0) return;

        int cols = count <= 2 ? Math.Max(1, count) : (count <= 6 ? 3 : 4);
        int rows = (int)Math.Ceiling(count / (double)cols);

        float padX = 56f;
        float topY = 108f;
        float gap = 22f;
        float availW = Width - padX * 2f - gap * (cols - 1);
        float availH = Height - topY - 92f - gap * (rows - 1);
        float cardW = Math.Min(432f, availW / cols);
        float cardH = Math.Min(296f, availH / Math.Max(1, rows));

        float totalGridW = cols * cardW + (cols - 1) * gap;
        float startX = (Width - totalGridW) * 0.5f;

        for (int i = 0; i < count; i++)
        {
            int c = i % cols;
            int r = i / cols;
            var rect = new RectangleF(startX + c * (cardW + gap), topY + r * (cardH + gap), cardW, cardH);
            var closeRect = new RectangleF(rect.Right - 30, rect.Y + 10, 20, 20);
            _cards.Add((_windows[i], rect, closeRect));
        }
    }

    protected override bool StepAnimations(float dtSeconds) => false;

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        ComputeCardLayout();
        var shapes = new List<GlassShapeItem>(3)
        {
            new() { X = 0, Y = 0, Width = Width, Height = Height, CornerRadius = 0, SquirclePower = 2.5f, HoverGlow = 0f, BevelScale = 0.20f, TintColorAndOpacity = new System.Numerics.Vector4(0.03f, 0.05f, 0.09f, 0.96f) },
            new() { X = (Width - 616f) * 0.5f, Y = 22, Width = 616, Height = 54, CornerRadius = 22, SquirclePower = 3.2f, HoverGlow = 0.18f, BevelScale = 1.0f }
        };

        if (_hoverIdx >= 0 && _hoverIdx < _cards.Count)
        {
            var c = _cards[_hoverIdx];
            shapes.Add(new GlassShapeItem
            {
                X = c.CardRect.X,
                Y = c.CardRect.Y,
                Width = c.CardRect.Width,
                Height = c.CardRect.Height,
                CornerRadius = 18,
                SquirclePower = 3.2f,
                HoverGlow = 0.48f,
                BevelScale = 1.15f
            });
        }
        return shapes;
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        _headerButtons.Clear();
        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);

        // Top Stage Control Island
        float barW = 616f;
        float barX = (Width - barW) * 0.5f;
        CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, barX, 22, barW, 54, 22, new Color4(1f, 1f, 1f, 0.45f), new Color4(1f, 1f, 1f, 0.12f), 1.0f);

        CrystalUIHelpers.DrawLuxuryExposeIcon(d2d, barX + 14f, 33f, 32f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Exposé Stage", barX + 54f, 24f, 124f, 28f, 14f, pal.PrimaryText, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, $"{_windows.Count} Open Windows", barX + 54f, 45f, 140f, 20f, 10f, pal.Accent, FontWeight.SemiBold);

        var prevSpace = new RectangleF(barX + 206, 33, 94, 32);
        var nextSpace = new RectangleF(barX + 306, 33, 94, 32);
        var deskBtn = new RectangleF(barX + 406, 33, 108, 32);
        var closeBtn = new RectangleF(barX + 522, 33, 78, 32);

        CrystalUIHelpers.DrawHairlineCard(d2d, prevSpace.X, prevSpace.Y, prevSpace.Width, prevSpace.Height, 10, pal);
        CrystalUIHelpers.DrawHairlineCard(d2d, nextSpace.X, nextSpace.Y, nextSpace.Width, nextSpace.Height, 10, pal);
        CrystalUIHelpers.DrawHairlineCard(d2d, deskBtn.X, deskBtn.Y, deskBtn.Width, deskBtn.Height, 10, pal);
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, closeBtn.X, closeBtn.Y, closeBtn.Width, closeBtn.Height, 10,
            pal.Accent, new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 0.96f));

        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "‹ Prev Space", prevSpace.X, prevSpace.Y, prevSpace.Width, prevSpace.Height, 11f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Next Space ›", nextSpace.X, nextSpace.Y, nextSpace.Width, nextSpace.Height, 11f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Show Desktop", deskBtn.X, deskBtn.Y, deskBtn.Width, deskBtn.Height, 11f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Done", closeBtn.X, closeBtn.Y, closeBtn.Width, closeBtn.Height, 11.5f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center);

        _headerButtons.Add((prevSpace, () => { CloseOverlay(); _windowMgr.SwitchVirtualDesktop(false); }));
        _headerButtons.Add((nextSpace, () => { CloseOverlay(); _windowMgr.SwitchVirtualDesktop(true); }));
        _headerButtons.Add((deskBtn, () => { CloseOverlay(); _windowMgr.ToggleShowDesktop(); }));
        _headerButtons.Add((closeBtn, CloseOverlay));

        if (_cards.Count == 0)
        {
            CrystalUIHelpers.DrawHairlineCard(d2d, (Width - 420f) * 0.5f, Height * 0.42f, 420f, 84f, 18f, pal);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "All windows minimized — clean Crystal Desktop", 0, Height * 0.42f, Width, 84f, 15.5f, pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Center);
            return;
        }

        for (int i = 0; i < _cards.Count; i++)
        {
            var (win, card, closeR) = _cards[i];
            bool hov = i == _hoverIdx;

            if (hov)
            {
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, card.X + card.Width * 0.5f, card.Y + card.Height * 0.5f,
                    card.Width * 0.58f, card.Height * 0.58f,
                    new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.24f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
                CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, card.X, card.Y, card.Width, card.Height, 18, pal.Accent, new Color4(1f, 1f, 1f, 0.45f), 1.8f);
            }
            else
            {
                CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, card.X, card.Y, card.Width, card.Height, 18,
                    new Color4(1f, 1f, 1f, 0.28f), new Color4(1f, 1f, 1f, 0.08f), 0.9f);
            }

            // Frosted card title chrome strip
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, card.X + 2f, card.Y + 2f, card.Width - 4f, 34f, 15f,
                new Color4(1f, 1f, 1f, hov ? 0.14f : 0.08f), new Color4(1f, 1f, 1f, 0.02f));

            var iconObj = _icons.GetIcon(d2d, win.ExecutablePath, win.Handle, win.ProcessName);
            if (iconObj != null)
                CrystalUIHelpers.DrawBitmapRect(d2d, iconObj.Bitmap, card.X + 12, card.Y + 8, 24, 24, 1f, iconObj.HasOpaqueCorners);

            float badgeReserve = win.IsMinimized ? 116f : 56f;
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, $"{win.ProcessName}  •  {win.Title}", card.X + 42, card.Y + 8, card.Width - 44 - badgeReserve, 24, 12f, pal.PrimaryText, FontWeight.SemiBold);

            if (win.IsMinimized)
            {
                CrystalUIHelpers.DrawRoundedRect(d2d, closeR.X - 76, card.Y + 10, 68, 20, 10, pal.AccentSoft);
                CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "MINIMIZED", closeR.X - 76, card.Y + 10, 68, 20, 8.8f, pal.Accent, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
            }

            // Close jewel orb
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, closeR.X + 10f, closeR.Y + 10f, 10f, 10f,
                new Color4(1f, 0.44f, 0.44f, hov ? 0.95f : 0.56f), new Color4(0.84f, 0.18f, 0.24f, hov ? 0.95f : 0.50f), -2f, -2f);
            CrystalUIHelpers.DrawCloseXIcon(d2d, closeR.X + 10f, closeR.Y + 10f, 9.5f, new Color4(1f, 1f, 1f, 0.98f));

            if (win.IsMinimized)
            {
                // Sculpted Frosted Window Preview Artwork for Minimized Apps (never a lonely empty icon!)
                float artX = card.X + 20f, artY = card.Y + 46f, artW = card.Width - 40f, artH = card.Height - 62f;
                CrystalUIHelpers.DrawHairlineCard(d2d, artX, artY, artW, artH, 14f, pal, hov);
                float cx = artX + artW * 0.5f;
                float cy = artY + artH * 0.46f;
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, cx, cy, 48f, 48f,
                    new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
                if (iconObj != null)
                    CrystalUIHelpers.DrawBitmapRect(d2d, iconObj.Bitmap, cx - 28f, cy - 28f, 56f, 56f, 0.96f, iconObj.HasOpaqueCorners);
                CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Click Card to Restore Window", artX + 12f, artY + artH - 30f, artW - 24f, 20f, 10.8f, pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Center);
            }
        }
    }

    private void OnExposeMouseClick(object? sender, MouseEventArgs e)
    {
        foreach (var hb in _headerButtons)
        {
            if (hb.Rect.Contains(e.X, e.Y)) { hb.OnClick(); return; }
        }

        foreach (var (win, card, closeR) in _cards)
        {
            if (closeR.Contains(e.X, e.Y))
            {
                if (CloseWindowRequested != null) CloseWindowRequested(win.Handle);
                else _windowMgr.CloseWindow(win.Handle);
                RefreshWindowCards();
                RegisterThumbnails();
                MarkDirty();
                return;
            }
            if (card.Contains(e.X, e.Y))
            {
                CloseOverlay();
                if (win.IsMinimized && RestoreWindowRequested != null) RestoreWindowRequested(win.Handle);
                else _windowMgr.FocusWindow(win.Handle);
                return;
            }
        }
        CloseOverlay();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) UnregisterThumbnails();
        base.Dispose(disposing);
    }
}
