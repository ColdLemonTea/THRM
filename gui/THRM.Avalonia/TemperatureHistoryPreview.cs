using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
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

internal static class ChartTooltipMotion
{
    private const double Gap = 14;
    private const double Margin = 4;

    public static Point CardTarget(Point anchor, Size bounds, Size card)
    {
        var right = anchor.X + Gap;
        var left = anchor.X - card.Width - Gap;
        var x = right + card.Width <= bounds.Width ? right : left;
        var below = anchor.Y + Gap;
        var above = anchor.Y - card.Height - Gap;
        var y = below + card.Height <= bounds.Height ? below : above;
        return new Point(
            Math.Clamp(x, Margin, Math.Max(Margin, bounds.Width - card.Width - Margin)),
            Math.Clamp(y, Margin, Math.Max(Margin, bounds.Height - card.Height - Margin)));
    }

    public static void SelfCheck()
    {
        var target = CardTarget(new Point(290, 190), new Size(300, 200), new Size(100, 50));
        if (target != new Point(176, 126))
        {
            throw new InvalidOperationException("Chart tooltip target check failed.");
        }
    }
}

internal sealed class ChartTooltipRow
{
    public ChartTooltipRow(Grid root, Border? dot, TextBlock label, TextBlock value)
    {
        Root = root;
        Dot = dot;
        Label = label;
        Value = value;
    }

    public Grid Root { get; }
    public Border? Dot { get; }
    public TextBlock Label { get; }
    public TextBlock Value { get; }
    public bool IsVisible
    {
        get => Root.IsVisible;
        set => Root.IsVisible = value;
    }
}

internal static class ChartTooltipOverlay
{
    public const double RowsBottomPadding = 8;

    private static readonly BlurEffect LocalBlur = new()
    {
        Radius = 12,
    };
    private static readonly BlurEffect LightLocalBlur = new()
    {
        Radius = 14,
    };

    private static readonly IBrush LightBlurTint = new SolidColorBrush(
        Color.FromArgb(128, 255, 255, 255));
    private static readonly IBrush DarkBlurTint = new SolidColorBrush(
        Color.FromArgb(120, 20, 30, 46));

    private static readonly ExperimentalAcrylicMaterial LightMaterial = new()
    {
        BackgroundSource = AcrylicBackgroundSource.None,
        TintColor = Colors.White,
        TintOpacity = 0.36,
        MaterialOpacity = 0.72,
        FallbackColor = Color.FromRgb(250, 250, 250),
    };

    private static readonly ExperimentalAcrylicMaterial DarkMaterial = new()
    {
        BackgroundSource = AcrylicBackgroundSource.None,
        TintColor = Color.FromRgb(32, 32, 32),
        TintOpacity = 0.56,
        MaterialOpacity = 0.72,
        FallbackColor = Color.FromRgb(32, 32, 32),
    };

    public static bool IsValidBlurRegion(Rect region, Size bounds) =>
        double.IsFinite(region.X) && double.IsFinite(region.Y)
        && double.IsFinite(region.Width) && double.IsFinite(region.Height)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
        && region.Width > 0 && region.Height > 0
        && bounds.Width > 0 && bounds.Height > 0
        && region.Right > 0 && region.Bottom > 0
        && region.Left < bounds.Width && region.Top < bounds.Height;

    public static void DrawLocalBlur(
        DrawingContext context,
        Rect region,
        Size bounds,
        bool dark,
        Action<DrawingContext> drawScene)
    {
        if (!IsValidBlurRegion(region, bounds))
        {
            return;
        }

        var roundedRegion = new RoundedRect(region, 4);
        using (context.PushClip(roundedRegion))
        {
            // Source-over tint first weakens the crisp scene; the public effect then adds
            // only the blurred local scene under the visual-tree acrylic border.
            context.DrawRectangle(dark ? DarkBlurTint : LightBlurTint, null, region);
            try
            {
                using (context.PushEffect(dark ? LocalBlur : LightLocalBlur, region))
                using (context.PushOpacity(0.90))
                {
                    drawScene(context);
                }
            }
            catch (NotSupportedException)
            {
                // Unsupported drawing backends retain the tint and acrylic fallback.
            }
        }
    }

