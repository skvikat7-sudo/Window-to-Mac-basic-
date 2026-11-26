using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.System;

public sealed class DisplayBrightnessService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PHYSICAL_MONITOR
    {
        public nint hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(nint hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(nint hMonitor, uint dwNewBrightness);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public IReadOnlyList<DisplayMonitorInfo> GetMonitors()
    {
        var list = new List<DisplayMonitorInfo>();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            nint hMon = Win32Interop.MonitorFromPoint(
                new Win32Interop.POINT { X = s.Bounds.X + s.Bounds.Width / 2, Y = s.Bounds.Y + s.Bounds.Height / 2 }, 2);
            uint dpi = 96;
            try
            {
                if (GetDpiForMonitor(hMon, 0, out uint dx, out _) == 0 && dx > 0)
                    dpi = dx;
            }
            catch { }

            list.Add(new DisplayMonitorInfo(
                i,
                s.DeviceName,
                s.Primary,
                s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height,
                s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height,
                dpi));
        }
        return list;
    }

    public BrightnessStatusInfo GetBrightnessStatus()
    {
        try
        {
            var primary = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
            if (primary is null)
                return new BrightnessStatusInfo(false, "None", 0, 100, 100, "No Display");

            nint hMon = Win32Interop.MonitorFromPoint(
                new Win32Interop.POINT { X = primary.Bounds.Width / 2, Y = primary.Bounds.Height / 2 }, 2);

            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var mons = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, mons))
                {
                    try
                    {
                        string desc = mons[0].szPhysicalMonitorDescription;
                        if (GetMonitorBrightness(mons[0].hPhysicalMonitor, out uint minB, out uint curB, out uint maxB) && maxB > minB)
                        {
                            return new BrightnessStatusInfo(true, "DDC/CI", (int)minB, (int)curB, (int)maxB, desc);
                        }
                        return new BrightnessStatusInfo(false, "DDC/CI Unsupported", 0, 100, 100, desc);
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, mons);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Display", $"Brightness query failed: {ex.Message}");
        }
        return new BrightnessStatusInfo(false, "Hardware Unsupported", 0, 100, 100, "Desktop Display");
    }

    public bool SetBrightness(int value)
    {
        try
        {
            var primary = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
            if (primary is null) return false;
            nint hMon = Win32Interop.MonitorFromPoint(
                new Win32Interop.POINT { X = primary.Bounds.Width / 2, Y = primary.Bounds.Height / 2 }, 2);

            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var mons = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, mons))
                {
                    try
                    {
                        return SetMonitorBrightness(mons[0].hPhysicalMonitor, (uint)Math.Clamp(value, 0, 100));
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, mons);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Display", $"SetBrightness failed: {ex.Message}");
        }
        return false;
    }

    public static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    public static bool SetWindowsDarkMode(bool darkMode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            int val = darkMode ? 0 : 1;
            key.SetValue("AppsUseLightTheme", val, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", val, RegistryValueKind.DWord);
            Win32Interop.SendMessageTimeout(new nint(0xFFFF), Win32Interop.WM_SETTINGCHANGE, 0, "ImmersiveColorSet", 0x0002, 200, out _);
            CrystalLogger.Info("Display", $"Switched Windows theme mode to {(darkMode ? "Dark" : "Light")}.");
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Error("Display", "Failed changing Windows theme mode", ex);
            return false;
        }
    }

    public static Color GetWindowsAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                byte r = (byte)(abgr & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                return Color.FromArgb(255, r, g, b);
            }
        }
        catch { }
        return Color.FromArgb(255, 76, 194, 255);
    }
}
