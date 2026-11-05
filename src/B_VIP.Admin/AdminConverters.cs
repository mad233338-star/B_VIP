using System.Globalization;
using System.Windows.Data;

namespace B_VIP.Admin
{
    /// <summary>Joins a string collection ("admin", "operator") so role columns read naturally.</summary>
    public sealed class ListJoinConverter : IValueConverter
    {
        public string Separator { get; set; } = ", ";

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is IEnumerable<string> items ? string.Join(Separator, items) : value?.ToString() ?? string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
