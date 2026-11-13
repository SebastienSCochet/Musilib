using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Musilib.Desktop.Converters;

/// <summary>Récupère une géométrie d'icône depuis les ressources de l'application à partir de sa clé.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string ?? parameter as string;
        if (key is null || Avalonia.Application.Current is null) return null;
        return Avalonia.Application.Current.TryFindResource(key, Avalonia.Application.Current.ActualThemeVariant, out var res) ? res as Geometry : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Formatters
{
    public static string Duration(long? ms)
    {
        if (ms is null or <= 0) return "—";
        var t = TimeSpan.FromMilliseconds(ms.Value);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Relative(DateTimeOffset? date)
    {
        if (date is null) return "—";
        var diff = DateTimeOffset.UtcNow - date.Value;
        if (diff < TimeSpan.FromMinutes(1)) return "à l'instant";
        if (diff < TimeSpan.FromHours(1)) return $"il y a {(int)diff.TotalMinutes} min";
        if (diff < TimeSpan.FromDays(1)) return $"il y a {(int)diff.TotalHours} h";
        if (diff < TimeSpan.FromDays(7)) return $"il y a {(int)diff.TotalDays} j";
        return date.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
    }

    public static string DateTime(DateTimeOffset? date) =>
        date is null ? "—" : date.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR"));

    public static string Percent(double? value) => value is null ? "—" : value.Value.ToString("P0", CultureInfo.GetCultureInfo("fr-FR"));
}
