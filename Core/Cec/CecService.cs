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
    private bool _isActionInProgress;
    private CancellationTokenSource? _pollCts;

    public int TargetLogicalAddress { get; set; } = LibCec.CECDEVICE_AUDIOSYSTEM;

    public bool IsConnected { get; private set; }
    public string? AdapterPort { get; private set; }
    public string SoundbarName { get; private set; } = "HDMI Soundbar";
    public string SoundbarVendor { get; private set; } = "Unknown";
    public int PowerStatus { get; private set; } = LibCec.CEC_POWER_STATUS_UNKNOWN;
    public CecAudioStatus AudioStatus { get; private set; } = new(false, 0);

    public bool AutoPowerSyncEnabled { get; set; } = true;
    public volatile bool IsUiActive = true;

    public CecVolumeController VolumeController { get; }

    public event Action? StateChanged;

    public CecService(bool autoPowerSync = true)
    {
        AutoPowerSyncEnabled = autoPowerSync;
        VolumeController = new CecVolumeController(this);
        VolumeController.VolumeStateChanged += () => StateChanged?.Invoke();
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
                if (ConnectOrReconnectAdapterInternal())
                {
                    StartBackgroundMonitor();
                }
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

    /// <summary>
    /// Closes any existing libcec connection (e.g. a dead USB serial handle after Windows Hibernate)
    /// and opens a fresh connection to the Pulse-Eight USB-CEC adapter.
    /// </summary>
    private bool ConnectOrReconnectAdapterInternal()
    {
        lock (_lock)
        {
            if (_isDisposed) return false;

            if (_conn != IntPtr.Zero)
            {
                try
                {
                    LogService.LogInfo("CEC: Closing existing libcec handle before reconnecting...");
                    LibCec.libcec_close(_conn);
                    LibCec.libcec_destroy(_conn);
                }
                catch (Exception ex)
                {
                    LogService.LogWarn($"CEC: Warning while closing old handle: {ex.Message}");
                }
                _conn = IntPtr.Zero;
                IsConnected = false;
            }
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

            // Clear wakeDevices (offset 56..75) and powerOffDevices (offset 76..95) to prevent libcec touching the TV/monitor
            for (int i = 56; i < 96; i++)
            {
                Marshal.WriteByte(configPtr, i, 0);
            }

            // bActivateSource = 0 at offset 101: Never make adapter active source (prevents monitor HDMI port switching / screen blackout)
            Marshal.WriteByte(configPtr, 101, 0);

            LogService.LogInfo("CEC: Initializing libcec configuration (bActivateSource=0)...");
            IntPtr conn = LibCec.libcec_initialise(configPtr);
            if (conn == IntPtr.Zero)
            {
                LogService.LogError("CEC: libcec_initialise returned NULL");
                return false;
            }

            IntPtr descBuf = Marshal.AllocHGlobal(2064 * 10);
            string? comPort = null;
            try
            {
                // Retry adapter detection up to 4 times in case USB serial is still enumerating after Hibernate resume
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    sbyte count = LibCec.libcec_detect_adapters(conn, descBuf, 10, null, 1);
                    LogService.LogInfo($"CEC: Detected {count} adapter(s) (attempt {attempt})");
                    if (count > 0)
                    {
                        string? detectedName = Marshal.PtrToStringAnsi(descBuf + 1024);
                        if (!string.IsNullOrEmpty(detectedName))
                        {
                            comPort = detectedName;
                        }
                        break;
                    }
                    Thread.Sleep(750);
                }
            }
            catch (Exception ex)
            {
                LogService.LogWarn($"CEC: Adapter auto-detect warning: {ex.Message}.");
            }
            finally
            {
                Marshal.FreeHGlobal(descBuf);
            }

            if (string.IsNullOrEmpty(comPort))
            {
                LogService.LogWarn("CEC: No Pulse-Eight USB-CEC adapter found on any COM port.");
                LibCec.libcec_destroy(conn);
                return false;
            }

            LogService.LogInfo($"CEC: Opening adapter on {comPort}...");
            int openResult = LibCec.libcec_open(conn, comPort, 10000);
            if (openResult != 1)
            {
                LogService.LogError($"CEC: libcec_open failed with code {openResult}");
                LibCec.libcec_destroy(conn);
                return false;
            }

            lock (_lock)
            {
                _conn = conn;
                IsConnected = true;
                AdapterPort = comPort;
            }
            LogService.LogInfo($"CEC: Connected successfully on {comPort}");

            // Query initial status after bus discovery settles
            Thread.Sleep(1500);
            lock (_lock)
            {
                RefreshDeviceInfoInternal();
            }
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogError("CEC: Error during ConnectOrReconnectAdapterInternal", ex);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(configPtr);
        }
    }

    private void RefreshDeviceInfoInternal()
    {
        if (_conn == IntPtr.Zero) return;

        try
        {
            // 1. Force fresh power status directly from the wire (0x8F) strictly to TargetLogicalAddress
            var pwrCmd = CecCommand.Create(
                LibCec.CECDEVICE_RECORDINGDEVICE1,
                TargetLogicalAddress,
                LibCec.CEC_OPCODE_GIVE_DEVICE_POWER_STATUS);
            LibCec.libcec_transmit(_conn, ref pwrCmd);

            Thread.Sleep(120);
            int pwr = LibCec.libcec_get_device_power_status(_conn, TargetLogicalAddress);
            PowerStatus = pwr;

            // 2. Query audio status directly from the wire (0x71) strictly to TargetLogicalAddress
            var audioCmd = CecCommand.Create(
                LibCec.CECDEVICE_RECORDINGDEVICE1,
                TargetLogicalAddress,
                LibCec.CEC_OPCODE_GIVE_AUDIO_STATUS);
            LibCec.libcec_transmit(_conn, ref audioCmd);

            Thread.Sleep(120);
            byte rawAudio = LibCec.libcec_audio_get_status(_conn);
            bool isMuted = (rawAudio & 0x80) != 0;
            int vol = rawAudio & 0x7F;
            AudioStatus = new CecAudioStatus(isMuted, vol);
            VolumeController.OnHardwareAudioStatusReported(AudioStatus, syncRequestedIfIdle: true);

            // 3. Only query OSD name if not yet resolved
            if (SoundbarName == "HDMI Soundbar")
            {
                byte[] nameBuf = new byte[64];
                if (LibCec.libcec_get_device_osd_name(_conn, TargetLogicalAddress, nameBuf) == 1)
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
                uint vendorId = LibCec.libcec_get_device_vendor_id(_conn, TargetLogicalAddress);
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

    private void StartBackgroundMonitor()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Fast responsive polling (500ms) when UI is active; throttled (5000ms) when minimized/hidden
                    int pollDelay = IsUiActive ? 500 : 5000;
                    await Task.Delay(pollDelay, token);
                    if (token.IsCancellationRequested) break;

                    // Pause background polling while the closed-loop volume chaser is actively stepping the soundbar
                    if (VolumeController.IsChasing) continue;

                    lock (_lock)
                    {
                        if (_conn == IntPtr.Zero || !IsConnected || _isActionInProgress || VolumeController.IsChasing) continue;

                        int oldPower = PowerStatus;
                        var oldAudio = AudioStatus;

                        RefreshDeviceInfoInternal();

                        // If the background monitor sees PowerStatus drop to UNKNOWN (153 / 0x99),
                        // the USB serial connection was reset (e.g. after Hibernate). Reconnect automatically.
                        if (PowerStatus == LibCec.CEC_POWER_STATUS_UNKNOWN)
                        {
                            LogService.LogWarn("CEC: Background monitor detected PowerStatus=UNKNOWN (153). Reconnecting USB-CEC adapter...");
                            ConnectOrReconnectAdapterInternal();
                        }

                        if (PowerStatus != oldPower || AudioStatus != oldAudio)
                        {
                            LogService.LogInfo($"CEC: Live state updated: Power={PowerStatus}, Vol={AudioStatus.VolumePercent}%, Muted={AudioStatus.IsMuted}");
                            StateChanged?.Invoke();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogService.LogWarn($"CEC: Background monitor warning: {ex.Message}");
                }
            }
        }, token);
    }

    /// <summary>
    /// Sends a small burst of Volume Up or Down keypresses (followed by a single release) and reads the updated
    /// audio status reported back by the soundbar. Used by CecVolumeController's closed-loop chaser.
    /// </summary>
    internal CecAudioStatus ExecuteVolumeStepBurst(bool up, int steps)
    {
        lock (_lock)
        {
            if (_conn == IntPtr.Zero || !IsConnected) return new CecAudioStatus(false, -1);

            int code = up ? LibCec.CEC_USER_CONTROL_CODE_VOLUME_UP : LibCec.CEC_USER_CONTROL_CODE_VOLUME_DOWN;
            int count = Math.Clamp(steps, 1, 5);

            for (int i = 0; i < count; i++)
            {
                LibCec.libcec_send_keypress(_conn, TargetLogicalAddress, code, 1);
                if (i < count - 1)
                {
                    // Brief spacing between consecutive presses so soundbar processes each step reliably
                    Thread.Sleep(45);
                }
            }

            LibCec.libcec_send_key_release(_conn, TargetLogicalAddress, 1);

            // Allow soundbar's automatic <Report Audio Status> (0x7A) frame to arrive
            Thread.Sleep(55);

            byte rawAudio = LibCec.libcec_audio_get_status(_conn);
            bool isMuted = (rawAudio & 0x80) != 0;
            int vol = rawAudio & 0x7F;
            if (vol >= 0 && vol <= 100)
            {
                AudioStatus = new CecAudioStatus(isMuted, vol);
            }

            return AudioStatus;
        }
    }

    /// <summary>
    /// Queries <Give Audio Status> (0x71) directly from the wire without querying power/OSD/vendor.
    /// </summary>
    internal CecAudioStatus QueryAudioStatusFromWire()
    {
        lock (_lock)
        {
            if (_conn == IntPtr.Zero || !IsConnected) return new CecAudioStatus(false, -1);

            var audioCmd = CecCommand.Create(
                LibCec.CECDEVICE_RECORDINGDEVICE1,
                TargetLogicalAddress,
                LibCec.CEC_OPCODE_GIVE_AUDIO_STATUS);
            LibCec.libcec_transmit(_conn, ref audioCmd);

            Thread.Sleep(90);
            byte rawAudio = LibCec.libcec_audio_get_status(_conn);
            bool isMuted = (rawAudio & 0x80) != 0;
            int vol = rawAudio & 0x7F;
            if (vol >= 0 && vol <= 100)
            {
                AudioStatus = new CecAudioStatus(isMuted, vol);
            }
            return AudioStatus;
        }
    }

    public Task StepVolumeAsync(bool up)
    {
        VolumeController.RequestStep(up ? 2 : -2);
        return Task.CompletedTask;
    }

    public Task SetVolumeTargetAsync(int targetPercent)
    {
        VolumeController.RequestVolume(targetPercent);
        return Task.CompletedTask;
    }

    public async Task SetMuteAsync(bool targetMute)
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_conn == IntPtr.Zero || !IsConnected) return;
                if (AudioStatus.IsMuted == targetMute) return;

                LogService.LogInfo($"CEC: Toggling soundbar mute to {targetMute}...");
                LibCec.libcec_send_keypress(_conn, TargetLogicalAddress, LibCec.CEC_USER_CONTROL_CODE_MUTE, 1);
                LibCec.libcec_send_key_release(_conn, TargetLogicalAddress, 1);
                Thread.Sleep(70);

                var st = QueryAudioStatusFromWire();
                VolumeController.OnHardwareAudioStatusReported(st, syncRequestedIfIdle: false);
            }
            StateChanged?.Invoke();
        });
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

                _isActionInProgress = true;
                try
                {
                    // 1. Query live power status strictly for the target soundbar
                    RefreshDeviceInfoInternal();

                    // Self-heal: if power status returned UNKNOWN (153 / 0x99), the USB-CEC COM handle was likely
                    // invalidated by a Windows Hibernate / USB bus reset. Re-open the adapter before sending Power On.
                    if (PowerStatus == LibCec.CEC_POWER_STATUS_UNKNOWN)
                    {
                        LogService.LogWarn("CEC: PowerStatus is UNKNOWN (153) before PowerOn — USB-CEC handle appears stale. Reconnecting adapter...");
                        if (!ConnectOrReconnectAdapterInternal() || _conn == IntPtr.Zero)
                        {
                            LogService.LogError("CEC: Adapter reconnection failed during PowerOn.");
                            return false;
                        }
                    }

                    if (PowerStatus == LibCec.CEC_POWER_STATUS_ON)
                    {
                        LogService.LogInfo($"CEC: Device {TargetLogicalAddress} is already powered ON. Idempotent check passed, no wake needed.");
                        return true;
                    }

                    LogService.LogInfo($"CEC: Sending targeted Power On strictly to device {TargetLogicalAddress}...");
                    PowerStatus = LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON;
                    StateChanged?.Invoke();

                    // Targeted attempt 1: Discrete Power On Function key (0x6D) addressed ONLY to soundbar
                    LibCec.libcec_send_keypress(_conn, TargetLogicalAddress, LibCec.CEC_USER_CONTROL_CODE_POWER_ON_FUNCTION, 1);
                    LibCec.libcec_send_key_release(_conn, TargetLogicalAddress, 1);

                    Thread.Sleep(1500);
                    RefreshDeviceInfoInternal();

                    // Targeted attempt 2: Targeted Power button key (0x40) addressed ONLY to soundbar
                    if (PowerStatus != LibCec.CEC_POWER_STATUS_ON)
                    {
                        LogService.LogInfo($"CEC: Trying targeted Power key (0x40) strictly to device {TargetLogicalAddress}...");
                        LibCec.libcec_send_keypress(_conn, TargetLogicalAddress, LibCec.CEC_USER_CONTROL_CODE_POWER, 1);
                        LibCec.libcec_send_key_release(_conn, TargetLogicalAddress, 1);
                        Thread.Sleep(2000);
                        RefreshDeviceInfoInternal();
                    }

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
                    _isActionInProgress = false;
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

                _isActionInProgress = true;
                try
                {
                    // 1. Query live power status strictly for the target soundbar
                    RefreshDeviceInfoInternal();

                    // Self-heal: if power status returned UNKNOWN (153 / 0x99), reconnect the USB-CEC adapter first
                    if (PowerStatus == LibCec.CEC_POWER_STATUS_UNKNOWN)
                    {
                        LogService.LogWarn("CEC: PowerStatus is UNKNOWN (153) before Standby — USB-CEC handle appears stale. Reconnecting adapter...");
                        if (!ConnectOrReconnectAdapterInternal() || _conn == IntPtr.Zero)
                        {
                            LogService.LogError("CEC: Adapter reconnection failed during Standby.");
                            return false;
                        }
                    }

                    if (PowerStatus == LibCec.CEC_POWER_STATUS_STANDBY)
                    {
                        LogService.LogInfo($"CEC: Device {TargetLogicalAddress} is already in STANDBY. Idempotent check passed, no standby needed.");
                        return true;
                    }

                    LogService.LogInfo($"CEC: Sending targeted Standby strictly to device {TargetLogicalAddress} (<Standby> 0x36)...");
                    PowerStatus = LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY;
                    StateChanged?.Invoke();

                    // Send targeted Standby (0x36) strictly to TargetLogicalAddress (NEVER broadcast 15, NEVER TV 0)
                    var standbyDirect = CecCommand.Create(
                        LibCec.CECDEVICE_RECORDINGDEVICE1,
                        TargetLogicalAddress,
                        LibCec.CEC_OPCODE_STANDBY);
                    LibCec.libcec_transmit(_conn, ref standbyDirect);

                    // Wait 1.5s for standby relay transition
                    Thread.Sleep(1500);
                    RefreshDeviceInfoInternal();

                    // If still on, send confirmed targeted Power key (0x40) strictly to soundbar
                    if (PowerStatus != LibCec.CEC_POWER_STATUS_STANDBY && PowerStatus != LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY)
                    {
                        LogService.LogInfo($"CEC: Trying targeted Power key (0x40) strictly to device {TargetLogicalAddress} for standby...");
                        LibCec.libcec_send_keypress(_conn, TargetLogicalAddress, LibCec.CEC_USER_CONTROL_CODE_POWER, 1);
                        LibCec.libcec_send_key_release(_conn, TargetLogicalAddress, 1);
                        Thread.Sleep(2000);
                        RefreshDeviceInfoInternal();
                    }

                    LogService.LogInfo($"CEC: Post-standby power status: {PowerStatus}");
                    return PowerStatus == LibCec.CEC_POWER_STATUS_STANDBY;
                }
                catch (Exception ex)
                {
                    LogService.LogError("CEC: Exception during StandbySoundbarAsync", ex);
                    return false;
                }
                finally
                {
                    _isActionInProgress = false;
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
        if (e.Mode == PowerModes.Suspend)
        {
            LogService.LogInfo("CEC: Windows PowerModes.Suspend received.");
            if (AutoPowerSyncEnabled && IsConnected)
            {
                // Windows PC going to sleep/hibernate -> turn soundbar standby
                _ = StandbySoundbarAsync();
            }
        }
        else if (e.Mode == PowerModes.Resume)
        {
            LogService.LogInfo("CEC: Windows PowerModes.Resume received. Re-initializing USB-CEC connection and waking soundbar...");
            _ = Task.Run(async () =>
            {
                // Allow Windows USB serial driver (COM6) a moment to re-enumerate after Hibernate/Suspend
                await Task.Delay(1500);

                // Always reconnect the USB-CEC handle on Resume because Hibernate invalidates open COM handles
                lock (_lock)
                {
                    if (_isDisposed) return;
                    _isActionInProgress = true;
                }

                try
                {
                    ConnectOrReconnectAdapterInternal();
                }
                finally
                {
                    lock (_lock)
                    {
                        _isActionInProgress = false;
                    }
                    StateChanged?.Invoke();
                }

                if (AutoPowerSyncEnabled)
                {
                    await PowerOnSoundbarAsync();
                }
            });
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;

            VolumeController.Dispose();

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
