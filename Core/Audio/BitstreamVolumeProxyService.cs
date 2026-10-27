using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BarControl.Core.Cec;
using BarControl.Core.Cec.Native;
using BarControl.Core.Logging;

namespace BarControl.Core.Audio;

/// <summary>
/// Detects when the target HDMI soundbar audio endpoint enters Dolby Atmos / DTS / Exclusive Bitstream mode
/// (where Windows' software PCM mixer can no longer attenuate audio samples), and seamlessly bridges
/// Windows master volume & mute controls directly to the soundbar over HDMI-CEC (and vice-versa).
/// When the bitstream ends, both Windows and Soundbar volumes are restored to their pre-stream levels.
/// </summary>
public class BitstreamVolumeProxyService : IDisposable
{
    // Unique event context GUID so our own SetMasterVolumeLevelScalar calls don't echo back as user keyboard input
    private static readonly Guid ProxyEventContext = new("8B9E4A12-5C31-4D87-A610-9F2E4B7C1123");

    private readonly CecService _cecService;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    private bool _isEnabled = true;
    private string? _targetEndpointId;
    private bool _isBitstreamActive;

    private int _savedWindowsVolume = -1;
    private int _savedSoundbarVolume = -1;
    private int _lastKnownNormalWindowsVolume = 50;

    // Active COM objects for the currently monitored endpoint
    private Wasapi.IMMDevice? _activeDevice;
    private Wasapi.IAudioEndpointVolume? _endpointVolume;
    private EndpointVolumeCallbackSink? _callbackSink;

