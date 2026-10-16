using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>StatusKind → 상태 색 (단계 링 등)</summary>
public sealed class KindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is StatusKind k ? Theme.For(k) : Theme.Pending;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>double? → double (없으면 NaN: 링은 바탕 원만)</summary>
public sealed class NullableToNaNConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>문자열이 비었으면 Collapsed</summary>
public sealed class NonEmptyToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
