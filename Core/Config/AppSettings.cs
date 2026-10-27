using System.Collections.Generic;

namespace BarControl.Core.Config;

public class AppSettings
{
    public bool IsGuardEnabled { get; set; } = true;
    public string? TargetPrimaryHardwareId { get; set; }
    public string? TargetPrimaryDeviceName { get; set; }
    public List<string> ExcludedIdentifiers { get; set; } = new();

    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool KeepAudioAwake { get; set; } = true;
    public List<string> DisabledAudioKeepAliveMonitors { get; set; } = new();
    public int SweepIntervalMs { get; set; } = 1000;

    // HDMI-CEC Settings
    public bool CecPowerSyncEnabled { get; set; } = true;
    public bool CecWakeOnStartupEnabled { get; set; } = false;
    public bool BitstreamVolumeProxyEnabled { get; set; } = true;
    public string? SelectedSoundbarIdentifier { get; set; }
}