    public bool IsEnabled
    {
        get { lock (_lock) return _isEnabled; }
        set
        {
            bool changed = false;
            lock (_lock)
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    changed = true;
                    if (!_isEnabled && _isBitstreamActive)
                    {
                        ExitBitstreamModeLocked(restoreVolumes: true);
                    }
                }
            }
            if (changed)
            {
                StateChanged?.Invoke();
            }
        }
    }

    public bool IsBitstreamActive
    {
        get { lock (_lock) return _isBitstreamActive; }
    }

    public int SavedWindowsVolume
    {
        get { lock (_lock) return _savedWindowsVolume; }
    }

    public int SavedSoundbarVolume
    {
        get { lock (_lock) return _savedSoundbarVolume; }
    }

    public event Action? StateChanged;

    public BitstreamVolumeProxyService(CecService cecService, bool startEnabled = true)
    {
        _cecService = cecService ?? throw new ArgumentNullException(nameof(cecService));
        _isEnabled = startEnabled;

        // Listen for soundbar volume changes (e.g. from BarControl slider or physical soundbar remote)
        _cecService.VolumeController.VolumeStateChanged += OnCecVolumeStateChanged;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => MonitorLoopAsync(token), token);
    }

    public void SetTargetEndpoint(string? endpointId)
    {
        lock (_lock)
        {
            if (string.Equals(_targetEndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                return;

            if (_isBitstreamActive)
            {
                ExitBitstreamModeLocked(restoreVolumes: false);
            }

            DetachEndpointCallbackLocked();
            _targetEndpointId = endpointId;
        }
        StateChanged?.Invoke();
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        int consecutiveNormalProbes = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token);
                if (token.IsCancellationRequested) break;

                bool notify = false;

                lock (_lock)
                {
                    if (_isDisposed || !_isEnabled || string.IsNullOrWhiteSpace(_targetEndpointId))
                        continue;

                    EnsureEndpointHookedLocked(_targetEndpointId);
                    if (_activeDevice == null || _endpointVolume == null)
                        continue;

                    bool isLockedByBitstream = ProbeEndpointBitstreamLock(_activeDevice);

                    if (isLockedByBitstream)
                    {
                        consecutiveNormalProbes = 0;
                        if (!_isBitstreamActive)
                        {
                            EnterBitstreamModeLocked();
                            notify = true;
                        }
                    }
                    else
                    {
                        // Track normal Windows volume while in standard PCM mode
                        if (_endpointVolume.GetMasterVolumeLevelScalar(out float scalar) == 0)
                        {
                            _lastKnownNormalWindowsVolume = (int)Math.Round(scalar * 100.0f);
                        }

                        if (_isBitstreamActive)
                        {
                            // Require 2 consecutive normal probes (1.0s) before exiting so brief track transitions don't flap
                            consecutiveNormalProbes++;
                            if (consecutiveNormalProbes >= 2)
                            {
                                ExitBitstreamModeLocked(restoreVolumes: true);
                                consecutiveNormalProbes = 0;
                                notify = true;
                            }
                        }
                    }
                }

                if (notify)
                {
                    StateChanged?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.LogWarn($"BitstreamProxy: Monitor warning: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Probes whether the endpoint is currently locked in Exclusive / Dolby Atmos / DTS Bitstream mode.
    /// When an app streams Atmos (e.g. Netflix, VLC, Kodi), IAudioClient::Initialize(AUDCLNT_SHAREMODE_SHARED)
    /// immediately returns 0x8889000A (AUDCLNT_E_DEVICE_IN_USE) or 0x8889000E (AUDCLNT_E_EXCLUSIVE_MODE_ONLY).
    /// </summary>
    private static bool ProbeEndpointBitstreamLock(Wasapi.IMMDevice device)
    {
        Wasapi.IAudioClient? client = null;
        IntPtr pFormat = IntPtr.Zero;
        try
        {
            if (device.GetState(out int state) != 0 || state != Wasapi.DEVICE_STATE_ACTIVE)
                return false;

            var iid = Wasapi.IID_IAudioClient;
            int hr = device.Activate(ref iid, Wasapi.CLSCTX_INPROC_SERVER, IntPtr.Zero, out object clientObj);
            if (hr != 0 || clientObj == null)
                return false;

            client = (Wasapi.IAudioClient)clientObj;
            if (client.GetMixFormat(out pFormat) != 0 || pFormat == IntPtr.Zero)
                return false;

            Guid emptySession = Guid.Empty;
            int initHr = client.Initialize(
                Wasapi.AUDCLNT_SHAREMODE_SHARED,
                Wasapi.AUDCLNT_STREAMFLAGS_NOPERSIST,
                10000000, // 1000ms
                0,
                pFormat,
                ref emptySession);

            return initHr == Wasapi.AUDCLNT_E_DEVICE_IN_USE || initHr == Wasapi.AUDCLNT_E_EXCLUSIVE_MODE_ONLY;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(pFormat);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    private void EnterBitstreamModeLocked()
    {
        if (_isBitstreamActive) return;

        // 1. Snapshot current Windows and Soundbar volumes before Atmos takes over
        int currentWinVol = _lastKnownNormalWindowsVolume;
        if (_endpointVolume != null && _endpointVolume.GetMasterVolumeLevelScalar(out float scalar) == 0)
        {
            currentWinVol = (int)Math.Round(scalar * 100.0f);
        }

        int currentBarVol = _cecService.VolumeController.ActualVolume;
        if (currentBarVol <= 0 || currentBarVol > 100)
        {
            currentBarVol = _cecService.AudioStatus.VolumePercent;
        }

        _savedWindowsVolume = Math.Clamp(currentWinVol, 0, 100);
        _savedSoundbarVolume = Math.Clamp(currentBarVol, 0, 100);
        _isBitstreamActive = true;

        LogService.LogInfo($"BitstreamProxy: Dolby Atmos / Bitstream detected! Saved volumes (Windows={_savedWindowsVolume}%, Soundbar={_savedSoundbarVolume}%). Syncing Windows OSD to Soundbar ({_savedSoundbarVolume}%)...");

        // 2. Sync Windows Master Volume slider to match the soundbar's current % so keyboard volume keys
        // step smoothly from the soundbar's actual level and the Windows volume OSD shows the true soundbar %.
        if (_endpointVolume != null && _savedSoundbarVolume >= 0 && _savedSoundbarVolume <= 100)
        {
            var ctx = ProxyEventContext;
            _endpointVolume.SetMasterVolumeLevelScalar(_savedSoundbarVolume / 100.0f, ref ctx);
        }
    }

    private void ExitBitstreamModeLocked(bool restoreVolumes)
    {
        if (!_isBitstreamActive) return;
        _isBitstreamActive = false;

        int winToRestore = _savedWindowsVolume;
        int barToRestore = _savedSoundbarVolume;

        LogService.LogInfo($"BitstreamProxy: Bitstream ended. Restoring volumes (Windows={winToRestore}%, Soundbar={barToRestore}%)...");

        if (restoreVolumes)
        {
            if (_endpointVolume != null && winToRestore >= 0 && winToRestore <= 100)
            {
                var ctx = ProxyEventContext;
                _endpointVolume.SetMasterVolumeLevelScalar(winToRestore / 100.0f, ref ctx);
                _lastKnownNormalWindowsVolume = winToRestore;
            }

            if (_cecService.IsConnected && barToRestore >= 0 && barToRestore <= 100)
            {
                _cecService.VolumeController.RequestVolume(barToRestore);
            }
        }
    }

    private void EnsureEndpointHookedLocked(string endpointId)
    {
        if (_activeDevice != null && _endpointVolume != null)
            return;

        DetachEndpointCallbackLocked();

        Wasapi.IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumeratorCom();
            if (enumerator.GetDevice(endpointId, out Wasapi.IMMDevice dev) != 0 || dev == null)
                return;

            var iidVol = Wasapi.IID_IAudioEndpointVolume;
            if (dev.Activate(ref iidVol, Wasapi.CLSCTX_ALL, IntPtr.Zero, out object volObj) != 0 || volObj == null)
            {
                Marshal.ReleaseComObject(dev);
                return;
            }

            var endpointVol = (Wasapi.IAudioEndpointVolume)volObj;
            if (endpointVol.GetMasterVolumeLevelScalar(out float initialScalar) == 0)
            {
                _lastKnownNormalWindowsVolume = (int)Math.Round(initialScalar * 100.0f);
            }

            var sink = new EndpointVolumeCallbackSink(this);
            endpointVol.RegisterControlChangeNotify(sink);

            _activeDevice = dev;
            _endpointVolume = endpointVol;
            _callbackSink = sink;
        }
        catch (Exception ex)
        {
            LogService.LogWarn($"BitstreamProxy: Failed to hook endpoint volume: {ex.Message}");
        }
        finally
        {
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private void DetachEndpointCallbackLocked()
    {
        if (_endpointVolume != null && _callbackSink != null)
        {
            try { _endpointVolume.UnregisterControlChangeNotify(_callbackSink); } catch { }
        }
        _callbackSink = null;

        if (_endpointVolume != null)
        {
            try { Marshal.ReleaseComObject(_endpointVolume); } catch { }
            _endpointVolume = null;
        }

        if (_activeDevice != null)
        {
            try { Marshal.ReleaseComObject(_activeDevice); } catch { }
            _activeDevice = null;
        }
    }

    /// <summary>
    /// Invoked immediately by Windows when the user presses Volume Up/Down/Mute on their keyboard or drags the Windows volume slider.
    /// </summary>
    private void OnWindowsEndpointVolumeNotification(Guid eventContext, float masterVolumeScalar, bool isMuted)
    {
        // Ignore volume changes that we triggered ourselves via ProxyEventContext
        if (eventContext == ProxyEventContext)
            return;

        int newVolumePercent = Math.Clamp((int)Math.Round(masterVolumeScalar * 100.0f), 0, 100);

        lock (_lock)
        {
            if (!_isEnabled) return;

            if (!_isBitstreamActive)
            {
                _lastKnownNormalWindowsVolume = newVolumePercent;
                return;
            }
        }

        // Bitstream is active! Relay keyboard volume and mute changes to the soundbar over HDMI-CEC!
        if (_cecService.IsConnected && _cecService.PowerStatus == LibCec.CEC_POWER_STATUS_ON)
        {
            if (_cecService.VolumeController.RequestedVolume != newVolumePercent)
            {
                _cecService.VolumeController.RequestVolume(newVolumePercent);
            }

            if (_cecService.AudioStatus.IsMuted != isMuted)
            {
                _ = _cecService.SetMuteAsync(isMuted);
            }
        }
    }

    /// <summary>
    /// Invoked when the soundbar volume changes (via BarControl's UI slider or the physical soundbar remote).
    /// While in Bitstream mode, keeps the Windows volume slider synced to the soundbar's target volume.
    /// </summary>
    private void OnCecVolumeStateChanged()
    {
        lock (_lock)
        {
            if (!_isEnabled || !_isBitstreamActive || _endpointVolume == null)
                return;

            int targetBarVol = _cecService.VolumeController.RequestedVolume;
            if (targetBarVol >= 0 && targetBarVol <= 100)
            {
                if (_endpointVolume.GetMasterVolumeLevelScalar(out float curScalar) == 0)
                {
                    int curWinVol = (int)Math.Round(curScalar * 100.0f);
                    if (curWinVol != targetBarVol)
                    {
                        var ctx = ProxyEventContext;
                        _endpointVolume.SetMasterVolumeLevelScalar(targetBarVol / 100.0f, ref ctx);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _cecService.VolumeController.VolumeStateChanged -= OnCecVolumeStateChanged;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            DetachEndpointCallbackLocked();
        }
    }

    private sealed class EndpointVolumeCallbackSink : Wasapi.IAudioEndpointVolumeCallback
    {
        private readonly BitstreamVolumeProxyService _owner;

        public EndpointVolumeCallbackSink(BitstreamVolumeProxyService owner)
        {
            _owner = owner;
        }

        public int OnNotify(IntPtr pNotify)
        {
            if (pNotify != IntPtr.Zero)
            {
                try
                {
                    var data = Marshal.PtrToStructure<Wasapi.AUDIO_VOLUME_NOTIFICATION_DATA>(pNotify);
                    _owner.OnWindowsEndpointVolumeNotification(data.guidEventContext, data.fMasterVolume, data.bMuted);
                }
                catch
                {
                    // Never throw inside COM callback
                }
            }
            return 0; // S_OK
        }
    }
}

