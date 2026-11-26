using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;

namespace ProjectCrystal.Shell;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        CrystalLogger.Initialize();

        // CLI Visual Verification Mode (renders real D3D11+HLSL+D2D1 PNG proofs and exits cleanly)
        if (args.Length >= 1 && string.Equals(args[0], "--verify-visual", StringComparison.OrdinalIgnoreCase))
        {
            string outDir = args.Length >= 2 ? args[1] : Path.Combine(AppContext.BaseDirectory, "visual_verify");
            ShellCoordinator.RunVisualVerificationAsync(outDir).GetAwaiter().GetResult();
            return 0;
        }

        // CLI Restore Mode (emergency recovery command to restore taskbar/config)
        if (args.Length >= 1 && string.Equals(args[0], "--restore-desktop", StringComparison.OrdinalIgnoreCase))
        {
            var rg = new RecoveryGuard();
            var shell = new Native.WindowManagement.ShellIntegrationService(rg);
            shell.RestoreTaskbarIfBackedUp();
            rg.EndSessionCleanly();
            return 0;
        }

        using var singleInstance = new Mutex(true, @"Local\ProjectCrystal_Desktop_Mutex", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            CrystalLogger.Warn("Startup", "Project Crystal is already running.");
            return 0;
        }

        var recoveryGuard = new RecoveryGuard();
        recoveryGuard.BeginSession();
        // Safe Mode only engages after 4+ consecutive unclean shutdowns (forced kills during development
        // count as crash streak 1-2, so we need a higher threshold to avoid false positives).
        // A crash streak of 1-2 is normal during development; 4+ indicates a true crash loop.
        bool safeMode = args.Any(a => string.Equals(a, "--safe-mode", StringComparison.OrdinalIgnoreCase))
                     || (recoveryGuard.PreviousSessionCrashed && recoveryGuard.ConsecutiveCrashCount >= 4);

        if (safeMode)
            CrystalLogger.Warn("Startup", "Starting in Safe Mode (Live WGC capture & taskbar auto-hide disabled for recovery).");

        var configMgr = new ConfigManager();
        ShellCoordinator? coordinator = null;

        Application.ThreadException += (_, exArgs) =>
        {
            CrystalLogger.Error("UnhandledUI", "Unhandled UI exception", exArgs.Exception);
            coordinator?.SafeExitApplication();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
        {
            CrystalLogger.Error("UnhandledDomain", "Fatal exception", exArgs.ExceptionObject as Exception);
            try
            {
                new Native.WindowManagement.ShellIntegrationService(recoveryGuard).RestoreTaskbarIfBackedUp();
            }
            catch { }
        };

        try
        {
            coordinator = new ShellCoordinator(configMgr, recoveryGuard, safeMode);
            var ctx = new ApplicationContext();
            var initTimer = new System.Windows.Forms.Timer { Interval = 10 };
            initTimer.Tick += async (_, __) =>
            {
                initTimer.Stop();
                initTimer.Dispose();
                await coordinator.InitializeAsync();
            };
            initTimer.Start();
            Application.Run(ctx);
        }
        finally
        {
            coordinator?.Dispose();
            recoveryGuard.EndSessionCleanly();
        }

        return 0;
    }
}
