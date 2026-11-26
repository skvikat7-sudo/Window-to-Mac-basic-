# PROJECT CRYSTAL — Setup & User Guide

## Installation
1. Run **`ProjectCrystalSetup.exe`** from `dist\ProjectCrystalSetup.exe`.
2. Choose an installation path (defaults to `%LocalAppData%\Programs\ProjectCrystal`, no administrator privileges required).
3. Click **Install Now**. Start Menu shortcuts and an entry in Windows **Apps & Features** are registered automatically.

## Desktop Behavior & Auto-Hide
- **Auto-Hide Top Bar & Dock (Default On)**: Both bars automatically retract completely (`Hide()`) when inactive so zero screen space or application title-bar controls (`Minimize` / `Maximize` / `Close`) are ever obstructed. Move the pointer to the top or bottom screen edge on any monitor to reveal them smoothly with spring physics. Both respect fullscreen applications and videos.
- **Original Windows Taskbar Integration**: In default **AutoHideAndReplace** mode, Project Crystal expands the desktop working area via `SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE)` and hides `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` so the old Windows taskbar does not collide with the Dock. You can temporarily peek at the original taskbar anytime via **Crystal Menu -> Peek Original Windows Taskbar** or the System Tray icon, and exiting, crashing, or uninstalling always restores the original Windows taskbar automatically.
- **Real Windows Pinned Apps**: On startup (and via **Sync Pinned Apps from Windows Taskbar** in Settings), Project Crystal imports your actual pinned Windows Taskbar shortcuts and Store/UWP apps (`%APPDATA%\...\User Pinned\TaskBar` and `Taskband\Favorites`), deduplicating multi-window instances cleanly.

## Keyboard Shortcuts
| Shortcut | Action |
|---|---|
| `Alt + Space` | Toggle Application Launcher |
| `Ctrl + Alt + Up` | Toggle Exposé Window Overview |
| `Ctrl + Alt + C` | Toggle Control Center |
| `Ctrl + Alt + N` | Toggle Notification Center |
| `Ctrl + Alt + Left` | Snap active window to Left Half |
| `Ctrl + Alt + Right` | Snap active window to Right Half |
| `Ctrl + Alt + M` | Maximize active window |

## Safe Exit & Uninstall
- Right-click the **Project Crystal** System Tray icon -> **Safe Exit & Restore Windows Desktop**, or use **Crystal Menu -> Safe Exit**, or run `ProjectCrystal.exe --restore-desktop`.
- Uninstall via Windows **Apps & Features** or Start Menu -> **Uninstall Project Crystal**.
