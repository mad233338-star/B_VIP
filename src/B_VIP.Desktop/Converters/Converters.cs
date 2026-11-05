using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using B_VIP.Desktop.Services;

namespace B_VIP.Desktop.Converters
{
    /// <summary>Console severity colours: cyan info, green success, amber warning, red error.</summary>
    public sealed class LogLevelToBrushConverter : IValueConverter
    {
        private static readonly Brush Info = Freeze(0x00, 0xE5, 0xFF);
        private static readonly Brush Success = Freeze(0x22, 0xC5, 0x5E);
        private static readonly Brush Warning = Freeze(0xF5, 0x9E, 0x0B);
        private static readonly Brush Error = Freeze(0xEF, 0x44, 0x44);
        private static readonly Brush Process = Freeze(0x7D, 0xE9, 0xFF);

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is LogLevel level
                ? level switch
                {
                    LogLevel.Success => Success,
                    LogLevel.Warning => Warning,
                    LogLevel.Error => Error,
                    LogLevel.Process => Process,
                    _ => Info
                }
                : Info;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static Brush Freeze(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class StatusToBrushConverter : IValueConverter
    {
        private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0x4F, 0xB4, 0x77));
        private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x5B));
        // Default accent for statuses without a severity: Cyber Blue, never the legacy gold.
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string? status = value?.ToString()?.ToLowerInvariant() ?? string.Empty;

            if (status is "pass" or "passed" or "succeeded" or "success" or "active" or "healthy" or "connected" or "available" or "resolved" or "done")
            {
                return Success;
            }

            if (status is "warn" or "warning" or "pending" or "requiresattention" or "inprogress" or "degraded" or "waitingforuser" or "waitingforsupport" or "reopened" or "open")
            {
                return Warning;
            }

            if (status is "fail" or "failed" or "error" or "unhealthy" or "revoked" or "expired" or "suspended" or "cancelled" or "timedout" or "closed" or "unauthorized" or "driverproblem")
            {
                return Danger;
            }

            if (status is "skip" or "skipped" or "unknown" or "notconfigured" or "unavailable" or "nodDevice" or "disconnected" or "no device")
            {
                return Muted;
            }

            return Accent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public bool HideWhenEmpty { get; set; } = true;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isEmpty = value is null ||
                           (value is string text && string.IsNullOrWhiteSpace(text)) ||
                           (value is int count && count == 0);

            bool hide = HideWhenEmpty ? isEmpty : !isEmpty;
            if (parameter?.ToString() == "invert")
            {
                hide = !hide;
            }

            return hide ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public sealed class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool flag ? !flag : Binding.DoNothing;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool flag ? !flag : Binding.DoNothing;
    }

    public sealed class LanguageEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public sealed class FlowDirectionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
