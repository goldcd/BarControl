using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BarControl.Core.Display;

namespace BarControl.UI;

public partial class MonitorVisualMap : UserControl
{
    public static readonly DependencyProperty DisplaysSourceProperty =
        DependencyProperty.Register(
            nameof(DisplaysSource),
            typeof(IEnumerable<DisplayInfo>),
            typeof(MonitorVisualMap),
            new PropertyMetadata(null, OnDisplaysSourceChanged));

    public static readonly DependencyProperty SelectedDisplayProperty =
        DependencyProperty.Register(
            nameof(SelectedDisplay),
            typeof(DisplayInfo),
            typeof(MonitorVisualMap),
            new PropertyMetadata(null, OnSelectedDisplayChanged));

    public IEnumerable<DisplayInfo>? DisplaysSource
    {
        get => (IEnumerable<DisplayInfo>?)GetValue(DisplaysSourceProperty);
        set => SetValue(DisplaysSourceProperty, value);
    }

    public DisplayInfo? SelectedDisplay
    {
        get => (DisplayInfo?)GetValue(SelectedDisplayProperty);
        set => SetValue(SelectedDisplayProperty, value);
    }

    public event Action<DisplayInfo>? MonitorSelected;

    public MonitorVisualMap()
    {
        InitializeComponent();
        SizeChanged += (s, e) => Redraw();
    }

