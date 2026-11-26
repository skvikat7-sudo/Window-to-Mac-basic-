# PROJECT CRYSTAL — Technical Limitations & Windows Composition Notes

Project Crystal documents Windows platform constraints transparently:

1. **Screen Recording vs. Live Desktop Refraction (`WDA_EXCLUDEFROMCAPTURE`)**:
   - Windows Desktop Window Manager (DWM) does not expose an arbitrary public custom-HLSL shader hook on `CompositionBackdropBrush`. Built-in `CompositionEffectFactory` supports only fixed D2D blur/tint effects and cannot perform Snell refraction or chromatic dispersion on other native windows.
   - To sample actual desktop pixels behind floating overlays in real time on the GPU without an infinite optical feedback loop, Project Crystal uses `Windows.Graphics.Capture` (`GraphicsCaptureItem.CreateForMonitor`, borderless) paired with `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`.
   - **Trade-off**: While **Live Desktop Refraction (WGC)** is active, external screen-capture apps (OBS, Teams, Zoom) using WGC/BitBlt will also omit Project Crystal's windows because `WDA_EXCLUDEFROMCAPTURE` is a DWM-level window attribute.
   - **Built-in Alternative**: Switch **Capture Mode** in **Settings -> Glass Lab** to **Wallpaper Composited (Screen-Recorder Visible)** if you need Project Crystal visible during recordings/streams; this disables `WDA_EXCLUDEFROMCAPTURE` and refracts the live Windows wallpaper cleanly without feedback artifacts.
   - Also note: `IDXGIOutput1.DuplicateOutput` (DXGI Desktop Duplication) returns `E_ACCESSDENIED (0x80070005)` in some Windows desktop/session configurations (observed on this Windows 11 Pro Build 22621 environment), making `Windows.Graphics.Capture` the primary reliable unprivileged GPU capture backend.

2. **Windows Shell Launcher Restrictions (Mode B)**:
   - Official Microsoft **Shell Launcher (v1/v2)** (`AssignedAccess`) is restricted by Windows licensing to **Windows Enterprise and Education** editions. On **Windows 11 Home / Pro** (such as this machine's `Professional` edition), Shell Launcher is unavailable.
   - Project Crystal therefore never forcibly kills `explorer.exe` and never performs unsafe `HKLM\...\Winlogon\Shell` registry replacements. Mode B instead provides reversible Windows Taskbar auto-hide (via `SHAppBarMessage`) and optional top work-area AppBar reservation with automatic restoration on exit or crash recovery.

3. **Hardware-Dependent Controls**:
   - **Wi-Fi / Bluetooth / Battery**: Displayed and enabled only when physical hardware radios/batteries are reported by Windows (`Radio.GetRadiosAsync`, `GetSystemPowerStatus`). On desktop PCs lacking Wi-Fi/Bluetooth/Battery hardware, Project Crystal reports real Ethernet status and `"No Hardware"` rather than simulating non-existent switches.
   - **Monitor Brightness**: Supported via DDC/CI (`dxva2.dll`) on compatible external monitors (e.g., `S22e-20`) and WMI on laptop panels; if a display link blocks DDC/CI writes, Control Center displays the hardware limitation honestly.

4. **Windows Notification Listener**:
   - Real Windows toast history relies on WinRT `UserNotificationListener` user permission (`Allowed` / `Denied`). If denied in Windows Privacy -> Notifications settings, or running in an isolated session without shell notification access, Notification Center displays the exact listener status instead of fabricated alerts.
