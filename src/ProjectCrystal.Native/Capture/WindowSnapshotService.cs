using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.Capture;

public sealed record WindowSnapshot(
    nint Hwnd,
    byte[] BgraPixels,
    int Width,
    int Height,
    Rectangle ScreenBounds,
    DateTime CapturedUtc);

public static class WindowSnapshotService
{
    private static readonly ConcurrentDictionary<nint, WindowSnapshot> _cache = new();

    public static WindowSnapshot? GetCached(nint hwnd, TimeSpan maxAge)
    {
        if (hwnd != 0 && _cache.TryGetValue(hwnd, out var snap))
        {
            if (DateTime.UtcNow - snap.CapturedUtc <= maxAge)
                return snap;
        }
        return null;
    }

    public static void Invalidate(nint hwnd)
    {
        if (hwnd != 0) _cache.TryRemove(hwnd, out _);
    }

    public static WindowSnapshot? CaptureWindow(nint hwnd, bool allowScreenFallback = true, WgcDesktopCaptureService? wgcFallback = null)
    {
        if (hwnd == 0 || !Win32Interop.IsWindow(hwnd)) return null;

        try
        {
            if (!TryGetRenderBounds(hwnd, out Rectangle rect))
                return null;

            bool isCloaked = Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_CLOAKED, out int cloakedVal, sizeof(int)) == 0 && cloakedVal != 0;
            byte[]? pixels = null;
            int w = Math.Clamp(rect.Width, 16, 3840);
            int h = Math.Clamp(rect.Height, 16, 2160);

            // 1. Prefer direct 1:1 GPU framebuffer crop via WGC for visible uncloaked windows
            //    (prevents Chromium/WinUI3 PrintWindow GDI scaling blur)
            if (allowScreenFallback && !isCloaked && wgcFallback != null
                && wgcFallback.IsLiveCaptureActive
                && Win32Interop.IsWindowVisible(hwnd) && !Win32Interop.IsIconic(hwnd))
            {
                var clamped = new Rectangle(
                    Math.Max(0, rect.Left),
                    Math.Max(0, rect.Top),
                    Math.Clamp(rect.Right, 1, Math.Max(1, wgcFallback.TextureWidth)) - Math.Max(0, rect.Left),
                    Math.Clamp(rect.Bottom, 1, Math.Max(1, wgcFallback.TextureHeight)) - Math.Max(0, rect.Top));
                if (clamped.Width >= 16 && clamped.Height >= 16)
                {
                    pixels = wgcFallback.CaptureRegionBgra(clamped);
                    if (pixels != null)
                    {
                        w = clamped.Width;
                        h = clamped.Height;
                        rect = clamped;
                    }
                }
            }

            // 2. Fallback to PrintWindow (works even while cloaked) / BitBlt
            pixels ??= CaptureViaGdi(hwnd, rect, w, h, allowScreenFallback && !isCloaked);
            if (pixels == null)
                return null;

            var snap = new WindowSnapshot(hwnd, pixels, w, h, rect, DateTime.UtcNow);
            _cache[hwnd] = snap;
            return snap;
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Snapshot", $"CaptureWindow failed for 0x{hwnd:X}: {ex.Message}");
            return null;
        }
    }

    public static bool TryGetRenderBounds(nint hwnd, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (hwnd == 0) return false;

        if (Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32Interop.RECT ext, Marshal.SizeOf<Win32Interop.RECT>()) == 0
            && ext.Width >= 40 && ext.Height >= 40)
        {
            bounds = new Rectangle(ext.Left, ext.Top, ext.Width, ext.Height);
            return true;
        }

        if (Win32Interop.GetWindowRect(hwnd, out var wr) && wr.Width >= 40 && wr.Height >= 40)
        {
            bounds = new Rectangle(wr.Left, wr.Top, wr.Width, wr.Height);
            return true;
        }

        return false;
    }

    private static unsafe byte[]? CaptureViaGdi(nint hwnd, Rectangle rect, int w, int h, bool allowScreenFallback)
    {
        nint screenDc = Win32Interop.GetDC(0);
        if (screenDc == 0) return null;

        nint memDc = 0;
        nint hBitmap = 0;
        nint oldObj = 0;
        try
        {
            memDc = Win32Interop.CreateCompatibleDC(screenDc);
            if (memDc == 0) return null;

            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down BGRA
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            hBitmap = Win32Interop.CreateDIBSection(memDc, ref bmi, 0, out nint bitsPtr, 0, 0);
            if (hBitmap == 0 || bitsPtr == 0) return null;

            oldObj = Win32Interop.SelectObject(memDc, hBitmap);
            int byteCount = w * h * 4;

            bool ok = Win32Interop.PrintWindow(hwnd, memDc, Win32Interop.PW_RENDERFULLCONTENT);
            if (!ok || IsBufferMostlyEmpty(bitsPtr, w, h))
            {
                if (allowScreenFallback && Win32Interop.IsWindowVisible(hwnd) && !Win32Interop.IsIconic(hwnd))
                {
                    Win32Interop.BitBlt(memDc, 0, 0, w, h, screenDc, rect.Left, rect.Top, Win32Interop.SRCCOPY);
                }
            }

            if (IsBufferMostlyEmpty(bitsPtr, w, h))
                return null;

            var pixels = new byte[byteCount];
            Marshal.Copy(bitsPtr, pixels, 0, byteCount);

            // Ensure opaque alpha channel (PrintWindow / BitBlt leave alpha=0 on some Win32 GDI windows)
            for (int i = 3; i < byteCount; i += 4)
                pixels[i] = 255;

            return pixels;
        }
        finally
        {
            if (oldObj != 0 && memDc != 0) Win32Interop.SelectObject(memDc, oldObj);
            if (hBitmap != 0) Win32Interop.DeleteObject(hBitmap);
            if (memDc != 0) Win32Interop.DeleteDC(memDc);
            Win32Interop.ReleaseDC(0, screenDc);
        }
    }

    private static unsafe bool IsBufferMostlyEmpty(nint bitsPtr, int w, int h)
    {
        uint* px = (uint*)bitsPtr;
        int nonZero = 0;
        int stepX = Math.Max(1, w / 16);
        int stepY = Math.Max(1, h / 16);
        for (int y = stepY / 2; y < h; y += stepY)
        {
            int row = y * w;
            for (int x = stepX / 2; x < w; x += stepX)
            {
                if ((px[row + x] & 0x00FFFFFF) != 0)
                {
                    nonZero++;
                    if (nonZero >= 6) return false;
                }
            }
        }
        return true;
    }
}