    public static void SelfCheck()
    {
        if (!IsValidBlurRegion(new Rect(4, 4, 100, 50), new Size(120, 80))
            || IsValidBlurRegion(new Rect(double.NaN, 4, 100, 50), new Size(120, 80))
            || IsValidBlurRegion(new Rect(200, 200, 10, 10), new Size(120, 80)))
        {
            throw new InvalidOperationException("Chart tooltip blur region check failed.");
        }

        if (LightBlurTint is not SolidColorBrush lightBlur
            || lightBlur.Color != Color.FromArgb(128, 255, 255, 255)
            || LightMaterial.TintColor != Colors.White
            || Math.Abs(LightMaterial.TintOpacity - 0.36) > 0.0001
            || Math.Abs(LightMaterial.MaterialOpacity - 0.72) > 0.0001
            || LightMaterial.FallbackColor != Color.FromRgb(250, 250, 250)
            || DarkMaterial.TintColor != Color.FromRgb(32, 32, 32)
            || Math.Abs(DarkMaterial.TintOpacity - 0.56) > 0.0001
            || Math.Abs(DarkMaterial.MaterialOpacity - 0.72) > 0.0001
            || DarkMaterial.FallbackColor != Color.FromRgb(32, 32, 32)
            || LocalBlur.Radius != 12
            || LightLocalBlur.Radius != 14)
        {
            throw new InvalidOperationException("Chart tooltip light material check failed.");
        }

        var dotted = CreateRow(withDot: true);
        var plain = CreateRow(withDot: false);
        var rowsHost = new Grid();
        var rowsPanel = CreateRows(rowsHost);
        var dottedDot = dotted.Dot;
        if (VisibleRowCount(false, true, false) != 1
            || dotted.Root.ColumnDefinitions.Count != 3
            || plain.Root.ColumnDefinitions.Count != 2
            || dotted.Root.ColumnDefinitions[0].Width != new GridLength(8, GridUnitType.Pixel)
            || dotted.Root.ColumnSpacing != 7
            || dottedDot is null
            || dottedDot.Width != 8
            || dottedDot.Height != 8
            || dottedDot.CornerRadius != new CornerRadius(4)
            || rowsPanel.Margin != new Thickness(0, 0, 0, RowsBottomPadding)
            || dotted.Root.VerticalAlignment != VerticalAlignment.Center
            || dottedDot.VerticalAlignment != VerticalAlignment.Center
            || dotted.Label.VerticalAlignment != VerticalAlignment.Center
            || dotted.Value.VerticalAlignment != VerticalAlignment.Center)
        {
            throw new InvalidOperationException("Chart tooltip layout check failed.");
        }
    }

    public static Border Create(out ExperimentalAcrylicBorder acrylic, out Grid content)
    {
        content = new Grid
        {
            IsHitTestVisible = false,
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 0,
            VerticalAlignment = VerticalAlignment.Top,
        };
        acrylic = new ExperimentalAcrylicBorder
        {
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
            Child = content,
        };
        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = acrylic,
        };
    }

    public static StackPanel CreateRows(Grid content)
    {
        var rows = new StackPanel
        {
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 0, RowsBottomPadding),
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(rows, 1);
        content.Children.Add(rows);
        return rows;
    }

    public static void AddTitle(Grid content, TextBlock title)
    {
        title.Margin = new Thickness(12, 8, 12, 8);
        title.FontWeight = FontWeight.SemiBold;
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(title, 0);
        content.Children.Add(title);
    }

    public static TextBlock CreateText(double fontSize) => new()
    {
        FontSize = fontSize,
        IsHitTestVisible = false,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border CreateDot() => new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static ChartTooltipRow CreateRow(bool withDot, double fontSize = 11)
    {
        var root = new Grid
        {
            IsHitTestVisible = false,
            Margin = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ColumnSpacing = 7,
        };
        if (withDot)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition(8, GridUnitType.Pixel));
        }

        root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var dot = withDot ? CreateDot() : null;
        var label = CreateText(fontSize);
        var value = CreateText(fontSize);
        label.TextAlignment = TextAlignment.Left;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.TextAlignment = TextAlignment.Right;
        if (dot is not null)
        {
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(label, 1);
            Grid.SetColumn(value, 2);
            root.Children.Add(dot);
        }
        else
        {
            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
        }

        root.Children.Add(label);
        root.Children.Add(value);
        return new ChartTooltipRow(root, dot, label, value);
    }

    public static int VisibleRowCount(params bool[] visible)
    {
        var count = 0;
        foreach (var item in visible)
        {
            if (item)
            {
                count++;
            }
        }

        return count;
    }

    public static void Configure(
        Border card,
        ExperimentalAcrylicBorder acrylic,
        Grid content,
        Size size,
        IBrush border,
        bool dark)
    {
        card.Width = size.Width;
        card.Height = size.Height;
        card.BorderBrush = border;
        acrylic.Width = size.Width - 2;
        acrylic.Height = size.Height - 2;
        acrylic.Material = dark ? DarkMaterial : LightMaterial;
        content.Width = size.Width - 2;
        content.Height = size.Height - 2;
        card.IsVisible = true;
    }

    public static void SetPosition(Border card, Point position)
    {
        Canvas.SetLeft(card, position.X);
        Canvas.SetTop(card, position.Y);
    }
}

