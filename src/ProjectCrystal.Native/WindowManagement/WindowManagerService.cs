using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.WindowManagement;

public enum SnapTarget
{
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,
    TopLeftQuarter,
    TopRightQuarter,
    BottomLeftQuarter,
    BottomRightQuarter,
    CenterFocus,
    Maximize
}

public sealed class WindowManagerService
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly List<nint> _desktopHiddenWindows = new();
    private nint _lastExternalForeground;

    public nint LastExternalForeground => _lastExternalForeground;

    public IReadOnlyList<WindowInfo> GetOpenWindows()
    {
        var results = new List<WindowInfo>();
        nint shellHwnd = Win32Interop.GetShellWindow();
        nint fgHwnd = Win32Interop.GetForegroundWindow();
        var screens = Screen.AllScreens;

        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shellHwnd || !Win32Interop.IsWindowVisible(hwnd))
                return true;

            Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid)
                return true;

            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            if ((exStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0)
                return true;

            bool isAppWindow = (exStyle & Win32Interop.WS_EX_APPWINDOW) != 0;
            if (!isAppWindow && (exStyle & Win32Interop.WS_EX_NOACTIVATE) != 0)
                return true;

            // Standard Windows taskbar rule: skip owned popups/dialogs unless explicitly flagged WS_EX_APPWINDOW
            if (!isAppWindow && Win32Interop.GetWindow(hwnd, Win32Interop.GW_OWNER) != 0)
                return true;

            if (Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int len = Win32Interop.GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            Win32Interop.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title) || title == "Program Manager" || title == "Windows Input Experience")
                return true;

            var clsSb = new StringBuilder(128);
            Win32Interop.GetClassName(hwnd, clsSb, clsSb.Capacity);
            string cls = clsSb.ToString();
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
                or "Windows.UI.Core.CoreWindow" or "ForegroundStaging" or "TopLevelWindowForOverflowXamlIsland"
                or "NotifyIconOverflowWindow" or "TaskListThumbnailWnd")
                return true;

            bool isMinimized = Win32Interop.IsIconic(hwnd);
            if (!Win32Interop.GetWindowRect(hwnd, out var rect))
                return true;
            // Minimized (IsIconic) windows report compact taskbar bounds (-32000, width=160, height~28) and MUST remain accessible in the Dock!
            if (!isMinimized && (rect.Width < 40 || rect.Height < 40))
                return true;

            string procName = "Application";
            string rawProc = "";
            string exePath = QueryProcessPathSafely(pid);
            if (!string.IsNullOrEmpty(exePath))
            {
                rawProc = Path.GetFileNameWithoutExtension(exePath);
            }
            else
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    rawProc = proc.ProcessName;
                }
                catch { }
            }

            if (rawProc.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("LockApp", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("Widgets", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("WidgetBoard", StringComparison.OrdinalIgnoreCase)
                || rawProc.Equals("CrossDeviceResume", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(rawProc))
                procName = FormatProcessFriendlyName(rawProc);

            int monIdx = 0;
            int cx = rect.Left + rect.Width / 2;
            int cy = rect.Top + rect.Height / 2;
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i].Bounds.Contains(cx, cy)) { monIdx = i; break; }
            }

            if (hwnd == fgHwnd)
                _lastExternalForeground = hwnd;

            results.Add(new WindowInfo(
                Handle: hwnd,
                Title: title,
                ProcessName: procName,
                ProcessId: pid,
                X: rect.Left,
                Y: rect.Top,
                Width: rect.Width,
                Height: rect.Height,
                IsMinimized: Win32Interop.IsIconic(hwnd),
                IsMaximized: Win32Interop.IsZoomed(hwnd),
                IsForeground: hwnd == fgHwnd,
                MonitorIndex: monIdx,
                ExecutablePath: exePath));

            return true;
        }, 0);

        return results;
    }

    public WindowInfo? GetForegroundExternalWindow()
    {
        nint fg = Win32Interop.GetForegroundWindow();
        if (fg != 0)
        {
            Win32Interop.GetWindowThreadProcessId(fg, out uint pid);
            if (pid != _ownPid)
                _lastExternalForeground = fg;
        }

        nint target = _lastExternalForeground;
        if (target == 0) return null;
        return GetOpenWindows().FirstOrDefault(w => w.Handle == target);
    }

    public void FocusWindow(nint hwnd)
    {
        if (hwnd == 0) return;
        try
        {
            if (Win32Interop.IsIconic(hwnd))
                Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);

            nint curFg = Win32Interop.GetForegroundWindow();
            uint curFgTid = curFg != 0 ? Win32Interop.GetWindowThreadProcessId(curFg, out _) : 0;
            uint targetTid = Win32Interop.GetWindowThreadProcessId(hwnd, out _);
            uint ownTid = Win32Interop.GetCurrentThreadId();

            Win32Interop.AllowSetForegroundWindow(Win32Interop.ASFW_ANY);
            bool attachedFg = curFgTid != 0 && curFgTid != ownTid && Win32Interop.AttachThreadInput(ownTid, curFgTid, true);
            bool attachedTarget = targetTid != 0 && targetTid != ownTid && targetTid != curFgTid && Win32Interop.AttachThreadInput(ownTid, targetTid, true);
            try
            {
                Win32Interop.SetForegroundWindow(hwnd);
                Win32Interop.BringWindowToTop(hwnd);
                Win32Interop.SetWindowPos(hwnd, Win32Interop.HWND_TOP, 0, 0, 0, 0,
                    Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE);
            }
            finally
            {
                if (attachedTarget) Win32Interop.AttachThreadInput(ownTid, targetTid, false);
                if (attachedFg) Win32Interop.AttachThreadInput(ownTid, curFgTid, false);
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("WindowMgr", $"FocusWindow failed: {ex.Message}");
        }
    }

    public void MinimizeWindow(nint hwnd)
    {
        if (hwnd != 0) Win32Interop.ShowWindow(hwnd, Win32Interop.SW_MINIMIZE);
    }

    public bool ToggleShowDesktop()
    {
        try
        {
            var visibleOpen = GetOpenWindows().Where(w => !w.IsMinimized).ToList();
            if (visibleOpen.Count > 0)
            {
                _desktopHiddenWindows.Clear();
                foreach (var win in visibleOpen)
                {
                    _desktopHiddenWindows.Add(win.Handle);
                    Win32Interop.ShowWindow(win.Handle, Win32Interop.SW_MINIMIZE);
                }
                return true;
            }

            if (_desktopHiddenWindows.Count > 0)
            {
                foreach (nint hwnd in _desktopHiddenWindows)
                {
                    if (Win32Interop.IsWindowVisible(hwnd) && Win32Interop.IsIconic(hwnd))
                        Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);
                }
                _desktopHiddenWindows.Clear();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("WindowMgr", $"ToggleShowDesktop failed: {ex.Message}");
            return false;
        }
    }

    public void CloseWindow(nint hwnd)
    {
        if (hwnd != 0) Win32Interop.PostMessage(hwnd, Win32Interop.WM_CLOSE, 0, 0);
    }

    public bool SnapWindow(nint hwnd, SnapTarget target, int topMenuHeight = 34, int bottomDockReserve = 82)
    {
        if (hwnd == 0) hwnd = _lastExternalForeground;
        if (hwnd == 0) return false;

        try
        {
            if (Win32Interop.IsIconic(hwnd) || Win32Interop.IsZoomed(hwnd))
                Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);

            nint hMon = Win32Interop.MonitorFromWindow(hwnd, 2);
            var screen = Screen.AllScreens.FirstOrDefault(s =>
                Win32Interop.MonitorFromPoint(new Win32Interop.POINT { X = s.Bounds.X + 10, Y = s.Bounds.Y + 10 }, 2) == hMon)
                ?? Screen.PrimaryScreen!;

            var wa = screen.WorkingArea;
            int left = wa.X + 8;
            int top = Math.Max(wa.Y, screen.Bounds.Y + topMenuHeight + 6);
            int width = wa.Width - 16;
            int height = Math.Max(300, (wa.Bottom - bottomDockReserve) - top);

            Rectangle dest = target switch
            {
                SnapTarget.LeftHalf => new Rectangle(left, top, width / 2 - 4, height),
                SnapTarget.RightHalf => new Rectangle(left + width / 2 + 4, top, width / 2 - 4, height),
                SnapTarget.TopHalf => new Rectangle(left, top, width, height / 2 - 4),
                SnapTarget.BottomHalf => new Rectangle(left, top + height / 2 + 4, width, height / 2 - 4),
                SnapTarget.TopLeftQuarter => new Rectangle(left, top, width / 2 - 4, height / 2 - 4),
                SnapTarget.TopRightQuarter => new Rectangle(left + width / 2 + 4, top, width / 2 - 4, height / 2 - 4),
                SnapTarget.BottomLeftQuarter => new Rectangle(left, top + height / 2 + 4, width / 2 - 4, height / 2 - 4),
                SnapTarget.BottomRightQuarter => new Rectangle(left + width / 2 + 4, top + height / 2 + 4, width / 2 - 4, height / 2 - 4),
                SnapTarget.CenterFocus => new Rectangle(left + (int)(width * 0.12), top + (int)(height * 0.06), (int)(width * 0.76), (int)(height * 0.88)),
                SnapTarget.Maximize => new Rectangle(left, top, width, height),
                _ => new Rectangle(left, top, width, height)
            };

            Win32Interop.SetWindowPos(hwnd, 0, dest.X, dest.Y, dest.Width, dest.Height, Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE);
            Win32Interop.SetForegroundWindow(hwnd);
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("WindowMgr", $"SnapWindow failed: {ex.Message}");
            return false;
        }
    }

    public bool IsFullscreenApplicationActive(Rectangle monitorBounds)
    {
        try
        {
            if (Win32Interop.SHQueryUserNotificationState(out var qState) == 0)
            {
                if (qState == Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                    || qState == Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                    return true;
            }

            nint fg = Win32Interop.GetForegroundWindow();
            if (fg == 0 || fg == Win32Interop.GetShellWindow()) return false;
            Win32Interop.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == _ownPid) return false;

            var clsSb = new StringBuilder(64);
            Win32Interop.GetClassName(fg, clsSb, clsSb.Capacity);
            string cls = clsSb.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

            if (!Win32Interop.GetWindowRect(fg, out var r)) return false;
            bool coversMonitor = r.Left <= monitorBounds.Left && r.Top <= monitorBounds.Top
                && r.Right >= monitorBounds.Right && r.Bottom >= monitorBounds.Bottom;
            if (!coversMonitor) return false;

            // Maximized desktop windows (including borderless Chrome/Edge/Electron with DWM overhang) are NOT fullscreen games
            if (Win32Interop.IsZoomed(fg)) return false;

            const int WS_CAPTION = 0x00C00000;
            int style = Win32Interop.GetWindowLong(fg, Win32Interop.GWL_STYLE);
            bool exactMonitorSize = Math.Abs(r.Width - monitorBounds.Width) <= 4 && Math.Abs(r.Height - monitorBounds.Height) <= 4;
            return exactMonitorSize && (style & WS_CAPTION) != WS_CAPTION;
        }
        catch
        {
            return false;
        }
    }

    public void SwitchVirtualDesktop(bool next)
    {
        const ushort VK_LWIN = 0x5B;
        const ushort VK_CONTROL = 0x11;
        const ushort VK_LEFT = 0x25;
        const ushort VK_RIGHT = 0x27;
        ushort arrow = next ? VK_RIGHT : VK_LEFT;

        var inputs = new Win32Interop.INPUT[6];
        inputs[0] = KeyInput(VK_CONTROL, false);
        inputs[1] = KeyInput(VK_LWIN, false);
        inputs[2] = KeyInput(arrow, false);
        inputs[3] = KeyInput(arrow, true);
        inputs[4] = KeyInput(VK_LWIN, true);
        inputs[5] = KeyInput(VK_CONTROL, true);
        Win32Interop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Interop.INPUT>());
    }

    private static Win32Interop.INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = Win32Interop.INPUT_KEYBOARD,
        U = new Win32Interop.INPUTUNION
        {
            ki = new Win32Interop.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? Win32Interop.KEYEVENTF_KEYUP : 0
            }
        }
    };

    private static string QueryProcessPathSafely(uint pid)
    {
        nint hProc = Win32Interop.OpenProcess(Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == 0) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int cap = sb.Capacity;
            if (Win32Interop.QueryFullProcessImageName(hProc, 0, sb, ref cap))
                return sb.ToString();
            return "";
        }
        finally
        {
            Win32Interop.CloseHandle(hProc);
        }
    }

    private static string FormatProcessFriendlyName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Desktop";
        return raw.ToLowerInvariant() switch
        {
            "explorer" => "File Explorer",
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "code" => "VS Code",
            "antigravity" => "Antigravity",
            "windowsterminal" => "Terminal",
            "notepad" => "Notepad",
            "applicationframehost" => "App",
            _ => char.ToUpper(raw[0]) + raw[1..]
        };
    }
}
