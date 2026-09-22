using System.Globalization;
using System.Windows.Data;

namespace FloatingTransferStation.Converters;

/// <summary>多布尔值取或；未设置或非布尔值按 false 处理。</summary>
public sealed class BooleanOrConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.OfType<bool>().Any(value => value);

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
