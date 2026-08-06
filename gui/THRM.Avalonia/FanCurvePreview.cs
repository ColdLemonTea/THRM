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

public sealed class FanCurvePreview : Decorator
{
    private const double MinTemperature = 30;
    private const double MaxTemperature = 110;
    private const double MaxRpm = 4000;
    private const double DefaultWidth = 420;
    private const double DefaultHeight = 320;
    private const double PointHitRadius = 16;
    private int? _hoveredIndex;
    private int? _draggedIndex;
    private int? _selectedIndex;
    private bool _hasTooltipPosition;
    private Point _tooltipAnchor;
    private readonly Canvas _overlayCanvas;
    private readonly Border _tooltip;
    private readonly ExperimentalAcrylicBorder _tooltipAcrylic;
    private readonly Grid _tooltipContent;
    private readonly StackPanel _tooltipRows;
    private readonly TextBlock _tooltipTemperature;
    private readonly ChartTooltipRow _tooltipBaseRow;
    private readonly ChartTooltipRow _tooltipEffectiveRow;

    public static readonly StyledProperty<IReadOnlyList<FanCurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurvePreview, IReadOnlyList<FanCurvePoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<FanCurvePoint>?> LearnedPointsProperty =
        AvaloniaProperty.Register<FanCurvePreview, IReadOnlyList<FanCurvePoint>?>(nameof(LearnedPoints));

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<FanCurvePreview, bool>(nameof(IsEditable));

    private static readonly StyledProperty<Point> TooltipPositionProperty =
        AvaloniaProperty.Register<FanCurvePreview, Point>(nameof(TooltipPosition));

    static FanCurvePreview() =>
        AffectsRender<FanCurvePreview>(PointsProperty, LearnedPointsProperty, IsEditableProperty, TooltipPositionProperty);

