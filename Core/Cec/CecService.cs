using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using BarControl.Core.Cec.Native;
using BarControl.Core.Logging;

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

            IntPtr configPtr = Marshal.AllocHGlobal(1024);
            try
            {
                byte[] zeros = new byte[1024];
                Marshal.Copy(zeros, 0, configPtr, 1024);

                LibCec.libcec_clear_configuration(configPtr);

                // clientVersion = 0x00040002
                Marshal.WriteInt32(configPtr, 0, (4 << 16) | (0 << 8) | 2);

                // strDeviceName = "BarControl" (offset 4)
                byte[] nameBytes = Encoding.ASCII.GetBytes("BarControl\0");
                Marshal.Copy(nameBytes, 0, configPtr + 4, nameBytes.Length);

                // deviceTypes.types[0] = 1 (RECORDING_DEVICE) at offset 20
                Marshal.WriteInt32(configPtr, 20, LibCec.CEC_DEVICE_TYPE_RECORDING_DEVICE);

                LogService.LogInfo("CEC: Initializing libcec configuration...");
                IntPtr conn = LibCec.libcec_initialise(configPtr);
                if (conn == IntPtr.Zero)
                {
                    LogService.LogError("CEC: libcec_initialise returned NULL");
                    return;
                }

                IntPtr descBuf = Marshal.AllocHGlobal(2064 * 10);
                string comPort = "COM6";
                try
                {
                    sbyte count = LibCec.libcec_detect_adapters(conn, descBuf, 10, null, 1);
                    LogService.LogInfo($"CEC: Detected {count} adapter(s)");
                    if (count > 0)
                    {
                        string? detectedName = Marshal.PtrToStringAnsi(descBuf + 1024);
                        if (!string.IsNullOrEmpty(detectedName))
                        {
                            comPort = detectedName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarn($"CEC: Adapter auto-detect warning: {ex.Message}. Falling back to COM6.");
                }
                finally
                {
                    Marshal.FreeHGlobal(descBuf);
                }

                LogService.LogInfo($"CEC: Opening adapter on {comPort}...");
                int openResult = LibCec.libcec_open(conn, comPort, 10000);
                if (openResult != 1)
                {
                    LogService.LogError($"CEC: libcec_open failed with code {openResult}");
                    LibCec.libcec_destroy(conn);
                    return;
                }

                lock (_lock)
                {
                    _conn = conn;
                    IsConnected = true;
                    AdapterPort = comPort;
                }
                LogService.LogInfo($"CEC: Connected successfully on {comPort}");

                // Query initial status after bus discovery settles
                Thread.Sleep(3000);
                RefreshDeviceInfoInternal();
            }
            catch (Exception ex)
            {
                LogService.LogError("CEC: Error during InitializeAsync", ex);
            }
            finally
            {
                Marshal.FreeHGlobal(configPtr);
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
            LogService.LogInfo("CEC: RefreshDeviceInfoInternal entered, querying power status...");
            // 1. Fast: Always query power status first
            int pwr = LibCec.libcec_get_device_power_status(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
            PowerStatus = pwr;
            LogService.LogInfo($"CEC: PowerStatus is {PowerStatus}");

            // 2. Fast: Query audio status
            byte rawAudio = LibCec.libcec_audio_get_status(_conn);
            bool isMuted = (rawAudio & 0x80) != 0;
            int vol = rawAudio & 0x7F;
            AudioStatus = new CecAudioStatus(isMuted, vol);

            LogService.LogInfo($"CEC: Status updated. Power={PowerStatus}, Vol={vol}%, Muted={isMuted}, Vendor={SoundbarVendor}, Name={SoundbarName}");

            // 3. Only query OSD name if not yet resolved
            if (SoundbarName == "HDMI Soundbar")
            {
                byte[] nameBuf = new byte[64];
                if (LibCec.libcec_get_device_osd_name(_conn, LibCec.CECDEVICE_AUDIOSYSTEM, nameBuf) == 1)
                {
                    string osd = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0').Trim();
                    if (!string.IsNullOrEmpty(osd))
                    {
                        SoundbarName = osd;
                    }
                }
            }

            // 4. Only query Vendor if not yet resolved
            if (SoundbarVendor == "Unknown")
            {
                uint vendorId = LibCec.libcec_get_device_vendor_id(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                byte[] vendorBuf = new byte[64];
                LibCec.libcec_vendor_id_to_string(vendorId, vendorBuf, (UIntPtr)64);
                string vendor = Encoding.ASCII.GetString(vendorBuf).TrimEnd('\0').Trim();
                if (!string.IsNullOrEmpty(vendor))
                {
                    SoundbarVendor = vendor;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogError("CEC: Error querying device info", ex);
        }
    }

    public async Task<bool> PowerOnSoundbarAsync()
    {
        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_conn == IntPtr.Zero)
                {
                    LogService.LogWarn("CEC: Cannot PowerOn, adapter not connected");
                    return false;
                }

                try
                {
                    int currentPwr = LibCec.libcec_get_device_power_status(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    if (currentPwr == LibCec.CEC_POWER_STATUS_ON)
                    {
                        LogService.LogInfo("CEC: Soundbar is already powered ON. Refreshing info...");
                        PowerStatus = LibCec.CEC_POWER_STATUS_ON;
                        RefreshDeviceInfoInternal();
                        return true;
                    }

                    LogService.LogInfo($"CEC: Waking soundbar from power state {currentPwr}...");
                    PowerStatus = LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON;
                    StateChanged?.Invoke();

                    // Send Power toggle/on keypress
                    LibCec.libcec_send_keypress(_conn, LibCec.CECDEVICE_AUDIOSYSTEM, LibCec.CEC_USER_CONTROL_CODE_POWER, 1);
                    LibCec.libcec_send_key_release(_conn, LibCec.CECDEVICE_AUDIOSYSTEM, 1);

                    // Also signal CEC power on devices & poll
                    LibCec.libcec_power_on_devices(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    LibCec.libcec_poll_device(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);

                    // Panasonic soundbars take ~3.5 seconds to boot and report status
                    Thread.Sleep(3500);
                    RefreshDeviceInfoInternal();
                    LogService.LogInfo($"CEC: Post-wake power status: {PowerStatus}");
                    return PowerStatus == LibCec.CEC_POWER_STATUS_ON;
                }
                catch (Exception ex)
                {
                    LogService.LogError("CEC: Exception during PowerOnSoundbarAsync", ex);
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
                if (_conn == IntPtr.Zero)
                {
                    LogService.LogWarn("CEC: Cannot Standby, adapter not connected");
                    return false;
                }

                try
                {
                    int currentPwr = LibCec.libcec_get_device_power_status(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    if (currentPwr == LibCec.CEC_POWER_STATUS_STANDBY)
                    {
                        LogService.LogInfo("CEC: Soundbar is already in STANDBY. Refreshing info...");
                        PowerStatus = LibCec.CEC_POWER_STATUS_STANDBY;
                        RefreshDeviceInfoInternal();
                        return true;
                    }

                    LogService.LogInfo($"CEC: Putting soundbar into standby from power state {currentPwr}...");
                    PowerStatus = LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY;
                    StateChanged?.Invoke();

                    // 1. Send Power keypress
                    LibCec.libcec_send_keypress(_conn, LibCec.CECDEVICE_AUDIOSYSTEM, LibCec.CEC_USER_CONTROL_CODE_POWER, 1);
                    LibCec.libcec_send_key_release(_conn, LibCec.CECDEVICE_AUDIOSYSTEM, 1);

                    // 2. Also send CEC Standby command to device and broadcast
                    LibCec.libcec_standby_devices(_conn, LibCec.CECDEVICE_AUDIOSYSTEM);
                    LibCec.libcec_standby_devices(_conn, LibCec.CECDEVICE_BROADCAST);

                    // Wait 3.5 seconds for relay shutdown
                    Thread.Sleep(3500);
                    RefreshDeviceInfoInternal();
                    LogService.LogInfo($"CEC: Post-standby power status: {PowerStatus}");
                    return true;
                }
                catch (Exception ex)
                {
                    LogService.LogError("CEC: Exception during StandbySoundbarAsync", ex);
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
