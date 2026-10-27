using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BarControl.Core.Display;

public class DisplayInfo : INotifyPropertyChanged
{
    private bool _isTargetPrimary;
    private bool _isExcluded;

    public int Index { get; set; }
    public IntPtr HMonitor { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;

    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public int WorkLeft { get; set; }
    public int WorkTop { get; set; }
    public int WorkRight { get; set; }
    public int WorkBottom { get; set; }
    public int WorkWidth => WorkRight - WorkLeft;
    public int WorkHeight => WorkBottom - WorkTop;

    public bool IsOsPrimary { get; set; }

    public bool IsTargetPrimary
    {
        get => _isTargetPrimary;
        set
        {
            if (_isTargetPrimary != value)
            {
                _isTargetPrimary = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDescription));
                OnPropertyChanged(nameof(StatusBadgeBackground));
                OnPropertyChanged(nameof(StatusBadgeForeground));
            }
        }
    }

    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded != value)
            {
                _isExcluded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDescription));
                OnPropertyChanged(nameof(StatusBadgeBackground));
                OnPropertyChanged(nameof(StatusBadgeForeground));
            }
        }
    }

    private bool _hasAudioEndpoint;
    public bool HasAudioEndpoint
    {
        get => _hasAudioEndpoint;
        set
        {
            if (_hasAudioEndpoint != value)
            {
                _hasAudioEndpoint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    private string? _audioEndpointId;
    public string? AudioEndpointId
    {
        get => _audioEndpointId;
        set
        {
            if (_audioEndpointId != value)
            {
                _audioEndpointId = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _audioEndpointName;
    public string? AudioEndpointName
    {
        get => _audioEndpointName;
        set
        {
            if (_audioEndpointName != value)
            {
                _audioEndpointName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AudioEndpointSubtitle));
            }
        }
    }

    private bool _keepAudioAlive = true;
    public bool KeepAudioAlive
    {
        get => _keepAudioAlive;
        set
        {
            if (_keepAudioAlive != value)
            {
                _keepAudioAlive = value;
                OnPropertyChanged();
            }
        }
    }

    public string ResolutionString => $"{Width} × {Height}";
    public string PositionString => $"Position: ({Left}, {Top})";

    public string DisplayTitle => string.IsNullOrWhiteSpace(FriendlyName)
        ? $"Display {Index} ({DeviceName})"
        : FriendlyName;

    public string Subtitle => $"{ResolutionString} • {(IsOsPrimary ? "Windows Primary" : "Secondary")}{(HasAudioEndpoint ? " • 🔊 Audio" : "")}";

    public string AudioEndpointSubtitle => string.IsNullOrEmpty(AudioEndpointName)
        ? "No associated audio output"
        : $"Prevents {AudioEndpointName} from sleeping over HDMI/DP";

    public string StatusDescription
    {
        get
        {
            if (IsTargetPrimary) return "SAFE TARGET (PRIMARY)";
            if (IsExcluded) return "OFF-LIMITS (BLOCKED)";
            return "ALLOWED";
        }
    }

    public string StatusBadgeBackground
    {
        get
        {
            if (IsTargetPrimary) return "#107C41"; // Fluent Green
            if (IsExcluded) return "#C42B1C"; // Fluent Red
            return "#3A3D40"; // Neutral grey
        }
    }

    public string StatusBadgeForeground => "#FFFFFF";

    public bool ContainsPoint(int x, int y)
    {
        return x >= Left && x < Right && y >= Top && y < Bottom;
    }

    public double OverlapArea(int wLeft, int wTop, int wRight, int wBottom)
    {
        int intersectLeft = Math.Max(Left, wLeft);
        int intersectTop = Math.Max(Top, wTop);
        int intersectRight = Math.Min(Right, wRight);
        int intersectBottom = Math.Min(Bottom, wBottom);

        if (intersectRight > intersectLeft && intersectBottom > intersectTop)
        {
            return (double)(intersectRight - intersectLeft) * (double)(intersectBottom - intersectTop);
        }
        return 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
