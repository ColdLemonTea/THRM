using Avalonia;
using Avalonia.Controls;

namespace THRM.Avalonia;

public sealed class AnimatedContentClip : Decorator
{
    public static readonly StyledProperty<double> RevealProgressProperty =
        AvaloniaProperty.Register<AnimatedContentClip, double>(
            nameof(RevealProgress),
            defaultValue: 0,
            validate: value => value is >= 0 and <= 1);

    static AnimatedContentClip()
    {
        AffectsMeasure<AnimatedContentClip>(RevealProgressProperty);
        ClipToBoundsProperty.OverrideDefaultValue<AnimatedContentClip>(true);
    }

    public double RevealProgress
    {
        get => GetValue(RevealProgressProperty);
        set => SetValue(RevealProgressProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var desired = Child.DesiredSize;
        return new Size(desired.Width, desired.Height * RevealProgress);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(new Size(finalSize.Width, Child.DesiredSize.Height)));
        return finalSize;
    }
}
