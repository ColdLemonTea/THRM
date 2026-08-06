using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace THRM.Avalonia;

public sealed class FanCurvePreview : Control
{
    private const double MaxTemperature = 110;
    private const double MaxRpm = 4000;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 240;

    public static readonly StyledProperty<IReadOnlyList<FanCurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurvePreview, IReadOnlyList<FanCurvePoint>?>(nameof(Points));

    static FanCurvePreview() => AffectsRender<FanCurvePreview>(PointsProperty);

    public FanCurvePreview()
    {
        AutomationProperties.SetName(this, "Fan curve preview");
        AutomationProperties.SetHelpText(this, "Read-only fan speed curve from 0 to 110 degrees Celsius and 0 to 4000 RPM.");
    }

    public IReadOnlyList<FanCurvePoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        Desired(DefaultWidth, availableSize.Width),
        Desired(DefaultHeight, availableSize.Height));

    public override void Render(DrawingContext context)
    {
        if (!double.IsFinite(Bounds.Width) || !double.IsFinite(Bounds.Height)
            || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        const double left = 48;
        const double top = 22;
        const double right = 14;
        const double bottom = 34;
        var plot = new Rect(left, top, Bounds.Width - left - right, Bounds.Height - top - bottom);
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var primary = FindBrush(
            dark ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black),
            "TextFillColorPrimaryBrush");
        var secondary = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(210, 210, 210)) : new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            "TextFillColorSecondaryBrush");
        var grid = FindBrush(
            dark ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            "DividerStrokeColorDefaultBrush");
        var accent = FindBrush(
            new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            "AccentFillColorDefaultBrush", "SystemAccentColor");
        var gridPen = new Pen(grid, 1);

        for (var index = 0; index <= 2; index++)
        {
            var x = plot.Left + plot.Width * index / 2;
            var y = plot.Top + plot.Height * index / 2;
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        DrawText(context, secondary, "RPM", new Point(6, 2));
        DrawText(context, secondary, "4000", new Point(6, plot.Top - 6));
        DrawText(context, secondary, "2000", new Point(6, plot.Top + plot.Height / 2 - 7));
        DrawText(context, secondary, "0", new Point(6, plot.Bottom - 7));
        DrawText(context, secondary, "0", new Point(plot.Left - 2, plot.Bottom + 6));
        DrawText(context, secondary, "55", new Point(plot.Left + plot.Width / 2 - 8, plot.Bottom + 6));
        DrawText(context, secondary, "110", new Point(plot.Right - 20, plot.Bottom + 6));
        DrawText(context, secondary, "Temperature (°C)", new Point(plot.Left + plot.Width / 2 - 48, Bounds.Height - 16));

        var linePen = new Pen(accent, 2);
        var markerPen = new Pen(primary, 1);
        using (context.PushClip(plot))
        {
            Point? previous = null;
            if (Points is { Count: > 0 } points)
            {
                foreach (var point in points)
                {
                    var temperature = Math.Clamp(point.Temperature, 0, (int)MaxTemperature);
                    var rpm = Math.Clamp(point.Rpm, 0, (int)MaxRpm);
                    var location = new Point(
                        plot.Left + temperature / MaxTemperature * plot.Width,
                        plot.Bottom - rpm / MaxRpm * plot.Height);

                    if (previous is { } previousLocation)
                    {
                        context.DrawLine(linePen, previousLocation, location);
                    }

                    context.DrawEllipse(accent, markerPen, location, 4, 4);
                    previous = location;
                }
            }
        }
    }

    private static double Desired(double fallback, double available) =>
        double.IsFinite(available) ? Math.Clamp(available, 0, fallback) : fallback;

    private IBrush FindBrush(IBrush fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (this.TryFindResource(key, out var value))
            {
                if (value is IBrush brush)
                {
                    return brush;
                }

                if (value is Color color)
                {
                    return new SolidColorBrush(color);
                }
            }
        }

        return fallback;
    }

    private static void DrawText(DrawingContext context, IBrush brush, string text, Point origin)
    {
        context.DrawText(
            new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                11,
                brush),
            origin);
    }
}
