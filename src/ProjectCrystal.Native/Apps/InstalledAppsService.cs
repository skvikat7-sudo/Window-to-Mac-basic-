using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.Apps;

public sealed record AppIconBitmap(int Width, int Height, byte[] BgraPixels, bool HasOpaqueCorners = false);

public sealed class InstalledAppsService
{
    private readonly ConcurrentDictionary<string, AppIconBitmap?> _iconCache = new();
    private readonly List<AppEntry> _cachedApps = new();
    private readonly Dictionary<string, DateTimeOffset> _recentLaunches = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint pbc, [In] ref Guid bhid, [In] ref Guid riid, out nint ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface)] IShellItem[] rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out nint phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderItem([In] ref Guid rfid, uint flags, nint hToken, [In] ref Guid riid, out IShellItem ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, nint pbc, [In] ref Guid riid, out IShellItem ppv);

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObject);

    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    public async Task<IReadOnlyList<AppEntry>> RefreshInstalledAppsAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<AppEntry>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Guid shellItemGuid = typeof(IShellItem).GUID;
                Guid folderGuid = FOLDERID_AppsFolder;
                int hr = SHGetKnownFolderItem(ref folderGuid, 0, 0, ref shellItemGuid, out var appsFolder);
                if (hr == 0 && appsFolder != null)
                {
                    Guid bhid = BHID_EnumItems;
                    Guid enumGuid = typeof(IEnumShellItems).GUID;
                    appsFolder.BindToHandler(0, ref bhid, ref enumGuid, out nint enumPtr);
                    if (enumPtr != 0)
                    {
                        var enumItems = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
                        Marshal.Release(enumPtr);

                        var batch = new IShellItem[1];
                        while (enumItems.Next(1, batch, out uint fetched) == 0 && fetched == 1)
                        {
                            var item = batch[0];
                            try
                            {
                                item.GetDisplayName(SIGDN_NORMALDISPLAY, out string displayName);
                                item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out string parsingName);
                                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(parsingName))
                                    continue;
                                if (ShouldFilterNoiseApp(displayName, parsingName))
                                    continue;
                                if (!seenNames.Add(displayName))
                                    continue;

                                bool isUwp = parsingName.Contains('!') && !Path.IsPathRooted(parsingName);
                                string target = isUwp ? $"shell:AppsFolder\\{parsingName}" : parsingName;
                                string category = CategorizeApp(displayName, parsingName);

                                _recentLaunches.TryGetValue(parsingName, out var lastLaunched);
                                results.Add(new AppEntry(
                                    Id: parsingName,
                                    Name: displayName,
                                    LaunchTarget: target,
                                    Arguments: "",
                                    Category: category,
                                    IconKey: parsingName,
                                    IsPackagedUwp: isUwp,
                                    LastLaunched: lastLaunched == default ? null : lastLaunched));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CrystalLogger.Error("Apps", "Shell AppsFolder enumeration failed", ex);
            }

            // Merge Start Menu .lnk fallback if needed
            ScanStartMenuShortcuts(results, seenNames);

            results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            lock (_cachedApps)
            {
                _cachedApps.Clear();
                _cachedApps.AddRange(results);
            }
            CrystalLogger.Info("Apps", $"Indexed {results.Count} installed Windows applications.");
            return (IReadOnlyList<AppEntry>)results;
        });
    }

    public IReadOnlyList<AppEntry> GetCachedApps()
    {
        lock (_cachedApps) return _cachedApps.ToArray();
    }

    public string? FindAppTargetByNameOrTitle(string? nameOrTitle)
    {
        if (string.IsNullOrWhiteSpace(nameOrTitle)) return null;
        string trimmed = nameOrTitle.Trim();
        lock (_cachedApps)
        {
            var exact = _cachedApps.FirstOrDefault(a =>
                string.Equals(a.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return string.IsNullOrWhiteSpace(exact.IconKey) ? exact.LaunchTarget : exact.IconKey;

            var partial = _cachedApps.FirstOrDefault(a =>
                trimmed.Contains(a.Name, StringComparison.OrdinalIgnoreCase)
                || a.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
            return partial == null ? null : (string.IsNullOrWhiteSpace(partial.IconKey) ? partial.LaunchTarget : partial.IconKey);
        }
    }

    public AppIconBitmap? GetOrExtractWindowIcon(nint hwnd, int size = 96)
    {
        if (hwnd == 0) return null;
        string cacheKey = $"hwnd:{hwnd}@{size}";
        if (_iconCache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            nint hIcon = 0;
            if (Win32Interop.SendMessageTimeout(hwnd, Win32Interop.WM_GETICON, Win32Interop.ICON_BIG, 0, 0x0002, 80, out var resBig) != 0 && resBig != 0)
                hIcon = resBig;
            if (hIcon == 0 && Win32Interop.SendMessageTimeout(hwnd, Win32Interop.WM_GETICON, Win32Interop.ICON_SMALL2, 0, 0x0002, 80, out var resSmall) != 0 && resSmall != 0)
                hIcon = resSmall;
            if (hIcon == 0)
                hIcon = Win32Interop.GetClassLongPtr(hwnd, Win32Interop.GCLP_HICON);
            if (hIcon == 0) return null;

            using var ico = Icon.FromHandle(hIcon);
            using var rawBmp = ico.ToBitmap();
            using var scaled = new Bitmap(rawBmp, new Size(size, size));
            nint hbm = scaled.GetHbitmap(Color.Transparent);
            try
            {
                var bmp = ExtractPremultipliedBgraFromHbitmap(hbm, size, size);
                if (bmp != null)
                {
                    _iconCache[cacheKey] = bmp;
                    return bmp;
                }
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        catch { }

        _iconCache[cacheKey] = null;
        return null;
    }

    public AppIconBitmap? GetOrExtractIcon(string parsingOrPath, int size = 96)
    {
        if (string.IsNullOrWhiteSpace(parsingOrPath)) return null;
        string cacheKey = $"{parsingOrPath}@{size}";
        if (_iconCache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            string lookupPath = parsingOrPath;
            if (!Path.IsPathRooted(lookupPath) && !lookupPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                lookupPath = $"shell:AppsFolder\\{lookupPath}";
            }

            Guid shellItemGuid = typeof(IShellItem).GUID;
            int hr = SHCreateItemFromParsingName(lookupPath, 0, ref shellItemGuid, out var item);
            if (hr != 0 || item == null) return null;

            if (item is IShellItemImageFactory factory)
            {
                // SIIGBF_ICONONLY = 0x04, fallback to 0x00 if needed
                int imgHr = factory.GetImage(new SIZE { cx = size, cy = size }, 0x04, out nint hbm);
                if (imgHr != 0 || hbm == 0)
                    imgHr = factory.GetImage(new SIZE { cx = size, cy = size }, 0x00, out hbm);

                if (imgHr == 0 && hbm != 0)
                {
                    try
                    {
                        var bmp = ExtractPremultipliedBgraFromHbitmap(hbm, size, size);
                        if (bmp != null)
                        {
                            _iconCache[cacheKey] = bmp;
                            return bmp;
                        }
                    }
                    finally
                    {
                        DeleteObject(hbm);
                    }
                }
            }
        }
        catch { }

        _iconCache[cacheKey] = null;
        return null;
    }

    public bool LaunchApp(string target, string arguments = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            string effective = target;
            if (!Path.IsPathRooted(effective)
                && !effective.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                && !effective.Contains("://")
                && !effective.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
            {
                effective = $"shell:AppsFolder\\{effective}";
            }

            var psi = new ProcessStartInfo
            {
                FileName = effective,
                Arguments = arguments,
                UseShellExecute = true
            };
            Process.Start(psi);
            _recentLaunches[target] = DateTimeOffset.Now;
            CrystalLogger.Info("Apps", $"Launched application: {effective}");
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Error("Apps", $"Failed launching '{target}'", ex);
            return false;
        }
    }

    private static AppIconBitmap? ExtractPremultipliedBgraFromHbitmap(nint hbm, int width, int height)
    {
        nint hdc = GetDC(0);
        try
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };
            byte[] pixels = new byte[width * height * 4];
            int lines = GetDIBits(hdc, hbm, 0, (uint)height, pixels, ref bmi, 0);
            if (lines <= 0) return null;

            // Ensure alpha premultiplication for Direct2D Premultiplied format
            bool anyNonZeroAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) { anyNonZeroAlpha = true; break; }
            }
            if (!anyNonZeroAlpha)
            {
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }
            else
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte a = pixels[i + 3];
                    if (a < 255)
                    {
                        pixels[i] = (byte)(pixels[i] * a / 255);
                        pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                        pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
                    }
                }
            }

            bool opaqueCorners = pixels[3] > 235 && pixels[(width - 1) * 4 + 3] > 235
                && pixels[(height - 1) * width * 4 + 3] > 235 && pixels[(width * height - 1) * 4 + 3] > 235;

            return new AppIconBitmap(width, height, pixels, opaqueCorners);
        }
        finally
        {
            ReleaseDC(0, hdc);
        }
    }

    private void ScanStartMenuShortcuts(List<AppEntry> results, HashSet<string> seenNames)
    {
        string[] roots =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (ShouldFilterNoiseApp(name, file)) continue;
                    if (!seenNames.Add(name)) continue;
                    results.Add(new AppEntry(
                        Id: file,
                        Name: name,
                        LaunchTarget: file,
                        Arguments: "",
                        Category: CategorizeApp(name, file),
                        IconKey: file,
                        IsPackagedUwp: false));
                }
            }
            catch { }
        }
    }

    private static bool ShouldFilterNoiseApp(string name, string path)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("uninstall") || lower.Contains("readme") || lower.Contains("documentation")
            || lower.Contains("manual") || lower.Contains("help") || lower.StartsWith("visit "))
            return true;
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string CategorizeApp(string name, string path)
    {
        string text = $"{name} {path}".ToLowerInvariant();
        if (text.Contains("chrome") || text.Contains("edge") || text.Contains("firefox") || text.Contains("brave") || text.Contains("browser"))
            return "Internet";
        if (text.Contains("code") || text.Contains("studio") || text.Contains("python") || text.Contains("git") || text.Contains("terminal") || text.Contains("cmd") || text.Contains("powershell") || text.Contains("keil") || text.Contains("xilinx"))
            return "Developer";
        if (text.Contains("word") || text.Contains("excel") || text.Contains("powerpoint") || text.Contains("onenote") || text.Contains("notepad") || text.Contains("acrobat") || text.Contains("office"))
            return "Productivity";
        if (text.Contains("music") || text.Contains("video") || text.Contains("media") || text.Contains("spotify") || text.Contains("vlc") || text.Contains("photo") || text.Contains("paint"))
            return "Media & Creative";
        if (text.Contains("settings") || text.Contains("control") || text.Contains("task") || text.Contains("calc") || text.Contains("explorer") || text.Contains("defender") || text.Contains("system"))
            return "System";
        return "Utilities";
    }
}
