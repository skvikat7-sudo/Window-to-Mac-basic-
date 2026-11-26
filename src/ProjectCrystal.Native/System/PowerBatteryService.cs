using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.System;

public static class PowerBatteryService
{
    public static BatteryStatusInfo GetBatteryStatus()
    {
        if (!Win32Interop.GetSystemPowerStatus(out var s))
            return new BatteryStatusInfo(false, true, 100, -1, "AC Power");

        bool noBattery = (s.BatteryFlag & 128) != 0;
        if (noBattery)
        {
            return new BatteryStatusInfo(
                HasBattery: false,
                IsCharging: true,
                ChargePercent: 100,
                EstimatedMinutesRemaining: -1,
                PowerMode: "AC Desktop");
        }

        int pct = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 100;
        bool charging = s.ACLineStatus == 1 || (s.BatteryFlag & 8) != 0;
        int mins = s.BatteryLifeTime > 0 ? s.BatteryLifeTime / 60 : -1;
        string mode = s.SystemStatusFlag == 1 ? "Battery Saver" : (charging ? "Charging" : "On Battery");

        return new BatteryStatusInfo(true, charging, pct, mins, mode);
    }
}
