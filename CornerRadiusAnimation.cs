using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIslandWindows
{
    /// <summary>
    /// Animação de CornerRadius nativa para WPF, compatível com EasingFunctions (incluindo SpringEase).
    /// Permite a morfologia orgânica e contínua do contêiner da Dynamic Island entre pílula e folha expandida.
    /// </summary>
    public class CornerRadiusAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(CornerRadius);

        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(CornerRadius?), typeof(CornerRadiusAnimation), new PropertyMetadata(null));

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(CornerRadius?), typeof(CornerRadiusAnimation), new PropertyMetadata(null));

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(CornerRadiusAnimation), new PropertyMetadata(null));

        public CornerRadius? From
        {
            get => (CornerRadius?)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public CornerRadius? To
        {
            get => (CornerRadius?)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        public CornerRadiusAnimation() { }

        public CornerRadiusAnimation(CornerRadius to, Duration duration)
        {
            To = to;
            Duration = duration;
        }

        public CornerRadiusAnimation(CornerRadius from, CornerRadius to, Duration duration)
        {
            From = from;
            To = to;
            Duration = duration;
        }

        protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation();

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            if (animationClock.CurrentProgress == null) return defaultDestinationValue;

            double progress = animationClock.CurrentProgress.Value;
            if (EasingFunction != null)
            {
                progress = EasingFunction.Ease(progress);
            }

            CornerRadius from = From ?? (CornerRadius)defaultOriginValue;
            CornerRadius to = To ?? (CornerRadius)defaultDestinationValue;

            double topLeft = from.TopLeft + (to.TopLeft - from.TopLeft) * progress;
            double topRight = from.TopRight + (to.TopRight - from.TopRight) * progress;
            double bottomRight = from.BottomRight + (to.BottomRight - from.BottomRight) * progress;
            double bottomLeft = from.BottomLeft + (to.BottomLeft - from.BottomLeft) * progress;

            return new CornerRadius(
                Math.Max(0, topLeft),
                Math.Max(0, topRight),
                Math.Max(0, bottomRight),
                Math.Max(0, bottomLeft));
        }
    }
}
