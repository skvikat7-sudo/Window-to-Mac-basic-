# PROJECT CRYSTAL
### A macOS-Inspired Liquid Glass Desktop Environment for Windows 11

Project Crystal transforms the Windows desktop into a cohesive, fluid workspace powered by **Direct3D 11**, **HLSL optical refraction shaders**, **DirectComposition**, **Direct2D 1.1**, and **Windows Runtime / Win32 APIs**.

---

## Quick Start (Download & Run on Any Windows 10/11 PC)

### Option A — One-Click Setup (Recommended)
1. Click **Code → Download ZIP** (or clone this repository) and extract it.
2. Double-click **`Install-ProjectCrystal.bat`** (or run **`dist\ProjectCrystalSetup.exe`** directly).
   - `Install-ProjectCrystal.bat` automatically checks for the **.NET 8 Desktop Runtime (x64)** (installing it via `winget` if needed) and launches the installer.
3. Follow the setup prompt to install and launch Project Crystal.

> **Requirements:** Windows 10/11 (x64), DirectX 11 capable GPU, and [.NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

---

## Key Capabilities
- **Genuine Liquid Glass Optical Engine**: Real-time screen-space refraction of live desktop windows via `Windows.Graphics.Capture` + `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` with analytic superellipse SDFs, 3D meniscus + convex dome surface normals, magnification, spectral R/G/B chromatic dispersion, Fresnel rim lighting, internal bounce reflections, and adaptive background luminance tinting.
- **Cohesive Desktop Shell**:
  - **Top Menu Bar**: Crystal system menu, active application menu, media mini-pill, network/Bluetooth/volume/battery indicators, search, Control Center, Notification Center, and clock.
  - **Animated Liquid Glass Dock**: Spring-physics parabolic hover magnification, running/active indicators, drag-and-drop pinning/reordering, folder stacks (`Downloads`), right-click context menus, Bottom/Left/Right positioning, and auto-hide.
  - **Application Launcher** (`Alt+Space`): Instant search across installed Win32 & UWP applications (`shell:AppsFolder`), categories, recent apps, quick math (`=...`), and keyboard navigation.
  - **Control Center** (`Ctrl+Alt+C`): Real Windows audio volume & output device switching, DDC/CI monitor brightness, Wi-Fi/Ethernet & Bluetooth hardware status/toggles, Dark/Light mode switching, SMTC media controls, and quick window tiling.
  - **Notification Center** (`Ctrl+Alt+N`): Windows Action Center integration via `UserNotificationListener`, mini-calendar, app grouping, Do Not Disturb, individual dismissal, and Clear All.
  - **Exposé & Window Management** (`Ctrl+Alt+Up`, `Ctrl+Alt+Left/Right/M`): Live `DwmRegisterThumbnail` window overview, window snapping, and virtual desktop switching.
  - **Desktop Widgets**: Draggable Clock, Open-Meteo Live Weather, System Telemetry (CPU/RAM/Network/Disk), SMTC Media Player, and Calendar.
  - **Glass Appearance Lab**: Real-time tuning of Transparency, Refraction, Distortion, Magnification, Blur, Dispersion, Tint, Reflections, Edge Lighting, Depth, and Animation Speed with presets and a live GPU test lens.

---

## Keyboard Shortcuts
| Shortcut | Action |
|----------|--------|
| `Alt + Space` | Open Application Launcher |
| `Ctrl + Alt + C` | Open Control Center |
| `Ctrl + Alt + N` | Open Notification Center |
| `Ctrl + Alt + Up` | Open Exposé Window Overview |
| `Ctrl + Alt + Left / Right / M` | Snap / Maximize Active Window |
| `Esc` | Close active overlay / Glass Lab |

---

## Building From Source
Requires **.NET 8.0 SDK** on Windows x64:

```powershell
# Clone repository
git clone https://github.com/skvikat7-sudo/Window-to-Mac-basic-.git
cd Window-to-Mac-basic-

# Run automated tests
dotnet test src\ProjectCrystal.Tests\ProjectCrystal.Tests.csproj --configuration Release

# Build standalone installer (dist\ProjectCrystalSetup.exe)
.\package.ps1
```

## Safe Recovery
If you ever need to restore the original Windows Taskbar immediately:
```powershell
"$env:LOCALAPPDATA\Programs\ProjectCrystal\ProjectCrystal.exe" --restore-desktop
```

## Documentation
- [Architecture & Rendering Pipeline](docs/ARCHITECTURE.md)
- [User Guide & Shortcuts](docs/USER_GUIDE.md)
- [Technical Limitations & Windows Composition Notes](docs/TECHNICAL_LIMITATIONS.md)
- [Troubleshooting & Safe Recovery](docs/TROUBLESHOOTING_AND_RECOVERY.md)
