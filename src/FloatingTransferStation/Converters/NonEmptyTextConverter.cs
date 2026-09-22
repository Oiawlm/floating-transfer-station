using System.Globalization;
using System.Windows.Data;

namespace FloatingTransferStation.Converters;

/// <summary>非空字符串转 true，用于状态文本驱动显隐淡入。</summary>
public sealed class NonEmptyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
