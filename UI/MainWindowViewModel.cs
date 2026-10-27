using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
using BarControl.Models;
using System.Threading.Tasks;
using System.Windows;

namespace BarControl.UI;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly WindowRelocationEngine _engine;
    private readonly TrayIconService _trayService;
    private readonly AudioKeepAliveService? _audioService;
    private readonly CecService? _cecService;
    private AppSettings _settings;

    private bool _isGuardActive = true;
    private int _relocatedCount = 0;
    private string _statusText = "GUARD ACTIVE";
    private Brush _statusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));

    public ObservableCollection<DisplayInfo> Displays { get; } = new();
    public ObservableCollection<RelocationLogItem> RelocationLog { get; } = new();

    public bool IsGuardActive
    {
        get => _isGuardActive;
        set
        {
            if (_isGuardActive != value)
            {
                _isGuardActive = value;
                _settings.IsGuardEnabled = value;
                UpdateGuardStatusVisuals();
                _trayService.SetGuardState(value);
                SyncConfiguration();
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    public void ToggleGuard()
    {
        IsGuardActive = !IsGuardActive;
    }

    public int RelocatedCount
    {
        get => _relocatedCount;
        set
        {
            _relocatedCount = value;
            OnPropertyChanged();
        }
    }

    private DisplayInfo? _selectedDisplay;

    public DisplayInfo? SelectedDisplay
    {
        get => _selectedDisplay;
        set
        {
            if (_selectedDisplay != value)
            {
                _selectedDisplay = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSelectedPrimary));
                OnPropertyChanged(nameof(IsSelectedExcluded));
            }
        }
    }

    private IntPtr _currentAppHMonitor = IntPtr.Zero;
    public IntPtr CurrentAppHMonitor
    {
        get => _currentAppHMonitor;
        set
        {
            if (_currentAppHMonitor != value)
            {
                _currentAppHMonitor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSetSelectedAsPrimary));
                OnPropertyChanged(nameof(PrimaryButtonToolTip));
            }
        }
    }

    public bool CanSetSelectedAsPrimary
    {
        get
        {
            if (SelectedDisplay == null) return false;
            if (SelectedDisplay.IsTargetPrimary) return false;
            if (SelectedDisplay.IsExcluded) return false;

            // Feedback 3: Only the display the app is currently on can be set as primary!
            if (CurrentAppHMonitor != IntPtr.Zero && SelectedDisplay.HMonitor != IntPtr.Zero)
            {
                return SelectedDisplay.HMonitor == CurrentAppHMonitor;
            }
            return true;
        }
    }

    public string PrimaryButtonToolTip
    {
        get
        {
            if (SelectedDisplay == null) return string.Empty;
            if (SelectedDisplay.IsTargetPrimary) return "This display is already designated as the primary target.";
            if (SelectedDisplay.IsExcluded) return "A blocked display cannot be designated as primary.";
            if (CurrentAppHMonitor != IntPtr.Zero && SelectedDisplay.HMonitor != IntPtr.Zero && SelectedDisplay.HMonitor != CurrentAppHMonitor)
            {
                return "Safety Protection: Only the display currently showing this BarControl window can be set as primary. Move this window to that display first.";
            }
            return "Set this display as the primary target.";
        }
    }

    public void UpdateCurrentAppMonitor(IntPtr hMonitor)
    {
        CurrentAppHMonitor = hMonitor;
    }

    public bool IsSelectedPrimary => SelectedDisplay?.IsTargetPrimary ?? false;
    public bool IsSelectedExcluded => SelectedDisplay?.IsExcluded ?? false;

    public int BlockedCount => Displays.Count(d => d.IsExcluded);
    public int AllowedCount => Displays.Count(d => !d.IsExcluded);

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set
        {
            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public string AppVersion
    {
        get
        {
            var v = typeof(MainWindowViewModel).Assembly.GetName().Version;
            return v != null ? $"v{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "v1.0.0";
        }
    }

    private readonly UpdateService _updateService = new();
    private UpdateInfo? _availableUpdate;
    private bool _isDownloadingUpdate;
    private string _updateButtonText = "Update";

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

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update != null)
            {
                _availableUpdate = update;
                UpdateButtonText = $"Update to {update.Version}";
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(AvailableUpdateVersion));
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

    public bool ShowNotifications
    {
        get => _settings.ShowNotificationOnRelocate;
        set
        {
            if (_settings.ShowNotificationOnRelocate != value)
            {
                _settings.ShowNotificationOnRelocate = value;
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    public bool KeepAudioAwake
    {
        get => _settings.KeepAudioAwake;
        set
        {
            if (_settings.KeepAudioAwake != value)
            {
                _settings.KeepAudioAwake = value;
                if (_audioService != null)
                {
                    _audioService.IsEnabled = value;
                }
                SaveSettings();
                OnPropertyChanged();
            }
        }
    }

    public MainWindowViewModel(WindowRelocationEngine engine, TrayIconService trayService, AudioKeepAliveService? audioService = null, CecService? cecService = null)
    {
        _engine = engine;
        _trayService = trayService;
        _audioService = audioService;
        _cecService = cecService;
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

        _isGuardActive = _settings.IsGuardEnabled;
        UpdateGuardStatusVisuals();

        _engine.OnWindowRelocated += HandleWindowRelocated;

        LoadDisplays();

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await CheckForUpdatesAsync();
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
                SelectedDisplay = value;
                if (value != null)
                {
                    _settings.SelectedSoundbarIdentifier = !string.IsNullOrEmpty(value.HardwareId) ? value.HardwareId : value.FriendlyName;
                    SaveSettings();
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPhantomDisplayGuardEnabled));
                OnPropertyChanged(nameof(IsAudioKeepAliveEnabled));
            }
        }
    }

    public bool IsPhantomDisplayGuardEnabled
    {
        get => SelectedSoundbar?.IsExcluded ?? false;
        set
        {
            if (SelectedSoundbar != null && SelectedSoundbar.IsExcluded != value)
            {
                SelectedSoundbar.IsExcluded = value;
                if (value && SelectedSoundbar.IsTargetPrimary)
                {
                    SelectedSoundbar.IsTargetPrimary = false;
                    EnsureValidTargetPrimary();
                }
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
    public string CecPortName => _cecService?.AdapterPort ?? "COM6";
    public string SoundbarCecName => _cecService?.SoundbarName ?? "Audio";
    public string SoundbarVendor => _cecService?.SoundbarVendor ?? "Panasonic";

    public string CecPowerStatusText
    {
        get
        {
            if (_cecService == null || !_cecService.IsConnected) return "SEARCHING FOR ADAPTER...";
            return _cecService.PowerStatus switch
            {
                LibCec.CEC_POWER_STATUS_ON => "POWERED ON",
                LibCec.CEC_POWER_STATUS_STANDBY => "STANDBY",
                LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON => "POWERING ON...",
                LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY => "ENTERING STANDBY...",
                _ => "UNKNOWN / SLEEP"
            };
        }
    }

    public Brush CecPowerStatusBrush
    {
        get
        {
            if (_cecService == null || !_cecService.IsConnected) return new SolidColorBrush(Color.FromRgb(113, 113, 122));
            return _cecService.PowerStatus switch
            {
                LibCec.CEC_POWER_STATUS_ON => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                LibCec.CEC_POWER_STATUS_STANDBY => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                LibCec.CEC_POWER_STATUS_IN_TRANSITION_STANDBY_TO_ON => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                LibCec.CEC_POWER_STATUS_IN_TRANSITION_ON_TO_STANDBY => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
            };
        }
    }

    public string CecVolumeText
    {
        get
        {
            if (_cecService == null || !_cecService.IsConnected) return string.Empty;
            var st = _cecService.AudioStatus;
            if (st.VolumePercent == 127) return "Vol: --";
            return (st.IsMuted ? "🔇 " : "🔊 ") + $"Vol: {st.VolumePercent}%" + (st.IsMuted ? " (Muted)" : "");
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
                OnPropertyChanged(nameof(CanOperatePower));
                OnPropertyChanged(nameof(CanTogglePower));
            }
        }
    }

    public bool CanOperatePower => !IsOperatingPower;

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

    public int CecVolumeSliderValue
    {
        get => _sliderVolumePercent;
        set
        {
            if (_sliderVolumePercent != value)
            {
                _sliderVolumePercent = value;
                OnPropertyChanged();
            }
        }
    }

    public void OnSliderDragStarted()
    {
        _isUserDraggingSlider = true;
    }

    public async Task OnSliderDragEndedAsync()
    {
        _isUserDraggingSlider = false;
        if (_cecService != null && CanControlVolume)
        {
            await _cecService.SetVolumeTargetAsync(_sliderVolumePercent);
        }
    }

    public async Task StepVolumeUpAsync()
    {
        if (_cecService != null && CanControlVolume)
        {
            await _cecService.StepVolumeAsync(true);
        }
    }

    public async Task StepVolumeDownAsync()
    {
        if (_cecService != null && CanControlVolume)
        {
            await _cecService.StepVolumeAsync(false);
        }
    }

    public void NotifyPowerButtonState()
    {
        OnPropertyChanged(nameof(IsOperatingPower));
        OnPropertyChanged(nameof(CanOperatePower));
        OnPropertyChanged(nameof(CanTogglePower));
        OnPropertyChanged(nameof(CanControlVolume));
        OnPropertyChanged(nameof(PowerToggleButtonText));
        OnPropertyChanged(nameof(PowerToggleButtonBackground));
        OnPropertyChanged(nameof(PowerToggleButtonBorderBrush));
        OnPropertyChanged(nameof(PowerToggleButtonForeground));
        OnPropertyChanged(nameof(CecPowerStatusText));
        OnPropertyChanged(nameof(CecPowerStatusBrush));
        OnPropertyChanged(nameof(CecVolumeText));

        if (!_isUserDraggingSlider && _cecService != null)
        {
            int vol = _cecService.AudioStatus.VolumePercent;
            if (vol >= 0 && vol <= 100)
            {
                CecVolumeSliderValue = vol;
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

    public async Task RefreshCecStatusAsync()
    {
        if (_cecService != null)
        {
            await _cecService.RefreshStatusAsync();
            OnCecStateChanged();
        }
    }

    public void LoadDisplays()
    {
        Displays.Clear();
        var monitors = DisplayService.GetConnectedMonitors();

        foreach (var m in monitors)
        {
            // Restore saved settings
            if (_settings.ExcludedIdentifiers.Contains(m.HardwareId) ||
                _settings.ExcludedIdentifiers.Contains(m.DeviceName) ||
                _settings.ExcludedIdentifiers.Contains(m.FriendlyName))
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

        // If the saved target primary monitor is no longer connected (e.g. replaced with a new monitor),
        // clear the stale reference so BarControl cleanly adopts the new Windows primary.
        if (!string.IsNullOrEmpty(_settings.TargetPrimaryHardwareId) &&
            !monitors.Any(m => m.HardwareId == _settings.TargetPrimaryHardwareId || m.DeviceName == _settings.TargetPrimaryDeviceName))
        {
            _settings.TargetPrimaryHardwareId = null;
            _settings.TargetPrimaryDeviceName = null;
            SaveSettings();
        }

        // If no target primary is set or target primary is excluded, pick OS primary or first non-excluded
        EnsureValidTargetPrimary();

        // Auto-select soundbar: match saved setting, or display with audio endpoint (e.g. SOUNDSLAYER), or first non-primary
        SelectedSoundbar = Displays.FirstOrDefault(d => !string.IsNullOrEmpty(_settings.SelectedSoundbarIdentifier) &&
                                                        (d.HardwareId == _settings.SelectedSoundbarIdentifier || d.FriendlyName == _settings.SelectedSoundbarIdentifier))
                           ?? Displays.FirstOrDefault(d => d.HasAudioEndpoint)
                           ?? Displays.FirstOrDefault(d => !d.IsOsPrimary)
                           ?? Displays.FirstOrDefault();

        // Default selection to target primary or first display
        SelectedDisplay = SelectedSoundbar ?? Displays.FirstOrDefault(d => d.IsTargetPrimary) ?? Displays.FirstOrDefault();

        SyncConfiguration();
        UpdateAudioKeepAliveTargets();
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(AllowedCount));
    }

    public void SetSelectedAsPrimary()
    {
        if (SelectedDisplay != null)
        {
            SetTargetPrimary(SelectedDisplay);
            OnPropertyChanged(nameof(IsSelectedPrimary));
            OnPropertyChanged(nameof(IsSelectedExcluded));
        }
    }

    public void ToggleSelectedExcluded()
    {
        if (SelectedDisplay != null)
        {
            ToggleExcluded(SelectedDisplay);
            OnPropertyChanged(nameof(IsSelectedPrimary));
            OnPropertyChanged(nameof(IsSelectedExcluded));
        }
    }

    public void IdentifySelected()
    {
        if (SelectedDisplay != null)
        {
            IdentifySingleDisplay(SelectedDisplay);
        }
    }

    private void EnsureValidTargetPrimary()
    {
        var target = Displays.FirstOrDefault(d => d.IsTargetPrimary && !d.IsExcluded);
        if (target == null)
        {
            // Pick OS primary if not excluded
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

    public void SetTargetPrimary(DisplayInfo selectedDisplay)
    {
        if (selectedDisplay.IsExcluded)
        {
            selectedDisplay.IsExcluded = false; // Cannot be primary and excluded
        }

        foreach (var d in Displays)
        {
            d.IsTargetPrimary = (d == selectedDisplay);
        }

        _settings.TargetPrimaryHardwareId = selectedDisplay.HardwareId;
        _settings.TargetPrimaryDeviceName = selectedDisplay.DeviceName;

        SyncConfiguration();
        SaveSettings();
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(AllowedCount));
    }

    public void ToggleExcluded(DisplayInfo display)
    {
        if (!display.IsExcluded && display.IsTargetPrimary)
        {
            MessageBox.Show(
                "You cannot block your primary target display.\nPlease set another display as your primary target first.",
                "Primary Display Cannot Be Blocked",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        display.IsExcluded = !display.IsExcluded;

        UpdateExcludedSettingsList();
        SyncConfiguration();
        SaveSettings();

        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(AllowedCount));
        OnPropertyChanged(nameof(IsSelectedPrimary));
        OnPropertyChanged(nameof(IsSelectedExcluded));

        if (display.IsExcluded)
        {
            // Immediately relocate any windows sitting on this newly excluded monitor
            SweepNow();
        }
    }

    public void ResetToWindowsPrimary()
    {
        var osPrimary = Displays.FirstOrDefault(d => d.IsOsPrimary) ?? Displays.FirstOrDefault();
        if (osPrimary != null)
        {
            osPrimary.IsExcluded = false;
            foreach (var d in Displays)
            {
                d.IsTargetPrimary = (d == osPrimary);
            }

            _settings.TargetPrimaryHardwareId = osPrimary.HardwareId;
            _settings.TargetPrimaryDeviceName = osPrimary.DeviceName;

            UpdateExcludedSettingsList();
            SyncConfiguration();
            SaveSettings();

            SelectedDisplay = osPrimary;
            OnPropertyChanged(nameof(IsSelectedPrimary));
            OnPropertyChanged(nameof(IsSelectedExcluded));
            OnPropertyChanged(nameof(BlockedCount));
            OnPropertyChanged(nameof(AllowedCount));

            SweepNow();
        }
    }

    private void OnDisplaySettingChanged(DisplayInfo display, string propertyName)
    {
        if (propertyName == nameof(DisplayInfo.IsExcluded) && display.IsExcluded && display.IsTargetPrimary)
        {
            display.IsTargetPrimary = false;
            EnsureValidTargetPrimary();
        }

        UpdateExcludedSettingsList();
        SyncConfiguration();
        SaveSettings();
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(AllowedCount));
    }

    private void UpdateExcludedSettingsList()
    {
        _settings.ExcludedIdentifiers.Clear();
        foreach (var d in Displays.Where(d => d.IsExcluded))
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

    public void IdentifyAllDisplays()
    {
        foreach (var display in Displays)
        {
            var overlay = new IdentifyOverlayWindow(display);
            overlay.Show();
        }
    }

    public void IdentifySingleDisplay(DisplayInfo display)
    {
        var overlay = new IdentifyOverlayWindow(display);
        overlay.Show();
    }

    private void HandleWindowRelocated(RelocationLogItem item)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RelocationLog.Insert(0, item);
            if (RelocationLog.Count > 100)
            {
                RelocationLog.RemoveAt(RelocationLog.Count - 1);
            }
            RelocatedCount++;

            if (_settings.ShowNotificationOnRelocate)
            {
                _trayService.ShowRelocationNotification(item);
            }
        });
    }

    public void ClearLog()
    {
        RelocationLog.Clear();
        RelocatedCount = 0;
    }

    private void SyncConfiguration()
    {
        _engine.UpdateConfiguration(Displays.ToList(), IsGuardActive, _settings.SweepIntervalMs);
    }

    private void SaveSettings()
    {
        ConfigService.Save(_settings);
    }

    private void UpdateGuardStatusVisuals()
    {
        if (_isGuardActive)
        {
            StatusText = "GUARD ACTIVE";
            StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
        }
        else
        {
            StatusText = "GUARD PAUSED";
            StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
