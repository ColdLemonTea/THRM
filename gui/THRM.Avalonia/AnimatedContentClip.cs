using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace THRM.Avalonia;

public sealed class AnimatedContentClip : Decorator
{
    internal static readonly Easing PowerToysEaseOutCubic = new SplineEasing
    {
        X1 = 0,
        Y1 = 0,
        X2 = 0,
        Y2 = 1,
    };
    private static readonly Easing FluentExpandEasing = new CubicEaseOut();
    private static readonly Easing FluentCollapseEasing = new CubicEaseIn();
    private static readonly TimeSpan FluentExpandDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FluentCollapseDuration = TimeSpan.FromMilliseconds(167);

    public static readonly StyledProperty<double> RevealProgressProperty =
        AvaloniaProperty.Register<AnimatedContentClip, double>(
            nameof(RevealProgress),
            defaultValue: 0,
            validate: value => value is >= 0 and <= 1);
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<AnimatedContentClip, bool>(nameof(IsExpanded));

    static AnimatedContentClip()
    {
        AffectsMeasure<AnimatedContentClip>(RevealProgressProperty);
        ClipToBoundsProperty.OverrideDefaultValue<AnimatedContentClip>(true);
    }

    public AnimatedContentClip()
    {
        Opacity = 0;
    }

    public double RevealProgress
    {
        get => GetValue(RevealProgressProperty);
        set => SetValue(RevealProgressProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsExpandedProperty)
        {
            return;
        }

        foreach (var transition in Transitions?.OfType<DoubleTransition>() ?? [])
        {
            if (transition.Property == RevealProgressProperty)
            {
                transition.Easing = IsExpanded
                    ? FluentExpandEasing
                    : FluentCollapseEasing;
                transition.Duration = IsExpanded
                    ? FluentExpandDuration
                    : FluentCollapseDuration;
            }
            else if (transition.Property == Visual.OpacityProperty)
            {
                transition.Easing = IsExpanded
                    ? FluentExpandEasing
                    : FluentCollapseEasing;
                transition.Duration = IsExpanded
                    ? FluentExpandDuration
                    : FluentCollapseDuration;
            }
        }

        RevealProgress = IsExpanded ? 1 : 0;
        Opacity = IsExpanded ? 1 : 0;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var desired = Child.DesiredSize;
        return new Size(desired.Width, CalculateRevealedHeight(desired.Height, RevealProgress));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null)
        {
            Clip = null;
            return finalSize;
        }

        var childHeight = Child.DesiredSize.Height;
        Clip = new RectangleGeometry(new Rect(0, 0, finalSize.Width, finalSize.Height));
        Child.Arrange(new Rect(new Size(finalSize.Width, childHeight)));
        return finalSize;
    }

    internal static double CalculateRevealedHeight(double childHeight, double revealProgress) =>
        childHeight * Math.Clamp(revealProgress, 0, 1);

    public static void SelfCheck()
    {
        if (CalculateRevealedHeight(100, 0) != 0
            || CalculateRevealedHeight(100, 1) != 100
            || CalculateRevealedHeight(100, -1) != 0
            || CalculateRevealedHeight(100, 2) != 100
            || PowerToysEaseOutCubic is not SplineEasing { X1: 0, Y1: 0, X2: 0, Y2: 1 }
            || FluentExpandEasing is not CubicEaseOut
            || FluentCollapseEasing is not CubicEaseIn
            || FluentExpandDuration != TimeSpan.FromMilliseconds(200)
            || FluentCollapseDuration != TimeSpan.FromMilliseconds(167))
        {
            throw new InvalidOperationException("Expander reveal check failed.");
        }
    }
}
