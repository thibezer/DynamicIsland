using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIslandWindows
{
    /// <summary>
    /// Easing function que replica com precisão matemática a física de mola (Spring Physics)
    /// usada pelo SwiftUI / Dynamic Island da Apple.
    ///
    /// USO OBRIGATÓRIO:
    ///   - EasingMode deve ser EasingMode.EaseOut (definido no construtor por padrão).
    ///   - A Duration do DoubleAnimation deve ser GetSettleTime(), não um valor fixo.
    ///
    /// XAML:
    ///   <anim:SpringEase DampingRatio="0.85" Response="0.55"/>
    /// </summary>
    public class SpringEase : EasingFunctionBase
    {
        // ── DependencyProperties (obrigatório para uso em XAML e Storyboard) ─────────

        public static readonly DependencyProperty DampingRatioProperty =
            DependencyProperty.Register(
                nameof(DampingRatio),
                typeof(double),
                typeof(SpringEase),
                new PropertyMetadata(0.85));

        public static readonly DependencyProperty ResponseProperty =
            DependencyProperty.Register(
                nameof(Response),
                typeof(double),
                typeof(SpringEase),
                new PropertyMetadata(0.55));

        // ── Propriedades ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Coeficiente de amortecimento (ζ).
        /// 0.85 = padrão Apple (Dynamic Island). Range válido: [0.01, 0.999].
        /// Não use 1.0 — causa divisão por zero na fórmula de ωd.
        /// </summary>
        public double DampingRatio
        {
            get => (double)GetValue(DampingRatioProperty);
            set => SetValue(DampingRatioProperty, Math.Clamp(value, 0.01, 0.999));
        }

        /// <summary>
        /// Tempo de resposta da mola em segundos (T).
        /// 0.55 = expansão fluida | 0.35 = snappy (clique). Range válido: > 0.
        /// </summary>
        public double Response
        {
            get => (double)GetValue(ResponseProperty);
            set => SetValue(ResponseProperty, Math.Max(value, 0.01));
        }

        // ── Construtor ────────────────────────────────────────────────────────────────

        public SpringEase()
        {
            // EaseOut é o único modo fisicamente correto para uma mola.
            // EaseIn produziria movimento invertido (WPF aplica f_out(t) = 1 - f_in(1-t)).
            EasingMode = EasingMode.EaseOut;
        }

        // ── Núcleo da equação diferencial ─────────────────────────────────────────────

        protected override double EaseInCore(double normalizedTime)
        {
            double t = normalizedTime;

            // Guards de boundary — WPF pode chamar com valores ligeiramente fora de [0,1]
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            double zeta    = DampingRatio;
            double T       = Response;

            // Frequência natural (rad/s)
            double omegaN  = (2.0 * Math.PI) / T;

            // Frequência amortecida — max() previne raiz negativa por erro de ponto flutuante
            double omegaD  = omegaN * Math.Sqrt(Math.Max(0.0, 1.0 - zeta * zeta));

            // Fator de decaimento exponencial
            double alpha   = zeta * omegaN;

            // Settle factor: quantas "vezes T" são necessárias para estabilizar a 0.1%.
            // Derivado de: settleTime = -ln(0.001) / alpha = 6.908 / (2π·ζ) * T
            // Logo settle_factor = 6.908 / (2π·ζ) — independente de T.
            double settleFactor = -Math.Log(0.001) / (2.0 * Math.PI * zeta);

            // Mapeia t ∈ [0,1] para o tempo físico real que cobre o ciclo de estabilização
            double physicalTime = t * T * settleFactor;

            // Resolução da equação diferencial para sistema subamortecido
            double envelope = Math.Exp(-alpha * physicalTime);
            double cosTerm  = Math.Cos(omegaD * physicalTime);
            double sinTerm  = (omegaD > 1e-10)
                ? (alpha / omegaD) * Math.Sin(omegaD * physicalTime)
                : 0.0;  // Guarda: omegaD → 0 quando zeta → 1

            return 1.0 - envelope * (cosTerm + sinTerm);
        }

        // ── Obrigatório: Freezable / clone do WPF ────────────────────────────────────

        protected override Freezable CreateInstanceCore()
        {
            // O sistema de animação do WPF clona easing functions durante Storyboard.Begin().
            // Sem este override, InvalidOperationException em tempo de execução.
            return new SpringEase
            {
                DampingRatio = this.DampingRatio,
                Response     = this.Response,
                EasingMode   = this.EasingMode
            };
        }

        // ── Utilitário ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Retorna o TimeSpan ideal para usar como Duration no DoubleAnimation,
        /// calculado a partir dos parâmetros físicos da mola (threshold 0.1%).
        ///
        /// Uso:
        ///   var anim = new DoubleAnimation { Duration = appleSpring.GetSettleTime() };
        /// </summary>
        public TimeSpan GetSettleTime()
        {
            double omegaN   = (2.0 * Math.PI) / Response;
            double alpha    = DampingRatio * omegaN;
            double settleSeconds = -Math.Log(0.001) / alpha;
            return TimeSpan.FromSeconds(settleSeconds);
        }
    }
}