public sealed class TemperatureHistoryPreview : Decorator
{
    public const int DefaultDeviceFanMaximumRpm = FanRatedRpm.FallbackRpm;
    private const double TemperatureLowerBound = 30;
    private const double TemperatureFallbackUpperBound = 90;
    private const double TemperatureUpperCap = 110;
    private const double FanAxisRightGutter = 52;
    private const int DefaultHistoryWindowHours = 1;
    private const long MinimumHistoryBucketMilliseconds = 5000;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 260;
    private IReadOnlyList<TemperatureHistoryPointSnapshot>? _cachedSource;
    private int _cachedMaximumPoints = -1;
    private int _cachedWindowHours = -1;
    private List<TemperatureHistoryPointSnapshot> _cachedAllSamples = [];
    private List<TemperatureHistoryPointSnapshot> _cachedSamples = [];
    private bool _hasTooltipPosition;
    private Point _tooltipAnchor;
    private readonly Canvas _overlayCanvas;
    private readonly Border _tooltip;
    private readonly ExperimentalAcrylicBorder _tooltipAcrylic;
    private readonly Grid _tooltipContent;
    private readonly StackPanel _tooltipRows;
    private readonly TextBlock _tooltipTitle;
    private readonly ChartTooltipRow _tooltipCpuRow;
    private readonly ChartTooltipRow _tooltipGpuRow;
    private readonly ChartTooltipRow _tooltipFanRow;

    public static readonly StyledProperty<IReadOnlyList<TemperatureHistoryPointSnapshot>?> PointsProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, IReadOnlyList<TemperatureHistoryPointSnapshot>?>(nameof(Points));

    public static readonly StyledProperty<HistoryMetric> MetricProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, HistoryMetric>(nameof(Metric));

    public static readonly StyledProperty<int> DeviceFanMaximumRpmProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, int>(nameof(DeviceFanMaximumRpm), DefaultDeviceFanMaximumRpm);

    public static readonly StyledProperty<int> HistoryWindowHoursProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, int>(nameof(HistoryWindowHours), DefaultHistoryWindowHours);

    public static readonly StyledProperty<long?> HoverTimestampProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, long?>(nameof(HoverTimestamp));

    private static readonly StyledProperty<Point> TooltipPositionProperty =
        AvaloniaProperty.Register<TemperatureHistoryPreview, Point>(nameof(TooltipPosition));

    static TemperatureHistoryPreview() =>
        AffectsRender<TemperatureHistoryPreview>(
            PointsProperty,
            MetricProperty,
            DeviceFanMaximumRpmProperty,
            HistoryWindowHoursProperty,
            HoverTimestampProperty,
            TooltipPositionProperty);

