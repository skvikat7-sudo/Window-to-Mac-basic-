using System.Net.NetworkInformation;
using System.Net.Sockets;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace ProjectCrystal.Native.System;

public sealed class NetworkRadioService
{
    public async Task<NetworkStatusInfo> GetNetworkStatusAsync()
    {
        bool isConnected = false;
        bool hasWifi = false;
        bool isWifiConnected = false;
        bool isWifiEnabled = false;
        string activeName = "Disconnected";
        string connType = "Offline";
        string ip = "-";
        long speedMbps = 0;
        int signalPercent = 0;

        try
        {
            var interfaces = await Task.Run(() => NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                         && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                         && !n.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase))
                .ToArray());

            foreach (var ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    hasWifi = true;
            }

            var active = interfaces.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up);
            if (active != null)
            {
                isConnected = true;
                activeName = active.Name;
                speedMbps = active.Speed > 0 ? active.Speed / 1_000_000 : 0;
                connType = active.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
                if (active.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    isWifiConnected = true;

                var ipProps = active.GetIPProperties();
                var v4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) ip = v4.Address.ToString();
            }

            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null)
                {
                    activeName = profile.ProfileName ?? activeName;
                    if (profile.IsWlanConnectionProfile)
                    {
                        hasWifi = true;
                        isWifiConnected = true;
                        byte? bars = profile.GetSignalBars();
                        if (bars.HasValue) signalPercent = Math.Clamp(bars.Value * 20, 0, 100);
                    }
                }
            }
            catch { }

            try
            {
                var radios = await Radio.GetRadiosAsync();
                var wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                if (wifiRadio != null)
                {
                    hasWifi = true;
                    isWifiEnabled = wifiRadio.State == RadioState.On;
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Network", $"GetNetworkStatusAsync error: {ex.Message}");
        }

        return new NetworkStatusInfo(isConnected, hasWifi, isWifiConnected, isWifiEnabled, activeName, connType, ip, speedMbps, signalPercent);
    }

    public async Task<bool> SetWifiEnabledAsync(bool enable)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return false;
            var radios = await Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifi is null) return false;
            var result = await wifi.SetStateAsync(enable ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Network", $"SetWifiEnabledAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<BluetoothStatusInfo> GetBluetoothStatusAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bt is null)
                return new BluetoothStatusInfo(false, false, "No Bluetooth Hardware", 0);

            return new BluetoothStatusInfo(true, bt.State == RadioState.On, bt.Name, 0);
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Bluetooth", $"Query failed: {ex.Message}");
            return new BluetoothStatusInfo(false, false, "Unavailable", 0);
        }
    }

    public async Task<bool> SetBluetoothEnabledAsync(bool enable)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return false;
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (bt is null) return false;
            var res = await bt.SetStateAsync(enable ? RadioState.On : RadioState.Off);
            return res == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Bluetooth", $"SetBluetoothEnabledAsync failed: {ex.Message}");
            return false;
        }
    }
}
