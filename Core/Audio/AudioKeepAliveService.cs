using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BarControl.Core.Audio;

/// <summary>
/// Keeps specific HDMI audio devices (soundbars, AV receivers, DACs) awake by maintaining
/// an active, shared-mode inaudible WASAPI audio stream targeted specifically to each device.
///
/// Prevents the soundbar from entering power-saving sleep mode and eliminates the
/// annoying 1-2 second audio delay/clip when sound starts playing.
///
/// When the target soundbar is powered off, BarControl stops streaming and waits for it without
/// ever keeping alive any other audio output (such as headphones or PC speakers).
///
/// Concept and default 'Fluctuate' stream implementation inspired by SoundKeeper
/// by VEG (vrubleg): https://github.com/vrubleg/soundkeeper
/// </summary>
public class AudioKeepAliveService : IDisposable
{
    private readonly object _lock = new();
    private bool _isEnabled = true;
    private bool _isDisposed;
    private readonly HashSet<string> _targetEndpointIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EndpointSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled
    {
        get
        {
            lock (_lock) return _isEnabled;
        }
        set
        {
            lock (_lock)
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    if (_isEnabled)
                    {
                        SyncSessionsLocked();
                    }
                    else
                    {
                        StopAllSessionsLocked();
                    }
                }
            }
        }
    }

    public AudioKeepAliveService(bool startEnabled = true)
    {
        _isEnabled = startEnabled;
    }

    /// <summary>
    /// Updates the collection of target audio endpoint IDs that should be kept awake.
    /// Only the specified endpoints are kept alive. Any device that is powered off or
    /// disconnected will be gracefully waited on without affecting any other devices.
    /// </summary>
    public void SetTargetEndpoints(IEnumerable<string> endpointIds)
    {
        lock (_lock)
        {
            _targetEndpointIds.Clear();
            foreach (var id in endpointIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _targetEndpointIds.Add(id);
                }
            }

            if (_isEnabled)
            {
                SyncSessionsLocked();
            }
        }
    }

    private void SyncSessionsLocked()
    {
        // Stop sessions that are no longer targeted
        var toRemove = _sessions.Keys.Where(k => !_targetEndpointIds.Contains(k)).ToList();
        foreach (var id in toRemove)
        {
            if (_sessions.TryGetValue(id, out var session))
            {
                session.Dispose();
                _sessions.Remove(id);
            }
        }

        // Start sessions for newly targeted endpoints
        foreach (var id in _targetEndpointIds)
        {
            if (!_sessions.ContainsKey(id))
            {
                var session = new EndpointSession(id);
                _sessions[id] = session;
                session.Start();
            }
        }
    }

    private void StopAllSessionsLocked()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                StopAllSessionsLocked();
            }
        }
    }

    /// <summary>
    /// Represents an active, low-overhead WASAPI keep-alive session dedicated to a single
    /// audio endpoint ID (e.g. Panasonic SoundSlayer).
    ///
    /// The rendering behaviour is a port of SoundKeeper's default configuration
    /// (CSoundSession.cpp, KeepStreamType::Fluctuate @ 50Hz):
    ///  - dedicated render thread registered with MMCSS ("Audio")
    ///  - shared-mode, timer-driven stream, 1000ms buffer, mix format, NOPERSIST | AUTOCONVERTPCM
    ///  - buffer is pre-rolled with the signal before Start()
    ///  - buffer is topped up every 3/4 of its length (750ms)
    ///  - any rendering error tears the stream down and retries (100ms if it was playing, else 1000ms)
    /// </summary>
    private sealed class EndpointSession : IDisposable
    {
        /// <summary>Fluctuate pulse rate; SoundKeeper's default (m_cfg_frequency = 50.0).</summary>
        private const double FluctuateFrequencyHz = 50.0;

        /// <summary>Shared-mode buffer length; SoundKeeper uses 1000ms on non-leaky WASAPI.</summary>
        private const long BufferDurationHns = 1000L * 10000L;

        /// <summary>Wait used while the target device is absent/off (BarControl-specific, per-device targeting).</summary>
        private const int DeviceUnavailableWaitMs = 3000;

        private readonly string _endpointId;
        private CancellationTokenSource? _cts;
        private Thread? _thread;

        // Persisted across stream restarts, like SoundKeeper's m_curr_frame.
        private ulong _currentFrame;
        private uint _playAttempts;

        public EndpointSession(string endpointId)
        {
            _endpointId = endpointId;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _thread = new Thread(() => ThreadEntry(token))
            {
                IsBackground = true,
                Name = "BarControl Audio Keep-Alive"
            };
            _thread.Start();
        }

        private void ThreadEntry(CancellationToken token)
        {
            // Enable Multimedia Class Scheduler Service (MMCSS) so the refill timing is not starved.
            uint taskIndex = 0;
            IntPtr mmcss = IntPtr.Zero;
            try
            {
                mmcss = Wasapi.AvSetMmThreadCharacteristics("Audio", ref taskIndex);
            }
            catch
            {
                // avrt.dll unavailable: continue without MMCSS
            }

            try
            {
                WorkerLoop(token);
            }
            finally
            {
                if (mmcss != IntPtr.Zero)
                {
                    try { Wasapi.AvRevertMmThreadCharacteristics(mmcss); } catch { }
                }
            }
        }

        private void WorkerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool deviceAvailable;
                try
                {
                    deviceAvailable = RenderingSession(token);
                }
                catch
                {
                    // Device removed / invalidated mid-stream: treat like any other rendering error.
                    deviceAvailable = true;
                }

                if (token.IsCancellationRequested) break;

                // SoundKeeper Retry: m_play_attempts == 0 means it was interrupted while playing.
                int delay = !deviceAvailable
                    ? DeviceUnavailableWaitMs
                    : (_playAttempts == 0 ? 100 : 1000);
                token.WaitHandle.WaitOne(delay);
            }
        }

        /// <summary>
        /// One full stream lifetime: activate, initialize, pre-roll, start, render until failure/cancel.
        /// Returns false when the target device is absent or in standby (so the caller waits longer).
        /// </summary>
        private bool RenderingSession(CancellationToken token)
        {
            IntPtr pFormat = IntPtr.Zero;
            Wasapi.IMMDeviceEnumerator? enumerator = null;
            Wasapi.IMMDevice? device = null;
            Wasapi.IAudioClient? audioClient = null;
            Wasapi.IAudioRenderClient? renderClient = null;

            try
            {
                enumerator = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumeratorCom();
                int hr = enumerator.GetDevice(_endpointId, out device);
                if (hr != 0 || device == null)
                {
                    // Device not plugged in or turned off (e.g. soundbar powered off)
                    return false;
                }

                hr = device.GetState(out int state);
                if (hr != 0 || state != Wasapi.DEVICE_STATE_ACTIVE)
                {
                    // Soundbar is off or in standby
                    return false;
                }

                _playAttempts++;

                var iidAudioClient = Wasapi.IID_IAudioClient;
                hr = device.Activate(ref iidAudioClient, Wasapi.CLSCTX_INPROC_SERVER, IntPtr.Zero, out object clientObj);
                if (hr != 0 || clientObj == null) return true;
                audioClient = (Wasapi.IAudioClient)clientObj;

                // Real hardware output format: decides the minimal sample that survives conversion.
                bool output16Bit = Wasapi.IsDeviceOutput16Bit(device);

                // Mixer format. This is always 32-bit float in shared mode.
                hr = audioClient.GetMixFormat(out pFormat);
                if (hr != 0 || pFormat == IntPtr.Zero) return true;

                // WAVEFORMATEX: +2 nChannels, +4 nSamplesPerSec, +12 nBlockAlign, +14 wBitsPerSample
                int channels = (ushort)Marshal.ReadInt16(pFormat, 2);
                int sampleRate = Marshal.ReadInt32(pFormat, 4);
                int frameSize = (ushort)Marshal.ReadInt16(pFormat, 12);
                int bitsPerSample = (ushort)Marshal.ReadInt16(pFormat, 14);

                if (channels == 0 || sampleRate <= 0 || frameSize == 0 || bitsPerSample != 32)
                {
                    // SoundKeeper refuses non-float32 mix formats as well.
                    return false;
                }

                uint hitSample = output16Bit ? 0x38000100U : 0x34000001U;
                ulong onceInFrames = Math.Max((ulong)(sampleRate / FluctuateFrequencyHz), 2UL);

                var sessionGuid = Guid.Empty;
                hr = audioClient.Initialize(
                    Wasapi.AUDCLNT_SHAREMODE_SHARED,
                    Wasapi.AUDCLNT_STREAMFLAGS_NOPERSIST | Wasapi.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                    BufferDurationHns,
                    0,
                    pFormat,
                    ref sessionGuid);
                if (hr != 0) return true;

                hr = audioClient.GetBufferSize(out uint bufferFrames);
                if (hr != 0 || bufferFrames == 0) return true;

                int bufferMs = (int)((long)bufferFrames * 1000 / sampleRate);

                var iidRenderClient = Wasapi.IID_IAudioRenderClient;
                hr = audioClient.GetService(ref iidRenderClient, out IntPtr renderPtr);
                if (hr != 0 || renderPtr == IntPtr.Zero) return true;
                try
                {
                    renderClient = (Wasapi.IAudioRenderClient)Marshal.GetTypedObjectForIUnknown(renderPtr, typeof(Wasapi.IAudioRenderClient));
                }
                finally
                {
                    Marshal.Release(renderPtr);
                }

                // We need to pre-roll one buffer of data into the pipeline before starting.
                if (Render(audioClient, renderClient, bufferFrames, channels, frameSize, hitSample, onceInFrames) != 0)
                {
                    return true;
                }

                hr = audioClient.Start();
                if (hr != 0) return true;

                _playAttempts = 0;

                // Rendering loop: refill every 3/4 of the buffer length (SoundKeeper: ms/2 + ms/4).
                int timeoutMs = bufferMs / 2 + bufferMs / 4;
                while (!token.WaitHandle.WaitOne(timeoutMs))
                {
                    if (Render(audioClient, renderClient, bufferFrames, channels, frameSize, hitSample, onceInFrames) != 0)
                    {
                        break;
                    }
                }

                audioClient.Stop();
                return true;
            }
            finally
            {
                if (pFormat != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pFormat);
                }

                if (renderClient != null) Marshal.ReleaseComObject(renderClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        /// <summary>
        /// Port of CSoundSession::Render() for the Fluctuate stream type. Returns an HRESULT (0 = OK).
        /// </summary>
        private int Render(Wasapi.IAudioClient audioClient, Wasapi.IAudioRenderClient renderClient,
            uint bufferFrames, int channels, int frameSize, uint hitSample, ulong onceInFrames)
        {
            // Find out how much of the buffer *isn't* available (is padding).
            int hr = audioClient.GetCurrentPadding(out uint padding);
            if (hr != 0) return hr;

            // Number of frames available. It can be 0 right after waking the PC up after sleeping.
            uint needFrames = bufferFrames > padding ? bufferFrames - padding : 0;
            if (needFrames == 0) return 0;

            hr = renderClient.GetBuffer(needFrames, out IntPtr pData);
            if (hr != 0 || pData == IntPtr.Zero) return hr != 0 ? hr : -1;

            Wasapi.ZeroMemory(pData, (IntPtr)((long)needFrames * frameSize));

            // Fluctuate: a minimal non-zero sample every onceInFrames frames, negated each odd time.
            // 0x38000100 = 1.0/32767 (minimal 16-bit deviation), 0x34000001 = 1.0/8388607 (minimal 24-bit deviation).
            for (uint i = 0; i < needFrames; i++)
            {
                if (_currentFrame % onceInFrames == 0)
                {
                    uint sample = hitSample;
                    if (((_currentFrame / onceInFrames) & 1) != 0)
                    {
                        sample |= 0x80000000U;
                    }

                    IntPtr framePtr = pData + (int)(i * (uint)frameSize);
                    for (int ch = 0; ch < channels; ch++)
                    {
                        Marshal.WriteInt32(framePtr + (ch * sizeof(float)), unchecked((int)sample));
                    }
                }
                _currentFrame++;
            }

            return renderClient.ReleaseBuffer(needFrames, 0);
        }

        public void Dispose()
        {
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                    _thread?.Join(1500);
                }
                catch
                {
                    // Ignore cancellation / join exceptions
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    _thread = null;
                }
            }
        }
    }
}
