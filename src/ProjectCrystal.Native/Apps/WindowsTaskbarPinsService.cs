using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;

namespace ProjectCrystal.Native.Apps;

public static class WindowsTaskbarPinsService
{
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

    public static List<PinnedAppEntry> ReadRealWindowsTaskbarPins(InstalledAppsService appsService)
    {
        var result = new List<PinnedAppEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string tbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

            var lnkByFileName = new Dictionary<string, PinnedAppEntry>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(tbDir))
            {
                foreach (string lnkPath in Directory.EnumerateFiles(tbDir, "*.lnk"))
                {
                    var entry = ResolveTaskbarShortcut(lnkPath);
                    if (entry != null)
                        lnkByFileName[Path.GetFileName(lnkPath)] = entry;
                }
            }

            // Parse Taskband\Favorites to extract ordered Win32 .lnk references AND Store/UWP AUMIDs
            var orderedEntries = new List<PinnedAppEntry>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
                if (key?.GetValue("Favorites") is byte[] favBytes && favBytes.Length > 0)
                {
                    string utf16 = Encoding.Unicode.GetString(favBytes);
                    string ascii = Encoding.ASCII.GetString(favBytes);
                    var installed = appsService.GetCachedApps();

                    // Extract all .lnk names and UWP AUMIDs (!App / !...) in registry sequence
                    var matches = new List<(int Offset, PinnedAppEntry Entry)>();

                    foreach (var kv in lnkByFileName)
                    {
                        int idx = utf16.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                        {
                            string stem = Path.GetFileNameWithoutExtension(kv.Key).Replace(" ", "");
                            idx = ascii.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
                        }
                        if (idx >= 0)
                            matches.Add((idx, kv.Value));
                    }

                    foreach (Match m in Regex.Matches(utf16, @"[A-Za-z0-9_.\-]+_[a-z0-9]{13}(?:![A-Za-z0-9_.\-]+)?"))
                    {
                        string token = m.Value;
                        var matchedApp = token.Contains('!')
                            ? installed.FirstOrDefault(a => string.Equals(a.Id, token, StringComparison.OrdinalIgnoreCase))
                            : installed.FirstOrDefault(a => a.Id.StartsWith(token + "!", StringComparison.OrdinalIgnoreCase));

                        if (matchedApp == null && !token.Contains('!'))
                            continue;

                        string aumid = matchedApp?.Id ?? token;
                        string friendly = matchedApp?.Name ?? DeriveFriendlyNameFromAumid(aumid);
                        var uwpEntry = new PinnedAppEntry
                        {
                            Id = aumid,
                            Name = friendly,
                            Target = $"shell:AppsFolder\\{aumid}",
                            Arguments = "",
                            IsUwp = true
                        };
                        matches.Add((m.Index * 2, uwpEntry));
                    }

                    foreach (var item in matches.OrderBy(m => m.Offset))
                    {
                        if (seenTargets.Add(NormalizeKey(item.Entry.Target)))
                            orderedEntries.Add(item.Entry);
                    }
                }
            }
            catch (Exception ex)
            {
                CrystalLogger.Warn("TaskbarPins", $"Taskband Favorites parse fallback: {ex.Message}");
            }

            result.AddRange(orderedEntries);

            // Append any TaskBar .lnk not found inside Taskband binary
            foreach (var kv in lnkByFileName)
            {
                if (seenTargets.Add(NormalizeKey(kv.Value.Target)))
                    result.Add(kv.Value);
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Error("TaskbarPins", "Failed reading Windows Taskbar pins", ex);
        }

        // Only if Windows has zero pinned items at all, include File Explorer cleanly
        if (result.Count == 0)
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            result.Add(new PinnedAppEntry { Id = "explorer", Name = "File Explorer", Target = explorer });
        }

        CrystalLogger.Info("TaskbarPins", $"Loaded {result.Count} pinned apps from Windows Taskbar ({string.Join(", ", result.Select(r => r.Name))}).");
        return result;
    }

    public static List<PinnedAppEntry> RepairOrPopulatePinnedApps(List<PinnedAppEntry> current, InstalledAppsService appsService)
    {
        var realTaskbarPins = ReadRealWindowsTaskbarPins(appsService);
        if (current == null || current.Count == 0)
            return realTaskbarPins;

        var repaired = new List<PinnedAppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pin in current)
        {
            if (string.IsNullOrWhiteSpace(pin.Name) || string.IsNullOrWhiteSpace(pin.Target))
                continue;
            // Never allow Project Crystal itself or Setup to be pinned inside the Dock
            if (pin.Target.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase))
                continue;

            string norm = NormalizeKey(pin.Target);
            if (!seen.Add(norm))
                continue;

            if (!pin.IsUwp && Path.IsPathRooted(pin.Target) && !File.Exists(pin.Target))
            {
                var match = appsService.GetCachedApps().FirstOrDefault(a =>
                    string.Equals(a.Name, pin.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    repaired.Add(new PinnedAppEntry
                    {
                        Id = match.Id,
                        Name = match.Name,
                        Target = match.LaunchTarget,
                        Arguments = match.Arguments,
                        IsUwp = match.IsPackagedUwp
                    });
                }
                continue;
            }

            repaired.Add(pin);
        }

        return repaired.Count > 0 ? repaired : realTaskbarPins;
    }

    private static PinnedAppEntry? ResolveTaskbarShortcut(string lnkPath)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(lnkPath);
            if (name.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase))
                return null;

            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);

            var sbTarget = new StringBuilder(1024);
            link.GetPath(sbTarget, sbTarget.Capacity, 0, 0);
            string target = sbTarget.ToString();

            var sbArgs = new StringBuilder(512);
            link.GetArguments(sbArgs, sbArgs.Capacity);
            string args = sbArgs.ToString();

            if (string.IsNullOrWhiteSpace(target) && name.Contains("Explorer", StringComparison.OrdinalIgnoreCase))
            {
                target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                args = "";
            }

            if (string.IsNullOrWhiteSpace(target))
                target = lnkPath; // Shell shortcut (launchable via ShellExecute)

            return new PinnedAppEntry
            {
                Id = name.ToLowerInvariant().Replace(" ", "-"),
                Name = name,
                Target = target,
                Arguments = args,
                IsUwp = false
            };
        }
        catch
        {
            return null;
        }
    }

    private static string DeriveFriendlyNameFromAumid(string aumid)
    {
        if (aumid.Contains("WindowsStore", StringComparison.OrdinalIgnoreCase)) return "Microsoft Store";
        if (aumid.Contains("LenovoCompanion", StringComparison.OrdinalIgnoreCase)) return "Lenovo Vantage";
        string prefix = aumid.Split('_')[0].Split('.').LastOrDefault() ?? aumid;
        return prefix;
    }

    public static string NormalizeKey(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";
        if (target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            return target["shell:AppsFolder\\".Length..].Trim();
        return target.Trim();
    }
}
