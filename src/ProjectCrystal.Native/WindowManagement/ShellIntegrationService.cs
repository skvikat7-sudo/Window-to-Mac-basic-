using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.WindowManagement;

public sealed class ShellIntegrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ProjectCrystal";
    private readonly RecoveryGuard _recoveryGuard;
    private nint _registeredAppBarHwnd;
    private int _workAreaFixCount;

    public bool IsOriginalTaskbarPeeked { get; private set; }

    public ShellIntegrationService(RecoveryGuard recoveryGuard)
    {
        _recoveryGuard = recoveryGuard;
    }

    public static string GetShellCompatibilityExplanation(HardwareProfile profile)
    {
        if (profile.SupportsShellLauncher)
        {
            return $"Edition '{profile.WindowsEditionId}' supports Windows Shell Launcher, while Project Crystal runs safely via reversible Taskbar hiding/auto-hide without modifying Winlogon.";
        }
        return $"Windows {profile.WindowsEditionId} restricts Shell Launcher to Enterprise/Education editions. Project Crystal safely hides the original Taskbar via SHAppBarMessage + SW_HIDE and automatically restores it on exit, crash recovery, or uninstall.";
    }

    public bool IsWindowsTaskbarAutoHide()
    {
        var abd = new Win32Interop.APPBARDATA { cbSize = Marshal.SizeOf<Win32Interop.APPBARDATA>() };
        nuint state = Win32Interop.SHAppBarMessage(Win32Interop.ABM_GETSTATE, ref abd);
        return ((uint)state & Win32Interop.ABS_AUTOHIDE) != 0;
    }

    public void ApplyShellMode(ShellModeConfig config, HardwareProfile hardware)
    {
        try
        {
            bool wantsHide = config.Mode == ShellOperatingMode.AdvancedShellIntegration
                             && config.AutoHideWindowsTaskbar
                             && config.TaskbarMode != WindowsTaskbarMode.KeepVisible;

            if (wantsHide && !IsOriginalTaskbarPeeked)
            {
                bool origAutoHide = IsWindowsTaskbarAutoHide();
                bool replaceWindow = config.TaskbarMode == WindowsTaskbarMode.AutoHideAndReplace;
                var origWork = new Win32Interop.RECT();
                bool gotWork = Win32Interop.SystemParametersInfo(Win32Interop.SPI_GETWORKAREA, 0, ref origWork, 0);

                _recoveryGuard.SaveTaskbarOriginalState(
                    origAutoHide,
                    hiddenByCrystal: replaceWindow,
                    gotWork ? origWork.Left : null,
                    gotWork ? origWork.Top : null,
                    gotWork ? origWork.Right : null,
                    gotWork ? origWork.Bottom : null);

                if (!origAutoHide)
                {
                    SetWindowsTaskbarAutoHide(true);
                    Thread.Sleep(100);
                }

                SetAllTaskbarWindowsVisible(!replaceWindow);
                if (replaceWindow)
                {
                    _workAreaFixCount = 0;
                    ExpandWorkAreaToFullMonitors(broadcast: false);
                    ExpandStuckMaximizedWindowsOnce();

                    var checkRect = new Win32Interop.RECT();
                    Win32Interop.SystemParametersInfo(Win32Interop.SPI_GETWORKAREA, 0, ref checkRect, 0);
                    CrystalLogger.Info("Shell", $"Applied Windows Taskbar mode '{config.TaskbarMode}' (WindowHidden={replaceWindow}, WorkArea={checkRect.Left},{checkRect.Top},{checkRect.Right},{checkRect.Bottom}).");
                }
                else
                {
                    CrystalLogger.Info("Shell", $"Applied Windows Taskbar mode '{config.TaskbarMode}' (AutoHide=true, WindowHidden={replaceWindow}).");
                }
            }
            else if (!wantsHide)
            {
                RestoreTaskbarIfBackedUp();
            }

            SetRunAtStartup(config.StartWithWindows);
        }
        catch (Exception ex)
        {
            CrystalLogger.Error("Shell", "Failed applying shell integration settings", ex);
        }
    }

    public void TogglePeekOriginalTaskbar(ShellModeConfig config)
    {
        IsOriginalTaskbarPeeked = !IsOriginalTaskbarPeeked;
        if (IsOriginalTaskbarPeeked)
        {
            SetAllTaskbarWindowsVisible(true);
            var backup = _recoveryGuard.ReadTaskbarBackup();
            if (backup is { HasSavedWorkArea: true })
            {
                var r = new Win32Interop.RECT { Left = backup.WorkLeft, Top = backup.WorkTop, Right = backup.WorkRight, Bottom = backup.WorkBottom };
                Win32Interop.SystemParametersInfo(Win32Interop.SPI_SETWORKAREA, 0, ref r, Win32Interop.SPIF_SENDCHANGE);
            }
            nint tray = Win32Interop.FindWindow("Shell_TrayWnd", null);
            if (tray != 0) Win32Interop.SetForegroundWindow(tray);
        }
        else
        {
            bool replaceWindow = config.Mode == ShellOperatingMode.AdvancedShellIntegration
                                 && config.AutoHideWindowsTaskbar
                                 && config.TaskbarMode == WindowsTaskbarMode.AutoHideAndReplace;
            SetAllTaskbarWindowsVisible(!replaceWindow);
            if (replaceWindow)
            {
                ExpandWorkAreaToFullMonitors(broadcast: false);
                ExpandStuckMaximizedWindowsOnce();
            }
        }
    }

    public void RestoreTaskbarIfBackedUp()
    {
        try
        {
            IsOriginalTaskbarPeeked = false;
            SetAllTaskbarWindowsVisible(true);

            var backup = _recoveryGuard.ReadTaskbarBackup();
            if (backup is { Saved: true })
            {
                SetWindowsTaskbarAutoHide(backup.WasAutoHide);
                if (backup.HasSavedWorkArea && backup.WorkRight > backup.WorkLeft && backup.WorkBottom > backup.WorkTop)
                {
                    var r = new Win32Interop.RECT
                    {
                        Left = backup.WorkLeft,
                        Top = backup.WorkTop,
                        Right = backup.WorkRight,
                        Bottom = backup.WorkBottom
                    };
                    Win32Interop.SystemParametersInfo(Win32Interop.SPI_SETWORKAREA, 0, ref r, Win32Interop.SPIF_SENDCHANGE);
                }
                _recoveryGuard.ClearTaskbarBackup();
                CrystalLogger.Info("Shell", $"Restored original Windows Taskbar visibility, auto-hide ({backup.WasAutoHide}), and desktop work area.");
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Shell", $"RestoreTaskbarIfBackedUp failed: {ex.Message}");
        }
    }

    public bool SetWindowsTaskbarAutoHide(bool autoHide)
    {
        try
        {
            nint taskbar = Win32Interop.FindWindow("Shell_TrayWnd", null);
            bool wasHidden = taskbar != 0 && !Win32Interop.IsWindowVisible(taskbar);
            if (wasHidden)
                Win32Interop.ShowWindow(taskbar, Win32Interop.SW_SHOWNOACTIVATE);

            var abd = new Win32Interop.APPBARDATA
            {
                cbSize = Marshal.SizeOf<Win32Interop.APPBARDATA>(),
                hWnd = taskbar,
                lParam = autoHide
                    ? (nint)(Win32Interop.ABS_AUTOHIDE | Win32Interop.ABS_ALWAYSONTOP)
                    : (nint)Win32Interop.ABS_ALWAYSONTOP
            };
            Win32Interop.SHAppBarMessage(Win32Interop.ABM_SETSTATE, ref abd);
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Shell", $"SetWindowsTaskbarAutoHide failed: {ex.Message}");
            return false;
        }
    }

    public static void SetAllTaskbarWindowsVisible(bool visible)
    {
        int cmd = visible ? Win32Interop.SW_SHOW : Win32Interop.SW_HIDE;
        nint primary = Win32Interop.FindWindow("Shell_TrayWnd", null);
        if (primary != 0)
            Win32Interop.ShowWindow(primary, cmd);

        Win32Interop.EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            Win32Interop.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "Shell_SecondaryTrayWnd")
                Win32Interop.ShowWindow(hwnd, cmd);
            return true;
        }, 0);
    }

    public void EnsureTaskbarRemainsHiddenIfNeeded(ShellModeConfig config)
    {
        if (IsOriginalTaskbarPeeked) return;
        bool shouldHide = config.Mode == ShellOperatingMode.AdvancedShellIntegration
                          && config.AutoHideWindowsTaskbar
                          && config.TaskbarMode == WindowsTaskbarMode.AutoHideAndReplace;
        if (!shouldHide) return;

        nint primary = Win32Interop.FindWindow("Shell_TrayWnd", null);
        if (primary != 0 && Win32Interop.IsWindowVisible(primary))
        {
            // Taskbar snuck back in (e.g. Explorer restart). Re-hide and reset work-area fix counter
            // so we re-verify the work area on the next few polls.
            SetAllTaskbarWindowsVisible(false);
            _workAreaFixCount = 0;
            CrystalLogger.Info("Shell", "Taskbar reappeared; re-hiding and resetting work-area fix pass.");
        }

        // Allow up to 5 work-area correction passes each time the counter resets.
        if (_workAreaFixCount < 5)
        {
            var curWork = new Win32Interop.RECT();
            var priBounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            if (Win32Interop.SystemParametersInfo(Win32Interop.SPI_GETWORKAREA, 0, ref curWork, 0)
                && (curWork.Bottom - curWork.Top) < priBounds.Height - 16)
            {
                _workAreaFixCount++;
                ExpandWorkAreaToFullMonitors(broadcast: false);
                ExpandStuckMaximizedWindowsOnce();
            }
            else
            {
                // Work area is correct — no further expansion needed this cycle.
                _workAreaFixCount = 5;
            }
        }
    }

    public static void ExpandWorkAreaToFullMonitors(bool broadcast = false)
    {
        try
        {
            uint flags = broadcast ? Win32Interop.SPIF_SENDCHANGE : 0u;
            foreach (var scr in Screen.AllScreens)
            {
                var r = new Win32Interop.RECT
                {
                    Left = scr.Bounds.Left,
                    Top = scr.Bounds.Top,
                    Right = scr.Bounds.Right,
                    Bottom = scr.Bounds.Bottom
                };
                Win32Interop.SystemParametersInfo(Win32Interop.SPI_SETWORKAREA, 0, ref r, flags);
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Shell", $"ExpandWorkAreaToFullMonitors failed: {ex.Message}");
        }
    }

    private static void ExpandStuckMaximizedWindowsOnce()
    {
        try
        {
            int ownPid = Environment.ProcessId;
            Win32Interop.EnumWindows((hwnd, _) =>
            {
                if (!Win32Interop.IsWindowVisible(hwnd) || !Win32Interop.IsZoomed(hwnd))
                    return true;

                Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == ownPid) return true;

                int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
                if ((exStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0) return true;

                if (!Win32Interop.GetWindowRect(hwnd, out var rect)) return true;
                var scr = Screen.FromHandle(hwnd);
                if (rect.Bottom <= scr.Bounds.Bottom - 20 && rect.Bottom >= scr.Bounds.Bottom - 84 && rect.Top <= scr.Bounds.Top + 10)
                {
                    int padX = 0;
                    int padY = 0;
                    if (Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32Interop.RECT ext, Marshal.SizeOf<Win32Interop.RECT>()) == 0)
                    {
                        padX = Math.Clamp((rect.Width - ext.Width) / 2, 0, 12);
                        padY = Math.Clamp((rect.Height - ext.Height) / 2, 0, 12);
                    }
                    Win32Interop.SetWindowPos(
                        hwnd,
                        0,
                        scr.Bounds.Left - padX,
                        scr.Bounds.Top - padY,
                        scr.Bounds.Width + padX * 2,
                        scr.Bounds.Height + padY * 2,
                        Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE);
                }
                return true;
            }, 0);
        }
        catch { }
    }

    public void RegisterTopMenuAppBar(nint hwnd, int screenWidth, int height)
    {
        if (hwnd == 0) return;
        UnregisterAppBar();

        var abd = new Win32Interop.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Win32Interop.APPBARDATA>(),
            hWnd = hwnd,
            uEdge = Win32Interop.ABE_TOP,
            rc = new Win32Interop.RECT { Left = 0, Top = 0, Right = screenWidth, Bottom = height }
        };
        Win32Interop.SHAppBarMessage(Win32Interop.ABM_NEW, ref abd);
        Win32Interop.SHAppBarMessage(Win32Interop.ABM_QUERYPOS, ref abd);
        abd.rc.Bottom = abd.rc.Top + height;
        Win32Interop.SHAppBarMessage(Win32Interop.ABM_SETPOS, ref abd);
        _registeredAppBarHwnd = hwnd;
    }

    public void UnregisterAppBar()
    {
        if (_registeredAppBarHwnd == 0) return;
        try
        {
            var abd = new Win32Interop.APPBARDATA
            {
                cbSize = Marshal.SizeOf<Win32Interop.APPBARDATA>(),
                hWnd = _registeredAppBarHwnd
            };
            Win32Interop.SHAppBarMessage(Win32Interop.ABM_REMOVE, ref abd);
        }
        catch { }
        _registeredAppBarHwnd = 0;
    }

    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enable)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                    key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Shell", $"Startup registry update failed: {ex.Message}");
        }
    }
}
