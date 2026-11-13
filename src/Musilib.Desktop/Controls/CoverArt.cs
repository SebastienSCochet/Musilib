using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Musilib.Desktop.Services;

namespace Musilib.Desktop.Controls;

/// <summary>
/// Pochette / miniature chargée de façon asynchrone (cache disque + mémoire). En l'absence d'image,
/// affiche un dégradé déterministe avec les initiales, dans l'esprit des applications musicales.
/// </summary>
public sealed class CoverArt : Border
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<CoverArt, string?>(nameof(Source));

    public static readonly StyledProperty<string?> FallbackTextProperty =
        AvaloniaProperty.Register<CoverArt, string?>(nameof(FallbackText));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<CoverArt, int>(nameof(DecodeWidth), 240);

    private static readonly (Color A, Color B)[] Palettes =
    [
        (Color.Parse("#FF1ED760"), Color.Parse("#FF0B5C3A")),
        (Color.Parse("#FF7B5CFF"), Color.Parse("#FF2B1C6E")),
        (Color.Parse("#FFFF6B6B"), Color.Parse("#FF6E1C2B")),
        (Color.Parse("#FFFFB547"), Color.Parse("#FF7A3E07")),
        (Color.Parse("#FF3FB8FF"), Color.Parse("#FF0D3B6E")),
        (Color.Parse("#FFFF5FA2"), Color.Parse("#FF5E1240")),
        (Color.Parse("#FF2DD4BF"), Color.Parse("#FF0E4D47")),
        (Color.Parse("#FFA3E635"), Color.Parse("#FF365314"))
    ];

    private readonly Border _gradient;
    private readonly TextBlock _initials;
    private readonly Image _image;
    private int _loadToken;

    public CoverArt()
    {
        ClipToBounds = true;
        CornerRadius = new CornerRadius(8);
        _gradient = new Border();
        _initials = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            Opacity = 0.92
        };
        _image = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
        _image.Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }
        };
        Child = new Grid { Children = { _gradient, _initials, _image } };
        UpdateFallback();
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? FallbackText
    {
        get => GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) _ = LoadAsync(Source);
        else if (change.Property == FallbackTextProperty) UpdateFallback();
        else if (change.Property == BoundsProperty) _initials.FontSize = Math.Max(10, Bounds.Height * 0.32);
    }

    private void UpdateFallback()
    {
        var text = FallbackText ?? string.Empty;
        var hash = 17;
        foreach (var ch in text) hash = unchecked(hash * 31 + ch);
        var (a, b) = Palettes[Math.Abs(hash % Palettes.Length)];
        _gradient.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) }
        };
        _initials.Text = Initials(text);
    }

    private static string Initials(string text)
    {
        var words = text.Split([' ', '-', '_', '·'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).Take(2).ToList();
        return words.Count == 0 ? "♪" : string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
    }

    private async Task LoadAsync(string? source)
    {
        var token = ++_loadToken;
        _image.Opacity = 0;
        _image.Source = null;
        if (string.IsNullOrWhiteSpace(source)) return;
        var loader = App.Services?.GetService<ImageLoader>();
        if (loader is null) return;
        Bitmap? bitmap;
        try { bitmap = await loader.LoadAsync(source, DecodeWidth); }
        catch { bitmap = null; }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token != _loadToken || bitmap is null) return;
            _image.Source = bitmap;
            _image.Opacity = 1;
        });
    }
}
