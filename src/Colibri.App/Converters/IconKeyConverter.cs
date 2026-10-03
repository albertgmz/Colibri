using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Colibri.App.Converters;

/// <summary>
/// Turns an icon resource key (such as "IconCategoryVideo") into the icon's geometry from
/// Resources/Icons.axaml. Lets view models name an icon without referencing Avalonia types.
/// </summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string { Length: > 0 } key
            && Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var resource))
        {
            return resource as Geometry;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
