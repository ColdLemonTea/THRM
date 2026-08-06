using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace THRM.Avalonia;

public sealed class TemperatureHistoryPreview : Control
{
    private const double MaxTemperature = 110;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 260;

    public static readonly StyledProperty<IReadOnlyList<TemperatureHistoryPointSnapshot>?> PointsProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, IReadOnlyList<TemperatureHistoryPointSnapshot>?>(nameof(Points));

    static TemperatureHistoryPreview() => AffectsRender<TemperatureHistoryPreview>(PointsProperty);

    public TemperatureHistoryPreview()
    {
        AutomationProperties.SetName(this, "Temperature history preview");
        AutomationProperties.SetHelpText(this, "Read-only CPU and GPU temperature history from 0 to 110 degrees Celsius.");
    }

    public IReadOnlyList<TemperatureHistoryPointSnapshot>? Points
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

        const double left = 44;
        const double top = 30;
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
        var cpu = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(76, 194, 255)) : new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            "AccentFillColorDefaultBrush", "SystemAccentColor");
        var gpu = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(255, 190, 92)) : new SolidColorBrush(Color.FromRgb(190, 100, 0)),
            "SystemFillColorCautionBrush", "AccentFillColorDefaultBrush", "SystemAccentColor");

        var gridPen = new Pen(grid, 1);
        for (var index = 0; index <= 2; index++)
        {
            var x = plot.Left + plot.Width * index / 2;
            var y = plot.Top + plot.Height * index / 2;
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        var axisPen = new Pen(secondary, 1);
        context.DrawLine(axisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        context.DrawLine(axisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        DrawText(context, secondary, "°C", new Point(8, 2));
        DrawText(context, secondary, "110", new Point(4, plot.Top - 6));
        DrawText(context, secondary, "55", new Point(10, plot.Top + plot.Height / 2 - 7));
        DrawText(context, secondary, "0", new Point(16, plot.Bottom - 7));

        DrawLegend(context, cpu, primary, "CPU", plot.Left, 8);
        DrawLegend(context, gpu, primary, "GPU", plot.Left + 62, 8);

        var samples = new List<TemperatureHistoryPointSnapshot>();
        if (Points is { Count: > 0 } points)
        {
            foreach (var point in points)
            {
                if (point is not null && point.Timestamp > 0)
                {
                    samples.Add(point);
                }
            }
        }

        samples.Sort(static (first, second) => first.Timestamp.CompareTo(second.Timestamp));
        var maxSamples = Math.Max(2, (int)Math.Min(int.MaxValue, Math.Floor(plot.Width * 2)));
        samples = Downsample(samples, maxSamples);
        var firstTimestamp = samples.Count > 0 ? samples[0].Timestamp : 0;
        var lastTimestamp = samples.Count > 0 ? samples[^1].Timestamp : 0;
        var timestampSpan = (double)lastTimestamp - firstTimestamp;

        DrawText(context, secondary, FormatTime(firstTimestamp), new Point(plot.Left - 2, plot.Bottom + 6));
        DrawText(context, secondary, FormatTime(samples.Count > 0 ? samples[samples.Count / 2].Timestamp : 0),
            new Point(plot.Left + plot.Width / 2 - 18, plot.Bottom + 6));
        DrawText(context, secondary, FormatTime(lastTimestamp), new Point(plot.Right - 36, plot.Bottom + 6));

        var cpuPen = new Pen(cpu, 2);
        var gpuPen = new Pen(gpu, 2);
        var markerPen = new Pen(primary, 1);
        using (context.PushClip(plot))
        {
            Point? previousCpu = null;
            Point? previousGpu = null;
            foreach (var sample in samples)
            {
                var x = X(sample.Timestamp, plot, firstTimestamp, timestampSpan);
                if (sample.CpuTemp > 0)
                {
                    var location = new Point(x, Y(sample.CpuTemp, plot));
                    if (previousCpu is { } previous)
                    {
                        context.DrawLine(cpuPen, previous, location);
                    }

                    context.DrawEllipse(cpu, markerPen, location, 3.5, 3.5);
                    previousCpu = location;
                }
                else
                {
                    previousCpu = null;
                }

                if (sample.GpuTemp > 0)
                {
                    var location = new Point(x, Y(sample.GpuTemp, plot));
                    if (previousGpu is { } previous)
                    {
                        context.DrawLine(gpuPen, previous, location);
                    }

                    context.DrawEllipse(gpu, markerPen, location, 3.5, 3.5);
                    previousGpu = location;
                }
                else
                {
                    previousGpu = null;
                }
            }
        }
    }

    private static double Desired(double fallback, double available) =>
        double.IsFinite(available) ? Math.Clamp(available, 0, fallback) : fallback;

    private static List<TemperatureHistoryPointSnapshot> Downsample(
        List<TemperatureHistoryPointSnapshot> points,
        int maxPoints)
    {
        if (maxPoints < 2 || points.Count <= maxPoints)
        {
            return points;
        }

        var stride = (int)Math.Ceiling((double)(points.Count - 1) / (maxPoints - 1));
        var sampled = new List<TemperatureHistoryPointSnapshot>();
        for (var index = 0; index < points.Count; index += stride)
        {
            sampled.Add(points[index]);
        }

        var last = points[^1];
        if (!ReferenceEquals(sampled[^1], last))
        {
            sampled.Add(last);
        }

        return sampled;
    }

    private static double X(long timestamp, Rect plot, long firstTimestamp, double timestampSpan) =>
        timestampSpan > 0 && double.IsFinite(timestampSpan)
            ? plot.Left + ((double)timestamp - firstTimestamp) / timestampSpan * plot.Width
            : plot.Left + plot.Width / 2;

    private static double Y(int temperature, Rect plot) =>
        plot.Bottom - Math.Clamp(temperature, 0, (int)MaxTemperature) / MaxTemperature * plot.Height;

    private static string FormatTime(long timestamp)
    {
        if (timestamp <= 0)
        {
            return "—";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "—";
        }
    }

    private static void DrawLegend(DrawingContext context, IBrush lineBrush, IBrush textBrush, string label, double x, double y)
    {
        context.DrawLine(new Pen(lineBrush, 2), new Point(x, y + 6), new Point(x + 18, y + 6));
        DrawText(context, textBrush, label, new Point(x + 23, y));
    }

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