    private static void OnDisplaysSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MonitorVisualMap map)
        {
            if (e.OldValue is INotifyCollectionChanged oldCol)
            {
                oldCol.CollectionChanged -= map.OnCollectionChanged;
            }
            if (e.NewValue is INotifyCollectionChanged newCol)
            {
                newCol.CollectionChanged += map.OnCollectionChanged;
            }

            map.HookItemPropertyChanged(e.OldValue as IEnumerable<DisplayInfo>, false);
            map.HookItemPropertyChanged(e.NewValue as IEnumerable<DisplayInfo>, true);

            map.Redraw();
        }
    }

    private static void OnSelectedDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MonitorVisualMap map)
        {
            map.Redraw();
        }
    }

    private void HookItemPropertyChanged(IEnumerable<DisplayInfo>? items, bool hook)
    {
        if (items == null) return;
        foreach (var item in items)
        {
            if (hook)
                item.PropertyChanged += OnItemPropertyChanged;
            else
                item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HookItemPropertyChanged(e.OldItems?.Cast<DisplayInfo>(), false);
        HookItemPropertyChanged(e.NewItems?.Cast<DisplayInfo>(), true);
        Redraw();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplayInfo.IsExcluded) or nameof(DisplayInfo.IsTargetPrimary) or nameof(DisplayInfo.FriendlyName))
        {
            Dispatcher.Invoke(Redraw);
        }
    }

    public void Redraw()
    {
        MapCanvas.Children.Clear();

        var displays = DisplaysSource?.ToList();
        if (displays == null || displays.Count == 0)
        {
            EmptyNotice.Visibility = Visibility.Visible;
            return;
        }

        EmptyNotice.Visibility = Visibility.Collapsed;

        double availableW = Math.Max(200, (ActualWidth > 0 ? ActualWidth : 750) - 40);
        double availableH = Math.Max(120, (ActualHeight > 0 ? ActualHeight : 220) - 30);

        int minX = displays.Min(d => d.Left);
        int minY = displays.Min(d => d.Top);
        int maxX = displays.Max(d => d.Right);
        int maxY = displays.Max(d => d.Bottom);

        int totalSpanX = Math.Max(1, maxX - minX);
        int totalSpanY = Math.Max(1, maxY - minY);

        // Base geometric scale
        double scale = Math.Min(availableW / totalSpanX, availableH / totalSpanY);

        // Minimum box dimensions for clean text layout
        double minBoxW = 140.0;
        double minBoxH = 88.0;

        // Calculate preliminary box positions and dimensions
        var boxSpecs = displays.Select(d =>
        {
            double x = (d.Left - minX) * scale;
            double y = (d.Top - minY) * scale;
            double w = Math.Max(minBoxW, d.Width * scale);
            double h = Math.Max(minBoxH, d.Height * scale);
            return (Display: d, X: x, Y: y, W: w, H: h);
        }).ToList();

        // Ensure furthest right and bottom edges strictly fit inside available canvas space without clipping
        double currentMaxX = boxSpecs.Max(b => b.X + b.W);
        double currentMaxY = boxSpecs.Max(b => b.Y + b.H);

        if (currentMaxY > availableH || currentMaxX > availableW)
        {
            double adjustFactor = Math.Min(availableW / currentMaxX, availableH / currentMaxY);
            scale *= adjustFactor;

            double adjustedMinW = Math.Max(90.0, minBoxW * adjustFactor);
            double adjustedMinH = Math.Max(60.0, minBoxH * adjustFactor);

            boxSpecs = displays.Select(d =>
            {
                double x = (d.Left - minX) * scale;
                double y = (d.Top - minY) * scale;
                double w = Math.Max(adjustedMinW, d.Width * scale);
                double h = Math.Max(adjustedMinH, d.Height * scale);
                return (Display: d, X: x, Y: y, W: w, H: h);
            }).ToList();
        }

        double finalCanvasW = Math.Round(boxSpecs.Max(b => b.X + b.W));
        double finalCanvasH = Math.Round(boxSpecs.Max(b => b.Y + b.H));

        MapCanvas.Width = finalCanvasW;
        MapCanvas.Height = finalCanvasH;

        foreach (var b in boxSpecs)
        {
            var d = b.Display;
            double x = Math.Round(b.X);
            double y = Math.Round(b.Y);
            double w = Math.Round(b.W);
            double h = Math.Round(b.H);

            bool isSelected = SelectedDisplay != null && (SelectedDisplay == d || SelectedDisplay.DeviceName == d.DeviceName);

            var border = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(10),
                Cursor = Cursors.Hand,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ToolTip = $"{d.DisplayTitle}\nResolution: {d.ResolutionString}\nStatus: {d.StatusDescription}\nClick to select"
            };

            if (isSelected)
            {
                border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                border.BorderThickness = new Thickness(2.5);
                border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 37, 99, 235));
                border.Effect = new DropShadowEffect
                {
                    Color = System.Windows.Media.Color.FromRgb(59, 130, 246),
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.5
                };
            }
            else
            {
                border.BorderThickness = new Thickness(1.5);
                border.Effect = null;

                if (d.IsTargetPrimary)
                {
                    border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 16, 185, 129));
                }
                else if (d.IsExcluded)
                {
                    border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                    border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(45, 239, 68, 68));
                }
                else
                {
                    border.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 82, 91));
                    border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255));
                }
            }

            var grid = new Grid
            {
                Margin = new Thickness(10, 8, 10, 8)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Top Row: Number badge on left, status badge on right (NEVER collides with name!)
            var topGrid = new Grid();
            Grid.SetRow(topGrid, 0);

            var indexBadge = new Border
            {
                Width = 24,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = isSelected
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235))
                    : new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 40, 44, 52)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            indexBadge.Child = new TextBlock
            {
                Text = d.Index.ToString(),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            topGrid.Children.Add(indexBadge);

            // Status pill on the top right
            string statusStr = d.IsTargetPrimary ? "TARGET" : (d.IsExcluded ? "BLOCKED ⛔" : "ALLOWED");
            var statusColor = d.IsTargetPrimary
                ? System.Windows.Media.Color.FromRgb(52, 211, 153)
                : (d.IsExcluded ? System.Windows.Media.Color.FromRgb(248, 113, 113) : System.Windows.Media.Color.FromRgb(161, 161, 170));

            var statusBadge = new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, statusColor.R, statusColor.G, statusColor.B)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            statusBadge.Child = new TextBlock
            {
                Text = statusStr,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(statusColor)
            };
            topGrid.Children.Add(statusBadge);

            grid.Children.Add(topGrid);

            // Center Row: Display Title & Resolution (Unclipped and centered)
            var centerStack = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetRow(centerStack, 1);

            var nameText = new TextBlock
            {
                Text = d.DisplayTitle,
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.None
            };
            centerStack.Children.Add(nameText);

            var resText = new TextBlock
            {
                Text = d.ResolutionString,
                FontSize = 11,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            centerStack.Children.Add(resText);

            grid.Children.Add(centerStack);
            border.Child = grid;

            border.MouseLeftButtonUp += (s, e) =>
            {
                SelectedDisplay = d;
                MonitorSelected?.Invoke(d);
            };

            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            MapCanvas.Children.Add(border);
        }
    }
}
