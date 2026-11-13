using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Musilib.Desktop.Controls;

/// <summary>Jauge horizontale compacte (scores de correspondance), sans largeur minimale imposée.</summary>
public sealed class MeterBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Track));

    static MeterBar()
    {
        AffectsRender<MeterBar>(ValueProperty, MaximumProperty, FillProperty, TrackProperty);
    }

    public MeterBar()
    {
        Height = 4;
        Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition { Property = ValueProperty, Duration = TimeSpan.FromMilliseconds(300) }
        };
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 60 : 0, double.IsNaN(Height) ? 4 : Height);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var radius = h / 2;
        if (Track is { } track) context.DrawRectangle(track, null, new Rect(0, 0, w, h), radius, radius);
        var ratio = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        if (ratio > 0 && Fill is { } fill)
            context.DrawRectangle(fill, null, new Rect(0, 0, Math.Max(h, w * ratio), h), radius, radius);
    }
}
