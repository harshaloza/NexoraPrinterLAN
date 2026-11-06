using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Nexora.UI;

/// <summary>Job state (wire or display name) → status colour.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString()?.Replace(" ", "").ToUpperInvariant() ?? "";
        string key = s switch
        {
            "COMPLETED" or "VALIDATED" or "RECEIVED" => "Brush.Ok",
            "FAILED" => "Brush.Bad",
            "CANCELLED" => "Brush.Unknown",
            _ => "Brush.Warn",
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool? health → green (true), red (false), grey (null = not checked yet / unknown).</summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(value switch { true => "Brush.Ok", false => "Brush.Bad", _ => "Brush.Unknown" }) ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Navigation: Visible when the bound value equals the parameter.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Navigation radio buttons: checked when value equals parameter; checking sets the value to the parameter.</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter! : Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
