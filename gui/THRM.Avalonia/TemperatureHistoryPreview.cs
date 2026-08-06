using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace THRM.Avalonia;

public enum HistoryMetric
{
    Temperature,
    Power,
}

public sealed class HistoryHoverChangedEventArgs(long? timestamp, Point? pointerRatio) : EventArgs
{
    public long? Timestamp { get; } = timestamp;
    public Point? PointerRatio { get; } = pointerRatio;
}

public sealed class TemperatureHistoryPreview : Control
{
    private const double MaxTemperature = 110;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 260;
    private IReadOnlyList<TemperatureHistoryPointSnapshot>? _cachedSource;
    private int _cachedMaximumPoints = -1;
    private List<TemperatureHistoryPointSnapshot> _cachedSamples = [];

    public static readonly StyledProperty<IReadOnlyList<TemperatureHistoryPointSnapshot>?> PointsProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, IReadOnlyList<TemperatureHistoryPointSnapshot>?>(nameof(Points));

    public static readonly StyledProperty<HistoryMetric> MetricProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, HistoryMetric>(nameof(Metric));

    public static readonly StyledProperty<long?> HoverTimestampProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, long?>(nameof(HoverTimestamp));

    private static readonly StyledProperty<Point> TooltipAnchorProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, Point>(nameof(TooltipAnchor));

    static TemperatureHistoryPreview() =>
        AffectsRender<TemperatureHistoryPreview>(PointsProperty, MetricProperty, HoverTimestampProperty, TooltipAnchorProperty);

