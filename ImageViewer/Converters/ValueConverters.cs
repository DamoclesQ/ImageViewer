using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ImageViewer.Converters;

/// <summary>bool 未激活背景色</summary>
public sealed class BoolToActiveBrushConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(126, 182, 217));
    public Brush InactiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(220, 220, 220));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? ActiveBrush : InactiveBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Color 转换为 SolidColorBrush</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Color c ? new SolidColorBrush(c) : Brushes.Red;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
