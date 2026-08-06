using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace THRM.Avalonia;

public sealed class SmoothScrollViewer : ScrollViewer
{
    private const double WheelStep = 48;
    private readonly DispatcherTimer _wheelSequenceTimer;
    private bool _wheelSequenceActive;
    private Vector _wheelTarget;

    public SmoothScrollViewer()
    {
        Transitions = new Transitions
        {
            new VectorTransition
            {
                Property = OffsetProperty,
                Duration = TimeSpan.FromMilliseconds(167),
                Easing = new CubicEaseOut(),
            },
        };
        _wheelSequenceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _wheelSequenceTimer.Tick += (_, _) =>
        {
            _wheelSequenceActive = false;
            _wheelSequenceTimer.Stop();
        };
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || e.Delta.Y == 0 || Extent.Height <= Viewport.Height)
        {
            return;
        }

        var current = _wheelSequenceActive ? _wheelTarget : Offset;
        var next = NextOffset(current, Extent, Viewport, e.Delta.Y);
        if (next.Y == current.Y)
        {
            return;
        }

        _wheelTarget = next;
        _wheelSequenceActive = true;
        _wheelSequenceTimer.Stop();
        _wheelSequenceTimer.Start();
        SetCurrentValue(OffsetProperty, next);
        e.Handled = true;
    }

    internal static Vector NextOffset(Vector current, Size extent, Size viewport, double wheelDelta) =>
        new(current.X, Math.Clamp(current.Y - wheelDelta * WheelStep, 0, Math.Max(0, extent.Height - viewport.Height)));

    internal static void SelfCheck()
    {
        var extent = new Size(300, 1_000);
        var viewport = new Size(300, 400);
        var down = NextOffset(new Vector(0, 100), extent, viewport, -1);
        var up = NextOffset(down, extent, viewport, 1);
        var limit = NextOffset(new Vector(0, 590), extent, viewport, -1);
        if (down.Y != 148 || up.Y != 100 || limit.Y != 600)
        {
            throw new InvalidOperationException("Smooth wheel scrolling check failed.");
        }
    }
}
