using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.WindowManagement;

public sealed class WindowLifecycleMonitor : IDisposable
{
    private readonly Win32Interop.WinEventDelegate _proc;
    private readonly List<nint> _hooks = new();
    private bool _disposed;

    public event Action<nint>? WindowForegroundChanged;
    public event Action<nint>? WindowMinimizeStarted;
    public event Action<nint>? WindowMinimizeEnded;
    public event Action<nint>? WindowShown;
    public event Action<nint>? WindowDestroyed;

    public WindowLifecycleMonitor()
    {
        // Keep delegate rooted for the lifetime of the monitor so GC cannot collect the callback
        _proc = OnWinEvent;
    }

    public void Start()
    {
        if (_hooks.Count > 0 || _disposed) return;
        uint flags = Win32Interop.WINEVENT_OUTOFCONTEXT | Win32Interop.WINEVENT_SKIPOWNPROCESS;
        AddHook(Win32Interop.EVENT_SYSTEM_FOREGROUND, Win32Interop.EVENT_SYSTEM_FOREGROUND, flags);
        AddHook(Win32Interop.EVENT_SYSTEM_MINIMIZESTART, Win32Interop.EVENT_SYSTEM_MINIMIZEEND, flags);
        AddHook(Win32Interop.EVENT_OBJECT_DESTROY, Win32Interop.EVENT_OBJECT_SHOW, flags);
    }

    private void AddHook(uint min, uint max, uint flags)
    {
        nint h = Win32Interop.SetWinEventHook(min, max, 0, _proc, 0, 0, flags);
        if (h != 0) _hooks.Add(h);
        else CrystalLogger.Debug("Lifecycle", $"SetWinEventHook failed for 0x{min:X}..0x{max:X}");
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == 0 || idObject != Win32Interop.OBJID_WINDOW || idChild != 0) return;

        try
        {
            if (eventType == Win32Interop.EVENT_SYSTEM_FOREGROUND)
                WindowForegroundChanged?.Invoke(hwnd);
            else if (eventType == Win32Interop.EVENT_SYSTEM_MINIMIZESTART)
                WindowMinimizeStarted?.Invoke(hwnd);
            else if (eventType == Win32Interop.EVENT_SYSTEM_MINIMIZEEND)
                WindowMinimizeEnded?.Invoke(hwnd);
            else if (eventType == Win32Interop.EVENT_OBJECT_SHOW)
                WindowShown?.Invoke(hwnd);
            else if (eventType == Win32Interop.EVENT_OBJECT_DESTROY)
                WindowDestroyed?.Invoke(hwnd);
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Lifecycle", $"WinEvent callback error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (nint h in _hooks)
            Win32Interop.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
