using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed record CrystalMenuEntry(
    string Label,
    Action? OnClick = null,
    bool IsSeparator = false,
    string? ShortcutHint = null,
    bool Enabled = true
);

public sealed class ContextMenuWindow : GlassWindowBase
{
    private List<CrystalMenuEntry> _entries = new();
    private int _hoverIdx = -1;
    private SpringFloat _hoverHighlightY = new(0f);
    private SpringFloat _hoverHighlightAlpha = new(0f);
    private const int Pad = 10;
    private const int ItemH = 32;
    private const int SepH = 10;

    protected override PanelMotionPreset MotionPreset => PanelMotionPreset.MacContextMenu;

    public ContextMenuWindow(GpuDeviceContext gpu, ConfigManager configMgr, IDesktopBackdropSource? backdrop)
        : base(gpu, configMgr, backdrop, noActivate: false, topMost: true)
    {
        MouseMove += (_, e) =>
        {
            int idx = HitTestIndex(e.Y);
            if (idx != _hoverIdx) { _hoverIdx = idx; MarkDirty(); }
        };
        MouseLeave += (_, __) => { _hoverIdx = -1; MarkDirty(); };
        MouseClick += (_, e) =>
        {
            int idx = HitTestIndex(e.Y);
            if (idx >= 0 && idx < _entries.Count && !_entries[idx].IsSeparator && _entries[idx].Enabled)
            {
                var action = _entries[idx].OnClick;
                HideAnimated(-6f);
                action?.Invoke();
            }
        };
        Deactivate += (_, __) => HideAnimated(-6f);
    }

    public void ShowMenu(Point screenPos, IReadOnlyList<CrystalMenuEntry> entries, int minWidth = 240)
    {
        _entries = entries.ToList();
        _hoverIdx = -1;
        _hoverHighlightAlpha = new SpringFloat(0f);

        int totalH = Pad * 2;
        foreach (var e in _entries)
            totalH += e.IsSeparator ? SepH : ItemH;

        int w = minWidth;
        var screen = Screen.FromPoint(screenPos);
        int x = Math.Clamp(screenPos.X, screen.Bounds.Left + 8, screen.Bounds.Right - w - 8);
        int y = Math.Clamp(screenPos.Y, screen.Bounds.Top + 8, screen.Bounds.Bottom - totalH - 8);

        SetBounds(x, y, w, totalH);
        ShowAnimated(-8f);
        Activate();
    }

    private int HitTestIndex(int localY)
    {
        int y = Pad;
        for (int i = 0; i < _entries.Count; i++)
        {
            int h = _entries[i].IsSeparator ? SepH : ItemH;
            if (localY >= y && localY < y + h)
                return _entries[i].IsSeparator ? -1 : i;
            y += h;
        }
        return -1;
    }

    private float GetItemY(int idx)
    {
        int y = Pad;
        for (int i = 0; i < idx && i < _entries.Count; i++)
            y += _entries[i].IsSeparator ? SepH : ItemH;
        return y;
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        bool moving = false;
        bool instant = ShouldReduceMotion();
        if (_hoverIdx >= 0 && _hoverIdx < _entries.Count && _entries[_hoverIdx].Enabled)
        {
            float targetY = GetItemY(_hoverIdx);
            if (_hoverHighlightAlpha.Value < 0.05f) _hoverHighlightY.Value = targetY;
            _hoverHighlightY.Target = targetY;
            _hoverHighlightAlpha.Target = 1f;
        }
        else
        {
            _hoverHighlightAlpha.Target = 0f;
        }
        moving |= _hoverHighlightY.Step(dtSeconds, 560f, 34f, instant);
        moving |= _hoverHighlightAlpha.Step(dtSeconds, 520f, 32f, instant);
        return moving;
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        return new[]
        {
            new GlassShapeItem
            {
                X = 2f,
                Y = 2f,
                Width = Width - 4f,
                Height = Height - 4f,
                CornerRadius = 15f,
                SquirclePower = 3.2f,
                HoverGlow = IsMouseInside ? 0.16f : 0.04f,
                BevelScale = 0.80f
            }
        };
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);

        if (_hoverHighlightAlpha.Value > 0.02f)
        {
            float a = _hoverHighlightAlpha.Value;
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, 8f, _hoverHighlightY.Value + 2f, Width - 16f, ItemH - 4f, 8.5f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.30f * a),
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.14f * a));
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, 8f, _hoverHighlightY.Value + 2f, Width - 16f, ItemH - 4f, 8.5f,
                new Color4(1f, 1f, 1f, 0.36f * a), new Color4(1f, 1f, 1f, 0.08f * a), 0.85f);
            CrystalUIHelpers.DrawRoundedRect(d2d, 10f, _hoverHighlightY.Value + 7f, 2.6f, ItemH - 14f, 1.3f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.95f * a));
        }

        int y = Pad;
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.IsSeparator)
            {
                CrystalUIHelpers.DrawSolidLine(d2d, new System.Numerics.Vector2(14f, y + SepH * 0.5f), new System.Numerics.Vector2(Width - 14f, y + SepH * 0.5f), pal.Divider, 1f);
                y += SepH;
                continue;
            }

            Color4 textCol = entry.Enabled ? pal.PrimaryText : pal.Accent;
            float hintW = string.IsNullOrEmpty(entry.ShortcutHint) ? 0f : 86f;
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, entry.Label, 19f, y, Width - 38f - hintW, ItemH,
                entry.Enabled ? 12.5f : 10.5f, textCol, entry.Enabled ? FontWeight.Medium : FontWeight.Bold);

            if (!string.IsNullOrEmpty(entry.ShortcutHint))
            {
                float kw = Math.Clamp(entry.ShortcutHint.Length * 6.8f + 14f, 26f, 80f);
                CrystalUIHelpers.DrawLuxuryKeycap(d2d, Gpu.DWriteFactory, entry.ShortcutHint, Width - 16f - kw, y + (ItemH - 20f) * 0.5f, 20f, pal);
            }
            y += ItemH;
        }
    }
}
