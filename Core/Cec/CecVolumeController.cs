using System;
using System.Threading;
using System.Threading.Tasks;
using BarControl.Core.Logging;

namespace BarControl.Core.Cec;

/// <summary>
/// Closed-loop HDMI-CEC Volume Controller.
/// Maintains a RequestedVolume (0..100%) and asynchronously chases it on the soundbar hardware
/// by stepping Volume Up / Down until the reported ActualVolume converges on RequestedVolume.
/// Designed as a clean, reusable module for UI sliders, buttons, hotkeys, and Bitstream/Atmos volume proxies.
/// </summary>
public class CecVolumeController : IDisposable
{
    private readonly CecService _cecService;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _cts;
    private bool _isDisposed;
    private bool _hasInitializedTarget;

    private int _actualVolume;
    private int _requestedVolume;
    private bool _isMuted;
    private bool _isChasing;
    private volatile bool _isUserInteracting;
    private long _lastActivityTick;

    /// <summary>
    /// Current volume percentage reported by the soundbar hardware (0..100).
    /// </summary>
    public int ActualVolume
    {
        get { lock (_stateLock) return _actualVolume; }
    }

    /// <summary>
    /// Target volume percentage requested by the user or automation API (0..100).
    /// </summary>
    public int RequestedVolume
    {
        get { lock (_stateLock) return _requestedVolume; }
    }

    /// <summary>
    /// Current mute state reported by the soundbar hardware.
    /// </summary>
    public bool IsMuted
    {
        get { lock (_stateLock) return _isMuted; }
    }

    /// <summary>
    /// True while the background chaser is actively stepping the soundbar volume toward RequestedVolume.
    /// </summary>
    public bool IsChasing
    {
        get { lock (_stateLock) return _isChasing; }
    }

    /// <summary>
    /// True while the user is dragging the slider, the chaser is actively stepping volume,
    /// or within a 2-second cooldown after volume adjustment finishes so background polling
    /// never contends with the CEC bus during volume changes.
    /// </summary>
    public bool IsActiveOrCooldown
    {
        get
        {
            if (_isUserInteracting) return true;
            lock (_stateLock)
            {
                if (_isChasing) return true;
            }
            return (Environment.TickCount64 - Interlocked.Read(ref _lastActivityTick)) < 2000;
        }
    }