    public FanCurvePreview()
    {
        Focusable = true;
        AutomationProperties.SetName(this, "Fan curve editor");
        AutomationProperties.SetHelpText(
            this,
            "Drag a curve point vertically to change its target speed. Speeds snap to 50 RPM and remain non-decreasing. Use the arrow keys after selecting a point for the same adjustment.");
        PointerPressed += OnPointerPressed;
        PointerEntered += OnPointerEntered;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        PointerExited += OnPointerExited;

        _tooltip = ChartTooltipOverlay.Create(out _tooltipAcrylic, out _tooltipContent);
        _tooltipRows = ChartTooltipOverlay.CreateRows(_tooltipContent);
        _tooltipTemperature = ChartTooltipOverlay.CreateText(12);
        _tooltipBaseRow = ChartTooltipOverlay.CreateRow(withDot: false);
        _tooltipEffectiveRow = ChartTooltipOverlay.CreateRow(withDot: false);
        ChartTooltipOverlay.AddTitle(_tooltipContent, _tooltipTemperature);
        _tooltipRows.Children.Add(_tooltipBaseRow.Root);
        _tooltipRows.Children.Add(_tooltipEffectiveRow.Root);
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

    public event EventHandler<FanCurvePointDragEventArgs>? PointDragged;

    public IReadOnlyList<FanCurvePoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IReadOnlyList<FanCurvePoint>? LearnedPoints
    {
        get => GetValue(LearnedPointsProperty);
        set => SetValue(LearnedPointsProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    private Point TooltipPosition
    {
        get => GetValue(TooltipPositionProperty);
        set => SetValue(TooltipPositionProperty, value);
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
            || change.Property == LearnedPointsProperty)
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
        var accent = FindBrush(
            new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            "AccentFillColorDefaultBrush", "SystemAccentColor");
        var learned = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(132, 192, 255)) : new SolidColorBrush(Color.FromRgb(0, 92, 184)),
            "AccentFillColorSecondaryBrush", "AccentFillColorDefaultBrush", "SystemAccentColor");
        var surface = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(45, 45, 45)) : new SolidColorBrush(Colors.White),
            "CardBackgroundFillColorDefaultBrush", "SystemControlBackgroundBaseLowBrush");
        DrawChartScene(context, plot, primary, secondary, grid, accent, learned, surface);
        DrawTooltipBlur(context, plot, primary, secondary, grid, accent, learned, surface, dark);
    }

    private void DrawTooltipBlur(
        DrawingContext context,
        Rect plot,
        IBrush primary,
        IBrush secondary,
        IBrush grid,
        IBrush accent,
        IBrush learned,
        IBrush surface,
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
            blurred => DrawChartScene(blurred, plot, primary, secondary, grid, accent, learned, surface));
    }

    private void DrawChartScene(
        DrawingContext context,
        Rect plot,
        IBrush primary,
        IBrush secondary,
        IBrush grid,
        IBrush accent,
        IBrush learned,
        IBrush surface)
    {
        var gridPen = new Pen(grid, 1);
        foreach (var rpm in new[] { 0, 1000, 2000, 3000, 4000 })
        {
            var y = Y(rpm, plot);
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, secondary, rpm.ToString(CultureInfo.InvariantCulture), new Point(4, y - 7));
        }

        foreach (var temperature in new[] { 30, 50, 70, 90, 110 })
        {
            var x = X(temperature, plot);
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = temperature.ToString(CultureInfo.InvariantCulture);
            DrawText(context, secondary, label, new Point(x - label.Length * 3.4, plot.Bottom + 7));
        }

        var axisPen = new Pen(secondary, 1);
        context.DrawLine(axisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        context.DrawLine(axisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        const double headerTextTop = 6;
        DrawText(context, secondary, "RPM", new Point(4, headerTextTop - 1));
        DrawText(context, secondary, "Temperature (°C)", new Point(plot.Left + plot.Width / 2 - 47, Bounds.Height - 16));

        DrawLegend(context, accent, primary, "Base curve", plot.Left, headerTextTop, false);
        if (LearnedPoints is { Count: > 0 })
        {
            DrawLegend(context, learned, primary, "Learned effective curve", plot.Left + 98, headerTextTop, true);
        }

        var basePoints = Points;
        if (basePoints is not { Count: > 0 })
        {
            DrawText(context, secondary, "No fan curve is available.", new Point(plot.Left + 12, plot.Top + 14));
            return;
        }

        // Keep the full pen visible when a curve endpoint or hover line lands on the plot edge.
        using (context.PushClip(new Rect(plot.X - 1.25, plot.Y - 1.25, plot.Width + 2.5, plot.Height + 2.5)))
        {
            DrawCurve(context, LearnedPoints, plot, new Pen(learned, 2, new DashStyle(new[] { 5d, 3d }, 0)));
            DrawCurve(context, basePoints, plot, new Pen(accent, 2.5));

            if (_hoveredIndex is { } hovered && hovered >= 0 && hovered < basePoints.Count)
            {
                var point = PointFor(basePoints[hovered], plot);
                context.DrawLine(new Pen(accent, 1, DashStyle.Dash), new Point(point.X, plot.Top), new Point(point.X, plot.Bottom));
            }
        }

        // Curve lines stay inside the plot, but handles may sit on the maximum value.
        // Drawing them after the clip keeps a 4,000 RPM handle whole instead of halving it.
        for (var index = 0; index < basePoints.Count; index++)
        {
            var point = PointFor(basePoints[index], plot);
            context.DrawEllipse(accent, new Pen(surface, 2), point, 4.5, 4.5);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEditable || Points is not { Count: > 0 })
        {
            return;
        }

        var change = e.Key switch
        {
            Key.Up or Key.Right => 50,
            Key.Down or Key.Left => -50,
            _ => 0,
        };
        if (change == 0)
        {
            return;
        }

        var index = _selectedIndex ?? _hoveredIndex ?? 0;
        if (index < 0 || index >= Points.Count)
        {
            return;
        }

        _selectedIndex = index;
        PointDragged?.Invoke(this, new FanCurvePointDragEventArgs(index, Points[index].Rpm + change));
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEditable || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !TryGetPlot(out var plot))
        {
            return;
        }

        var index = HitTestPoint(e.GetPosition(this), plot);
        if (index is null)
        {
            return;
        }

        Focus();
        _draggedIndex = index;
        _selectedIndex = index;
        _hoveredIndex = index;
        UpdateTooltipTarget(index.Value, e.GetPosition(this), plot);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (TryGetPlot(out var plot))
        {
            UpdateHover(e.GetPosition(this), plot);
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!TryGetPlot(out var plot))
        {
            return;
        }

        if (_draggedIndex is { } dragged)
        {
            UpdateDraggedPoint(dragged, e.GetPosition(this), plot);
            UpdateTooltipTarget(dragged, e.GetPosition(this), plot);
            return;
        }

        UpdateHover(e.GetPosition(this), plot);
    }

    private void UpdateHover(Point pointer, Rect plot)
    {
        var next = NearestPointByTemperature(pointer, plot);
        if (_hoveredIndex != next)
        {
            _hoveredIndex = next;
            InvalidateVisual();
        }

        if (next is { } index && Points is { Count: > 0 } points && index < points.Count)
        {
            UpdateTooltipTarget(index, pointer, plot);
        }
        else
        {
            ClearHover();
        }

        Cursor = IsEditable && HitTestPoint(pointer, plot) is not null
            ? new Cursor(StandardCursorType.SizeNorthSouth)
            : Cursor.Default;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e.Pointer);

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(e.Pointer);

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_draggedIndex is not null)
        {
            return;
        }

        ClearHover();
    }

    private void EndDrag(IPointer pointer)
    {
        if (_draggedIndex is null)
        {
            return;
        }

        _draggedIndex = null;
        if (ReferenceEquals(pointer.Captured, this))
        {
            pointer.Capture(null);
        }

        InvalidateVisual();
    }

    private void UpdateDraggedPoint(int index, Point pointer, Rect plot)
    {
        if (Points is not { Count: > 0 } points || index < 0 || index >= points.Count)
        {
            return;
        }

        var rpm = Math.Clamp((int)Math.Round((plot.Bottom - pointer.Y) / plot.Height * MaxRpm / 50) * 50, 0, (int)MaxRpm);
        if (rpm == points[index].Rpm)
        {
            return;
        }

        PointDragged?.Invoke(this, new FanCurvePointDragEventArgs(index, rpm));
    }

    private void UpdateTooltipTarget(int index, Point pointer, Rect plot)
    {
        if (Points is not { Count: > 0 } points || index < 0 || index >= points.Count)
        {
            ClearHover();
            return;
        }

        var learnedPoint = LearnedPoints is { Count: > 0 } learned && index < learned.Count ? learned[index] : null;
        var anchor = new Point(PointFor(points[index], plot).X, pointer.Y);
        var size = TooltipSize(learnedPoint);
        UpdateTooltipOverlay(points[index], learnedPoint);
        SetTooltipTarget(anchor, size);
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

    private void ClearHover()
    {
        _hasTooltipPosition = false;
        _tooltip.IsVisible = false;
        if (_hoveredIndex is null)
        {
            return;
        }

        _hoveredIndex = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    private void RefreshTooltip()
    {
        if (!_hasTooltipPosition || _hoveredIndex is not { } index || Points is not { Count: > 0 } points
            || index < 0 || index >= points.Count)
        {
            return;
        }

        var learnedPoint = LearnedPoints is { Count: > 0 } learned && index < learned.Count ? learned[index] : null;
        UpdateTooltipOverlay(points[index], learnedPoint);
        SetTooltipTarget(_tooltipAnchor, TooltipSize(learnedPoint));
    }

    private void UpdateTooltipOverlay(FanCurvePoint basePoint, FanCurvePoint? learnedPoint)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var primary = FindBrush(
            dark ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black),
            "TextFillColorPrimaryBrush");
        var secondary = FindBrush(
            dark ? new SolidColorBrush(Color.FromRgb(210, 210, 210)) : new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            "TextFillColorSecondaryBrush");
        var border = FindBrush(
            dark ? new SolidColorBrush(Color.FromArgb(96, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            "CardStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush");

        _tooltipTemperature.Text = $"{basePoint.Temperature} °C";
        _tooltipTemperature.Foreground = primary;
        _tooltipBaseRow.Label.Text = "Base curve";
        _tooltipBaseRow.Label.Foreground = secondary;
        _tooltipBaseRow.Value.Text = $"{basePoint.Rpm:N0} RPM";
        _tooltipBaseRow.Value.Foreground = secondary;
        _tooltipBaseRow.IsVisible = true;
        _tooltipEffectiveRow.Label.Text = "Effective";
        _tooltipEffectiveRow.Label.Foreground = secondary;
        _tooltipEffectiveRow.Value.Text = learnedPoint is null ? string.Empty : $"{learnedPoint.Rpm:N0} RPM";
        _tooltipEffectiveRow.Value.Foreground = secondary;
        _tooltipEffectiveRow.IsVisible = learnedPoint is not null;

        ChartTooltipOverlay.Configure(
            _tooltip,
            _tooltipAcrylic,
            _tooltipContent,
            TooltipSize(learnedPoint),
            border,
            dark);
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

    private int? HitTestPoint(Point pointer, Rect plot)
    {
        if (Points is not { Count: > 0 } points)
        {
            return null;
        }

        var closest = -1;
        var distance = PointHitRadius * PointHitRadius;
        for (var index = 0; index < points.Count; index++)
        {
            var location = PointFor(points[index], plot);
            var dx = location.X - pointer.X;
            var dy = location.Y - pointer.Y;
            var currentDistance = dx * dx + dy * dy;
            if (currentDistance <= distance)
            {
                closest = index;
                distance = currentDistance;
            }
        }

        return closest >= 0 ? closest : null;
    }

    private int? NearestPointByTemperature(Point pointer, Rect plot)
    {
        if (Points is not { Count: > 0 } points || !plot.Contains(pointer))
        {
            return null;
        }

        var closest = 0;
        var distance = double.MaxValue;
        for (var index = 0; index < points.Count; index++)
        {
            var currentDistance = Math.Abs(PointFor(points[index], plot).X - pointer.X);
            if (currentDistance < distance)
            {
                closest = index;
                distance = currentDistance;
            }
        }

        return closest;
    }

    private bool TryGetPlot(out Rect plot)
    {
        const double left = 48;
        const double top = 28;
        const double right = 16;
        const double bottom = 38;
        plot = new Rect(left, top, Bounds.Width - left - right, Bounds.Height - top - bottom);
        return double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height)
            && Bounds.Width > 0 && Bounds.Height > 0 && plot.Width > 0 && plot.Height > 0;
    }

    private static void DrawCurve(DrawingContext context, IReadOnlyList<FanCurvePoint>? points, Rect plot, Pen pen)
    {
        if (points is not { Count: > 1 })
        {
            return;
        }

        var previous = PointFor(points[0], plot);
        for (var index = 1; index < points.Count; index++)
        {
            var next = PointFor(points[index], plot);
            context.DrawLine(pen, previous, next);
            previous = next;
        }
    }

    private static Size TooltipSize(FanCurvePoint? learnedPoint) =>
        learnedPoint is null
            ? new Size(152, 54 + ChartTooltipOverlay.RowsBottomPadding)
            : new Size(184, 72 + ChartTooltipOverlay.RowsBottomPadding);

    private static Point PointFor(FanCurvePoint point, Rect plot) => new(X(point.Temperature, plot), Y(point.Rpm, plot));

    private static double X(int temperature, Rect plot) =>
        plot.Left + (Math.Clamp(temperature, (int)MinTemperature, (int)MaxTemperature) - MinTemperature)
            / (MaxTemperature - MinTemperature) * plot.Width;

    private static double Y(int rpm, Rect plot) =>
        plot.Bottom - Math.Clamp(rpm, 0, (int)MaxRpm) / MaxRpm * plot.Height;

    private static double Desired(double fallback, double available) =>
        double.IsFinite(available) ? Math.Clamp(available, 0, fallback) : fallback;

    private static void DrawLegend(DrawingContext context, IBrush lineBrush, IBrush textBrush, string label, double x, double y, bool dashed)
    {
        var pen = dashed ? new Pen(lineBrush, 2, new DashStyle(new[] { 5d, 3d }, 0)) : new Pen(lineBrush, 2);
        context.DrawLine(pen, new Point(x, y + 6), new Point(x + 18, y + 6));
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

    private static void DrawText(DrawingContext context, IBrush brush, string text, Point origin, double size = 11)
    {
        context.DrawText(
            new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default),
                size,
                brush),
            origin);
    }
}

