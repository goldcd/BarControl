using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using BarControl.Core.Audio;
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

    private readonly AudioKeepAliveService? _audioService;

    public MainWindowViewModel(WindowRelocationEngine engine, TrayIconService trayService, AudioKeepAliveService? audioService = null)
    {
        _engine = engine;
        _trayService = trayService;
        _audioService = audioService;
        _settings = ConfigService.Load();

        if (_audioService != null)
        {
            _audioService.IsEnabled = _settings.KeepAudioAwake;
        }

        _isGuardActive = _settings.IsGuardEnabled;
        UpdateGuardStatusVisuals();

        _engine.OnWindowRelocated += HandleWindowRelocated;
        _trayService.OnGuardToggled += (enabled) => IsGuardActive = enabled;
        _trayService.OnIdentifyRequested += () => IdentifyAllDisplays();
        _trayService.OnResetPrimaryRequested += () => ResetToWindowsPrimary();

        LoadDisplays();

        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            await CheckForUpdatesAsync();
        });
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

        // Default selection to target primary or first display
        SelectedDisplay = Displays.FirstOrDefault(d => d.IsTargetPrimary) ?? Displays.FirstOrDefault();

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
