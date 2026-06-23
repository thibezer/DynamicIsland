using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIslandWindows
{
    /// <summary>
    /// Easing Function matemática que replica exatamente a física de mola (Spring Physics)
    /// usada pela Apple no iOS e na Dynamic Island.
    /// </summary>
    public class SpringEase : EasingFunctionBase
    {
        private double _dampingRatio = 0.85; // Amortecimento relativo (0.85 = Estável e fluido)
        private double _response = 0.55;     // Tempo de resposta em segundos (0.55s = Padrão iOS)

        public double DampingRatio
        {
            get => _dampingRatio;
            set => _dampingRatio = Math.Clamp(value, 0.01, 1.0);
        }

        public double Response
        {
            get => _response;
            set => _response = Math.Max(value, 0.01);
        }

        protected override Freezable CreateInstanceCore()
        {
            return new SpringEase();
        }

        protected override double EaseInCore(double normalizedTime)
        {
            double t = normalizedTime;

            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            // Frequência natural baseada no tempo de resposta
            double omegaN = (2.0 * Math.PI) / _response;
            
            // Frequência angular amortecida
            double omegaD = omegaN * Math.Sqrt(1.0 - (_dampingRatio * _dampingRatio));
            
            // Fator de decaimento (fricção/atrito)
            double alpha = _dampingRatio * omegaN;

            // Mapeamento do ciclo de 0..1 do WPF para o tempo de estabilização física real (aprox. 5 constantes de tempo)
            double physicalTime = t * (_response * 5.0);

            // Resolução da equação diferencial de movimento
            double envelope = Math.Exp(-alpha * physicalTime);
            double cosTerm = Math.Cos(omegaD * physicalTime);
            double sinTerm = Math.Sin(omegaD * physicalTime);

            double springValue = 1.0 - envelope * (cosTerm + (alpha / omegaD) * sinTerm);

            return springValue;
        }
    }
}
