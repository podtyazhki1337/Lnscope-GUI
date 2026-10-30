using System.Globalization;
using System.Windows.Data;

namespace GUI.Mvvm;

/// <summary>Boolean inversion for bindings, such as IsReadOnly to IsEnabled.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
