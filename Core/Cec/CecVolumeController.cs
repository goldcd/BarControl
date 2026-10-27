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

                // Soundbars often have 50 internal steps (2% per step).
                // Allow a 1% deadband if we aren't targeting 0% or 100% and have already stepped,
                // or stop immediately when diff == 0.
                if (diff == 0 || (Math.Abs(diff) == 1 && target != 0 && target != 100 && lastDirection != 0))
                {
                    break;
                }

                int direction = diff > 0 ? 1 : -1;

                // If direction reversed right after a 1-step overshoot (e.g. soundbar jumped from 64% to 66% for target 65%),
                // we have converged as closely as the hardware step resolution allows.
                if (lastDirection != 0 && direction != lastDirection && Math.Abs(diff) <= 2)
                {
                    LogService.LogInfo($"CEC Volume Chaser: Converged at {actual}% (target {target}%, hardware step resolution reached).");
                    break;
                }

                // Adaptive burst sizing:
                // - Large gap (> 12%): 3 steps per burst before checking audio status
                // - Medium gap (5..12%): 2 steps per burst
                // - Close gap (1..4%): 1 step for fine landing
                int absDiff = Math.Abs(diff);
                int burstSteps = absDiff > 12 ? 3 : (absDiff > 4 ? 2 : 1);

                var updatedStatus = _cecService.ExecuteVolumeStepBurst(direction > 0, burstSteps);

                if (updatedStatus.VolumePercent >= 0 && updatedStatus.VolumePercent <= 100)
                {
                    int previousActual = actual;
                    OnHardwareAudioStatusReported(updatedStatus, syncRequestedIfIdle: false);
                    actual = updatedStatus.VolumePercent;

                    if (actual == previousActual)
                    {
                        stalledCount++;
                        // If hardware hasn't moved after 3 consecutive bursts, force a wire query once on burst 2,
                        // and abort on burst 4 so we never loop infinitely at hardware limits.
                        if (stalledCount == 2)
                        {
                            var wireStatus = _cecService.QueryAudioStatusFromWire();
                            if (wireStatus.VolumePercent >= 0 && wireStatus.VolumePercent <= 100)
                            {
                                OnHardwareAudioStatusReported(wireStatus, syncRequestedIfIdle: false);
                                actual = wireStatus.VolumePercent;
                                if (actual != previousActual)
                                {
                                    stalledCount = 0;
                                }
                            }
                        }
                        else if (stalledCount >= 4)
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

                // Brief yield so UI updates smoothly and user slider movements can preempt next iteration
                await Task.Delay(15, token);
            }

            // Do one final wire confirmation query after settling so ActualVolume is 100% accurate
            if (_cecService.IsConnected && !token.IsCancellationRequested)
            {
                var finalStatus = _cecService.QueryAudioStatusFromWire();
                if (finalStatus.VolumePercent >= 0 && finalStatus.VolumePercent <= 100)
                {
                    OnHardwareAudioStatusReported(finalStatus, syncRequestedIfIdle: false);
                }
            }
        }
        finally
        {
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

