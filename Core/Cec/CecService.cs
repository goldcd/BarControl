using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using BarControl.Core.Cec.Native;

namespace BarControl.Core.Cec;

public record CecAudioStatus(bool IsMuted, int VolumePercent);

public class CecService : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _conn = IntPtr.Zero;
    private bool _isDisposed;
    private bool _isConnecting;

    public bool IsConnected { get; private set; }
    public string? AdapterPort { get; private set; }
    public string SoundbarName { get; private set; } = "HDMI Soundbar";
    public string SoundbarVendor { get; private set; } = "Unknown";
    public int PowerStatus { get; private set; } = LibCec.CEC_POWER_STATUS_UNKNOWN;
    public CecAudioStatus AudioStatus { get; private set; } = new(false, 0);

    public bool AutoPowerSyncEnabled { get; set; } = true;

    public event Action? StateChanged;

    public CecService(bool autoPowerSync = true)
    {
        AutoPowerSyncEnabled = autoPowerSync;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (IsConnected || _isConnecting) return;
                _isConnecting = true;
            }

            try
            {
                var config = new LibCecConfiguration();
                LibCec.libcec_clear_configuration(ref config);

                config.ClientVersion = (4 << 16) | (0 << 8) | 2;
                config.ServerVersion = config.ClientVersion;
                config.StrDeviceName = "BarControl";
                config.DeviceTypes = new CecDeviceTypeList
                {
                    Types = new int[] { LibCec.CEC_DEVICE_TYPE_RECORDING_DEVICE, 2, 2, 2, 2 }
                };
                config.BActivateSource = 0;

                IntPtr conn = LibCec.libcec_initialise(ref config);
                if (conn == IntPtr.Zero)
                {
                    return;
                }

                var descriptors = new CecAdapterDescriptor[10];
                sbyte count = LibCec.libcec_detect_adapters(conn, descriptors, 10, null, 1);
                if (count <= 0)
                {
                    LibCec.libcec_destroy(conn);
                    return;
                }

                string comPort = descriptors[0].StrComName ?? "COM6";
                int openResult = LibCec.libcec_open(conn, comPort, 10000);
                if (openResult != 1)
                {
                    LibCec.libcec_destroy(conn);
                    return;
                }

                lock (_lock)
                {
                    _conn = conn;
                    IsConnected = true;
                    AdapterPort = comPort;
                }

                // Query initial status after bus discovery settles
                Thread.Sleep(3000);
                RefreshDeviceInfoInternal();
            }
            catch (Exception)
            {
                // Fall back gracefully if adapter or native DLL cannot be loaded
            }
            finally
            {
                lock (_lock)
                {
                    _isConnecting = false;
                }
                StateChanged?.Invoke();
            }
        });
    }

    private void RefreshDeviceInfoInternal()
    {
        if (_conn == IntPtr.Zero) return;

        try
        {
            // OSD Name
            byte[] nameBuf = new byte[64];
            if (LibCec.libcec_get_device_osd_name(_conn, LibCec.CECDEVICE_AUDIOSYSTEM, nameBuf) == 1)
            {
                string osd = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0').Trim();
                if (!string.IsNullOrEmpty(osd))
                {
                    SoundbarName = osd;
                }
            }

            // Vendor
            uint vendorId = LibCec.libcec_get_device_vendor_id(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
            byte[] vendorBuf = new byte[64];
            LibCec.libcec_vendor_id_to_string(vendorId, vendorBuf, (UIntPtr)64);
            string vendor = Encoding.ASCII.GetString(vendorBuf).TrimEnd('\0').Trim();
            if (!string.IsNullOrEmpty(vendor))
            {
                SoundbarVendor = vendor;
            }

            // Power status
            PowerStatus = LibCec.libcec_get_device_power_status(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);

            // Audio Status
            byte rawAudio = LibCec.libcec_audio_get_status(_conn);
            bool isMuted = (rawAudio & 0x80) != 0;
            int vol = rawAudio & 0x7F;
            AudioStatus = new CecAudioStatus(isMuted, vol);
        }
        catch (Exception)
        {
            // Ignore interrogation errors
        }
    }

    public async Task<bool> PowerOnSoundbarAsync()
    {
        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_conn == IntPtr.Zero) return false;

                try
                {
                    // 1. User Control: POWER_ON (0x6D)
                    var cmdOn = new CecCommand
                    {
                        Initiator = LibCec.CECDEVICE_RECORDINGDEVICE1,
                        Destination = LibCec.CECDEVICE_AUDIOSYSTEM,
                        Opcode = LibCec.CEC_OPCODE_USER_CONTROL_PRESSED,
                        OpcodeSet = 1,
                        Eom = 1,
                        TransmitTimeout = 1000,
                        ParametersSize = 1,
                        Parameters = new byte[] { 0x6D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
                    };
                    LibCec.libcec_transmit(_conn, ref cmdOn);

                    var cmdRel = new CecCommand
                    {
                        Initiator = LibCec.CECDEVICE_RECORDINGDEVICE1,
                        Destination = LibCec.CECDEVICE_AUDIOSYSTEM,
                        Opcode = LibCec.CEC_OPCODE_USER_CONTROL_RELEASE,
                        OpcodeSet = 1,
                        Eom = 1,
                        TransmitTimeout = 1000,
                        ParametersSize = 0,
                        Parameters = new byte[16]
                    };
                    LibCec.libcec_transmit(_conn, ref cmdRel);

                    // 2. System Audio Mode Request with 1.0.0.0
                    var cmdSam = new CecCommand
                    {
                        Initiator = LibCec.CECDEVICE_RECORDINGDEVICE1,
                        Destination = LibCec.CECDEVICE_AUDIOSYSTEM,
                        Opcode = LibCec.CEC_OPCODE_SYSTEM_AUDIO_MODE_REQUEST,
                        OpcodeSet = 1,
                        Eom = 1,
                        TransmitTimeout = 1000,
                        ParametersSize = 2,
                        Parameters = new byte[] { 0x10, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
                    };
                    LibCec.libcec_transmit(_conn, ref cmdSam);

                    // 3. Builtin PowerOn
                    LibCec.libcec_power_on_devices(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);

                    Thread.Sleep(2000);
                    PowerStatus = LibCec.libcec_get_device_power_status(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    return PowerStatus == LibCec.CEC_POWER_STATUS_ON;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    StateChanged?.Invoke();
                }
            }
        });
    }

    public async Task<bool> StandbySoundbarAsync()
    {
        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_conn == IntPtr.Zero) return false;

                try
                {
                    int res = LibCec.libcec_standby_devices(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    Thread.Sleep(1500);
                    PowerStatus = LibCec.libcec_get_device_power_status(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    return res == 1;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    StateChanged?.Invoke();
                }
            }
        });
    }

    public async Task RefreshStatusAsync()
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                RefreshDeviceInfoInternal();
            }
            StateChanged?.Invoke();
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (!AutoPowerSyncEnabled || !IsConnected) return;

        if (e.Mode == PowerModes.Suspend)
        {
            // Windows PC going to sleep -> turn soundbar standby
            _ = StandbySoundbarAsync();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            // Windows PC waking from sleep -> wake soundbar
            Task.Delay(1000).ContinueWith(_ => PowerOnSoundbarAsync());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            if (_conn != IntPtr.Zero)
            {
                try
                {
                    LibCec.libcec_close(_conn);
                    LibCec.libcec_destroy(_conn);
                }
                catch
                {
                    // Ignore disposal errors
                }
                _conn = IntPtr.Zero;
            }
            IsConnected = false;
        }
    }
}