public sealed class FanCurvePointDragEventArgs(int index, int rpm) : EventArgs
{
    public int Index { get; } = index;
    public int Rpm { get; } = rpm;
}

internal static class FanCurveEdit
{
    public static List<FanCurvePoint> SetRpm(IReadOnlyList<FanCurvePoint> curve, int index, int rpm)
    {
        var next = curve.Select(point => new FanCurvePoint { Temperature = point.Temperature, Rpm = point.Rpm }).ToList();
        if (index < 0 || index >= next.Count)
        {
            return next;
        }

        var normalized = Math.Clamp((int)Math.Round(rpm / 50d) * 50, 0, 4000);
        next[index] = new FanCurvePoint { Temperature = next[index].Temperature, Rpm = normalized };
        for (var left = index - 1; left >= 0 && next[left].Rpm > next[left + 1].Rpm; left--)
        {
            next[left] = new FanCurvePoint { Temperature = next[left].Temperature, Rpm = next[left + 1].Rpm };
        }

        for (var right = index + 1; right < next.Count && next[right].Rpm < next[right - 1].Rpm; right++)
        {
            next[right] = new FanCurvePoint { Temperature = next[right].Temperature, Rpm = next[right - 1].Rpm };
        }

        return next;
    }

    public static void SelfCheck()
    {
        var curve = new[]
        {
            new FanCurvePoint { Temperature = 30, Rpm = 1000 },
            new FanCurvePoint { Temperature = 40, Rpm = 1500 },
            new FanCurvePoint { Temperature = 50, Rpm = 2000 },
        };
        var raised = SetRpm(curve, 1, 2250);
        var lowered = SetRpm(curve, 1, 750);
        if (raised.Select(point => point.Rpm).SequenceEqual(new[] { 1000, 2250, 2250 })
            && lowered.Select(point => point.Rpm).SequenceEqual(new[] { 750, 750, 2000 }))
        {
            return;
        }

        throw new InvalidOperationException("Fan curve drag synchronization check failed.");
    }
}
