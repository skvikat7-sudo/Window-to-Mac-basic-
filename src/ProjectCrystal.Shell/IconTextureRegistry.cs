using System.Collections.Concurrent;
using ProjectCrystal.Native.Apps;
using ProjectCrystal.Rendering.UI;
using Vortice.Direct2D1;

namespace ProjectCrystal.Shell;

public sealed record CachedAppIcon(ID2D1Bitmap Bitmap, bool HasOpaqueCorners);

public sealed class IconTextureRegistry : IDisposable
{
    private readonly InstalledAppsService _appsService;
    private readonly ConcurrentDictionary<string, CachedAppIcon?> _d2dCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AppIconBitmap?> _rawCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public IconTextureRegistry(InstalledAppsService appsService)
    {
        _appsService = appsService;
    }

    public void PreloadIcon(string key, Action? onLoaded = null)
    {
        if (string.IsNullOrWhiteSpace(key) || _rawCache.ContainsKey(key)) return;
        if (!_inflight.TryAdd(key, 1)) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!_rawCache.ContainsKey(key))
                    _rawCache[key] = _appsService.GetOrExtractIcon(key, 96);
                onLoaded?.Invoke();
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        });
    }

    public void WarmIcons(IEnumerable<string> keys, int maxCount = 36)
    {
        var batch = keys.Where(k => !string.IsNullOrWhiteSpace(k) && !_rawCache.ContainsKey(k)).Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToArray();
        if (batch.Length == 0) return;
        _ = Task.Run(() =>
        {
            foreach (string key in batch)
            {
                if (!_rawCache.ContainsKey(key))
                    _rawCache[key] = _appsService.GetOrExtractIcon(key, 96);
            }
        });
    }

    public CachedAppIcon? GetIconNonBlocking(ID2D1DeviceContext d2d, string key, Action? onLoaded = null, nint fallbackHwnd = 0, string? appName = null)
    {
        string lookup = string.IsNullOrWhiteSpace(key) ? $"name:{appName}:hwnd:{fallbackHwnd}" : key;
        if (_d2dCache.TryGetValue(lookup, out var existing))
            return existing;

        bool isGenericFrameHost = key.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(key) && !isGenericFrameHost && _rawCache.TryGetValue(key, out var cachedRaw))
        {
            if (cachedRaw == null)
            {
                _d2dCache[lookup] = null;
                return null;
            }
            var bmpReady = CrystalUIHelpers.CreateBitmapFromBgra(d2d, cachedRaw.Width, cachedRaw.Height, cachedRaw.BgraPixels);
            var wrappedReady = bmpReady != null ? new CachedAppIcon(bmpReady, cachedRaw.HasOpaqueCorners) : null;
            _d2dCache[lookup] = wrappedReady;
            return wrappedReady;
        }

        if (_inflight.TryAdd(lookup, 1))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    AppIconBitmap? raw = null;
                    if (!string.IsNullOrWhiteSpace(key) && !isGenericFrameHost)
                    {
                        if (!_rawCache.TryGetValue(key, out raw))
                        {
                            raw = _appsService.GetOrExtractIcon(key, 96);
                            _rawCache[key] = raw;
                        }
                    }
                    if (raw == null && !string.IsNullOrWhiteSpace(appName))
                    {
                        string? resolved = _appsService.FindAppTargetByNameOrTitle(appName);
                        if (!string.IsNullOrEmpty(resolved))
                            raw = _appsService.GetOrExtractIcon(resolved, 96);
                    }
                    if (raw == null && fallbackHwnd != 0)
                        raw = _appsService.GetOrExtractWindowIcon(fallbackHwnd, 96);

                    if (!string.IsNullOrWhiteSpace(key) && !isGenericFrameHost)
                        _rawCache[key] = raw;
                    onLoaded?.Invoke();
                }
                finally
                {
                    _inflight.TryRemove(lookup, out _);
                }
            });
        }
        return null;
    }

    public ID2D1Bitmap? GetBitmap(ID2D1DeviceContext d2d, string key) => GetIcon(d2d, key)?.Bitmap;

    public CachedAppIcon? GetIcon(ID2D1DeviceContext d2d, string key, nint fallbackHwnd = 0, string? appName = null)
    {
        string lookup = string.IsNullOrWhiteSpace(key) ? $"name:{appName}:hwnd:{fallbackHwnd}" : key;
        if (_d2dCache.TryGetValue(lookup, out var existing))
            return existing;

        AppIconBitmap? raw = null;
        bool isGenericFrameHost = key.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(key) && !isGenericFrameHost)
        {
            if (!_rawCache.TryGetValue(key, out raw))
            {
                raw = _appsService.GetOrExtractIcon(key, 96);
                _rawCache[key] = raw;
            }
        }

        if (raw == null && !string.IsNullOrWhiteSpace(appName))
        {
            string? resolved = _appsService.FindAppTargetByNameOrTitle(appName);
            if (!string.IsNullOrEmpty(resolved))
            {
                raw = _appsService.GetOrExtractIcon(resolved, 96);
            }
        }

        if (raw == null && fallbackHwnd != 0)
        {
            raw = _appsService.GetOrExtractWindowIcon(fallbackHwnd, 96);
        }

        if (raw == null)
        {
            _d2dCache[lookup] = null;
            return null;
        }

        var bmp = CrystalUIHelpers.CreateBitmapFromBgra(d2d, raw.Width, raw.Height, raw.BgraPixels);
        var wrapped = bmp != null ? new CachedAppIcon(bmp, raw.HasOpaqueCorners) : null;
        _d2dCache[lookup] = wrapped;
        return wrapped;
    }

    public void Dispose()
    {
        foreach (var kv in _d2dCache)
            kv.Value?.Bitmap.Dispose();
        _d2dCache.Clear();
    }
}
