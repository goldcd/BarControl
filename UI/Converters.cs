using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BarControl.UI;

public class InverseBooleanConverter : IValueConverter
{
    public static readonly InverseBooleanConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return false;
    }
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public static readonly BooleanToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v == Visibility.Visible;
        }
        return false;
    }
}

public class PercentageToWidthConverter : IMultiValueConverter
{
    public static readonly PercentageToWidthConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is int percent && values[1] is double totalWidth)
        {
            if (double.IsNaN(totalWidth) || totalWidth <= 0) return 0.0;
            double clamped = Math.Clamp(percent, 0, 100) / 100.0;
            return Math.Max(0.0, totalWidth * clamped);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class PercentageToMarkerMarginConverter : IMultiValueConverter
{
    public static readonly PercentageToMarkerMarginConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is int percent && values[1] is double totalWidth)
        {
            if (double.IsNaN(totalWidth) || totalWidth <= 0) return new Thickness(0);
            double clamped = Math.Clamp(percent, 0, 100) / 100.0;
            double x = Math.Clamp((totalWidth * clamped) - 1.5, 0.0, Math.Max(0.0, totalWidth - 3.0));
            return new Thickness(x, 0, 0, 0);
        }
        return new Thickness(0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
