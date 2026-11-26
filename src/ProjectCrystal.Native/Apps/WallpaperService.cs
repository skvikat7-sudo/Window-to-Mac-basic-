using System.Drawing;
using Microsoft.Win32;
using ProjectCrystal.Core.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ProjectCrystal.Native.Apps;

public sealed record WallpaperSnapshot(
    string FilePath,
    int Width,
    int Height,
    byte[] BgraPixels,
    Color DominantColor
);

public static class WallpaperService
{
    public static string? GetCurrentWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string? path = key?.GetValue("Wallpaper")?.ToString();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;

            string transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (File.Exists(transcoded))
                return transcoded;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Wallpaper", $"Could not read wallpaper path: {ex.Message}");
        }
        return null;
    }

    public static async Task<WallpaperSnapshot> LoadWallpaperOrFallbackAsync(int targetWidth = 1920, int targetHeight = 1080)
    {
        string? path = GetCurrentWallpaperPath();
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                await using var fs = File.OpenRead(path);
                var memStream = fs.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(memStream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)targetWidth,
                    ScaledHeight = (uint)targetHeight,
                    InterpolationMode = BitmapInterpolationMode.Linear
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] bytes = pixelData.DetachPixelData();
                Color dominant = ComputeAverageColor(bytes);
                return new WallpaperSnapshot(path, targetWidth, targetHeight, bytes, dominant);
            }
            catch (Exception ex)
            {
                CrystalLogger.Warn("Wallpaper", $"Failed decoding wallpaper '{path}': {ex.Message}");
            }
        }

        // Crisp rich gradient fallback if no wallpaper file exists
        byte[] fallback = GenerateProceduralGradientBgra(targetWidth, targetHeight);
        return new WallpaperSnapshot("Procedural", targetWidth, targetHeight, fallback, Color.FromArgb(255, 34, 88, 165));
    }

    private static Color ComputeAverageColor(byte[] bgra)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (int i = 0; i < bgra.Length - 3; i += 64)
        {
            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
            count++;
        }
        if (count == 0) return Color.FromArgb(255, 48, 112, 198);
        return Color.FromArgb(255, (int)(r / count), (int)(g / count), (int)(b / count));
    }

    private static byte[] GenerateProceduralGradientBgra(int w, int h)
    {
        byte[] buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float ty = y / (float)h;
            for (int x = 0; x < w; x++)
            {
                float tx = x / (float)w;
                byte b = (byte)Math.Clamp((int)(95 + 115 * (1 - ty) + 35 * Math.Sin(tx * 3.14f)), 0, 255);
                byte g = (byte)Math.Clamp((int)(45 + 90 * tx * (1 - ty) + 25 * ty), 0, 255);
                byte r = (byte)Math.Clamp((int)(25 + 85 * tx + 55 * ty * (1 - tx)), 0, 255);
                int idx = (y * w + x) * 4;
                buf[idx] = b;
                buf[idx + 1] = g;
                buf[idx + 2] = r;
                buf[idx + 3] = 255;
            }
        }
        return buf;
    }
}
