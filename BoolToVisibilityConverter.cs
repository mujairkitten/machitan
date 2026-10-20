using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Machitan;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static Visibility ToVis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