    public TemperatureHistoryPreview()
    {
        AutomationProperties.SetName(this, "History trend preview");
        AutomationProperties.SetHelpText(
            this,
            "Read-only CPU and GPU history. Hover anywhere in the chart to inspect the closest recorded time.");
        Transitions = new Transitions
        {
            new PointTransition
            {
                Property = TooltipAnchorProperty,
                Duration = TimeSpan.FromMilliseconds(400),
                Easing = new SplineEasing(0.25, 0.1, 0.25, 1),
            },
        };
        PointerEntered += OnPointerEntered;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    public event EventHandler<HistoryHoverChangedEventArgs>? HoverChanged;

    public IReadOnlyList<TemperatureHistoryPointSnapshot>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public HistoryMetric Metric
    {
        get => GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    public long? HoverTimestamp
    {
        get => GetValue(HoverTimestampProperty);
        set => SetValue(HoverTimestampProperty, value);
    }

    private Point TooltipAnchor
    {
        get => GetValue(TooltipAnchorProperty);
        set => SetValue(TooltipAnchorProperty, value);
    }

    public void SetLinkedHover(long? timestamp, Point? pointerRatio)
    {
        SetCurrentValue(HoverTimestampProperty, timestamp);
        if (timestamp is not null && pointerRatio is { } ratio && TryGetPlot(out var plot))
        {
            var samples = GetSamples(plot);
            if (FindNearest(samples, timestamp) is { } sample)
            {
                var firstTimestamp = samples[0].Timestamp;
                var timestampSpan = (double)samples[^1].Timestamp - firstTimestamp;
                SetCurrentValue(
                    TooltipAnchorProperty,
                    new Point(
                        X(sample.Timestamp, plot, firstTimestamp, timestampSpan),
                        plot.Top + Math.Clamp(ratio.Y, 0, 1) * plot.Height));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        Desired(DefaultWidth, availableSize.Width),
        Desired(DefaultHeight, availableSize.Height));

    public override void Render(DrawingContext context)
    {
        if (!TryGetPlot(out var plot))
        {
            return;
        }

        context.DrawRectangle(Brushes.Transparent, null, Bounds);

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
            Metric == HistoryMetric.Power
                ? dark ? new SolidColorBrush(Color.FromRgb(190, 150, 255)) : new SolidColorBrush(Color.FromRgb(112, 48, 180))
                : dark ? new SolidColorBrush(Color.FromRgb(76, 194, 255)) : new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            "AccentFillColorDefaultBrush", "SystemAccentColor");
        var gpu = FindBrush(
            Metric == HistoryMetric.Power
                ? dark ? new SolidColorBrush(Color.FromRgb(255, 143, 190)) : new SolidColorBrush(Color.FromRgb(196, 35, 111))
                : dark ? new SolidColorBrush(Color.FromRgb(255, 190, 92)) : new SolidColorBrush(Color.FromRgb(190, 100, 0)),
            "SystemFillColorCautionBrush", "AccentFillColorDefaultBrush", "SystemAccentColor");
        var surface = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(45, 45, 45)) : new SolidColorBrush(Colors.White),
            "CardBackgroundFillColorDefaultBrush", "SystemControlBackgroundBaseLowBrush");
        var border = FindBrush(
            dark ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            "CardStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush");

        var samples = GetSamples(plot);
        var maximum = Maximum(samples);
        var displayMaximum = maximum > 0 ? maximum : Metric == HistoryMetric.Power ? 20 : MaxTemperature;
        var firstTimestamp = samples.Count > 0 ? samples[0].Timestamp : 0;
        var lastTimestamp = samples.Count > 0 ? samples[^1].Timestamp : 0;
        var timestampSpan = (double)lastTimestamp - firstTimestamp;

        DrawGrid(context, plot, grid, secondary, displayMaximum);
        DrawHeader(context, plot, primary, secondary, cpu, gpu);
        DrawTimeAxis(context, plot, secondary, samples, firstTimestamp, lastTimestamp);

        if (samples.Count == 0)
        {
            DrawText(context, secondary, "No recorded samples.", new Point(plot.Left + 12, plot.Top + 14));
            return;
        }

        if (maximum <= 0)
        {
            DrawText(context, secondary, "No recorded power readings.", new Point(plot.Left + 12, plot.Top + 14));
            return;
        }

        using (context.PushClip(plot))
        {
            DrawSeries(context, samples, plot, firstTimestamp, timestampSpan, displayMaximum, cpu, gpu);

            if (FindNearest(samples, HoverTimestamp) is { } hovered)
            {
                var x = X(hovered.Timestamp, plot, firstTimestamp, timestampSpan);
                context.DrawLine(new Pen(secondary, 1, DashStyle.Dash), new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }

        if (FindNearest(samples, HoverTimestamp) is { } tooltipSample)
        {
            DrawTooltip(context, tooltipSample, TooltipAnchor, primary, secondary, surface, border, cpu, gpu);
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e) => UpdateHover(e.GetPosition(this));

    private void OnPointerMoved(object? sender, PointerEventArgs e) => UpdateHover(e.GetPosition(this));

    private void OnPointerExited(object? sender, PointerEventArgs e) => SetHoverTimestamp(null, null, notify: true);

    private void UpdateHover(Point pointer)
    {
        if (!TryGetPlot(out var plot) || !plot.Contains(pointer))
        {
            SetHoverTimestamp(null, null, notify: true);
            return;
        }

        var samples = GetSamples(plot);
        if (samples.Count == 0)
        {
            SetHoverTimestamp(null, null, notify: true);
            return;
        }

        var firstTimestamp = samples[0].Timestamp;
        var timestampSpan = (double)samples[^1].Timestamp - firstTimestamp;
        var closest = samples[0];
        var distance = double.MaxValue;
        foreach (var sample in samples)
        {
            var currentDistance = Math.Abs(X(sample.Timestamp, plot, firstTimestamp, timestampSpan) - pointer.X);
            if (currentDistance < distance)
            {
                closest = sample;
                distance = currentDistance;
            }
        }

        SetCurrentValue(
            TooltipAnchorProperty,
            new Point(X(closest.Timestamp, plot, firstTimestamp, timestampSpan), pointer.Y));
        SetHoverTimestamp(
            closest.Timestamp,
            new Point((pointer.X - plot.Left) / plot.Width, (pointer.Y - plot.Top) / plot.Height),
            notify: true);
    }

    private void SetHoverTimestamp(long? timestamp, Point? pointerRatio, bool notify)
    {
        if (HoverTimestamp != timestamp)
        {
            SetCurrentValue(HoverTimestampProperty, timestamp);
        }

        if (notify)
        {
            HoverChanged?.Invoke(this, new HistoryHoverChangedEventArgs(timestamp, pointerRatio));
        }
    }

    private bool TryGetPlot(out Rect plot)
    {
        const double left = 44;
        const double top = 30;
        const double right = 14;
        const double bottom = 34;
        plot = new Rect(left, top, Bounds.Width - left - right, Bounds.Height - top - bottom);
        return double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height)
            && Bounds.Width > 0 && Bounds.Height > 0 && plot.Width > 0 && plot.Height > 0;
    }

    private List<TemperatureHistoryPointSnapshot> GetSamples(Rect plot)
    {
        var maximumPoints = Math.Max(2, (int)Math.Min(int.MaxValue, Math.Floor(plot.Width * 2)));
        if (ReferenceEquals(_cachedSource, Points) && _cachedMaximumPoints == maximumPoints)
        {
            return _cachedSamples;
        }

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
        _cachedSource = Points;
        _cachedMaximumPoints = maximumPoints;
        _cachedSamples = Downsample(samples, maximumPoints);
        return _cachedSamples;
    }

    private void DrawGrid(DrawingContext context, Rect plot, IBrush grid, IBrush secondary, double maximum)
    {
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
        DrawText(context, secondary, FormatAxisValue(maximum), new Point(4, plot.Top - 6));
        DrawText(context, secondary, FormatAxisValue(maximum / 2), new Point(10, plot.Top + plot.Height / 2 - 7));
        DrawText(context, secondary, "0", new Point(16, plot.Bottom - 7));
    }

    private void DrawHeader(DrawingContext context, Rect plot, IBrush primary, IBrush secondary, IBrush cpu, IBrush gpu)
    {
        const double headerTextTop = 6;
        DrawText(context, secondary, Metric == HistoryMetric.Power ? "W" : "°C", new Point(8, headerTextTop - 1));
        DrawLegend(context, cpu, primary, Metric == HistoryMetric.Power ? "CPU power" : "CPU", plot.Left, headerTextTop);
        DrawLegend(context, gpu, primary, Metric == HistoryMetric.Power ? "GPU power" : "GPU", plot.Left + (Metric == HistoryMetric.Power ? 112 : 62), headerTextTop);
    }

    private static void DrawTimeAxis(
        DrawingContext context,
        Rect plot,
        IBrush secondary,
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        long firstTimestamp,
        long lastTimestamp)
    {
        DrawText(context, secondary, FormatTime(firstTimestamp), new Point(plot.Left - 2, plot.Bottom + 6));
        DrawText(context, secondary, FormatTime(samples.Count > 0 ? samples[samples.Count / 2].Timestamp : 0),
            new Point(plot.Left + plot.Width / 2 - 18, plot.Bottom + 6));
        DrawText(context, secondary, FormatTime(lastTimestamp), new Point(plot.Right - 36, plot.Bottom + 6));
    }

    private void DrawSeries(
        DrawingContext context,
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        Rect plot,
        long firstTimestamp,
        double timestampSpan,
        double maximum,
        IBrush cpu,
        IBrush gpu)
    {
        var cpuPen = new Pen(cpu, 2);
        var gpuPen = new Pen(gpu, 2);
        Point? previousCpu = null;
        Point? previousGpu = null;
        foreach (var sample in samples)
        {
            var x = X(sample.Timestamp, plot, firstTimestamp, timestampSpan);
            var cpuValue = CpuValue(sample);
            if (cpuValue > 0)
            {
                var location = new Point(x, Y(cpuValue, plot, maximum));
                if (previousCpu is { } previous)
                {
                    context.DrawLine(cpuPen, previous, location);
                }

                previousCpu = location;
            }
            else
            {
                previousCpu = null;
            }

            var gpuValue = GpuValue(sample);
            if (gpuValue > 0)
            {
                var location = new Point(x, Y(gpuValue, plot, maximum));
                if (previousGpu is { } previous)
                {
                    context.DrawLine(gpuPen, previous, location);
                }

                previousGpu = location;
            }
            else
            {
                previousGpu = null;
            }
        }
    }

    private void DrawTooltip(
        DrawingContext context,
        TemperatureHistoryPointSnapshot sample,
        Point pointer,
        IBrush primary,
        IBrush secondary,
        IBrush surface,
        IBrush border,
        IBrush cpu,
        IBrush gpu)
    {
        var rows = new List<(string Label, double Value, IBrush Brush)>();
        var cpuValue = CpuValue(sample);
        var gpuValue = GpuValue(sample);
        if (cpuValue > 0)
        {
            rows.Add((Metric == HistoryMetric.Power ? "CPU power" : "CPU", cpuValue, cpu));
        }

        if (gpuValue > 0)
        {
            rows.Add((Metric == HistoryMetric.Power ? "GPU power" : "GPU", gpuValue, gpu));
        }

        if (rows.Count == 0)
        {
            return;
        }

        const double width = 166;
        var height = 29 + rows.Count * 19;
        var x = pointer.X + 14 + width <= Bounds.Width ? pointer.X + 14 : pointer.X - width - 14;
        x = Math.Clamp(x, 4, Math.Max(4, Bounds.Width - width - 4));
        var y = pointer.Y + 14 + height <= Bounds.Height ? pointer.Y + 14 : pointer.Y - height - 14;
        y = Math.Clamp(y, 4, Math.Max(4, Bounds.Height - height - 4));
        var card = new Rect(x, y, width, height);
        context.DrawRectangle(surface, new Pen(border, 1), card, 4, 4);
        DrawText(context, primary, FormatDateTime(sample.Timestamp), new Point(card.Left + 10, card.Top + 8));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var rowY = card.Top + 30 + index * 19;
            context.DrawEllipse(row.Brush, null, new Point(card.Left + 14, rowY + 5), 3, 3);
            DrawText(context, secondary, $"{row.Label}  {FormatValue(row.Value)}", new Point(card.Left + 24, rowY));
        }
    }

    private double CpuValue(TemperatureHistoryPointSnapshot sample) =>
        Metric == HistoryMetric.Power ? sample.CpuPower : sample.CpuTemp;

    private double GpuValue(TemperatureHistoryPointSnapshot sample) =>
        Metric == HistoryMetric.Power ? sample.GpuPower : sample.GpuTemp;

    private double Maximum(IReadOnlyList<TemperatureHistoryPointSnapshot> samples)
    {
        if (Metric == HistoryMetric.Temperature)
        {
            return MaxTemperature;
        }

        var peak = 0d;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Max(sample.CpuPower, sample.GpuPower));
        }

        return peak > 0 ? Math.Max(20, Math.Ceiling((peak + 10) / 10) * 10) : 0;
    }

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

