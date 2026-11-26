using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.System;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed class DeskMinderTask
{
    public string Id { get; init; } = "";
    public string Title { get; set; } = "";
    public string TimeTag { get; set; } = "";
    public bool Completed { get; set; }
}

public sealed class NotificationCenterWindow : GlassWindowBase
{
    private readonly NotificationService _notifSvc;
    private readonly List<(string Id, RectangleF Rect, Action OnClick)> _clickZones = new();
    private string _hoverId = "";

    private SpringFloat _dndSwitchSpring;
    private float _focusDurationSec = 15f * 60f;
    private float _focusRemainingSec = 15f * 60f;
    private float _focusTickAccum;
    private bool _focusRunning;
    private readonly List<DeskMinderTask> _tasks = new()
    {
        new() { Id = "t1", Title = "Deep focus sprint", TimeTag = "25m", Completed = false },
        new() { Id = "t2", Title = "Check Glass Lab shaders", TimeTag = "Today", Completed = true },
        new() { Id = "t3", Title = "Review window spaces", TimeTag = "15m", Completed = false }
    };

    public NotificationCenterWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        NotificationService notifSvc)
        : base(gpu, configMgr, backdrop, noActivate: false, topMost: true)
    {
        _notifSvc = notifSvc;
        _dndSwitchSpring = new SpringFloat(Config.Notifications.DoNotDisturb ? 1f : 0f);
        _notifSvc.NotificationsChanged += () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(MarkDirty); };
        SetBounds(0, 0, 384, 650);

        Deactivate += (_, __) => HideAnimated(-10f);
        MouseMove += (_, e) =>
        {
            string hit = _clickZones.FirstOrDefault(z => z.Rect.Contains(e.X, e.Y)).Id ?? "";
            if (hit != _hoverId) { _hoverId = hit; MarkDirty(); }
        };
        MouseLeave += (_, __) => { _hoverId = ""; MarkDirty(); };
        MouseClick += (_, e) =>
        {
            foreach (var z in _clickZones)
            {
                if (z.Rect.Contains(e.X, e.Y)) { z.OnClick(); break; }
            }
        };
    }

    public void ToggleAt(Rectangle monitorBounds, int menuBarHeight)
    {
        if (Visible && PanelMotion.IsOpenOrOpening) { HideAnimated(-10f); return; }
        int h = Math.Min(656, monitorBounds.Height - menuBarHeight - 64);
        int x = monitorBounds.Right - Width - 12;
        int y = monitorBounds.Y + menuBarHeight + 10;
        SetBounds(x, y, Width, h);
        _ = _notifSvc.RefreshAsync();
        ShowAnimated(-12f);
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        bool moving = false;
        bool instant = ShouldReduceMotion();
        _dndSwitchSpring.Target = Config.Notifications.DoNotDisturb ? 1f : 0f;
        moving |= _dndSwitchSpring.Step(dtSeconds, 520f, 30f, instant);

        if (_focusRunning && Visible)
        {
            _focusRemainingSec = Math.Max(0f, _focusRemainingSec - dtSeconds);
            if (_focusRemainingSec <= 0.01f)
            {
                _focusRunning = false;
                _focusRemainingSec = _focusDurationSec;
                moving = true;
            }
            else
            {
                _focusTickAccum += dtSeconds;
                if (_focusTickAccum >= 0.25f)
                {
                    _focusTickAccum = 0f;
                    moving = true;
                }
            }
        }
        return moving;
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        return new[]
        {
            new GlassShapeItem { X = 4, Y = 4, Width = Width - 8, Height = Height - 8, CornerRadius = 22, SquirclePower = 3.2f, HoverGlow = IsMouseInside ? 0.14f : 0.04f, BevelScale = 1.0f }
        };
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        RenderNotificationCenterContent(
            d2d, Gpu.DWriteFactory, Width, Height, IsDarkModeActive(), Config,
            _notifSvc.GetNotifications(Config.Notifications.MaxHistoryCount),
            _notifSvc.IsSupportedAndAllowed, _notifSvc.StatusNote,
            _dndSwitchSpring.Value, _dndSwitchSpring.Velocity,
            _focusDurationSec, _focusRemainingSec, _focusRunning,
            _tasks, _hoverId, _clickZones,
            onToggleDnd: () => { ConfigMgr.Update(c => c.Notifications.DoNotDisturb = !c.Notifications.DoNotDisturb); MarkDirty(); },
            onClearNotifs: () => { _notifSvc.ClearAll(); MarkDirty(); },
            onDismissNotif: pid => { _notifSvc.Dismiss(pid); MarkDirty(); },
            onToggleTimer: () => { _focusRunning = !_focusRunning; MarkDirty(); },
            onSelectPresetMins: mins => { _focusDurationSec = mins * 60f; _focusRemainingSec = _focusDurationSec; _focusRunning = true; MarkDirty(); },
            onToggleTask: task => { task.Completed = !task.Completed; MarkDirty(); });
    }

    public static void RenderNotificationCenterContent(
        ID2D1DeviceContext d2d,
        IDWriteFactory dw,
        int width, int height,
        bool isDark,
        CrystalConfig cfg,
        IReadOnlyList<NotificationItem> items,
        bool notifSupported,
        string notifNote,
        float dndVal, float dndVel,
        float focusDurSec, float focusRemSec, bool focusRunning,
        IReadOnlyList<DeskMinderTask> tasks,
        string hoverId,
        List<(string Id, RectangleF Rect, Action OnClick)> clickZones,
        Action? onToggleDnd = null,
        Action? onClearNotifs = null,
        Action<uint>? onDismissNotif = null,
        Action? onToggleTimer = null,
        Action<int>? onSelectPresetMins = null,
        Action<DeskMinderTask>? onToggleTask = null)
    {
        clickZones.Clear();
        var pal = CrystalUIHelpers.GetPalette(isDark, cfg.Appearance.AccentColorHex);

        // 1. Editorial Hero Date & Mini-Calendar Card
        CrystalUIHelpers.DrawHairlineCard(d2d, 18, 16, width - 36, 158, 16, pal);
        var now = DateTime.Now;
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, 28, 24, 46, 46, 12,
            pal.Accent, new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 0.96f));
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("dd"), 28, 24, 46, 46, 21f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("dddd").ToUpperInvariant(), 84, 27, 180, 14, 9.5f, pal.Accent, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("MMMM yyyy"), 84, 42, 220, 22, 15f, pal.PrimaryText, FontWeight.SemiBold);
        DrawMiniCalendar(d2d, dw, pal, 28, 76, width - 56);

        // 2. DeskMinder — Reminders, Quick Focus Timer & Checklists (inspired by deskminder-reminders-tasks + liquid-glass-switch)
        float dmY = 184f;
        float dmH = 192f;
        CrystalUIHelpers.DrawHairlineCard(d2d, 18, dmY, width - 36, dmH, 16, pal);
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, 64f, dmY + 34f, 44f, 36f,
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, focusRunning ? 0.24f : 0.12f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "DESKMINDER FOCUS", 30, dmY + 10, 150, 14, 9.2f, pal.Accent, FontWeight.Bold);

        // Timer Preset Pills (5m / 15m / 25m)
        int[] presets = { 5, 15, 25 };
        float px = width - 144f;
        foreach (int mins in presets)
        {
            var pr = new RectangleF(px, dmY + 8, 36, 20);
            bool sel = Math.Abs(focusDurSec - mins * 60f) < 1f;
            CrystalUIHelpers.DrawRoundedRect(d2d, pr.X, pr.Y, pr.Width, pr.Height, 10f,
                sel ? pal.Accent : pal.ControlSurface, sel ? null : pal.HairlineStroke, 0.7f);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"{mins}m", pr.X, pr.Y, pr.Width, pr.Height, 9.5f,
                sel ? new Color4(1f, 1f, 1f, 1f) : pal.SecondaryText, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
            if (onSelectPresetMins != null)
            {
                int captured = mins;
                clickZones.Add(($"dmPreset:{mins}", pr, () => onSelectPresetMins(captured)));
            }
            px += 40f;
        }

        // Countdown readout + Circular Play/Pause Crystal Orb
        var remTs = TimeSpan.FromSeconds(Math.Ceiling(focusRemSec));
        float pct = focusDurSec > 0 ? Math.Clamp(focusRemSec / focusDurSec, 0f, 1f) : 0f;
        CrystalUIHelpers.DrawAnalogGaugeArc(d2d, dw, 54f, dmY + 56f, 22f, pct, pal.Accent, new Color4(0.32f, 0.96f, 0.76f, 1f),
            $"{(int)remTs.TotalMinutes:00}", $"{remTs.Seconds:00}s", pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, focusRunning ? "Focus Timer Active" : "Ready for Reminder Sprint", 88f, dmY + 38f, 190f, 18f, 12.2f, pal.PrimaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"{tasks.Count(t => !t.Completed)} pending tasks • Quick floating reminder", 88f, dmY + 57f, 200f, 15f, 10f, pal.SecondaryText);

        var timerOrbR = new RectangleF(width - 60f, dmY + 39f, 30f, 30f);
        CrystalUIHelpers.DrawGlassOrbButton(d2d, timerOrbR.X + 15f, timerOrbR.Y + 15f, 15f, pal.Accent, true, hoverId == "dmTimer");
        if (focusRunning)
            CrystalUIHelpers.DrawPauseIcon(d2d, timerOrbR.X + 15f, timerOrbR.Y + 15f, 13f, new Color4(1f, 1f, 1f, 1f));
        else
            CrystalUIHelpers.DrawPlayIcon(d2d, timerOrbR.X + 15f, timerOrbR.Y + 15f, 13f, new Color4(1f, 1f, 1f, 1f));
        if (onToggleTimer != null)
            clickZones.Add(("dmTimer", timerOrbR, onToggleTimer));

        // DeskMinder Checklist Task Pills
        float ty = dmY + 90f;
        foreach (var task in tasks)
        {
            var tr = new RectangleF(28, ty, width - 56, 28);
            string id = $"dmTask:{task.Id}";
            CrystalUIHelpers.DrawDeskMinderTaskRow(d2d, dw, tr.X, tr.Y, tr.Width, tr.Height,
                task.Title, task.TimeTag, task.Completed, hoverId == id, pal);
            if (onToggleTask != null)
            {
                var captured = task;
                clickZones.Add((id, tr, () => onToggleTask(captured)));
            }
            ty += 33f;
        }

        // 3. Notifications Header + Viscous Liquid-Glass Silent Switch
        float ny = dmY + dmH + 12f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"Alerts ({items.Count})", 20, ny, 120, 26, 13f, pal.PrimaryText, FontWeight.SemiBold);

        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Silent", width - 196, ny + 1, 44, 24, 10.5f, cfg.Notifications.DoNotDisturb ? pal.Accent : pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Trailing);
        var dndBtn = new RectangleF(width - 146, ny + 1, 46, 24);
        CrystalUIHelpers.DrawGlassToggle(d2d, dndBtn.X, dndBtn.Y, dndVal, pal, 46f, 24f, dndVel);
        if (onToggleDnd != null) clickZones.Add(("dnd", dndBtn, onToggleDnd));

        var clearBtn = new RectangleF(width - 88, ny + 1, 70, 24);
        CrystalUIHelpers.DrawRoundedRect(d2d, clearBtn.X, clearBtn.Y, clearBtn.Width, clearBtn.Height, 12, hoverId == "clear" ? pal.ControlHover : pal.ControlSurface, pal.HairlineStroke, 0.8f);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Clear All", clearBtn.X, clearBtn.Y, clearBtn.Width, clearBtn.Height, 10f, pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Center);
        if (onClearNotifs != null) clickZones.Add(("clear", clearBtn, onClearNotifs));

        ny += 34f;
        if (items.Count == 0)
        {
            float emptyH = Math.Max(78f, height - ny - 18f);
            CrystalUIHelpers.DrawHairlineCard(d2d, 18, ny + 4, width - 36, emptyH, 15, pal);
            CrystalUIHelpers.DrawBellIcon(d2d, width * 0.5f, ny + emptyH * 0.34f, 20, pal.Accent);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, notifSupported
                ? "All caught up — no pending notifications"
                : notifNote, 32, ny + emptyH * 0.52f, width - 64, 42, 11.2f, pal.SecondaryText, FontWeight.Regular, TextAlignment.Center, ParagraphAlignment.Center, true, true);
            return;
        }

        IEnumerable<NotificationItem> ordered = cfg.Notifications.GroupByApp
            ? items.OrderBy(i => i.AppName).ThenByDescending(i => i.Timestamp)
            : items;

        string? lastApp = null;
        foreach (var n in ordered)
        {
            if (ny > height - 66) break;

            if (cfg.Notifications.GroupByApp && n.AppName != lastApp)
            {
                lastApp = n.AppName;
                CrystalUIHelpers.DrawTextCrisp(d2d, dw, n.AppName.ToUpperInvariant(), 22, ny, 220, 15, 9.2f, pal.SubtleText, FontWeight.Bold);
                ny += 17f;
            }

            var cardR = new RectangleF(18, ny, width - 36, 56);
            string dismissId = $"dismiss:{n.PlatformId}";
            CrystalUIHelpers.DrawHairlineCard(d2d, cardR.X, cardR.Y, cardR.Width, cardR.Height, 13, pal, hoverId == dismissId);
            CrystalUIHelpers.DrawRoundedRect(d2d, cardR.X + 6, cardR.Y + 11, 3, cardR.Height - 22, 1.5f, pal.Accent);
            string mono = string.IsNullOrEmpty(n.AppName) ? "N" : n.AppName[..1].ToUpperInvariant();
            CrystalUIHelpers.DrawRoundedRect(d2d, cardR.X + 15, cardR.Y + 14, 26, 26, 8, pal.AccentSoft);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, mono, cardR.X + 15, cardR.Y + 14, 26, 26, 12f, pal.Accent, FontWeight.Bold, TextAlignment.Center);

            CrystalUIHelpers.DrawTextCrisp(d2d, dw, n.Title, cardR.X + 48, cardR.Y + 6, cardR.Width - 112, 18, 11.8f, pal.PrimaryText, FontWeight.SemiBold);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, n.Timestamp.LocalDateTime.ToString("HH:mm"), cardR.Right - 66, cardR.Y + 7, 36, 16, 10f, pal.SubtleText, FontWeight.Regular, TextAlignment.Trailing);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, string.IsNullOrEmpty(n.Body) ? n.AppName : n.Body, cardR.X + 48, cardR.Y + 25, cardR.Width - 72, 24, 10.8f, pal.SecondaryText, FontWeight.Regular, TextAlignment.Leading, ParagraphAlignment.Near, true, true);

            var closeBtn = new RectangleF(cardR.Right - 24, cardR.Y + 7, 18, 18);
            CrystalUIHelpers.DrawCloseXIcon(d2d, closeBtn.X + 9, closeBtn.Y + 9, 10, pal.SecondaryText);
            if (onDismissNotif != null)
            {
                uint pid = n.PlatformId;
                clickZones.Add((dismissId, closeBtn, () => onDismissNotif(pid)));
            }

            ny += 62f;
        }
    }

    private static void DrawMiniCalendar(ID2D1DeviceContext d2d, IDWriteFactory dw, UiPalette pal, float x, float y, float w)
    {
        var now = DateTime.Now;
        string[] days = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
        float colW = w / 7f;
        for (int i = 0; i < 7; i++)
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, days[i], x + i * colW, y, colW, 14, 9.5f, pal.SubtleText, FontWeight.Bold, TextAlignment.Center);

        var first = new DateTime(now.Year, now.Month, 1);
        int startCol = (int)first.DayOfWeek;
        int dim = DateTime.DaysInMonth(now.Year, now.Month);
        float ry = y + 16f;
        for (int day = 1; day <= dim; day++)
        {
            int cell = startCol + day - 1;
            int col = cell % 7;
            int row = cell / 7;
            if (row > 4) break;
            float cx = x + col * colW;
            float cy = ry + row * 15.5f;
            if (day == now.Day)
            {
                using var acc = d2d.CreateSolidColorBrush(pal.Accent);
                d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(cx + colW * 0.5f, cy + 7.5f), 7.2f, 7.2f), acc);
            }
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, day.ToString(), cx, cy, colW, 14, 10.2f, day == now.Day ? new Color4(1f, 1f, 1f, 1f) : pal.PrimaryText, day == now.Day ? FontWeight.Bold : FontWeight.Regular, TextAlignment.Center);
        }
    }
}
