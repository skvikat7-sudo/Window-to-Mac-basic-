using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProjectCrystal.Installer;

internal static class Program
{
    private const string AppName = "Project Crystal";
    private const string AppVersion = "1.0.0";
    private const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectCrystal";
    private const string StartupRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "ProjectCrystal");

    public static string StartMenuFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs", AppName);

    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        bool silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        string? customDir = GetArgValue(args, "--dir");
        bool createStartup = args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        if (silent)
        {
            if (uninstall)
                return PerformUninstall(customDir ?? DefaultInstallDir, out _) ? 0 : 1;
            else
                return PerformInstall(customDir ?? DefaultInstallDir, createStartup, out _) ? 0 : 1;
        }

        Application.Run(new SetupForm(uninstall, customDir ?? DefaultInstallDir));
        return 0;
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    public static bool PerformInstall(string installDir, bool enableStartup, out string summary)
    {
        try
        {
            StopRunningCrystal(installDir);
            Directory.CreateDirectory(installDir);

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProjectCrystal.Installer.payload.zip");
            if (stream != null)
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(installDir, overwriteFiles: true);
            }
            else
            {
                // Fallback: copy alongside payload folder if running unpackaged
                string localPayload = Path.Combine(AppContext.BaseDirectory, "payload");
                if (!Directory.Exists(localPayload))
                {
                    summary = "Installer payload not found.";
                    return false;
                }
                CopyDirectory(localPayload, installDir);
            }

            string selfExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ProjectCrystalSetup.exe");
            string uninstallerPath = Path.Combine(installDir, "Uninstall.exe");
            if (!string.Equals(selfExe, uninstallerPath, StringComparison.OrdinalIgnoreCase) && File.Exists(selfExe))
                File.Copy(selfExe, uninstallerPath, overwrite: true);

            string appExe = Path.Combine(installDir, "ProjectCrystal.exe");
            Directory.CreateDirectory(StartMenuFolder);
            CreateShortcut(Path.Combine(StartMenuFolder, "Project Crystal.lnk"), appExe, "", installDir, "Project Crystal Liquid Glass Desktop");
            CreateShortcut(Path.Combine(StartMenuFolder, "Project Crystal (Safe Mode).lnk"), appExe, "--safe-mode", installDir, "Start Project Crystal in Safe Recovery Mode");
            CreateShortcut(Path.Combine(StartMenuFolder, "Restore Windows Desktop.lnk"), appExe, "--restore-desktop", installDir, "Restore Windows Taskbar and Desktop Work Area");
            CreateShortcut(Path.Combine(StartMenuFolder, "Uninstall Project Crystal.lnk"), uninstallerPath, "--uninstall", installDir, "Uninstall Project Crystal");

            if (enableStartup)
            {
                using var runKey = Registry.CurrentUser.CreateSubKey(StartupRegPath);
                runKey.SetValue("ProjectCrystal", $"\"{appExe}\"");
            }

            using (var reg = Registry.CurrentUser.CreateSubKey(UninstallRegPath))
            {
                reg.SetValue("DisplayName", AppName);
                reg.SetValue("DisplayVersion", AppVersion);
                reg.SetValue("Publisher", "Project Crystal");
                reg.SetValue("InstallLocation", installDir);
                reg.SetValue("DisplayIcon", appExe);
                reg.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
                reg.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --silent");
                reg.SetValue("NoModify", 1, RegistryValueKind.DWord);
                reg.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                reg.SetValue("EstimatedSize", 28000, RegistryValueKind.DWord);
            }

            summary = $"Installed successfully to {installDir}";
            return true;
        }
        catch (Exception ex)
        {
            summary = $"Installation failed: {ex.Message}";
            return false;
        }
    }

    public static bool PerformUninstall(string installDir, out string summary)
    {
        try
        {
            StopRunningCrystal(installDir);

            using (var runKey = Registry.CurrentUser.OpenSubKey(StartupRegPath, writable: true))
                runKey?.DeleteValue("ProjectCrystal", throwOnMissingValue: false);

            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, throwOnMissingSubKey: false);

            if (Directory.Exists(StartMenuFolder))
                Directory.Delete(StartMenuFolder, recursive: true);

            if (Directory.Exists(installDir))
            {
                foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch { }
                }

                // Schedule deferred cleanup if running from Uninstall.exe inside installDir
                string selfExe = Environment.ProcessPath ?? "";
                if (selfExe.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{installDir}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                else
                {
                    try { Directory.Delete(installDir, recursive: true); } catch { }
                }
            }

            summary = "Project Crystal uninstalled and Windows desktop state restored cleanly.";
            return true;
        }
        catch (Exception ex)
        {
            summary = $"Uninstall error: {ex.Message}";
            return false;
        }
    }

    private static void StopRunningCrystal(string installDir)
    {
        string exe = Path.Combine(installDir, "ProjectCrystal.exe");
        if (File.Exists(exe))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--restore-desktop", UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(2000);
            }
            catch { }
        }
        foreach (var proc in Process.GetProcessesByName("ProjectCrystal"))
        {
            try { proc.Kill(); proc.WaitForExit(1500); } catch { }
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, nint pfd, int fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(nint hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private static void CreateShortcut(string shortcutPath, string target, string args, string workDir, string desc)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetArguments(args);
        link.SetWorkingDirectory(workDir);
        link.SetDescription(desc);
        ((IPersistFile)link).Save(shortcutPath, false);
    }
}

