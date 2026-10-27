using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using BarControl.Core.Audio;
using BarControl.Core.Cec;
using BarControl.Core.Cec.Native;
using BarControl.Core.Config;
using BarControl.Core.Display;
using BarControl.Core.Engine;
using BarControl.Core.Startup;
using BarControl.Core.Tray;
using BarControl.Core.Update;

namespace BarControl.UI;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly WindowRelocationEngine _engine;
    private readonly TrayIconService _trayService;
    private readonly AudioKeepAliveService? _audioService;
    private readonly CecService? _cecService;
    private readonly BitstreamVolumeProxyService? _bitstreamProxyService;
    private readonly AppSettings _settings;

    private bool _isGuardActive = true;

    public ObservableCollection<DisplayInfo> Displays { get; } = new();

    public bool IsGuardActive
    {
        get => _isGuardActive;
        set
        {
            if (_isGuardActive != value)
            {
                _isGuardActive = value;
                _settings.IsGuardEnabled = value;
                _trayService.SetGuardState(value);
                SyncConfiguration();
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    public string AppVersion
    {
        get
        {
            var v = typeof(MainWindowViewModel).Assembly.GetName().Version;
            return v != null ? $"v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "v0.1.0";
        }
    }

    private readonly UpdateService _updateService = new();
    private UpdateInfo? _availableUpdate;
    private bool _isDownloadingUpdate;
    private string _updateButtonText = "Update";
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;

    public bool IsUpdateAvailable => _availableUpdate != null;
    public string? AvailableUpdateVersion => _availableUpdate?.Version;

    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        set
        {
            if (_isDownloadingUpdate != value)
            {
                _isDownloadingUpdate = value;
                OnPropertyChanged();
            }
        }
    }

    public string UpdateButtonText
    {
        get => _updateButtonText;
        set
        {
            if (_updateButtonText != value)
            {
                _updateButtonText = value;
                OnPropertyChanged();
            }
        }
    }

    public async Task CheckForUpdatesAsync(bool force = false)
    {
        try
        {
            if (!force && (DateTime.UtcNow - _lastUpdateCheckUtc).TotalSeconds < 30)
                return;

            _lastUpdateCheckUtc = DateTime.UtcNow;
            var update = await _updateService.CheckForUpdateAsync();
            if (update != null)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _availableUpdate = update;
                    UpdateButtonText = $"Update to {update.Version}";
                    OnPropertyChanged(nameof(IsUpdateAvailable));
                    OnPropertyChanged(nameof(AvailableUpdateVersion));
                });
            }
        }
        catch
        {
            // Silently ignore check failures
        }
    }

    public async Task ApplyUpdateAsync()
    {
        if (_availableUpdate == null || IsDownloadingUpdate)
            return;

        try
        {
            IsDownloadingUpdate = true;
            UpdateButtonText = "Downloading...";
            var progress = new Progress<double>(p =>
            {
                UpdateButtonText = $"Downloading... {p:P0}";
            });

            await _updateService.ApplyUpdateAsync(_availableUpdate, progress);
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            UpdateButtonText = "Update failed (retry)";
            MessageBox.Show($"Could not apply update:\n{ex.Message}", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows != value)
            {
                _settings.StartWithWindows = value;
                StartupService.SetStartup(value);
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set
        {
            if (_settings.StartMinimized != value)
            {
                _settings.StartMinimized = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    public MainWindowViewModel(
        WindowRelocationEngine engine,
        TrayIconService trayService,
        AudioKeepAliveService? audioService = null,
        CecService? cecService = null,
        BitstreamVolumeProxyService? bitstreamProxyService = null)
    {
        _engine = engine;
        _trayService = trayService;
        _audioService = audioService;
        _cecService = cecService;
        _bitstreamProxyService = bitstreamProxyService;
        _settings = ConfigService.Load();

        if (_audioService != null)
        {
            _audioService.IsEnabled = _settings.KeepAudioAwake;
        }

        if (_cecService != null)
        {
            _cecService.AutoPowerSyncEnabled = _settings.CecPowerSyncEnabled;
            _cecService.StateChanged += OnCecStateChanged;
        }

        if (_bitstreamProxyService != null)
        {
            _bitstreamProxyService.IsEnabled = _settings.BitstreamVolumeProxyEnabled;
            _bitstreamProxyService.StateChanged += OnBitstreamProxyStateChanged;
        }

        _isGuardActive = _settings.IsGuardEnabled;
        _trayService.SetGuardState(_isGuardActive);

        LoadDisplays();

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await CheckForUpdatesAsync(force: true);
        });
    }

    private void OnBitstreamProxyStateChanged()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(IsBitstreamProxyEnabled));
            OnPropertyChanged(nameof(IsBitstreamActive));
            OnPropertyChanged(nameof(BitstreamProxyStatusText));
            OnPropertyChanged(nameof(BitstreamProxySubtitleText));
            OnPropertyChanged(nameof(BitstreamProxyBadgeBackground));
            OnPropertyChanged(nameof(BitstreamProxyBadgeBorderBrush));
            OnPropertyChanged(nameof(BitstreamProxyBadgeForeground));
            OnPropertyChanged(nameof(BitstreamProxyCardBorderBrush));
        });
    }

    private void OnCecStateChanged()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(IsCecConnected));
            OnPropertyChanged(nameof(CecPortName));
            OnPropertyChanged(nameof(SoundbarCecName));
            OnPropertyChanged(nameof(SoundbarVendor));
            NotifyPowerButtonState();
        });
    }

    private DisplayInfo? _selectedSoundbar;
    public DisplayInfo? SelectedSoundbar
    {
        get => _selectedSoundbar;
        set
        {
            if (_selectedSoundbar != value)
            {
                _selectedSoundbar = value;
                if (value != null)
                {
                    _settings.SelectedSoundbarIdentifier = !string.IsNullOrEmpty(value.HardwareId) ? value.HardwareId : value.FriendlyName;
                    SaveSettings();
                }
                UpdateBitstreamProxyTarget();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPhantomDisplayGuardEnabled));
                OnPropertyChanged(nameof(CanEnablePhantomDisplayGuard));
                OnPropertyChanged(nameof(PhantomDisplayGuardToolTip));
                OnPropertyChanged(nameof(IsAudioKeepAliveEnabled));
            }
        }
    }

    public bool IsBitstreamProxyEnabled
    {
        get => _settings.BitstreamVolumeProxyEnabled;
        set
        {
            if (_settings.BitstreamVolumeProxyEnabled != value)
            {
                _settings.BitstreamVolumeProxyEnabled = value;
                if (_bitstreamProxyService != null)
                {
                    _bitstreamProxyService.IsEnabled = value;
                }
                SaveSettings();
                OnPropertyChanged();
                OnBitstreamProxyStateChanged();
            }
        }
    }

    public bool IsBitstreamActive => _bitstreamProxyService?.IsBitstreamActive ?? false;

    public string BitstreamProxyStatusText
    {
        get
        {
            if (!IsBitstreamProxyEnabled) return "DISABLED";
            return IsBitstreamActive ? "⚡ ATMOS / BITSTREAM ACTIVE" : "STANDBY (PCM)";
        }
    }

    public string BitstreamProxySubtitleText
    {
        get
        {
            if (IsBitstreamActive && _bitstreamProxyService != null)
            {
                string restoreHint = (_bitstreamProxyService.SavedWindowsVolume >= 0 && _bitstreamProxyService.SavedSoundbarVolume >= 0)
                    ? $" (will restore Win: {_bitstreamProxyService.SavedWindowsVolume}%, Bar: {_bitstreamProxyService.SavedSoundbarVolume}% when stream ends)"
                    : string.Empty;
                return $"Active: Keyboard & Windows volume controls are now directly driving your soundbar via HDMI-CEC{restoreHint}.";
            }
            return "Automatically detects Dolby Atmos / DTS streams, relays keyboard volume keys to your soundbar, and restores prior volume levels when playback stops.";
        }
    }

    public Brush BitstreamProxyBadgeBackground
    {
        get
        {
            if (!IsBitstreamProxyEnabled)
                return new SolidColorBrush(Color.FromRgb(38, 38, 43));
            if (IsBitstreamActive)
                return new SolidColorBrush(Color.FromRgb(6, 78, 59)); // Deep Emerald
            return new SolidColorBrush(Color.FromRgb(39, 39, 46));
        }
    }

    public Brush BitstreamProxyBadgeBorderBrush
    {
        get
        {
            if (!IsBitstreamProxyEnabled)
                return new SolidColorBrush(Color.FromRgb(56, 56, 64));
            if (IsBitstreamActive)
                return new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Bright Emerald Border
            return new SolidColorBrush(Color.FromRgb(63, 63, 72));
        }
    }

    public Brush BitstreamProxyBadgeForeground
    {
        get
        {
            if (!IsBitstreamProxyEnabled)
                return new SolidColorBrush(Color.FromRgb(113, 113, 122));
            if (IsBitstreamActive)
                return new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Bright Emerald Text
            return new SolidColorBrush(Color.FromRgb(161, 161, 170));
        }
    }

    public Brush BitstreamProxyCardBorderBrush =>
        (IsBitstreamProxyEnabled && IsBitstreamActive)
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
            : new SolidColorBrush(Color.FromRgb(44, 44, 50));

    /// <summary>
    /// Safety Protection: The Windows Primary (or Target Primary) monitor can NEVER be guarded/excluded,
    /// preventing the user from accidentally moving all windows off their real monitor onto the phantom soundbar display.
    /// </summary>
    public bool CanEnablePhantomDisplayGuard =>
        SelectedSoundbar != null && !SelectedSoundbar.IsOsPrimary && !SelectedSoundbar.IsTargetPrimary;

    public string PhantomDisplayGuardToolTip
    {
        get
        {
            if (SelectedSoundbar == null)
                return "Select a soundbar display above.";
            if (SelectedSoundbar.IsOsPrimary || SelectedSoundbar.IsTargetPrimary)
                return "Safety Protection: Your primary monitor cannot be blocked by Phantom Display Guard.";
            return "Prevent windows and dialogs from getting lost on this phantom soundbar display.";
        }
    }

    public bool IsPhantomDisplayGuardEnabled
    {
        get => SelectedSoundbar != null && !SelectedSoundbar.IsOsPrimary && !SelectedSoundbar.IsTargetPrimary && SelectedSoundbar.IsExcluded;
        set
        {
            if (SelectedSoundbar == null) return;

            if (value && (SelectedSoundbar.IsOsPrimary || SelectedSoundbar.IsTargetPrimary))
            {
                MessageBox.Show(
                    "You cannot enable Phantom Display Guard on your primary monitor.\nPlease select your HDMI soundbar display in the dropdown above.",
                    "Primary Monitor Protected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                OnPropertyChanged();
                return;
            }

            if (SelectedSoundbar.IsExcluded != value)
            {
                SelectedSoundbar.IsExcluded = value;
                UpdateExcludedSettingsList();
                SyncConfiguration();
                SaveSettings();
                OnPropertyChanged();
                if (value) SweepNow();
            }
        }
    }

    public bool IsAudioKeepAliveEnabled
    {
        get => SelectedSoundbar?.KeepAudioAlive ?? false;
        set
        {
            if (SelectedSoundbar != null && SelectedSoundbar.KeepAudioAlive != value)
            {
                SelectedSoundbar.KeepAudioAlive = value;
                OnDisplayAudioSettingChanged(SelectedSoundbar);
                OnPropertyChanged();
            }
        }
    }

    public bool IsCecConnected => _cecService?.IsConnected ?? false;
    public string CecPortName => _cecService?.AdapterPort ?? "Auto-Detecting";
    public string SoundbarCecName => _cecService?.SoundbarName ?? "HDMI Soundbar";
    public string SoundbarVendor => (_cecService?.SoundbarVendor is { Length: > 0 } v && v != "Unknown") ? v : "";

    public int CecActualVolumePercent => _cecService?.VolumeController.ActualVolume ?? 0;
    public int CecRequestedVolumePercent => _cecService?.VolumeController.RequestedVolume ?? 0;
    public bool IsVolumeChasing => _cecService?.VolumeController.IsChasing ?? false;

    public string CecVolumeText
    {
        get
        {
            if (_cecService == null || !_cecService.IsConnected) return string.Empty;
            var st = _cecService.AudioStatus;
            if (st.VolumePercent == 127) return "Vol: --";
            int actual = _cecService.VolumeController.ActualVolume;
            int requested = _cecService.VolumeController.RequestedVolume;
            bool chasing = _cecService.VolumeController.IsChasing;
            string icon = st.IsMuted ? "🔇 " : "🔊 ";
            string muteSuffix = st.IsMuted ? " (Muted)" : "";

            if (chasing && Math.Abs(requested - actual) > 1)
            {
                return $"{icon}Vol: {actual}% → {requested}%{muteSuffix}";
            }
            return $"{icon}Vol: {actual}%{muteSuffix}";
        }
    }

    public string PowerToggleButtonText
    {
        get
        {
            if (_cecService == null || !_cecService.IsConnected)
                return "Connecting...";

            if (IsOperatingPower)
            {
                if (_cecService.PowerStatus == LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON)
                    return "Powering On...";
                if (_cecService.PowerStatus == LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY)
                    return "Entering Standby...";
                return "Please wait...";
            }

            return _cecService.PowerStatus switch
            {
                LibCec.CEC_POWER_STATUS_ON => "🌙 Turn Off",
                LibCec.CEC_POWER_STATUS_STANDBY => "⚡ Turn On",
                LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON => "Powering On...",
                LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY => "Entering Standby...",
                _ => "⚡ Turn On"
            };
        }
    }

    public bool CanTogglePower
    {
        get
        {
            if (_cecService == null || !_cecService.IsConnected || IsOperatingPower)
                return false;

            int pwr = _cecService.PowerStatus;
            if (pwr == LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON ||
                pwr == LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY)
                return false;

            return true;
        }
    }

    public Brush PowerToggleButtonBackground
    {
        get
        {
            if (!CanTogglePower)
                return new SolidColorBrush(Color.FromRgb(38, 38, 43));

            if (_cecService?.PowerStatus == LibCec.CEC_POWER_STATUS_ON)
                return new SolidColorBrush(Color.FromRgb(59, 46, 21));

            return new SolidColorBrush(Color.FromRgb(0, 120, 212));
        }
    }

    public Brush PowerToggleButtonBorderBrush
    {
        get
        {
            if (!CanTogglePower)
                return new SolidColorBrush(Color.FromRgb(63, 63, 70));

            if (_cecService?.PowerStatus == LibCec.CEC_POWER_STATUS_ON)
                return new SolidColorBrush(Color.FromRgb(102, 80, 34));

            return new SolidColorBrush(Color.FromRgb(26, 136, 224));
        }
    }

    public Brush PowerToggleButtonForeground
    {
        get
        {
            if (!CanTogglePower)
                return new SolidColorBrush(Color.FromRgb(161, 161, 170));

            if (_cecService?.PowerStatus == LibCec.CEC_POWER_STATUS_ON)
                return new SolidColorBrush(Color.FromRgb(251, 191, 36));

            return new SolidColorBrush(Colors.White);
        }
    }

    public bool IsPowerSyncEnabled
    {
        get => _settings.CecPowerSyncEnabled;
        set
        {
            if (_settings.CecPowerSyncEnabled != value)
            {
                _settings.CecPowerSyncEnabled = value;
                if (_cecService != null)
                {
                    _cecService.AutoPowerSyncEnabled = value;
                }
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    private bool _isOperatingPower;
    public bool IsOperatingPower
    {
        get => _isOperatingPower;
        set
        {
            if (_isOperatingPower != value)
            {
                _isOperatingPower = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanTogglePower));
            }
        }
    }

    public bool IsUiActive
    {
        get => _cecService?.IsUiActive ?? true;
        set
        {
            if (_cecService != null)
            {
                _cecService.IsUiActive = value;
            }
        }
    }

    public bool CanControlVolume => IsCecConnected && _cecService?.PowerStatus == LibCec.CEC_POWER_STATUS_ON;

    private int _sliderVolumePercent;
    private bool _isUserDraggingSlider;
    private bool _isUpdatingSliderFromHardware;

    public int CecVolumeSliderValue
    {
        get => _sliderVolumePercent;
        set
        {
            if (_sliderVolumePercent != value)
            {
                _sliderVolumePercent = value;
                OnPropertyChanged();

                if (!_isUpdatingSliderFromHardware && _cecService != null && CanControlVolume)
                {
                    _cecService.VolumeController.RequestVolume(value);
                }
            }
        }
    }

    public void OnSliderDragStarted()
    {
        _isUserDraggingSlider = true;
    }

    public Task OnSliderDragEndedAsync()
    {
        _isUserDraggingSlider = false;
        if (_cecService != null && CanControlVolume)
        {
            _cecService.VolumeController.RequestVolume(_sliderVolumePercent);
        }
        return Task.CompletedTask;
    }

    public Task StepVolumeUpAsync()
    {
        if (_cecService != null && CanControlVolume)
        {
            _cecService.VolumeController.RequestStep(2);
            _isUpdatingSliderFromHardware = true;
            try
            {
                CecVolumeSliderValue = _cecService.VolumeController.RequestedVolume;
            }
            finally
            {
                _isUpdatingSliderFromHardware = false;
            }
        }
        return Task.CompletedTask;
    }

    public Task StepVolumeDownAsync()
    {
        if (_cecService != null && CanControlVolume)
        {
            _cecService.VolumeController.RequestStep(-2);
            _isUpdatingSliderFromHardware = true;
            try
            {
                CecVolumeSliderValue = _cecService.VolumeController.RequestedVolume;
            }
            finally
            {
                _isUpdatingSliderFromHardware = false;
            }
        }
        return Task.CompletedTask;
    }

    public void NotifyPowerButtonState()
    {
        OnPropertyChanged(nameof(IsOperatingPower));
        OnPropertyChanged(nameof(CanTogglePower));
        OnPropertyChanged(nameof(CanControlVolume));
        OnPropertyChanged(nameof(PowerToggleButtonText));
        OnPropertyChanged(nameof(PowerToggleButtonBackground));
        OnPropertyChanged(nameof(PowerToggleButtonBorderBrush));
        OnPropertyChanged(nameof(PowerToggleButtonForeground));
        OnPropertyChanged(nameof(CecVolumeText));
        OnPropertyChanged(nameof(CecActualVolumePercent));
        OnPropertyChanged(nameof(CecRequestedVolumePercent));
        OnPropertyChanged(nameof(IsVolumeChasing));

        if (!_isUserDraggingSlider && _cecService != null)
        {
            int targetVol = _cecService.VolumeController.RequestedVolume;
            if (targetVol >= 0 && targetVol <= 100)
            {
                _isUpdatingSliderFromHardware = true;
                try
                {
                    CecVolumeSliderValue = targetVol;
                }
                finally
                {
                    _isUpdatingSliderFromHardware = false;
                }
            }
        }
    }

    public async Task TogglePowerSoundbarAsync()
    {
        if (_cecService == null || !CanTogglePower) return;

        if (_cecService.PowerStatus == LibCec.CEC_POWER_STATUS_ON)
        {
            await StandbySoundbarAsync();
        }
        else
        {
            await PowerOnSoundbarAsync();
        }
    }

    public async Task PowerOnSoundbarAsync()
    {
        if (_cecService != null && !IsOperatingPower)
        {
            try
            {
                IsOperatingPower = true;
                NotifyPowerButtonState();
                await _cecService.PowerOnSoundbarAsync();
            }
            finally
            {
                IsOperatingPower = false;
                NotifyPowerButtonState();
            }
        }
    }

    public async Task StandbySoundbarAsync()
    {
        if (_cecService != null && !IsOperatingPower)
        {
            try
            {
                IsOperatingPower = true;
                NotifyPowerButtonState();
                await _cecService.StandbySoundbarAsync();
            }
            finally
            {
                IsOperatingPower = false;
                NotifyPowerButtonState();
            }
        }
    }

    public void LoadDisplays()
    {
        Displays.Clear();
        var monitors = DisplayService.GetConnectedMonitors();

        foreach (var m in monitors)
        {
            // Restore saved settings — NEVER allow the OS Primary monitor to be excluded
            if (!m.IsOsPrimary &&
                (_settings.ExcludedIdentifiers.Contains(m.HardwareId) ||
                 _settings.ExcludedIdentifiers.Contains(m.DeviceName) ||
                 _settings.ExcludedIdentifiers.Contains(m.FriendlyName)))
            {
                m.IsExcluded = true;
            }

            if (!string.IsNullOrEmpty(_settings.TargetPrimaryHardwareId) &&
                (m.HardwareId == _settings.TargetPrimaryHardwareId || m.DeviceName == _settings.TargetPrimaryDeviceName))
            {
                m.IsTargetPrimary = true;
            }

            if (m.HasAudioEndpoint)
            {
                bool isDisabled = _settings.DisabledAudioKeepAliveMonitors.Contains(m.HardwareId) ||
                                  _settings.DisabledAudioKeepAliveMonitors.Contains(m.FriendlyName) ||
                                  (!string.IsNullOrEmpty(m.AudioEndpointId) && _settings.DisabledAudioKeepAliveMonitors.Contains(m.AudioEndpointId));
                m.KeepAudioAlive = !isDisabled;
            }

            m.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(DisplayInfo.IsExcluded) or nameof(DisplayInfo.IsTargetPrimary))
                {
                    OnDisplaySettingChanged(m, e.PropertyName);
                }
                else if (e.PropertyName == nameof(DisplayInfo.KeepAudioAlive))
                {
                    OnDisplayAudioSettingChanged(m);
                }
            };

            Displays.Add(m);
        }

        // If the saved target primary monitor is no longer connected, clear the stale reference
        if (!string.IsNullOrEmpty(_settings.TargetPrimaryHardwareId) &&
            !monitors.Any(m => m.HardwareId == _settings.TargetPrimaryHardwareId || m.DeviceName == _settings.TargetPrimaryDeviceName))
        {
            _settings.TargetPrimaryHardwareId = null;
            _settings.TargetPrimaryDeviceName = null;
            SaveSettings();
        }

        EnsureValidTargetPrimary();

        // Auto-select soundbar: match saved setting, or non-primary display with audio endpoint (e.g. SOUNDSLAYER), or first non-primary
        SelectedSoundbar = Displays.FirstOrDefault(d => !string.IsNullOrEmpty(_settings.SelectedSoundbarIdentifier) &&
                                                        (d.HardwareId == _settings.SelectedSoundbarIdentifier || d.FriendlyName == _settings.SelectedSoundbarIdentifier))
                           ?? Displays.FirstOrDefault(d => !d.IsOsPrimary && d.HasAudioEndpoint)
                           ?? Displays.FirstOrDefault(d => !d.IsOsPrimary)
                           ?? Displays.FirstOrDefault();

        UpdateExcludedSettingsList();
        SyncConfiguration();
        UpdateAudioKeepAliveTargets();
        UpdateBitstreamProxyTarget();
    }

    private void UpdateBitstreamProxyTarget()
    {
        if (_bitstreamProxyService == null) return;

        string? endpointId = SelectedSoundbar?.AudioEndpointId;
        if (string.IsNullOrEmpty(endpointId))
        {
            endpointId = Displays.FirstOrDefault(d => !d.IsOsPrimary && d.HasAudioEndpoint && !string.IsNullOrEmpty(d.AudioEndpointId))?.AudioEndpointId
                      ?? Displays.FirstOrDefault(d => d.HasAudioEndpoint && !string.IsNullOrEmpty(d.AudioEndpointId))?.AudioEndpointId;
        }

        _bitstreamProxyService.SetTargetEndpoint(endpointId);
    }

    private void EnsureValidTargetPrimary()
    {
        // Primary monitor can never be excluded
        foreach (var d in Displays.Where(d => d.IsOsPrimary))
        {
            d.IsExcluded = false;
        }

        var target = Displays.FirstOrDefault(d => d.IsTargetPrimary && !d.IsExcluded);
        if (target == null)
        {
            target = Displays.FirstOrDefault(d => d.IsOsPrimary && !d.IsExcluded)
                     ?? Displays.FirstOrDefault(d => !d.IsExcluded);

            if (target != null)
            {
                foreach (var d in Displays) d.IsTargetPrimary = (d == target);
                _settings.TargetPrimaryHardwareId = target.HardwareId;
                _settings.TargetPrimaryDeviceName = target.DeviceName;
                SaveSettings();
            }
        }
    }

    private void OnDisplaySettingChanged(DisplayInfo display, string propertyName)
    {
        if (propertyName == nameof(DisplayInfo.IsExcluded) && display.IsExcluded && (display.IsTargetPrimary || display.IsOsPrimary))
        {
            // Refuse to exclude primary display
            display.IsExcluded = false;
            return;
        }

        UpdateExcludedSettingsList();
        SyncConfiguration();
        SaveSettings();
        OnPropertyChanged(nameof(IsPhantomDisplayGuardEnabled));
        OnPropertyChanged(nameof(CanEnablePhantomDisplayGuard));
    }

    private void UpdateExcludedSettingsList()
    {
        _settings.ExcludedIdentifiers.Clear();
        foreach (var d in Displays.Where(d => d.IsExcluded && !d.IsOsPrimary && !d.IsTargetPrimary))
        {
            if (!string.IsNullOrEmpty(d.HardwareId))
                _settings.ExcludedIdentifiers.Add(d.HardwareId);
            else if (!string.IsNullOrEmpty(d.FriendlyName))
                _settings.ExcludedIdentifiers.Add(d.FriendlyName);
            else
                _settings.ExcludedIdentifiers.Add(d.DeviceName);
        }
    }

    private void OnDisplayAudioSettingChanged(DisplayInfo display)
    {
        if (!display.KeepAudioAlive)
        {
            if (!string.IsNullOrEmpty(display.HardwareId) && !_settings.DisabledAudioKeepAliveMonitors.Contains(display.HardwareId))
                _settings.DisabledAudioKeepAliveMonitors.Add(display.HardwareId);
            if (!string.IsNullOrEmpty(display.FriendlyName) && !_settings.DisabledAudioKeepAliveMonitors.Contains(display.FriendlyName))
                _settings.DisabledAudioKeepAliveMonitors.Add(display.FriendlyName);
            if (!string.IsNullOrEmpty(display.AudioEndpointId) && !_settings.DisabledAudioKeepAliveMonitors.Contains(display.AudioEndpointId))
                _settings.DisabledAudioKeepAliveMonitors.Add(display.AudioEndpointId);
        }
        else
        {
            if (!string.IsNullOrEmpty(display.HardwareId))
                _settings.DisabledAudioKeepAliveMonitors.Remove(display.HardwareId);
            if (!string.IsNullOrEmpty(display.FriendlyName))
                _settings.DisabledAudioKeepAliveMonitors.Remove(display.FriendlyName);
            if (!string.IsNullOrEmpty(display.AudioEndpointId))
                _settings.DisabledAudioKeepAliveMonitors.Remove(display.AudioEndpointId);
        }

        SaveSettings();
        UpdateAudioKeepAliveTargets();
    }

    public void UpdateAudioKeepAliveTargets()
    {
        if (_audioService != null)
        {
            var activeEndpointIds = Displays
                .Where(d => d.HasAudioEndpoint && d.KeepAudioAlive && !string.IsNullOrEmpty(d.AudioEndpointId))
                .Select(d => d.AudioEndpointId!)
                .ToList();
            _audioService.SetTargetEndpoints(activeEndpointIds);
        }
    }

    public void SweepNow()
    {
        _engine.SweepAllWindows();
    }

    private void SyncConfiguration()
    {
        _engine.UpdateConfiguration(Displays.ToList(), IsGuardActive, _settings.SweepIntervalMs);
    }

    private void SaveSettings()
    {
        ConfigService.Save(_settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
