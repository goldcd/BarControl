using System;
using System.Runtime.InteropServices;

namespace BarControl.Core.Audio;

/// <summary>
/// Native WASAPI (Windows Audio Session API) COM interfaces and definitions.
/// Used for zero-dependency, low-overhead audio keep-alive streaming.
/// Inspired by SoundKeeper (https://github.com/vrubleg/soundkeeper).
/// </summary>
public static class Wasapi
{
    public const int eRender = 0;
    public const int eMultimedia = 1;

    public const int DEVICE_STATE_ACTIVE = 0x00000001;
    public const int DEVICE_STATE_DISABLED = 0x00000002;
    public const int DEVICE_STATE_NOTPRESENT = 0x00000004;
    public const int DEVICE_STATE_UNPLUGGED = 0x00000008;
    public const int DEVICE_STATEMASK_ALL = 0x0000000F;
    public const int STGM_READ = 0x00000000;

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const int AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
    public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;

    public const int CLSCTX_INPROC_SERVER = 1;
    public const int CLSCTX_ALL = 23;

    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_EXCLUSIVE_MODE_ONLY = unchecked((int)0x8889000E);

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorCom
    {
    }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out IntPtr pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // Native PROPVARIANT is 24 bytes on x64 (16 on x86); size must match or GetValue overruns the struct.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public uint blobSize;
        [FieldOffset(16)] public IntPtr blobData;
    }

    /// <summary>PKEY_AudioEngine_DeviceFormat: the real format the endpoint hardware outputs.</summary>
    public static readonly PROPERTYKEY PKEY_AudioEngine_DeviceFormat = new()
    {
        fmtid = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"),
        pid = 0
    };

    /// <summary>
    /// Returns true when the endpoint's hardware output format is 16-bit PCM. The shared-mode mix format
    /// is always 32-bit float, so it cannot be used to decide this. A 24-bit-sized keep-alive sample
    /// rounds to digital silence on a 16-bit output and never reaches the device.
    /// </summary>
    public static bool IsDeviceOutput16Bit(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(STGM_READ, out IPropertyStore store) != 0 || store == null)
                return false;
            try
            {
                var key = PKEY_AudioEngine_DeviceFormat;
                if (store.GetValue(ref key, out PROPVARIANT pv) != 0)
                    return false;
                try
                {
                    // VT_BLOB = 65, blob contains a WAVEFORMATEX(-TENSIBLE)
                    if (pv.vt == 65 && pv.blobData != IntPtr.Zero && pv.blobSize >= 18)
                    {
                        ushort bits = (ushort)Marshal.ReadInt16(pv.blobData, 14);
                        return bits == 16;
                    }
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            // Fall through to default (24-bit sized sample)
        }
        return false;
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr ppv);
    }

    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, int dwFlags);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
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

    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotify);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIO_VOLUME_NOTIFICATION_DATA
    {
        public Guid guidEventContext;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bMuted;
        public float fMasterVolume;
        public uint nChannels;
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory")]
    public static extern void ZeroMemory(IntPtr dest, IntPtr size);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    public record AudioEndpointInfo(string Id, string FriendlyName, int State = 1);

    /// <summary>
    /// Enumerates all audio render endpoints in Windows, including active and standby HDMI endpoints.
    /// Combines COM IMMDeviceEnumerator with registry verification so standby HDMI devices
    /// (such as soundbars that have entered sleep mode) are reliably discovered.
    /// </summary>
    public static System.Collections.Generic.List<AudioEndpointInfo> GetActiveAudioEndpoints()
    {
        var list = new System.Collections.Generic.List<AudioEndpointInfo>();
        var seenIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Enumerate via IMMDeviceEnumerator
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            int hr = enumerator.EnumAudioEndpoints(eRender, DEVICE_STATEMASK_ALL, out IMMDeviceCollection coll);
            if (hr == 0 && coll != null)
            {
                coll.GetCount(out uint count);
                var pkeyFriendlyName = new PROPERTYKEY
                {
                    fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                    pid = 14
                };
                var pkeyDeviceDesc = new PROPERTYKEY
                {
                    fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                    pid = 2
                };
                var pkeyInterfaceFriendly = new PROPERTYKEY
                {
                    fmtid = new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"),
                    pid = 6
                };

                for (uint i = 0; i < count; i++)
                {
                    if (coll.Item(i, out IMMDevice dev) == 0 && dev != null)
                    {
                        try
                        {
                            dev.GetId(out string id);
                            dev.GetState(out int state);
                            string friendlyName = string.Empty;
                            string deviceDesc = string.Empty;
                            string interfaceDesc = string.Empty;

                            if (dev.OpenPropertyStore(STGM_READ, out IPropertyStore store) == 0 && store != null)
                            {
                                try
                                {
                                    if (store.GetValue(ref pkeyFriendlyName, out PROPVARIANT pv14) == 0)
                                    {
                                        if (pv14.vt == 31 && pv14.pwszVal != IntPtr.Zero)
                                        {
                                            friendlyName = Marshal.PtrToStringUni(pv14.pwszVal) ?? string.Empty;
                                        }
                                        PropVariantClear(ref pv14);
                                    }

                                    if (store.GetValue(ref pkeyDeviceDesc, out PROPVARIANT pv2) == 0)
                                    {
                                        if (pv2.vt == 31 && pv2.pwszVal != IntPtr.Zero)
                                        {
                                            deviceDesc = Marshal.PtrToStringUni(pv2.pwszVal) ?? string.Empty;
                                        }
                                        PropVariantClear(ref pv2);
                                    }

                                    if (store.GetValue(ref pkeyInterfaceFriendly, out PROPVARIANT pv6) == 0)
                                    {
                                        if (pv6.vt == 31 && pv6.pwszVal != IntPtr.Zero)
                                        {
                                            interfaceDesc = Marshal.PtrToStringUni(pv6.pwszVal) ?? string.Empty;
                                        }
                                        PropVariantClear(ref pv6);
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(store);
                                }
                            }

                            string effectiveName = !string.IsNullOrWhiteSpace(friendlyName)
                                ? friendlyName
                                : (!string.IsNullOrWhiteSpace(deviceDesc)
                                    ? (!string.IsNullOrWhiteSpace(interfaceDesc) ? $"{deviceDesc} ({interfaceDesc})" : deviceDesc)
                                    : string.Empty);

                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(effectiveName) && !seenIds.Contains(id))
                            {
                                seenIds.Add(id);
                                list.Add(new AudioEndpointInfo(id, effectiveName, state));
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(dev);
                        }
                    }
                }
                Marshal.ReleaseComObject(coll);
            }
        }
        catch
        {
            // Fallback gracefully on systems with restricted audio service
        }

        // 2. Supplement with registry enumeration of MMDevices\Audio\Render
        // (Guarantees standby HDMI devices like SoundSlayer or monitors are discovered even if asleep)
        try
        {
            using var renderKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (renderKey != null)
            {
                foreach (var subKeyName in renderKey.GetSubKeyNames())
                {
                    string id = $"{{0.0.0.00000000}}.{subKeyName}";
                    if (seenIds.Contains(id)) continue;

                    using var devKey = renderKey.OpenSubKey(subKeyName);
                    using var propsKey = renderKey.OpenSubKey($@"{subKeyName}\Properties");
                    if (devKey != null && propsKey != null)
                    {
                        int state = (int)(devKey.GetValue("DeviceState") ?? 0);
                        string p14 = propsKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14") as string ?? string.Empty;
                        string p2 = propsKey.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string ?? string.Empty;
                        string p6 = propsKey.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string ?? string.Empty;

                        string effectiveName = !string.IsNullOrWhiteSpace(p14)
                            ? p14
                            : (!string.IsNullOrWhiteSpace(p2)
                                ? (!string.IsNullOrWhiteSpace(p6) ? $"{p2} ({p6})" : p2)
                                : string.Empty);

                        if (!string.IsNullOrWhiteSpace(effectiveName))
                        {
                            seenIds.Add(id);
                            list.Add(new AudioEndpointInfo(id, effectiveName, state));
                        }
                    }
                }
            }
        }
        catch
        {
            // Registry fallback ignored if inaccessible
        }

        // Order active endpoints (DEVICE_STATE_ACTIVE = 1) first
        return System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(list, e => e.State == DEVICE_STATE_ACTIVE ? 0 : 1));
    }
}
