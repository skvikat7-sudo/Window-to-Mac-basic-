using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using Xunit;

namespace ProjectCrystal.Tests;

public sealed class ConfigAndRecoveryTests
{
    [Fact]
    public void ConfigManager_PersistsClampsBackupsAndRestoresOnCorruption()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new ConfigManager(tempDir);
            mgr.Update(c =>
            {
                c.Glass.RefractionIntensity = 99f; // should clamp to 2.5f
                c.Dock.Position = DockPosition.Left;
                c.MenuBar.Use24HourClock = true;
            }, saveNow: true);

            Assert.Equal(2.5f, mgr.Current.Glass.RefractionIntensity);
            Assert.True(File.Exists(mgr.ConfigPath));

            // Second write ensures .bak exists
            mgr.Update(c => c.Glass.Magnification = 1.15f, saveNow: true);
            Assert.True(File.Exists(mgr.BackupPath));

            // Corrupt primary config and ensure fallback restores cleanly from backup
            File.WriteAllText(mgr.ConfigPath, "{ INVALID JSON !!!");
            var recoveredMgr = new ConfigManager(tempDir);
            Assert.Equal(DockPosition.Left, recoveredMgr.Current.Dock.Position);
            Assert.True(recoveredMgr.Current.MenuBar.Use24HourClock);

            // Export / Reset / Import cycle
            string exportPath = Path.Combine(tempDir, "exported.json");
            recoveredMgr.ExportBackup(exportPath);
            recoveredMgr.ResetToDefaults();
            Assert.Equal(DockPosition.Bottom, recoveredMgr.Current.Dock.Position);
            Assert.True(recoveredMgr.ImportBackup(exportPath));
            Assert.Equal(DockPosition.Left, recoveredMgr.Current.Dock.Position);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void RecoveryGuard_DetectsUncleanSessionAndTracksTaskbarBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalRec_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rg1 = new RecoveryGuard(tempDir);
            rg1.BeginSession();
            Assert.False(rg1.PreviousSessionCrashed);
            rg1.SaveTaskbarOriginalState(false);

            // Simulate crash (no EndSessionCleanly)
            var rg2 = new RecoveryGuard(tempDir);
            rg2.BeginSession();
            Assert.True(rg2.PreviousSessionCrashed);
            Assert.Equal(1, rg2.ConsecutiveCrashCount);
            var tb = rg2.ReadTaskbarBackup();
            Assert.NotNull(tb);
            Assert.False(tb!.WasAutoHide);

            rg2.EndSessionCleanly();
            var rg3 = new RecoveryGuard(tempDir);
            rg3.BeginSession();
            Assert.False(rg3.PreviousSessionCrashed);
            rg3.EndSessionCleanly();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
