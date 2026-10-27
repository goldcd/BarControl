using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BarControl.Core.Cec.Native;

[StructLayout(LayoutKind.Sequential)]
public struct CecDeviceTypeList
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
    public int[] Types;
}

[StructLayout(LayoutKind.Sequential)]
public struct CecLogicalAddresses
{
    public int Primary;
    public ushort Addresses;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct LibCecConfiguration
{
    public uint ClientVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 13)]
    public string StrDeviceName;
    public CecDeviceTypeList DeviceTypes;
    public byte BAutodetectAddress;
    public ushort IPhysicalAddress;
    public int BaseDevice;
    public byte IHDMIPort;
    public uint TvVendor;
    public CecLogicalAddresses WakeDevices;
    public CecLogicalAddresses PowerOffDevices;
    public uint ServerVersion;
    public byte BGetSettingsFromROM;
    public byte BActivateSource;
    public byte BPowerOffOnStandby;
    public IntPtr CallbackParam;
    public IntPtr Callbacks;
    public CecLogicalAddresses LogicalAddresses;
    public ushort IFirmwareVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 3)]
    public string StrDeviceLanguage;
    public uint IFirmwareBuildDate;
    public byte BMonitorOnly;
    public int CecVersion;
    public int AdapterType;
    public int ComboKey;
    public uint IComboKeyTimeoutMs;
    public uint IButtonRepeatRateMs;
    public uint IButtonReleaseDelayMs;
    public uint IDoubleTapTimeoutMs;
    public byte BAutoWakeAVR;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct CecAdapterDescriptor
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
    public string StrComPath;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
    public string StrComName;
    public ushort IVendorId;
    public ushort IProductId;
    public ushort IFirmwareVersion;
    public ushort IPhysicalAddress;
    public uint IFirmwareBuildDate;
    public int AdapterType;
}

[StructLayout(LayoutKind.Sequential)]
public struct CecCommand
{
    public int Initiator;
    public int Destination;
    public byte Ack;
    public byte Eom;
    public int Opcode;
    public byte ParametersSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] Parameters;
    public byte OpcodeSet;
    public int TransmitTimeout;
}

public static class LibCec
{
    public const string DllName = "cec";

    public const int CEC_DEVICE_TYPE_TV = 0;
    public const int CEC_DEVICE_TYPE_RECORDING_DEVICE = 1;
    public const int CEC_DEVICE_TYPE_RESERVED = 2;
    public const int CEC_DEVICE_TYPE_TUNER = 3;
    public const int CEC_DEVICE_TYPE_PLAYBACK_DEVICE = 4;
    public const int CEC_DEVICE_TYPE_AUDIO_SYSTEM = 5;

    public const int CECDEVICE_TV = 0;
    public const int CECDEVICE_RECORDINGDEVICE1 = 1;
    public const int CECDEVICE_AUDIOSYSTEM = 5;
    public const int CECDEVICE_BROADCAST = 15;

    public const int CEC_POWER_STATUS_ON = 0;
    public const int CEC_POWER_STATUS_STANDBY = 1;
    public const int CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON = 2;
    public const int CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY = 3;
    public const int CEC_POWER_STATUS_UNKNOWN = 0x99;

    public const int CEC_OPCODE_USER_CONTROL_PRESSED = 0x44;
    public const int CEC_OPCODE_USER_CONTROL_RELEASE = 0x45;
    public const int CEC_OPCODE_STANDBY = 0x36;
    public const int CEC_OPCODE_SYSTEM_AUDIO_MODE_REQUEST = 0x70;
    public const int CEC_OPCODE_GIVE_AUDIO_STATUS = 0x71;

    static LibCec()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibCec).Assembly, DllImportResolver);
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase))
        {
            string baseDir = AppContext.BaseDirectory;
            string[] probePaths =
            {
                Path.Combine(baseDir, "runtimes", "win-x64", "native", "cec.dll"),
                Path.Combine(baseDir, "cec.dll"),
                @"C:\Program Files (x86)\Pulse-Eight\USB-CEC Adapter\x64\cec.dll"
            };

            foreach (var path in probePaths)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                {
                    return handle;
                }
            }
        }
        return IntPtr.Zero;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libcec_clear_configuration(ref LibCecConfiguration configuration);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libcec_initialise(ref LibCecConfiguration configuration);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libcec_destroy(IntPtr connection);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern sbyte libcec_detect_adapters(IntPtr connection, [Out] CecAdapterDescriptor[] deviceList, byte iBufSize, string? strDevicePath, int bQuickScan);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int libcec_open(IntPtr connection, string strPort, uint iTimeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libcec_close(IntPtr connection);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_power_on_devices(IntPtr connection, int address);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_standby_devices(IntPtr connection, int address);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_get_device_power_status(IntPtr connection, int iLogicalAddress);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_get_device_osd_name(IntPtr connection, int iAddress, [Out] byte[] nameBuffer);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint libcec_get_device_vendor_id(IntPtr connection, int iLogicalAddress);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void libcec_vendor_id_to_string(uint vendor, [Out] byte[] buf, UIntPtr bufsize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte libcec_audio_get_status(IntPtr connection);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_volume_up(IntPtr connection, int bSendRelease);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_volume_down(IntPtr connection, int bSendRelease);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_mute_audio(IntPtr connection, int bSendRelease);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_transmit(IntPtr connection, ref CecCommand data);
}
