using System.Runtime.InteropServices;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;

namespace ProjectCrystal.Native.System;

public sealed class CoreAudioService
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint STGM_READ = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint pwszVal;
        public nint p2;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(nint pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, nint ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, nint ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, nint pEndpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, long pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, nint pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, nint mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, int eRole);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, bool bVisible);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    public (float VolumeScalar, bool IsMuted, string DefaultDeviceName) GetMasterVolumeState()
    {
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return (0.5f, false, "Unavailable");

            int hr = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device);
            if (hr != 0 || device is null) return (0f, true, "No Output Device");

            string name = GetDeviceName(device);
            var guid = IID_IAudioEndpointVolume;
            hr = device.Activate(ref guid, 23, 0, out object volObj);
            if (hr != 0 || volObj is not IAudioEndpointVolume endpoint) return (0.5f, false, name);

            endpoint.GetMasterVolumeLevelScalar(out float level);
            endpoint.GetMute(out bool muted);
            return (Math.Clamp(level, 0f, 1f), muted, name);
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Audio", $"GetMasterVolumeState failed: {ex.Message}");
            return (0.5f, false, "Audio Error");
        }
    }

    public bool SetMasterVolumeScalar(float scalar)
    {
        scalar = Math.Clamp(scalar, 0f, 1f);
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return false;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device is null) return false;
            var guid = IID_IAudioEndpointVolume;
            if (device.Activate(ref guid, 23, 0, out object volObj) != 0 || volObj is not IAudioEndpointVolume endpoint) return false;

            Guid ctx = Guid.Empty;
            int hr = endpoint.SetMasterVolumeLevelScalar(scalar, ref ctx);
            if (scalar > 0.001f) endpoint.SetMute(false, ref ctx);
            return hr == 0;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Audio", $"SetMasterVolumeScalar failed: {ex.Message}");
            return false;
        }
    }

    public bool ToggleMute()
    {
        var (_, isMuted, _) = GetMasterVolumeState();
        return SetMute(!isMuted);
    }

    public bool SetMute(bool muted)
    {
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return false;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device is null) return false;
            var guid = IID_IAudioEndpointVolume;
            if (device.Activate(ref guid, 23, 0, out object volObj) != 0 || volObj is not IAudioEndpointVolume endpoint) return false;

            Guid ctx = Guid.Empty;
            return endpoint.SetMute(muted, ref ctx) == 0;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Audio", $"SetMute failed: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return list;

            string defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var defDev) == 0 && defDev != null)
                defDev.GetId(out defaultId);

            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != 0 || collection is null)
                return list;

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var dev) == 0 && dev != null)
                {
                    dev.GetId(out string id);
                    string name = GetDeviceName(dev);
                    list.Add(new AudioDeviceInfo(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), true));
                }
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Audio", $"GetOutputDevices failed: {ex.Message}");
        }
        return list;
    }

    public bool SetDefaultOutputDevice(string deviceId)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            if (type is null) return false;
            var obj = Activator.CreateInstance(type);
            if (obj is IPolicyConfig policy)
            {
                policy.SetDefaultEndpoint(deviceId, 0); // Console
                policy.SetDefaultEndpoint(deviceId, 1); // Multimedia
                policy.SetDefaultEndpoint(deviceId, 2); // Communications
                CrystalLogger.Info("Audio", $"Switched default output device to {deviceId}");
                return true;
            }
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Audio", $"SetDefaultOutputDevice failed: {ex.Message}");
        }
        return false;
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
        return type is null ? null : Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    private static string GetDeviceName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(STGM_READ, out var store) == 0 && store != null)
            {
                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out var pv) == 0)
                {
                    try
                    {
                        if (pv.vt == 31 && pv.pwszVal != 0) // VT_LPWSTR
                            return Marshal.PtrToStringUni(pv.pwszVal) ?? "Audio Device";
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }
            }
        }
        catch { }
        return "Audio Output";
    }
}