    private static TemperatureHistoryPointSnapshot? FindNearest(
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        long? timestamp)
    {
        if (timestamp is not { } target || samples.Count == 0)
        {
            return null;
        }

        var closest = samples[0];
        var distance = Math.Abs((double)closest.Timestamp - target);
        for (var index = 1; index < samples.Count; index++)
        {
            var current = samples[index];
            var currentDistance = Math.Abs((double)current.Timestamp - target);
            if (currentDistance < distance)
            {
                closest = current;
                distance = currentDistance;
            }
        }

        return closest;
    }

    private static double Desired(double fallback, double available) =>
        double.IsFinite(available) ? Math.Clamp(available, 0, fallback) : fallback;

    private static double X(long timestamp, Rect plot, long firstTimestamp, double timestampSpan) =>
        timestampSpan > 0 && double.IsFinite(timestampSpan)
            ? plot.Left + ((double)timestamp - firstTimestamp) / timestampSpan * plot.Width
            : plot.Left + plot.Width / 2;

    private static double Y(double value, Rect plot, double maximum) =>
        plot.Bottom - Math.Clamp(value, 0, maximum) / maximum * plot.Height;

    private string FormatAxisValue(double value) =>
        Metric == HistoryMetric.Power
            ? value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)
            : Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private string FormatValue(double value) =>
        Metric == HistoryMetric.Power
            ? $"{value.ToString("0.0", CultureInfo.InvariantCulture)} W"
            : $"{Math.Round(value).ToString(CultureInfo.InvariantCulture)} °C";

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

    private static string FormatDateTime(long timestamp)
    {
        if (timestamp <= 0)
        {
            return "—";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "—";
        }
    }

    private static void DrawLegend(DrawingContext context, IBrush lineBrush, IBrush textBrush, string label, double x, double y)
    {
        context.DrawLine(new Pen(lineBrush, 2), new Point(x, y + 6), new Point(x + 18, y + 6));
        DrawText(context, textBrush, label, new Point(x + 23, y - 1));
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

    internal static void SelfCheck()
    {
        var points = new[]
        {
            new TemperatureHistoryPointSnapshot { Timestamp = 100, CpuPower = 12.5, GpuPower = 24.5 },
            new TemperatureHistoryPointSnapshot { Timestamp = 200, CpuPower = 20, GpuPower = 30 },
        };
        var preview = new TemperatureHistoryPreview { Metric = HistoryMetric.Power };
        if (preview.Maximum(points) != 40 || FindNearest(points, 175)?.Timestamp != 200)
        {
            throw new InvalidOperationException("History chart scale and hover synchronization check failed.");
        }
    }
}
