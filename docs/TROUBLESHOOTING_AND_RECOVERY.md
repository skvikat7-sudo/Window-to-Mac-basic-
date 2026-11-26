# PROJECT CRYSTAL — Troubleshooting & Safe Recovery

## Recovery Guarantees
Project Crystal is designed so a crash or power loss never leaves your Windows desktop unusable:
- Windows Explorer (`explorer.exe`) is never terminated or replaced in the Winlogon registry.
- If Mode B Taskbar Auto-Hide was enabled, the original Windows Taskbar state is backed up to `%LocalAppData%\ProjectCrystal\taskbar_backup.json` before modification.
- If two consecutive unclean exits are detected (`session.lock` / `crash_count.txt`), Project Crystal automatically launches in **Safe Mode** (disabling WGC capture and restoring the Windows Taskbar).

## Quick Recovery Paths
1. **System Tray Exit**: Right-click the Project Crystal icon in the Windows notification tray and select **Safe Exit & Restore Windows Desktop**.
2. **Command-Line Desktop Restore**:
   Run:
   ```
   ProjectCrystal.exe --restore-desktop
   ```
   This immediately restores the original Windows Taskbar state and clears the session lock without starting the shell UI.
3. **Start Menu Safe Shortcuts**:
   - **Restore Windows Desktop**: Runs `--restore-desktop`.
   - **Project Crystal (Safe Mode)**: Runs `--safe-mode` (wallpaper backdrop mode, no taskbar hiding, no widgets).

## Common Issues
- **No optical refraction behind windows**:
  - Verify **Settings -> Glass Lab -> Capture Mode** is set to **Live Desktop Refraction (WGC)** and **Refraction Intensity** > `0`.
  - Check **Settings -> Diagnostics** for the active capture backend status and GPU adapter name.
- **High GPU/CPU usage on low-end hardware**:
  - Switch **Settings -> Performance -> Quality Preset** to **Balanced** or **Eco**, ensure **Pause Rendering When Idle** is enabled, and lower **Target FPS** to `30`.
- **Configuration corruption or unwanted visual tweaks**:
  - Open **Settings -> Glass Lab -> Reset Glass Defaults** or **Settings -> Diagnostics -> Factory Reset**.
  - Alternatively, delete `%LocalAppData%\ProjectCrystal\config.json` (the automatic `.bak` backup or clean defaults will regenerate on next launch).
- **Logs Location**:
  - `%LocalAppData%\ProjectCrystal\logs\crystal-YYYYMMDD.log`