    public void SetUserInteracting(bool interacting)
    {
        _isUserInteracting = interacting;
        Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);
    }

    /// <summary>
    /// Raised whenever ActualVolume, RequestedVolume, IsMuted, or IsChasing changes.
    /// </summary>
    public event Action? VolumeStateChanged;

    public CecVolumeController(CecService cecService)
    {
        _cecService = cecService ?? throw new ArgumentNullException(nameof(cecService));
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => ChaserLoopAsync(token), token);
    }

    /// <summary>
    /// Sets the target volume percentage (0..100) and wakes the closed-loop chaser immediately.
    /// Safe to call at high frequency (e.g. during live slider dragging or mouse wheel scrolling).
    /// </summary>
    public void RequestVolume(int targetPercent)
    {
        int clamped = Math.Clamp(targetPercent, 0, 100);
        bool changed = false;
        Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);

        lock (_stateLock)
        {
            _hasInitializedTarget = true;
            if (_requestedVolume != clamped)
            {
                _requestedVolume = clamped;
                changed = true;
            }

            if (Math.Abs(_requestedVolume - _actualVolume) > 0 && !_isChasing)
            {
                _isChasing = true;
                changed = true;
            }
        }

        if (changed)
        {
            VolumeStateChanged?.Invoke();
        }

        SignalChaser();
    }

    /// <summary>
    /// Adjusts the current RequestedVolume by a relative delta (e.g. +2 or -2) and chases the new target.
    /// Because it increments RequestedVolume rather than ActualVolume, rapid clicks accumulate cleanly.
    /// </summary>
    public void RequestStep(int deltaPercent)
    {
        int newTarget;
        lock (_stateLock)
        {
            int baseVol = _hasInitializedTarget ? _requestedVolume : _actualVolume;
            newTarget = Math.Clamp(baseVol + deltaPercent, 0, 100);
        }
        RequestVolume(newTarget);
    }

    /// <summary>
    /// Called by CecService when a hardware audio status report is received.
    /// When not actively chasing, RequestedVolume automatically syncs to external remote changes.
    /// </summary>
    internal void OnHardwareAudioStatusReported(CecAudioStatus status, bool syncRequestedIfIdle = true)
    {
        if (status.VolumePercent < 0 || status.VolumePercent > 100)
            return;

        bool changed = false;
        lock (_stateLock)
        {
            if (_actualVolume != status.VolumePercent)
            {
                _actualVolume = status.VolumePercent;
                changed = true;
            }

            if (_isMuted != status.IsMuted)
            {
                _isMuted = status.IsMuted;
                changed = true;
            }

            if (!_hasInitializedTarget || (!_isChasing && syncRequestedIfIdle))
            {
                _hasInitializedTarget = true;
                if (_requestedVolume != status.VolumePercent)
                {
                    _requestedVolume = status.VolumePercent;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            VolumeStateChanged?.Invoke();
        }
    }

    private void SignalChaser()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                _wakeSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore during shutdown
        }
    }

    private async Task ChaserLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _wakeSignal.WaitAsync(token);
                if (token.IsCancellationRequested) break;

                await RunChaseSessionAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogService.LogWarn($"CEC Volume Chaser warning: {ex.Message}");
            }
        }
    }

    private void UpdateEstimatedActualVolume(int estimatedVolume)
    {
        bool changed = false;
        lock (_stateLock)
        {
            if (_actualVolume != estimatedVolume)
            {
                _actualVolume = estimatedVolume;
                changed = true;
            }
        }
        if (changed)
        {
            VolumeStateChanged?.Invoke();
        }
    }

    private static int CalculateOptimalPressCount(int absDiff)
    {
        if (absDiff <= 1) return 1;
        if (absDiff == 2) return 2;
        // Hardware calibration on Panasonic SoundSlayer:
        // - 1 press = 1%, 2 presses = 2%
        // - 3..4 presses cross the ~450ms key-repeat hold threshold (~3%)
        // - 5..12 presses accelerate in hardware repeat mode (e.g. 12 presses = ~16-18% in a single burst)
        return Math.Clamp((int)Math.Round(absDiff * 0.62) + 2, 4, 12);
    }

    private async Task RunChaseSessionAsync(CancellationToken token)
    {
        int stalledCount = 0;
        int lastDirection = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_cecService.IsConnected)
                {
                    break;
                }

                int target;
                int actual;
                lock (_stateLock)
                {
                    target = _requestedVolume;
                    actual = _actualVolume;
                }

                int diff = target - actual;
                if (diff == 0)
                {
                    break;
                }

                int direction = diff > 0 ? 1 : -1;

                // Guard against ping-pong oscillation if a soundbar only supports 2% steps and just reversed across target
                if (lastDirection != 0 && direction != lastDirection && Math.Abs(diff) == 1 && stalledCount > 0)
                {
                    LogService.LogInfo($"CEC Volume Chaser: Converged at {actual}% (target {target}%, hardware step resolution reached).");
                    break;
                }

                int absDiff = Math.Abs(diff);
                int burstSteps = CalculateOptimalPressCount(absDiff);
                int burstStartVolume = actual;

                Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);
                var updatedStatus = _cecService.ExecuteVolumeStepBurst(
                    direction > 0,
                    burstSteps,
                    burstStartVolume,
                    UpdateEstimatedActualVolume);
                Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);

                if (updatedStatus.VolumePercent >= 0 && updatedStatus.VolumePercent <= 100)
                {
                    OnHardwareAudioStatusReported(updatedStatus, syncRequestedIfIdle: false);
                    actual = updatedStatus.VolumePercent;

                    if (actual == burstStartVolume)
                    {
                        stalledCount++;
                        if (stalledCount >= 3)
                        {
                            LogService.LogInfo($"CEC Volume Chaser: Hardware volume stalled at {actual}% (target {target}%). Stopping chase.");
                            break;
                        }
                    }
                    else
                    {
                        stalledCount = 0;
                    }
                }
                else
                {
                    break;
                }

                lastDirection = direction;

                // Minimal yield so UI updates smoothly and user slider movements can preempt next iteration
                await Task.Delay(5, token);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastActivityTick, Environment.TickCount64);
            lock (_stateLock)
            {
                _isChasing = false;
            }
            VolumeStateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _wakeSignal.Dispose();
    }
}

