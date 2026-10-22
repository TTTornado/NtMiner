using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace NTMiner.Modern.WinUI;

/// <summary>
/// 把"该行没有检测到硬件"映射为次要文字色。
/// WinUI 3 不支持 DataTrigger，因此用转换器读取主题资源，浅色/深色主题都能跟随。
/// </summary>
public sealed partial class MissingBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var missing = value is bool flag && flag;
        var key = missing ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush";
        return Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush found
            ? found
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
