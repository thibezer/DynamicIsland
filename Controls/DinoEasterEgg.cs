using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIslandWindows.Controls
{
    /// <summary>
    /// Easter egg: o dino "sem internet", com o path exato do sprite original
    /// (incluindo o chão), desenhado via Geometry — sem depender de nenhuma
    /// imagem/asset externo. Ele fica parado e pisca o olho periodicamente.
    /// 
    /// Uso em XAML:
    ///   xmlns:c="clr-namespace:SeuNamespace.Controles"
    ///   <c:DinoEasterEgg Scale="2" BodyColor="#535353"/>
    /// 
    /// Uso em code-behind:
    ///   var dino = new DinoEasterEgg { Scale = 1.5 };
    ///   meuGrid.Children.Add(dino);
    /// </summary>
    public class DinoEasterEgg : FrameworkElement
    {
        // ---------------------------------------------------------------
        // Path original do sprite do dino (viewBox 144x144), incluindo o
        // chão. O prefixo "F1" força fill-rule non-zero, igual ao padrão
        // do SVG, pra respeitar o "buraco" do olho corretamente.
        // ---------------------------------------------------------------
        private const string DinoPathData =
            "F1 M0,142L8,142L8,144L0,144L0,142Z" +
            "M28,142L32,142L32,144L28,144L28,142Z" +
            "M96,142L104,142L104,144L96,144L96,142Z" +
            "M80,100L76,100L76,114L72,114L72,120L68,120L68,124L64,124L64,140L68,140L68,144L60,144L60,132L56,132L56,128L52,128L52,132L48,132L48,136L44,136L44,140L48,140L48,144L40,144L40,128L36,128L36,124L32,124L32,120L28,120L28,116L24,116L24,112L20,112L20,88L24,88L24,96L28,96L28,100L32,100L32,104L40,104L40,100L44,100L44,96L50,96L50,92L56,92L56,88L60,88L60,62L64,62L64,58L96,58L96,62L100,62L100,80L80,80L80,84L92,84L92,88L76,88L76,96L84,96L84,104L80,104L80,100Z" +
            "M82,140L84,140L84,142L82,142L82,140Z" +
            "M12,136L20,136L20,138L12,138L12,136Z" +
            "M110,134L116,134L116,136L110,136L110,134Z" +
            "M0,128L32,128L32,130L0,130L0,128Z" +
            "M72,128L128,128L128,130L72,130L72,128Z" +
            "M68,64L68,68L72,68L72,64L68,64Z";

        // Tamanho nativo do viewBox original (144x144)
        private const double NativeSize = 144;

        // Posição/tamanho exatos do "buraco" do olho dentro do path acima
        private static readonly Rect EyeHoleRect = new Rect(68, 64, 4, 4);

        private static readonly Geometry BodyGeometry = CriarGeometriaCongelada();

        private static Geometry CriarGeometriaCongelada()
        {
            var geo = Geometry.Parse(DinoPathData);
            geo.Freeze();
            return geo;
        }

        // ---------------------------------------------------------------
        // Dependency Properties
        // ---------------------------------------------------------------
        public static readonly DependencyProperty ScaleProperty =
            DependencyProperty.Register(nameof(Scale), typeof(double), typeof(DinoEasterEgg),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Fator de escala sobre o tamanho nativo de 144x144.</summary>
        public double Scale
        {
            get => (double)GetValue(ScaleProperty);
            set => SetValue(ScaleProperty, value);
        }

        private SolidColorBrush _cachedBodyBrush = CreateFrozenBrush(Color.FromRgb(0x53, 0x53, 0x53));

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public static readonly DependencyProperty BodyColorProperty =
            DependencyProperty.Register(nameof(BodyColor), typeof(Color), typeof(DinoEasterEgg),
                new FrameworkPropertyMetadata(Color.FromRgb(0x53, 0x53, 0x53), FrameworkPropertyMetadataOptions.AffectsRender, OnBodyColorChanged));

        private static void OnBodyColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DinoEasterEgg dino && e.NewValue is Color newColor)
            {
                dino._cachedBodyBrush = CreateFrozenBrush(newColor);
            }
        }

        public Color BodyColor
        {
            get => (Color)GetValue(BodyColorProperty);
            set => SetValue(BodyColorProperty, value);
        }

        // ---------------------------------------------------------------
        // Piscada
        // ---------------------------------------------------------------
        private bool _olhoAberto = true;
        private readonly DispatcherTimer _timerProximaPiscada = new DispatcherTimer();
        private readonly DispatcherTimer _timerDuracaoPiscada = new DispatcherTimer();

        public DinoEasterEgg()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _timerProximaPiscada.Tick += (s, e) =>
            {
                _timerProximaPiscada.Stop();
                if (!IsVisible) return;
                _olhoAberto = false;
                InvalidateVisual();

                _timerDuracaoPiscada.Interval = TimeSpan.FromMilliseconds(120);
                _timerDuracaoPiscada.Start();
            };

            _timerDuracaoPiscada.Tick += (s, e) =>
            {
                _timerDuracaoPiscada.Stop();
                _olhoAberto = true;
                if (IsVisible)
                {
                    InvalidateVisual();
                    AgendarProximaPiscada();
                }
            };

            Loaded += (s, e) => { if (IsVisible) AgendarProximaPiscada(); };
            Unloaded += (s, e) =>
            {
                _timerProximaPiscada.Stop();
                _timerDuracaoPiscada.Stop();
            };

            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible)
                {
                    _olhoAberto = true;
                    AgendarProximaPiscada();
                }
                else
                {
                    _timerProximaPiscada.Stop();
                    _timerDuracaoPiscada.Stop();
                }
            };
        }

        private void AgendarProximaPiscada()
        {
            if (!IsVisible) return;
            // Intervalo aleatório entre 2s e 5s, pra não ficar mecânico
            var segundos = 2 + Random.Shared.NextDouble() * 3;
            _timerProximaPiscada.Interval = TimeSpan.FromSeconds(segundos);
            _timerProximaPiscada.Start();
        }

        /// <summary>Permite forçar uma piscada manualmente (ex: ao clicar).</summary>
        public void Piscar()
        {
            if (!IsVisible || !_olhoAberto) return;
            _timerProximaPiscada.Stop();
            _olhoAberto = false;
            InvalidateVisual();
            _timerDuracaoPiscada.Interval = TimeSpan.FromMilliseconds(120);
            _timerDuracaoPiscada.Start();
        }

        // ---------------------------------------------------------------
        // Layout
        // ---------------------------------------------------------------
        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(NativeSize * Scale, NativeSize * Scale);
        }

        // ---------------------------------------------------------------
        // Desenho
        // ---------------------------------------------------------------
        protected override void OnRender(DrawingContext dc)
        {
            dc.PushTransform(new ScaleTransform(Scale, Scale));

            dc.DrawGeometry(_cachedBodyBrush, null, BodyGeometry);

            // Enquanto o olho estiver "fechado" (piscando), pinta o buraco
            // do olho com a cor do corpo, escondendo-o visualmente.
            if (!_olhoAberto)
            {
                dc.DrawRectangle(_cachedBodyBrush, null, EyeHoleRect);
            }

            dc.Pop();
        }
    }
}