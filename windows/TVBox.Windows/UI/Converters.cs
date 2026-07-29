using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TVBoxForWindows.UI;

/// <summary>bool → Visibility；ConverterParameter="invert" 时取反。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if ("invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>字符串空/空白 → Collapsed，非空 → Visible；ConverterParameter="invert" 时取反。</summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = !string.IsNullOrWhiteSpace(value?.ToString());
        if ("invert".Equals(parameter as string, StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>集合数量/整数 &gt; 0 → Visible，否则 Collapsed。</summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => 0,
        };
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>bool 取反。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => !(value is bool b && b);
}