internal sealed class SetupForm : Form
{
    private readonly bool _isUninstall;
    private readonly TextBox _dirBox;
    private readonly CheckBox _chkStartup;
    private readonly CheckBox _chkLaunch;
    private readonly Label _statusLabel;
    private readonly Button _actionBtn;

    public SetupForm(bool isUninstall, string defaultDir)
    {
        _isUninstall = isUninstall;
        Text = isUninstall ? "Uninstall Project Crystal" : "Project Crystal Setup";
        Size = new Size(560, 390);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(18, 22, 32);
        ForeColor = Color.White;

        var title = new Label
        {
            Text = isUninstall ? "Uninstall Project Crystal" : "Install Project Crystal 1.0",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Location = new Point(28, 24),
            AutoSize = true
        };
        var subtitle = new Label
        {
            Text = isUninstall
                ? "Safely remove Project Crystal and restore original Windows desktop & taskbar settings."
                : "macOS-inspired Liquid Glass Desktop Environment with real-time GPU optical refraction.",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(190, 205, 225),
            Location = new Point(30, 66),
            Size = new Size(490, 44)
        };

        var dirLabel = new Label { Text = "Installation Directory:", Location = new Point(30, 124), AutoSize = true };
        _dirBox = new TextBox
        {
            Text = defaultDir,
            Location = new Point(30, 148),
            Width = 480,
            BackColor = Color.FromArgb(30, 36, 50),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = isUninstall
        };

        _chkStartup = new CheckBox
        {
            Text = "Launch Project Crystal automatically when signing in to Windows",
            Location = new Point(30, 188),
            AutoSize = true,
            Visible = !isUninstall
        };
        _chkLaunch = new CheckBox
        {
            Text = "Launch Project Crystal after installation finishes",
            Location = new Point(30, 218),
            AutoSize = true,
            Checked = true,
            Visible = !isUninstall
        };

        _statusLabel = new Label
        {
            Text = "No administrator privileges required (user-scoped install).",
            Location = new Point(30, 258),
            Size = new Size(480, 36),
            ForeColor = Color.FromArgb(130, 200, 255)
        };

        _actionBtn = new Button
        {
            Text = isUninstall ? "Uninstall" : "Install Now",
            Location = new Point(370, 304),
            Size = new Size(140, 36),
            BackColor = isUninstall ? Color.FromArgb(195, 58, 64) : Color.FromArgb(46, 150, 240),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _actionBtn.Click += OnActionClick;

        var cancelBtn = new Button
        {
            Text = "Close",
            Location = new Point(256, 304),
            Size = new Size(100, 36),
            BackColor = Color.FromArgb(42, 48, 64),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        cancelBtn.Click += (_, __) => Close();

        Controls.AddRange(new Control[] { title, subtitle, dirLabel, _dirBox, _chkStartup, _chkLaunch, _statusLabel, _actionBtn, cancelBtn });
    }

    private void OnActionClick(object? sender, EventArgs e)
    {
        _actionBtn.Enabled = false;
        string targetDir = _dirBox.Text.Trim();
        bool ok;
        string msg;

        if (_isUninstall)
        {
            ok = Program.PerformUninstall(targetDir, out msg);
        }
        else
        {
            ok = Program.PerformInstall(targetDir, _chkStartup.Checked, out msg);
            if (ok && _chkLaunch.Checked)
            {
                string exe = Path.Combine(targetDir, "ProjectCrystal.exe");
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            }
        }

        _statusLabel.Text = msg;
        _statusLabel.ForeColor = ok ? Color.LightGreen : Color.OrangeRed;
        if (ok) _actionBtn.Text = "Done";
        else _actionBtn.Enabled = true;
    }
}