    public TemperatureHistoryPreview()
    {
        AutomationProperties.SetName(this, "History trend preview");
        AutomationProperties.SetHelpText(
            this,
            "Read-only CPU, GPU, and THRM device fan history. Hover anywhere in the chart to inspect the closest recorded time.");
        PointerEntered += OnPointerEntered;
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;

        _tooltip = ChartTooltipOverlay.Create(out _tooltipAcrylic, out _tooltipContent);
        _tooltipRows = ChartTooltipOverlay.CreateRows(_tooltipContent);
        _tooltipTitle = ChartTooltipOverlay.CreateText(11);
        _tooltipCpuRow = ChartTooltipOverlay.CreateRow(withDot: true);
        _tooltipGpuRow = ChartTooltipOverlay.CreateRow(withDot: true);
        _tooltipFanRow = ChartTooltipOverlay.CreateRow(withDot: true);
        ChartTooltipOverlay.AddTitle(_tooltipContent, _tooltipTitle);
        _tooltipRows.Children.Add(_tooltipCpuRow.Root);
        _tooltipRows.Children.Add(_tooltipGpuRow.Root);
        _tooltipRows.Children.Add(_tooltipFanRow.Root);
        _overlayCanvas = new Canvas
        {
            IsHitTestVisible = false,
        };
        _overlayCanvas.Children.Add(_tooltip);
        Child = _overlayCanvas;
        ActualThemeVariantChanged += (_, _) =>
        {
            InvalidateVisual();
            RefreshTooltip();
        };
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

    public int DeviceFanMaximumRpm
    {
        get => GetValue(DeviceFanMaximumRpmProperty);
        set => SetValue(DeviceFanMaximumRpmProperty, value);
    }

    public int HistoryWindowHours
    {
        get => GetValue(HistoryWindowHoursProperty);
        set => SetValue(HistoryWindowHoursProperty, value);
    }

    public long? HoverTimestamp
    {
        get => GetValue(HoverTimestampProperty);
        set => SetValue(HoverTimestampProperty, value);
    }

    private Point TooltipPosition
    {
        get => GetValue(TooltipPositionProperty);
        set => SetValue(TooltipPositionProperty, value);
    }

    public void SetLinkedHover(long? timestamp, Point? pointerRatio)
    {
        if (timestamp is null)
        {
            ClearHover(notify: false);
            return;
        }

        SetCurrentValue(HoverTimestampProperty, timestamp);
        if (pointerRatio is { } ratio && TryGetPlot(out var plot))
        {
            var samples = GetSamples(plot);
            if (FindNearest(samples, timestamp) is { } sample)
            {
                var (windowStart, windowEnd) = GetTimeDomain(samples);
                var timestampSpan = (double)windowEnd - windowStart;
                UpdateTooltipOverlay(sample);
                SetTooltipTarget(
                    new Point(
                        X(sample.Timestamp, plot, windowStart, timestampSpan),
                        plot.Top + Math.Clamp(ratio.Y, 0, 1) * plot.Height),
                    TooltipSize(sample));
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            Desired(DefaultWidth, availableSize.Width),
            Desired(DefaultHeight, availableSize.Height));
        _overlayCanvas.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _overlayCanvas.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TooltipPositionProperty)
        {
            ChartTooltipOverlay.SetPosition(_tooltip, TooltipPosition);
        }
        else if (change.Property == PointsProperty
            || change.Property == MetricProperty
            || change.Property == DeviceFanMaximumRpmProperty
            || change.Property == HistoryWindowHoursProperty)
        {
            RefreshTooltip();
        }
    }

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
        var fan = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(80, 200, 120)) : new SolidColorBrush(Color.FromRgb(16, 124, 16)),
            "SystemFillColorSuccessBrush");
        var samples = GetSamples(plot);
        var scaleSamples = Metric == HistoryMetric.Temperature ? _cachedAllSamples : samples;
        var minimum = Metric == HistoryMetric.Temperature ? TemperatureLowerBound : 0;
        var maximum = Maximum(scaleSamples);
        var displayMaximum = maximum > 0 ? maximum : Metric == HistoryMetric.Power ? 20 : TemperatureFallbackUpperBound;
        var fanMaximum = Metric == HistoryMetric.Temperature ? FanMaximum(scaleSamples) : 0;
        var (firstTimestamp, lastTimestamp) = GetTimeDomain(samples);
        var timestampSpan = (double)lastTimestamp - firstTimestamp;

        DrawChartScene(
            context,
            plot,
            primary,
            secondary,
            grid,
            cpu,
            gpu,
            fan,
            samples,
            maximum,
            minimum,
            displayMaximum,
            fanMaximum,
            firstTimestamp,
            lastTimestamp,
            timestampSpan);
        DrawTooltipBlur(
            context,
            plot,
            primary,
            secondary,
            grid,
            cpu,
            gpu,
            fan,
            samples,
            maximum,
            minimum,
            displayMaximum,
            fanMaximum,
            firstTimestamp,
            lastTimestamp,
            timestampSpan,
            dark);
    }

    private void DrawTooltipBlur(
        DrawingContext context,
        Rect plot,
        IBrush primary,
        IBrush secondary,
        IBrush grid,
        IBrush cpu,
        IBrush gpu,
        IBrush fan,
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        double maximum,
        double minimum,
        double displayMaximum,
        double fanMaximum,
        long firstTimestamp,
        long lastTimestamp,
        double timestampSpan,
        bool dark)
    {
        if (!_hasTooltipPosition || !_tooltip.IsVisible
            || !double.IsFinite(_tooltip.Width) || !double.IsFinite(_tooltip.Height))
        {
            return;
        }

        var region = new Rect(TooltipPosition, new Size(_tooltip.Width, _tooltip.Height));
        ChartTooltipOverlay.DrawLocalBlur(
            context,
            region,
            Bounds.Size,
            dark,
            blurred => DrawChartScene(
                blurred,
                plot,
                primary,
                secondary,
                grid,
                cpu,
                gpu,
                fan,
                samples,
                maximum,
                minimum,
                displayMaximum,
                fanMaximum,
                firstTimestamp,
                lastTimestamp,
                timestampSpan));
    }

    private void DrawChartScene(
        DrawingContext context,
        Rect plot,
        IBrush primary,
        IBrush secondary,
        IBrush grid,
        IBrush cpu,
        IBrush gpu,
        IBrush fan,
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        double maximum,
        double minimum,
        double displayMaximum,
        double fanMaximum,
        long firstTimestamp,
        long lastTimestamp,
        double timestampSpan)
    {
        DrawGrid(context, plot, grid, secondary, minimum, displayMaximum, fanMaximum);
        DrawHeader(context, plot, primary, secondary, cpu, gpu, fan, fanMaximum);
        DrawTimeAxis(context, plot, secondary, firstTimestamp, lastTimestamp);

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
            DrawSeries(context, samples, plot, firstTimestamp, timestampSpan, minimum, displayMaximum, fanMaximum, cpu, gpu, fan);

            if (FindNearest(samples, HoverTimestamp) is { } hovered)
            {
                var x = X(hovered.Timestamp, plot, firstTimestamp, timestampSpan);
                context.DrawLine(new Pen(secondary, 1, DashStyle.Dash), new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e) => UpdateHover(e.GetPosition(this));

    private void OnPointerMoved(object? sender, PointerEventArgs e) => UpdateHover(e.GetPosition(this));

    private void OnPointerExited(object? sender, PointerEventArgs e) => ClearHover();

    private void UpdateHover(Point pointer)
    {
        if (!TryGetPlot(out var plot) || !plot.Contains(pointer))
        {
            ClearHover();
            return;
        }

        var samples = GetSamples(plot);
        if (samples.Count == 0)
        {
            ClearHover();
            return;
        }

        var (windowStart, windowEnd) = GetTimeDomain(samples);
        var timestampSpan = (double)windowEnd - windowStart;
        var closest = samples[0];
        var distance = double.MaxValue;
        foreach (var sample in samples)
        {
            var currentDistance = Math.Abs(X(sample.Timestamp, plot, windowStart, timestampSpan) - pointer.X);
            if (currentDistance < distance)
            {
                closest = sample;
                distance = currentDistance;
            }
        }

        UpdateTooltipOverlay(closest);
        SetTooltipTarget(
            new Point(X(closest.Timestamp, plot, windowStart, timestampSpan), pointer.Y),
            TooltipSize(closest));
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

    private void SetTooltipTarget(Point anchor, Size card)
    {
        _tooltipAnchor = anchor;
        var target = ChartTooltipMotion.CardTarget(anchor, Bounds.Size, card);
        if (!_hasTooltipPosition)
        {
            _hasTooltipPosition = true;
            Transitions = null;
            SetValue(TooltipPositionProperty, target);
            ChartTooltipOverlay.SetPosition(_tooltip, target);
            Transitions = CreateTooltipTransitions();
            return;
        }

        SetValue(TooltipPositionProperty, target);
    }

    private void ClearHover(bool notify = true)
    {
        _hasTooltipPosition = false;
        _tooltip.IsVisible = false;
        SetHoverTimestamp(null, null, notify);
    }

    private void RefreshTooltip()
    {
        if (!_hasTooltipPosition || HoverTimestamp is not { } timestamp || !TryGetPlot(out var plot))
        {
            return;
        }

        if (FindNearest(GetSamples(plot), timestamp) is not { } sample)
        {
            ClearHover(notify: false);
            return;
        }

        UpdateTooltipOverlay(sample);
        SetTooltipTarget(_tooltipAnchor, TooltipSize(sample));
    }

    private void UpdateTooltipOverlay(TemperatureHistoryPointSnapshot sample)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var primary = FindBrush(
            dark ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black),
            "TextFillColorPrimaryBrush");
        var secondary = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(210, 210, 210)) : new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            "TextFillColorSecondaryBrush");
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
        var fan = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(80, 200, 120)) : new SolidColorBrush(Color.FromRgb(16, 124, 16)),
            "SystemFillColorSuccessBrush");
        var border = FindBrush(
            dark ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            "CardStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush");
        var cpuValue = CpuValue(sample);
        var gpuValue = GpuValue(sample);
        var fanValue = FanValue(sample);
        var cpuVisible = cpuValue > 0;
        var gpuVisible = gpuValue > 0;
        var fanVisible = fanValue > 0;

        _tooltipTitle.Text = FormatDateTime(sample.Timestamp);
        _tooltipTitle.Foreground = primary;
        _tooltipCpuRow.Label.Text = Metric == HistoryMetric.Power ? "CPU power" : "CPU";
        _tooltipCpuRow.Label.Foreground = secondary;
        _tooltipCpuRow.Value.Text = FormatValue(cpuValue);
        _tooltipCpuRow.Value.Foreground = secondary;
        _tooltipCpuRow.IsVisible = cpuVisible;
        _tooltipCpuRow.Dot!.Background = cpu;

        _tooltipGpuRow.Label.Text = Metric == HistoryMetric.Power ? "GPU power" : "GPU";
        _tooltipGpuRow.Label.Foreground = secondary;
        _tooltipGpuRow.Value.Text = FormatValue(gpuValue);
        _tooltipGpuRow.Value.Foreground = secondary;
        _tooltipGpuRow.IsVisible = gpuVisible;
        _tooltipGpuRow.Dot!.Background = gpu;

        _tooltipFanRow.Label.Text = "Device fan";
        _tooltipFanRow.Label.Foreground = secondary;
        _tooltipFanRow.Value.Text = $"{fanValue.ToString(CultureInfo.InvariantCulture)} RPM";
        _tooltipFanRow.Value.Foreground = secondary;
        _tooltipFanRow.IsVisible = fanVisible;
        _tooltipFanRow.Dot!.Background = fan;

        var size = TooltipSize(sample);
        ChartTooltipOverlay.Configure(_tooltip, _tooltipAcrylic, _tooltipContent, size, border, dark);
        _tooltip.IsVisible = cpuVisible || gpuVisible || fanVisible;
    }

    private static Transitions CreateTooltipTransitions() =>
        new()
        {
            new PointTransition
            {
                Property = TooltipPositionProperty,
                Duration = TimeSpan.FromMilliseconds(100),
                Easing = new CubicEaseOut(),
            },
        };

    private bool TryGetPlot(out Rect plot)
    {
        const double left = 44;
        const double top = 30;
        var right = FanAxisRightGutter;
        const double bottom = 34;
        plot = new Rect(left, top, Bounds.Width - left - right, Bounds.Height - top - bottom);
        return double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height)
            && Bounds.Width > 0 && Bounds.Height > 0 && plot.Width > 0 && plot.Height > 0;
    }

    private List<TemperatureHistoryPointSnapshot> GetSamples(Rect plot)
    {
        var maximumPoints = Math.Max(2, (int)Math.Min(int.MaxValue, Math.Floor(plot.Width * 2)));
        var windowHours = Math.Clamp(HistoryWindowHours, 1, 24);
        if (ReferenceEquals(_cachedSource, Points)
            && _cachedMaximumPoints == maximumPoints
            && _cachedWindowHours == windowHours)
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
        _cachedWindowHours = windowHours;
        _cachedAllSamples = samples;
        var (windowStart, windowEnd) = GetTimeDomain(samples, windowHours);
        _cachedSamples = Bucketize(samples, windowStart, windowEnd, maximumPoints);
        return _cachedSamples;
    }

    private (long Start, long End) GetTimeDomain(IReadOnlyList<TemperatureHistoryPointSnapshot> samples) =>
        GetTimeDomain(samples, Math.Clamp(HistoryWindowHours, 1, 24));

    private static (long Start, long End) GetTimeDomain(
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        int windowHours)
    {
        if (samples.Count == 0)
        {
            return (0, 0);
        }

        var end = samples[^1].Timestamp;
        var span = TimeSpan.FromHours(Math.Clamp(windowHours, 1, 24)).TotalMilliseconds;
        var start = Math.Max(1, end - (long)span);
        return (start, end);
    }

    private void DrawGrid(
        DrawingContext context,
        Rect plot,
        IBrush grid,
        IBrush secondary,
        double minimum,
        double maximum,
        double fanMaximum)
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
        var axisLabelRight = AxisLabelRightEdge(plot);
        DrawRightAlignedText(context, secondary, FormatAxisValue(maximum), axisLabelRight, plot.Top - 6);
        DrawRightAlignedText(
            context,
            secondary,
            FormatAxisValue(minimum + (maximum - minimum) / 2),
            axisLabelRight,
            plot.Top + plot.Height / 2 - 7);
        DrawRightAlignedText(context, secondary, FormatAxisValue(minimum), axisLabelRight, plot.Bottom - 7);

        if (Metric == HistoryMetric.Temperature && fanMaximum > 0)
        {
            DrawFanAxis(context, plot, secondary, fanMaximum);
        }
    }

    private void DrawFanAxis(DrawingContext context, Rect plot, IBrush secondary, double maximum)
    {
        var axisPen = new Pen(secondary, 1);
        context.DrawLine(axisPen, new Point(plot.Right, plot.Top), new Point(plot.Right, plot.Bottom));
        var labelRight = FanAxisLabelRightEdge(plot);
        DrawRightAlignedText(context, secondary, FormatFanAxisValue(maximum), labelRight, plot.Top - 6);
        DrawRightAlignedText(context, secondary, FormatFanAxisValue(maximum / 2), labelRight, plot.Top + plot.Height / 2 - 7);
        DrawRightAlignedText(context, secondary, "0", labelRight, plot.Bottom - 7);
        for (var index = 0; index <= 2; index++)
        {
            var y = plot.Top + plot.Height * index / 2;
            context.DrawLine(axisPen, new Point(plot.Right, y), new Point(plot.Right + 4, y));
        }
    }

    private void DrawHeader(
        DrawingContext context,
        Rect plot,
        IBrush primary,
        IBrush secondary,
        IBrush cpu,
        IBrush gpu,
        IBrush fan,
        double fanMaximum)
    {
        const double headerTextTop = 6;
        DrawRightAlignedText(context, secondary, Metric == HistoryMetric.Power ? "W" : "°C", AxisLabelRightEdge(plot), headerTextTop - 1);
        DrawLegend(context, cpu, primary, Metric == HistoryMetric.Power ? "CPU power" : "CPU", plot.Left, headerTextTop);
        DrawLegend(context, gpu, primary, Metric == HistoryMetric.Power ? "GPU power" : "GPU", plot.Left + (Metric == HistoryMetric.Power ? 112 : 62), headerTextTop);
        if (Metric == HistoryMetric.Temperature && fanMaximum > 0)
        {
            DrawRightAlignedText(context, secondary, "RPM", FanAxisLabelRightEdge(plot), headerTextTop - 1);
            DrawLegend(context, fan, primary, "Device fan", plot.Left + 124, headerTextTop);
        }
    }

    private static void DrawTimeAxis(
        DrawingContext context,
        Rect plot,
        IBrush secondary,
        long firstTimestamp,
        long lastTimestamp)
    {
        DrawText(context, secondary, FormatTime(firstTimestamp), new Point(plot.Left - 2, plot.Bottom + 6));
        var midpoint = firstTimestamp > 0 && lastTimestamp >= firstTimestamp
            ? firstTimestamp + (lastTimestamp - firstTimestamp) / 2
            : 0;
        DrawText(context, secondary, FormatTime(midpoint),
            new Point(plot.Left + plot.Width / 2 - 18, plot.Bottom + 6));
        DrawText(context, secondary, FormatTime(lastTimestamp), new Point(plot.Right - 36, plot.Bottom + 6));
    }

    private void DrawSeries(
        DrawingContext context,
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples,
        Rect plot,
        long firstTimestamp,
        double timestampSpan,
        double minimum,
        double maximum,
        double fanMaximum,
        IBrush cpu,
        IBrush gpu,
        IBrush fan)
    {
        var cpuPen = new Pen(cpu, 2);
        var gpuPen = new Pen(gpu, 2);
        var fanPen = new Pen(fan, 2);
        Point? previousCpu = null;
        Point? previousGpu = null;
        Point? previousFan = null;
        foreach (var sample in samples)
        {
            var x = X(sample.Timestamp, plot, firstTimestamp, timestampSpan);
            var cpuValue = CpuValue(sample);
            if (cpuValue > 0)
            {
                var location = new Point(x, Y(cpuValue, plot, minimum, maximum));
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
                var location = new Point(x, Y(gpuValue, plot, minimum, maximum));
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

            if (Metric == HistoryMetric.Temperature && fanMaximum > 0 && sample.FanRpm > 0)
            {
                var location = new Point(x, Y(sample.FanRpm, plot, 0, fanMaximum));
                if (previousFan is { } previous)
                {
                    context.DrawLine(fanPen, previous, location);
                }

                previousFan = location;
            }
            else
            {
                previousFan = null;
            }
        }
    }

    private Size TooltipSize(TemperatureHistoryPointSnapshot sample)
    {
        var rows = TooltipRowCount(CpuValue(sample) > 0, GpuValue(sample) > 0, FanValue(sample) > 0);
        return new Size(166, 32 + rows * 16 + Math.Max(0, rows - 1) * 8 + ChartTooltipOverlay.RowsBottomPadding);
    }

    private static int TooltipRowCount(bool cpuVisible, bool gpuVisible, bool fanVisible) =>
        (cpuVisible ? 1 : 0) + (gpuVisible ? 1 : 0) + (fanVisible ? 1 : 0);

    private double CpuValue(TemperatureHistoryPointSnapshot sample) =>
        Metric == HistoryMetric.Power ? sample.CpuPower : sample.CpuTemp;

    private double GpuValue(TemperatureHistoryPointSnapshot sample) =>
        Metric == HistoryMetric.Power ? sample.GpuPower : sample.GpuTemp;

    private double FanValue(TemperatureHistoryPointSnapshot sample) =>
        Metric == HistoryMetric.Temperature ? sample.FanRpm : 0;

    private double Maximum(IReadOnlyList<TemperatureHistoryPointSnapshot> samples)
    {
        if (Metric == HistoryMetric.Temperature)
        {
            return TemperatureRange(samples).Upper;
        }

        var peak = 0d;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Max(sample.CpuPower, sample.GpuPower));
        }

        return peak > 0 ? Math.Max(20, Math.Ceiling((peak + 10) / 10) * 10) : 0;
    }

    private static (double Lower, double Upper) TemperatureRange(
        IReadOnlyList<TemperatureHistoryPointSnapshot> samples)
    {
        var peak = 0d;
        foreach (var sample in samples)
        {
            if (sample.CpuTemp > 0)
            {
                peak = Math.Max(peak, sample.CpuTemp);
            }

            if (sample.GpuTemp > 0)
            {
                peak = Math.Max(peak, sample.GpuTemp);
            }
        }

        if (peak <= 0)
        {
            return (TemperatureLowerBound, TemperatureFallbackUpperBound);
        }

        var upper = Math.Min(TemperatureUpperCap, Math.Ceiling((peak + 4) / 5) * 5);
        return (TemperatureLowerBound, Math.Max(TemperatureLowerBound + 10, upper));
    }

    private int FanMaximum(IReadOnlyList<TemperatureHistoryPointSnapshot> samples)
    {
        if (!HasPositiveFan(samples))
        {
            return 0;
        }

        return DeviceFanMaximumRpm > 0 ? DeviceFanMaximumRpm : DefaultDeviceFanMaximumRpm;
    }

    private static bool HasPositiveFan(IReadOnlyList<TemperatureHistoryPointSnapshot>? samples)
    {
        if (samples is null)
        {
            return false;
        }

        foreach (var sample in samples)
        {
            if (sample.FanRpm > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static long HistoryBucketWidth(long windowSpan, int maxPoints)
    {
        if (windowSpan <= 0 || maxPoints <= 0)
        {
            return MinimumHistoryBucketMilliseconds;
        }

        var bucketCount = Math.Max(1d, Math.Floor((double)windowSpan / maxPoints));
        var width = Math.Max(MinimumHistoryBucketMilliseconds, bucketCount);
        return (long)Math.Ceiling(width / MinimumHistoryBucketMilliseconds) * MinimumHistoryBucketMilliseconds;
    }

    private static List<TemperatureHistoryPointSnapshot> Bucketize(
        IReadOnlyList<TemperatureHistoryPointSnapshot> points,
        long windowStart,
        long windowEnd,
        int maxPoints)
    {
        if (points.Count == 0 || windowEnd <= windowStart)
        {
            return [];
        }

        var bucketWidth = HistoryBucketWidth(windowEnd - windowStart, maxPoints);
        var bucketed = new List<TemperatureHistoryPointSnapshot>(Math.Min(points.Count, maxPoints));
        long previousBucket = long.MinValue;
        foreach (var point in points)
        {
            if (point.Timestamp < windowStart || point.Timestamp > windowEnd)
            {
                continue;
            }

            // Absolute buckets keep existing samples in place while the latest bucket fills.
            var bucket = point.Timestamp / bucketWidth;
            if (bucket == previousBucket)
            {
                bucketed[^1] = point;
            }
            else
            {
                bucketed.Add(point);
                previousBucket = bucket;
            }
        }

        return bucketed;
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

    private static double Y(double value, Rect plot, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum)
            || maximum <= minimum)
        {
            return plot.Bottom;
        }

        return plot.Bottom
            - (Math.Clamp(value, minimum, maximum) - minimum) / (maximum - minimum) * plot.Height;
    }

    private string FormatAxisValue(double value)
    {
        if (Metric == HistoryMetric.Power)
        {
            return value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
        }

        return Math.Abs(value - Math.Round(value)) < 0.0001
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
    }

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

    private static double AxisLabelRightEdge(Rect plot) => plot.Left - 8;

    private static double FanAxisLabelRightEdge(Rect plot) => plot.Right + FanAxisRightGutter - 4;

    private static double RightAlignedX(double rightEdge, double textWidth) => rightEdge - textWidth;

    private static void DrawRightAlignedText(DrawingContext context, IBrush brush, string text, double rightEdge, double y)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            11,
            brush);
        context.DrawText(formatted, new Point(RightAlignedX(rightEdge, formatted.Width), y));
    }

    private static string FormatFanAxisValue(double value) =>
        Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static void DrawLegend(DrawingContext context, IBrush lineBrush, IBrush textBrush, string label, double x, double y)
    {
        context.DrawLine(new Pen(lineBrush, 2), new Point(x, y + 6), new Point(x + 18, y + 6));
        DrawText(context, textBrush, label, new Point(x + 23, y - 1));
    }

    private IBrush FindBrush(IBrush fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (this.TryFindResource(key, ActualThemeVariant, out var value))
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
        var temperaturePoints = new[]
        {
            new TemperatureHistoryPointSnapshot { Timestamp = 100, CpuTemp = 44, GpuTemp = 51, FanRpm = 4114 },
            new TemperatureHistoryPointSnapshot { Timestamp = 200, CpuTemp = 63 },
        };
        var noFanPoints = new[]
        {
            new TemperatureHistoryPointSnapshot { Timestamp = 100, CpuTemp = 45, GpuTemp = 50 },
        };
        var preview = new TemperatureHistoryPreview { Metric = HistoryMetric.Power };
        ChartTooltipMotion.SelfCheck();
        ChartTooltipOverlay.SelfCheck();
        var axisLabelRight = AxisLabelRightEdge(new Rect(44, 30, 300, 200));
        var tempRange = TemperatureRange(temperaturePoints);
        var noTemperatureRange = TemperatureRange([]);
        var tempPlot = new Rect(44, 30, 300, 200);
        var temperaturePreview = new TemperatureHistoryPreview { Metric = HistoryMetric.Temperature };
        var bucketed = Bucketize(
            new[]
            {
                new TemperatureHistoryPointSnapshot { Timestamp = 10000, CpuTemp = 10 },
                new TemperatureHistoryPointSnapshot { Timestamp = 12000, CpuTemp = 20 },
                new TemperatureHistoryPointSnapshot { Timestamp = 16000, CpuTemp = 30 },
            },
            10000,
            20000,
            10);
        FanRatedRpm.SelfCheck();
        if (preview.Maximum(points) != 40 || FindNearest(points, 175)?.Timestamp != 200
            || axisLabelRight != 36 || RightAlignedX(axisLabelRight, 12) != 24 || RightAlignedX(axisLabelRight, 4) != 32
            || tempRange.Lower != TemperatureLowerBound || tempRange.Upper != 70
            || Y(TemperatureLowerBound, tempPlot, tempRange.Lower, tempRange.Upper) != tempPlot.Bottom
            || noTemperatureRange != (TemperatureLowerBound, TemperatureFallbackUpperBound)
            || temperaturePreview.FanMaximum(temperaturePoints) != DefaultDeviceFanMaximumRpm
            || temperaturePreview.FanMaximum(new[]
            {
                new TemperatureHistoryPointSnapshot { Timestamp = 100, FanRpm = 600 },
            }) != DefaultDeviceFanMaximumRpm
            || temperaturePreview.FanMaximum(noFanPoints) != 0
            || !HasPositiveFan(temperaturePoints) || HasPositiveFan(noFanPoints)
            || TooltipRowCount(false, false, true) != 1
            || ChartTooltipOverlay.VisibleRowCount(false, false, true) != 1
            || temperaturePreview.TooltipSize(new TemperatureHistoryPointSnapshot
            {
                CpuTemp = 45,
                GpuTemp = 50,
                FanRpm = 1500,
            }).Height != 104
            || temperaturePreview.FormatAxisValue(62.5) != "62.5"
            || HistoryBucketWidth(3_600_000, 1_000) != MinimumHistoryBucketMilliseconds
            || bucketed.Count != 2
            || bucketed[0].Timestamp != 12000
            || bucketed[1].Timestamp != 16000)
        {
            throw new InvalidOperationException("History chart scale, fan axis, hover, and tooltip layout checks failed.");
        }
    }
}
