using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIslandWindows
{
    /// <summary>
    /// Easing function que replica com precisão matemática a física de mola (Spring Physics)
    /// usada pelo SwiftUI / Dynamic Island da Apple.
    ///
    /// Suporta perfeitamente os 3 modos do WPF (EaseOut, EaseIn, EaseInOut).
    /// Por padrão, EasingMode é EasingMode.EaseOut (o padrão natural de molas da Apple).
    /// </summary>
    public class SpringEase : EasingFunctionBase
    {
        // ── DependencyProperties (obrigatório para uso em XAML e Storyboard) ─────────

        public static readonly DependencyProperty DampingRatioProperty =
            DependencyProperty.Register(
                nameof(DampingRatio),
                typeof(double),
                typeof(SpringEase),
                new PropertyMetadata(0.85, null, CoerceDampingRatio));

        public static readonly DependencyProperty ResponseProperty =
            DependencyProperty.Register(
                nameof(Response),
                typeof(double),
                typeof(SpringEase),
                new PropertyMetadata(0.55, null, CoerceResponse));

        // Coerção no nível da DependencyProperty: vale também para valores vindos de XAML,
        // Storyboard ou SetValue direto (o setter CLR é ignorado nesses casos)
        private static object CoerceDampingRatio(DependencyObject d, object value) =>
            Math.Clamp((double)value, 0.01, 0.999);

        private static object CoerceResponse(DependencyObject d, object value) =>
            Math.Max((double)value, 0.01);

        // ── Propriedades ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Coeficiente de amortecimento (ζ).
        /// 0.85 = padrão Apple (Dynamic Island). Range válido: [0.01, 0.999].
        /// Não use 1.0 — causa divisão por zero na fórmula de ωd.
        /// </summary>
        public double DampingRatio
        {
            get => (double)GetValue(DampingRatioProperty);
            set => SetValue(DampingRatioProperty, value);
        }

        /// <summary>
        /// Tempo de resposta da mola em segundos (T).
        /// 0.55 = expansão fluida | 0.35 = snappy (clique). Range válido: > 0.
        /// </summary>
        public double Response
        {
            get => (double)GetValue(ResponseProperty);
            set => SetValue(ResponseProperty, value);
        }

        // ── Presets Prontos (Apple HIG) ───────────────────────────────────────────────

        /// <summary>Expansão e retração da Dynamic Island (fluida, amortecida)</summary>
        public static SpringEase Expansion => new SpringEase { DampingRatio = 0.85, Response = 0.55 };

        /// <summary>Micro-interações rápidas e responsivas (botões, switches)</summary>
        public static SpringEase Snappy => new SpringEase { DampingRatio = 0.85, Response = 0.35 };

        /// <summary>Overshoot mais perceptível para pop-ins e badges</summary>
        public static SpringEase Bouncy => new SpringEase { DampingRatio = 0.65, Response = 0.40 };

        /// <summary>Transição ultra suave com quase nenhum overshoot</summary>
        public static SpringEase Smooth => new SpringEase { DampingRatio = 0.95, Response = 0.50 };

        // ── Construtor ────────────────────────────────────────────────────────────────

        public SpringEase()
        {
            // EaseOut é o padrão para molas (movimento rápido no início, amortecendo no alvo).
            EasingMode = EasingMode.EaseOut;
        }

        // ── Núcleo da equação diferencial ─────────────────────────────────────────────

        /// <summary>
        /// Calcula a curva fundamental Ease-Out da resposta ao degrau da mola.
        /// </summary>
        private double CalculateSpringEaseOut(double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            double zeta = DampingRatio;
            double T = Response;

            // Frequência natural (rad/s)
            double omegaN = (2.0 * Math.PI) / T;

            // Frequência amortecida — max() previne raiz negativa por erro de ponto flutuante
            double omegaD = omegaN * Math.Sqrt(Math.Max(0.0, 1.0 - zeta * zeta));

            // Fator de decaimento exponencial
            double alpha = zeta * omegaN;

            // Settle factor: quantas "vezes T" são necessárias para estabilizar a 0.1%
            double settleFactor = -Math.Log(0.001) / (2.0 * Math.PI * zeta);

            // Mapeia t ∈ [0,1] para o tempo físico real que cobre o ciclo de estabilização
            double physicalTime = t * T * settleFactor;

            // Resolução da equação diferencial para sistema subamortecido (Ease-Out)
            double envelope = Math.Exp(-alpha * physicalTime);
            double cosTerm = Math.Cos(omegaD * physicalTime);
            double sinTerm = (omegaD > 1e-10)
                ? (alpha / omegaD) * Math.Sin(omegaD * physicalTime)
                : 0.0;

            return 1.0 - envelope * (cosTerm + sinTerm);
        }

        /// <summary>
        /// O WPF define EaseOut como: Ease(t) = 1.0 - EaseInCore(1.0 - t).
        /// Para que Ease(t) resulte na curva correta CalculateSpringEaseOut(t) quando EasingMode == EaseOut,
        /// EaseInCore(u) deve retornar 1.0 - CalculateSpringEaseOut(1.0 - u).
        /// Isso também garante que EaseIn e EaseInOut funcionem perfeitamente segundo a especificação do WPF.
        /// </summary>
        protected override double EaseInCore(double normalizedTime)
        {
            if (normalizedTime <= 0.0) return 0.0;
            if (normalizedTime >= 1.0) return 1.0;

            return 1.0 - CalculateSpringEaseOut(1.0 - normalizedTime);
        }

        // ── Obrigatório: Freezable / clone do WPF ────────────────────────────────────

        protected override Freezable CreateInstanceCore()
        {
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
        /// </summary>
        public TimeSpan GetSettleTime()
        {
            double omegaN = (2.0 * Math.PI) / Response;
            double alpha = DampingRatio * omegaN;
            double settleSeconds = -Math.Log(0.001) / alpha;
            return TimeSpan.FromSeconds(settleSeconds);
        }
    }
}
