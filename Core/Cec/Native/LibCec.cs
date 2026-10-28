using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BarControl.Core.Cec.Native;

[StructLayout(LayoutKind.Sequential)]
public struct CecDatapacket
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] Data;
    public byte Size;

    public static CecDatapacket Create(params byte[] bytes)
    {
        var packet = new CecDatapacket
        {
            Data = new byte[64],
            Size = (byte)(bytes?.Length ?? 0)
        };
        if (bytes != null && bytes.Length > 0)
        {
            Array.Copy(bytes, packet.Data, Math.Min(bytes.Length, 64));
        }
        return packet;
    }

    public static CecDatapacket Empty() => new() { Data = new byte[64], Size = 0 };
}

[StructLayout(LayoutKind.Sequential)]
public struct CecCommand
{
    public int Initiator;
    public int Destination;
    public byte Ack;
    public byte Eom;
    public int Opcode;
    public CecDatapacket Parameters;
    public byte OpcodeSet;
    public int TransmitTimeout;

    public static CecCommand Create(int initiator, int destination, int opcode, CecDatapacket? parameters = null, int timeout = 2000)
    {
        return new CecCommand
        {
            Initiator = initiator,
            Destination = destination,
            Ack = 0,
            Eom = 1,
            Opcode = opcode,
            Parameters = parameters ?? CecDatapacket.Empty(),
            OpcodeSet = 1,
            TransmitTimeout = timeout
        };
    }
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
    public const int CEC_OPCODE_GIVE_DEVICE_POWER_STATUS = 0x8F;
    public const int CEC_OPCODE_REPORT_POWER_STATUS = 0x90;

    static LibCec()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibCec).Assembly, DllImportResolver);
    }

    private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase))
        {
            string baseDir = AppContext.BaseDirectory;
            string[] localProbePaths =
            {
                Path.Combine(baseDir, "runtimes", "win-x64", "native", "cec.dll"),
                Path.Combine(baseDir, "cec.dll")
            };

            foreach (var path in localProbePaths)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                {
                    return handle;
                }
            }

            // Extract bundled cec.dll from embedded resource into %LocalAppData%\BarControl\native\cec.dll
            // so single-file BarControl.exe works out-of-the-box without requiring external libCEC installation.
            try
            {
                string extractDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BarControl",
                    "native");
                Directory.CreateDirectory(extractDir);
                string extractedDllPath = Path.Combine(extractDir, "cec.dll");

                using Stream? resStream = assembly.GetManifestResourceStream("BarControl.Native.cec.dll");
                if (resStream != null)
                {
                    bool needsWrite = !File.Exists(extractedDllPath) || new FileInfo(extractedDllPath).Length != resStream.Length;
                    if (needsWrite)
                    {
                        using var fs = new FileStream(extractedDllPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        resStream.CopyTo(fs);
                    }

                    if (NativeLibrary.TryLoad(extractedDllPath, out IntPtr embeddedHandle))
                    {
                        return embeddedHandle;
                    }
                }
            }
            catch
            {
                // Fall through to system-wide fallback if extraction or load fails
            }

            string systemFallbackPath = @"C:\Program Files (x86)\Pulse-Eight\USB-CEC Adapter\x64\cec.dll";
            if (File.Exists(systemFallbackPath) && NativeLibrary.TryLoad(systemFallbackPath, out IntPtr sysHandle))
            {
                return sysHandle;
            }
        }
        return IntPtr.Zero;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libcec_clear_configuration(IntPtr configuration);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libcec_initialise(IntPtr configuration);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libcec_destroy(IntPtr connection);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern sbyte libcec_detect_adapters(IntPtr connection, IntPtr deviceList, byte iBufSize, string? strDevicePath, int bQuickScan);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int libcec_open(IntPtr connection, string strPort, uint iTimeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libcec_close(IntPtr connection);

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

    public const int CEC_USER_CONTROL_CODE_POWER = 0x40;
    public const int CEC_USER_CONTROL_CODE_VOLUME_UP = 0x41;
    public const int CEC_USER_CONTROL_CODE_VOLUME_DOWN = 0x42;
    public const int CEC_USER_CONTROL_CODE_MUTE = 0x43;
    public const int CEC_USER_CONTROL_CODE_POWER_ON_FUNCTION = 0x6D;
    public const int CEC_USER_CONTROL_CODE_POWER_OFF_FUNCTION = 0x6C;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_send_keypress(IntPtr connection, int iDestination, int key, int bWait);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_send_key_release(IntPtr connection, int iDestination, int bWait);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libcec_transmit(IntPtr connection, ref CecCommand data);
}
