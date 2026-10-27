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

    public string DisplayTitle => string.IsNullOrWhiteSpace(FriendlyName)
        ? $"Display {Index} ({DeviceName})"
        : FriendlyName;

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
