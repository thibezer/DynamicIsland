using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIslandWindows.Services
{
    public static class AnimationHelper
    {
        public static void AnimateIsland(Window window, double targetWidth, double targetHeight, double bottomAnchor)
        {
            // Se for a NativeIslandWindow principal, redireciona para a animação atômica livre de jitter vertical
            if (window is NativeIslandWindow islandWindow)
            {
                islandWindow.StartAtomicTransition(targetWidth, targetHeight, bottomAnchor);
                return;
            }

            var duration = TimeSpan.FromMilliseconds(350);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Animação de Largura da Janela
            var widthAnimation = new DoubleAnimation(window.Width, targetWidth, duration) { EasingFunction = easing };
            Storyboard.SetTarget(widthAnimation, window);
            Storyboard.SetTargetProperty(widthAnimation, new PropertyPath(Window.WidthProperty));

            // Cálculo do novo Top para manter a ancoragem inferior da janela
            double targetTop = bottomAnchor - targetHeight;
            var topAnimation = new DoubleAnimation(window.Top, targetTop, duration) { EasingFunction = easing };
            Storyboard.SetTarget(topAnimation, window);
            Storyboard.SetTargetProperty(topAnimation, new PropertyPath(Window.TopProperty));

            // Animação de Altura da Janela
            var heightAnimation = new DoubleAnimation(window.Height, targetHeight, duration) { EasingFunction = easing };
            Storyboard.SetTarget(heightAnimation, window);
            Storyboard.SetTargetProperty(heightAnimation, new PropertyPath(Window.HeightProperty));

            var sb = new Storyboard();
            sb.Children.Add(widthAnimation);
            sb.Children.Add(topAnimation);
            sb.Children.Add(heightAnimation);
            sb.Begin();
        }
    }
}
