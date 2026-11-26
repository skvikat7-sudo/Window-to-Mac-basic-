using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Interop;

namespace ProjectCrystal.Native.System;

public sealed class SystemMetricsService
{
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuSample;

    private long _prevBytesRx;
    private long _prevBytesTx;
    private DateTime _prevNetTime = DateTime.MinValue;

    public SystemMetricsSnapshot Sample()
    {
        double cpuPct = SampleCpuPercent();
        var (ramUsedGb, ramTotalGb, ramPct) = SampleMemory();
        var (downKbps, upKbps) = SampleNetworkKbps();
        var drives = SampleDrives();
        int procCount = Process.GetProcesses().Length;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        return new SystemMetricsSnapshot(
            cpuPct,
            ramUsedGb,
            ramTotalGb,
            ramPct,
            downKbps,
            upKbps,
            drives,
            procCount,
            uptime);
    }

    private double SampleCpuPercent()
    {
        if (!Win32Interop.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        ulong i = idle.ToUInt64();
        ulong k = kernel.ToUInt64();
        ulong u = user.ToUInt64();

        if (!_hasCpuSample)
        {
            _prevIdle = i;
            _prevKernel = k;
            _prevUser = u;
            _hasCpuSample = true;
            return 0;
        }

        ulong dIdle = i - _prevIdle;
        ulong dKernel = k - _prevKernel;
        ulong dUser = u - _prevUser;

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;

        ulong total = dKernel + dUser;
        if (total == 0) return 0;
        return Math.Clamp((total - dIdle) * 100.0 / total, 0.0, 100.0);
    }

    private static (double UsedGb, double TotalGb, double Pct) SampleMemory()
    {
        var mem = new Win32Interop.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX>() };
        if (!Win32Interop.GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0)
            return (0, 0, 0);

        double totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0);
        double pct = usedGb * 100.0 / totalGb;
        return (Math.Round(usedGb, 2), Math.Round(totalGb, 2), Math.Round(pct, 1));
    }

    private (double DownKbps, double UpKbps) SampleNetworkKbps()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var stats = ni.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch { }

        var now = DateTime.UtcNow;
        if (_prevNetTime == DateTime.MinValue)
        {
            _prevBytesRx = rx;
            _prevBytesTx = tx;
            _prevNetTime = now;
            return (0, 0);
        }

        double secs = Math.Max(0.1, (now - _prevNetTime).TotalSeconds);
        double down = Math.Max(0, (rx - _prevBytesRx) * 8.0 / 1000.0 / secs);
        double up = Math.Max(0, (tx - _prevBytesTx) * 8.0 / 1000.0 / secs);

        _prevBytesRx = rx;
        _prevBytesTx = tx;
        _prevNetTime = now;
        return (Math.Round(down, 1), Math.Round(up, 1));
    }

    private static IReadOnlyList<DriveUsageInfo> SampleDrives()
    {
        var list = new List<DriveUsageInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady || d.TotalSize <= 0) continue;
                double totalGb = d.TotalSize / (1024.0 * 1024.0 * 1024.0);
                double freeGb = d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double usedGb = Math.Max(0, totalGb - freeGb);
                list.Add(new DriveUsageInfo(
                    d.Name.TrimEnd('\\'),
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                    Math.Round(usedGb, 1),
                    Math.Round(totalGb, 1),
                    Math.Round(usedGb * 100.0 / totalGb, 1)));
            }
        }
        catch { }
        return list;
    }
}
