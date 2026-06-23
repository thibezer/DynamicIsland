using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using DynamicIslandWindows.Services;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow : Window, IDisposable
    {
        // ─── P/Invoke Windows 11 Nativo (Sem hacks antigos do Win10) ───────────────
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE  = 0x0002;
        private const uint SWP_NOSIZE  = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        public enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT            = 1,
            QUNS_BUSY                   = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE      = 4,
            QUNS_ACCEPTS_NOTIFICATIONS  = 5,
            QUNS_QUIET_TIME             = 6,
            QUNS_APP                    = 7
        }

        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SWP_HIDEWINDOW = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public int flags;
        }

        // ─── Constantes de Layout ─────────────────────────────────────────────────
        private const int  HOVER_OPEN_DELAY_MS   = 400;
        private const int  HOVER_CLOSE_DELAY_MS  = 300;
        private const int  DROPZONE_CLOSE_DELAY_MS = 3000;
        private const int  MEDIA_TIMER_S         = 10;
        private const int  DRAGLEAVE_DELAY_MS    = 150;

        // Dimensões físicas lógicas finais do widget (sincronizadas e sem saltos)
        private const double ISLAND_OPEN_W       = 410;
        private const double ISLAND_OPEN_H       = 400;
        private const double ISLAND_APPEARANCE_H = 300;
        private const double DROPZONE_W          = 320;
        private const double DROPZONE_H          = 200;

        // ─── Estado Interno ───────────────────────────────────────────────────────
        private DispatcherTimer? _openDelayTimer;
        private DispatcherTimer? _closeDelayTimer;
        private DispatcherTimer? _fullscreenTimer;
        private DispatcherTimer? _mediaTimer;
        private DispatcherTimer? _dragLeaveTimer;
        private DispatcherTimer? _wifiTransitionTimer;
        private bool _isWifiPanelTransitioning = false;
        private DispatcherTimer? _wifiAutoRefreshTimer;
        private PomodoroService? _pomodoroService;
        private DispatcherTimer? _pomodoroCloseTimer;

        private bool   _isIslandOpen       = false;
        private bool   _isDropZoneActive   = false;
        private bool   _isDraggingFile     = false;
        private string _currentMode        = "notification";
        private string? _pendingDropFilePath = null;
        private double _dpiX = 1.0, _dpiY = 1.0;
        private int _mediaUpdateInProgress = 0;
        private bool _disposed = false;
        private double _miniBaseHeightLogica = 43; 
        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private bool _isFullScreenCached = false;
        private bool _isInitializing = true;
        private double _cachedCompactWidth = -1;

        // Throttling de Brilho, Mute e Configuração
        private DateTime _lastBrightnessTime = DateTime.MinValue;
        private int _pendingBrightnessValue = -1;
        private bool _brightnessUpdatePending = false;
        private readonly object _brightnessLock = new();
        private IslandConfig _config = new IslandConfig();
        private Color _themeColor = Color.FromRgb(0xF6, 0xA7, 0x33);

        public class IslandConfig
        {
            public string ThemeColor { get; set; } = "#FFF6A733";
            public string BackgroundColor { get; set; } = "#FF000000";
            public string InactiveColor { get; set; } = "#FF1E1E20";
        }

        // Estados dos Toggles
        private bool _wifiOn = true;
        private bool _bluetoothOn = true;
        private bool _airplaneOn = false;
        private bool _accessibilityOn = false;
        private bool _batterySaverOn = false;
        private bool _pomodoroVisible = true;
        private bool _isPomodoroExpanded = false;
        private bool _nightLightOn = false;
        private bool _mobileHotspotOn = false;
        private bool _nearbyShareOn = false;
        private bool _castOn = false;
        private bool _projectOn = false;
        private HardwareService.WiFiNetworkInfo? _selectedWifiNetwork;

        // ─── Serviços ────────────────────────────────────────────────────────────
        private readonly ExcelService _excelService;
        private readonly HardwareService _hardwareService;
        private readonly MediaNotificationService _mediaService;

        // ─── Construtor ─────────────────────────────────────────────────────────
        public NativeIslandWindow()
        {
            InitializeComponent();

            _excelService = new ExcelService();
            _hardwareService = new HardwareService();
            _mediaService = new MediaNotificationService();
            
            _mediaService.StateChanged += MediaService_StateChanged;

            // Inicializa o serviço do Pomodoro de forma isolada
            try
            {
                _pomodoroService = new PomodoroService();
                _pomodoroService.Tick += PomodoroService_Tick;
                _pomodoroService.StateChanged += PomodoroService_StateChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NativeIslandWindow] Falha ao instanciar PomodoroService: {ex.Message}");
            }
        }

        private async void MediaService_StateChanged(object? sender, EventArgs e)
        {
            await UpdateMediaInfoAsync();
        }

        // ─── Eventos da Janela ───────────────────────────────────────────────────
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CacheDpi();

            // Seta a janela permanentemente com tamanho fixo grande (cobre o menu expandido completo)
            this.Width = 550;
            this.Height = 425;

            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left + 5;
            // Posiciona a base da Janela encaixando a pílula perfeitamente na barra de tarefas (canto inferior esquerdo da tela física)
            this.Top = SystemParameters.PrimaryScreenHeight - this.Height + 10 - (2.0 / _dpiY); 

            // Hook Win32 para remover barras nativas e fixar comportamento de ToolWindow
            SetupWindowHook();

            // Configuração física inicial da pílula compacta dentro da janela estática
            double startingWidth = GetDynamicCompactWidth();
            islandBorder.Width = startingWidth;
            islandBorder.Height = _miniBaseHeightLogica;

            // Carrega e aplica a configuração de cores
            LoadConfig();

            // Configura a inicialização automática com o Windows
            ConfigureStartup();

            // Sincronizar Hardware
            try
            {
                int vol = _hardwareService.GetCurrentVolume();
                int bri = _hardwareService.GetCurrentBrightness();
                
                brightnessSlider.Value = bri;
                volumeSlider.Value = vol;
                txtBrightnessVal.Text = $"{bri}%";
                txtVolumeVal.Text = $"{vol}%";
                UpdateMuteUI();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareInit] {ex.Message}");
            }

            _isInitializing = false;
            InitializeTogglesUI();
            _isFullScreenCached = IsFullScreen();
            SetupTimersAndEvents();

            await _mediaService.InitializeAsync();
            await UpdateMediaInfoAsync();

            _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MEDIA_TIMER_S) };
            _mediaTimer.Tick += async (s, ev) => await UpdateMediaInfoAsync();
            _mediaTimer.Start();
            StartIdleBreathingAnimation();
        }

        private void InitializeTogglesUI()
        {
            if (btnWifi != null && icoWifi != null)
            {
                UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
            }
            if (btnWifiMenu != null && icoWifiMenu != null)
            {
                UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
            }
            UpdateToggleButton(btnBluetooth, icoBluetooth, _bluetoothOn);
            UpdateToggleButton(btnAirplane, icoAirplane, _airplaneOn);
            UpdateToggleButton(btnAccessibility, icoAccessibility, _accessibilityOn);
            UpdateToggleButton(btnBatterySaver, icoBatterySaver, _batterySaverOn);
            UpdateToggleButton(btnPomodoroToggle, icoPomodoroToggle, _pomodoroVisible);
            UpdateToggleButton(btnNightLight, icoNightLight, _nightLightOn);
            UpdateToggleButton(btnMobileHotspot, icoMobileHotspot, _mobileHotspotOn);
            UpdateToggleButton(btnNearbyShare, icoNearbyShare, _nearbyShareOn);
            UpdateToggleButton(btnCast, icoCast, _castOn);
            UpdateToggleButton(btnProject, icoProject, _projectOn);

            if (btnFilePicker != null && icoFilePicker != null)
            {
                UpdateToggleButton(btnFilePicker, icoFilePicker, false);
            }
        }

        private void CacheDpi()
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                _dpiX = source.CompositionTarget.TransformToDevice.M11;
                _dpiY = source.CompositionTarget.TransformToDevice.M22;

                double barraFisica = (_dpiY > 1.1) ? 59.0 : 47.0;
                double pilulaFisica = barraFisica - 4.0;
                _miniBaseHeightLogica = pilulaFisica / _dpiY;
            }
        }

        // ─── Interação por Clique (Abertura Forçada) ────────────────────────
        private void IslandBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isIslandOpen && !_isDropZoneActive)
            {
                OpenIsland();
                e.Handled = true;
            }
        }

        // ─── Mecanismo de Hover Reativo e Tela Cheia sem Polling ───────────
        private void SetupTimersAndEvents()
        {
            _openDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HOVER_OPEN_DELAY_MS) };
            _openDelayTimer.Tick += OpenDelayTimer_Tick;

            _closeDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HOVER_CLOSE_DELAY_MS) };
            _closeDelayTimer.Tick += CloseDelayTimer_Tick;

            _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _fullscreenTimer.Tick += FullscreenTimer_Tick;
            _fullscreenTimer.Start();

            islandBorder.MouseEnter += IslandBorder_MouseEnter;
            islandBorder.MouseLeave += IslandBorder_MouseLeave;

            _pomodoroCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _pomodoroCloseTimer.Tick += PomodoroCloseTimer_Tick;
        }

        private void IslandBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isFullScreenCached) return;

            _closeDelayTimer?.Stop();

            if (!_isIslandOpen && _currentMode == "notification" && !_isDraggingFile && !_isDropZoneActive)
            {
                _openDelayTimer?.Stop();
                _openDelayTimer?.Start();
            }
        }

        private void IslandBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            _openDelayTimer?.Stop();

            // PROTEÇÃO: Se o painel Wi-Fi está em transição, não inicia o timer de fechamento
            if (_isWifiPanelTransitioning) return;

            if (_isIslandOpen || _isDropZoneActive)
            {
                _closeDelayTimer?.Stop();
                if (_closeDelayTimer != null)
                {
                    _closeDelayTimer.Interval = _isDropZoneActive 
                        ? TimeSpan.FromMilliseconds(DROPZONE_CLOSE_DELAY_MS) 
                        : TimeSpan.FromMilliseconds(HOVER_CLOSE_DELAY_MS);
                    _closeDelayTimer.Start();
                }
            }
        }

        private void OpenDelayTimer_Tick(object? sender, EventArgs e)
        {
            _openDelayTimer?.Stop();
            if (islandBorder.IsMouseOver && !_isFullScreenCached)
            {
                OpenIsland();
            }
        }

        private void CloseDelayTimer_Tick(object? sender, EventArgs e)
        {
            _closeDelayTimer?.Stop();
            if (!islandBorder.IsMouseOver)
            {
                if (_isIslandOpen) CloseIsland();
                if (_isDropZoneActive) DeactivateDropMode();
            }
        }

        private void FullscreenTimer_Tick(object? sender, EventArgs e)
        {
            bool wasFullScreen = _isFullScreenCached;
            _isFullScreenCached = IsFullScreen();

            if (_isFullScreenCached)
            {
                if (this.Visibility != Visibility.Collapsed)
                {
                    this.Visibility = Visibility.Collapsed;
                    if (_isIslandOpen) CloseIsland();
                    if (_isDropZoneActive) DeactivateDropMode();
                }
            }
            else
            {
                if (this.Visibility != Visibility.Visible)
                {
                    this.Visibility = Visibility.Visible;
                }
                EnsureTopmost();
            }
        }

        private double GetDynamicCompactWidth()
        {
            if (_cachedCompactWidth > 0) return _cachedCompactWidth;
            if (miniIslandPanel == null) return 340;
            miniIslandPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double idealWidth = miniIslandPanel.DesiredSize.Width + 30;
            _cachedCompactWidth = Math.Clamp(idealWidth, 200, 380);
            return _cachedCompactWidth;
        }

        // ─── Motor de Animação por Hardware Puro ──────────────────────────────────
        // Executado diretamente pela GPU, sem recalcular buffers de janela do SO.
        private void AnimateBorder(double targetWidth, double targetHeight, Action? onCompleted = null)
        {
            var widthSpring = new SpringEase
            {
                DampingRatio = 0.85,
                Response     = 0.55
            };

            var heightSpring = new SpringEase
            {
                DampingRatio = 0.85,
                Response     = 0.45  // response menor = mais rígida/rápida verticalmente (squash-and-stretch)
            };

            // WWDC 2025: Animação Ancorada na Fonte (Source-Anchored Animation)
            // Captura o tamanho visual real em tempo de execução para evitar saltos/jitter
            double currentWidth = islandBorder.RenderSize.Width;
            double currentHeight = islandBorder.RenderSize.Height;
            if (currentWidth <= 0) currentWidth = islandBorder.Width;
            if (currentHeight <= 0) currentHeight = islandBorder.Height;

            var widthAnim = new DoubleAnimation(currentWidth, targetWidth, widthSpring.GetSettleTime())
            {
                EasingFunction = widthSpring
            };

            var heightAnim = new DoubleAnimation(currentHeight, targetHeight, heightSpring.GetSettleTime())
            {
                EasingFunction = heightSpring
            };

            if (onCompleted != null)
            {
                heightAnim.Completed += (s, e) => onCompleted();
            }

            islandBorder.BeginAnimation(Border.WidthProperty,  widthAnim);
            islandBorder.BeginAnimation(Border.HeightProperty, heightAnim);
        }

        /// <summary>
        /// Anima a cor de qualquer SolidColorBrush em qualquer FrameworkElement.
        /// Cria o brush automaticamente caso o elemento use null ou não-SolidColorBrush.
        /// </summary>
        private static void AnimateColor(
            FrameworkElement element,
            DependencyProperty brushProperty,
            Color targetColor,
            TimeSpan duration)
        {
            if (element == null) return;

            // Garante que o brush é mutável (SolidColorBrush não-frozen)
            var currentBrush = element.GetValue(brushProperty) as SolidColorBrush;
            Color initialColor = (currentBrush != null && !currentBrush.IsFrozen)
                ? currentBrush.Color
                : (currentBrush?.Color ?? Colors.Transparent);

            if (currentBrush == null || currentBrush.IsFrozen)
            {
                var newBrush = new SolidColorBrush(initialColor);
                element.SetValue(brushProperty, newBrush);
                currentBrush = newBrush;
            }

            var anim = new ColorAnimation(targetColor, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private void OpenIsland()
        {
            EnsureTopmost();
            if (_isDropZoneActive) DeactivateDropMode();
            _isIslandOpen = true;

            StopIdleBreathingAnimation();

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            if (txtMiniTitle != null) txtMiniTitle.MaxWidth = 260;
            if (txtMiniSubtitle != null) txtMiniSubtitle.MaxWidth = 260;

            miniIslandPanel.Visibility = Visibility.Visible;
            islandSeparator.Visibility = Visibility.Visible;
            dropZonePanel.Visibility = Visibility.Collapsed;

            // Painel expandido entra com fade + slide APÓS delay para sincronizar com o border
            AnimatePanelIn(expandedIslandPanel, slideDistance: 20, delayMs: 100);

            // Mede a altura ideal dinamicamente para evitar cortes no conteúdo da pílula (Row 2)
            expandedIslandPanel.UpdateLayout();
            expandedIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double expandedHeight = expandedIslandPanel.DesiredSize.Height;

            miniIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double miniHeight = miniIslandPanel.DesiredSize.Height;

            // Altura total = expandedHeight + miniHeight + separador (6px) + padding (13px) = 19px
            double totalHeight = expandedHeight + miniHeight + 19;
            totalHeight = Math.Min(totalHeight, ISLAND_OPEN_H);

            AnimateBorder(ISLAND_OPEN_W, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Expanded);
        }

        private void CloseIsland()
        {
            _isIslandOpen = false;
            _isWifiPanelTransitioning = false; // Reseta a proteção ao fechar
            _wifiTransitionTimer?.Stop();
            StopWifiAutoRefresh();

            ApplyDepthHierarchy(IslandDepth.Compact);

            if (txtMiniTitle != null) txtMiniTitle.MaxWidth = 220;
            if (txtMiniSubtitle != null) txtMiniSubtitle.MaxWidth = 220;

            // Fade-out rápido dos painéis expandidos (não espera terminar)
            AnimatePanelOut(expandedIslandPanel);
            AnimatePanelOut(appearancePanel);
            AnimatePanelOut(wifiPanel);

            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            islandSeparator.Visibility = Visibility.Collapsed;
            dropZonePanel.Visibility = Visibility.Collapsed;
            miniIslandPanel.Visibility = Visibility.Visible;

            double dynamicWidth = GetDynamicCompactWidth();
            AnimateBorder(dynamicWidth, _miniBaseHeightLogica, () =>
            {
                if (mainContentGrid != null)
                {
                    mainContentGrid.VerticalAlignment = VerticalAlignment.Center;
                }
                StartIdleBreathingAnimation();
            });
        }

        // ─── Utilitários de Animação Avançada ────────────────────────────────────

        /// <summary>
        /// Faz um painel aparecer com fade + slide de baixo para cima (como iOS)
        /// </summary>
        private void AnimatePanelIn(FrameworkElement panel, double slideDistance = 15, int delayMs = 80)
        {
            panel.Visibility = Visibility.Visible;
            panel.Opacity = 0;
            
            // Configura TranslateTransform se não existir
            if (!(panel.RenderTransform is TranslateTransform))
                panel.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)panel.RenderTransform;
            translate.Y = slideDistance;
            
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            
            var slideUp = new DoubleAnimation(slideDistance, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            };
            
            panel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        /// <summary>
        /// Faz um painel desaparecer com fade-out rápido
        /// </summary>
        private void AnimatePanelOut(FrameworkElement panel, Action? onCompleted = null)
        {
            var fadeOut = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            
            fadeOut.Completed += (s, e) =>
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1; // Reset para próxima abertura
                onCompleted?.Invoke();
            };
            
            panel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        /// <summary>
        /// Faz uma pulsação sutil na ilha no modo compacto ocioso (breathing glow)
        /// </summary>
        private void StartIdleBreathingAnimation()
        {
            if (_isIslandOpen || _isDropZoneActive) return;

            var breathGlow = new DropShadowEffect
            {
                Color = _themeColor,
                ShadowDepth = 0,
                Opacity = 0,
                BlurRadius = 8
            };
            
            if (islandBorder.Effect == null)
            {
                islandBorder.Effect = breathGlow;
                
                var breathAnim = new DoubleAnimation(0, 0.15, TimeSpan.FromSeconds(3))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                breathGlow.BeginAnimation(DropShadowEffect.OpacityProperty, breathAnim);
            }
        }

        private void StopIdleBreathingAnimation()
        {
            if (islandBorder.Effect is DropShadowEffect glow && glow.Opacity < 0.2)
            {
                islandBorder.Effect = null;
            }
        }

        /// <summary>
        /// Faz um crossfade e scale suave na imagem de thumbnail do tocador de mídia
        /// </summary>
        private void AnimateThumbnailChange(BitmapImage newThumbnail)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fadeOut.Completed += (s, e) =>
            {
                miniThumbBorder.Background = new ImageBrush(newThumbnail) 
                { 
                    Stretch = Stretch.UniformToFill 
                };
                miniThumbBorder.Visibility = Visibility.Visible;
                txtMiniIcon.Visibility = Visibility.Collapsed;
                
                miniThumbBorder.RenderTransformOrigin = new Point(0.5, 0.5);
                miniThumbBorder.RenderTransform = new ScaleTransform(0.85, 0.85);
                
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var scaleX = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
                };
                var scaleY = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
                };
                
                miniThumbBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                ((ScaleTransform)miniThumbBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
                ((ScaleTransform)miniThumbBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            };
            
            miniThumbBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        /// <summary>
        /// Faz uma transição de texto com slide vertical elegante ao mudar títulos ou legendas
        /// </summary>
        private void AnimateTextChange(TextBlock textBlock, string newText)
        {
            if (textBlock.Text == newText) return;
            
            if (!(textBlock.RenderTransform is TranslateTransform))
                textBlock.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)textBlock.RenderTransform;
            
            // Fase 1: Slide-out + fade-out do texto atual
            var slideOut = new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(100));
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            
            fadeOut.Completed += (s, e) =>
            {
                textBlock.Text = newText;
                translate.Y = 8; // Posiciona abaixo
                
                // Fase 2: Slide-in + fade-in do novo texto
                var slideIn = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                
                translate.BeginAnimation(TranslateTransform.YProperty, slideIn);
                textBlock.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            
            translate.BeginAnimation(TranslateTransform.YProperty, slideOut);
            textBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void ActivateDropMode()
        {
            EnsureTopmost();
            if (_isDropZoneActive) return;
            _isDraggingFile = _isDropZoneActive = true;
        }

        private void DeactivateDropMode()
        {
            _isDraggingFile = _isDropZoneActive = false;
            _dragLeaveTimer?.Stop();
            _pendingDropFilePath = null;

            if (islandBorder != null)
            {
                islandBorder.Effect = null;
                islandBorder.BorderThickness = new Thickness(0);
            }

            CloseIsland();
        }

        private void ShowDropPanel()
        {
            EnsureTopmost();
            miniIslandPanel.Visibility = Visibility.Collapsed;
            expandedIslandPanel.Visibility = Visibility.Collapsed;
            dropZonePanel.Visibility = Visibility.Visible;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            if (islandBorder != null)
            {
                var dropShadow = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x4C, 0xC2, 0xFF),
                    ShadowDepth = 0,
                    Opacity = 1.0,
                    BlurRadius = 25
                };
                islandBorder.Effect = dropShadow;
                islandBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

                DoubleAnimation blurAnim = new DoubleAnimation(15, 35, new Duration(TimeSpan.FromSeconds(1.5))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                dropShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            }

            AnimateBorder(DROPZONE_W, DROPZONE_H);
        }

        // ─── Sliders ──────────────────────────────────────────────────────────────
        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hardwareService == null || txtBrightnessVal == null) return;
            int val = (int)e.NewValue;
            txtBrightnessVal.Text = $"{val}%";
            SetBrightnessThrottled(val);
        }

        private void SetBrightnessThrottled(int value)
        {
            lock (_brightnessLock)
            {
                _pendingBrightnessValue = value;
                if (_brightnessUpdatePending) return;

                var elapsed = DateTime.UtcNow - _lastBrightnessTime;
                if (elapsed.TotalMilliseconds >= 150)
                {
                    ExecuteBrightnessUpdate();
                }
                else
                {
                    _brightnessUpdatePending = true;
                    int delay = 150 - (int)elapsed.TotalMilliseconds;
                    Task.Delay(delay).ContinueWith(t => ExecuteBrightnessUpdate());
                }
            }
        }

        private void ExecuteBrightnessUpdate()
        {
            int value;
            lock (_brightnessLock)
            {
                value = _pendingBrightnessValue;
                _brightnessUpdatePending = false;
                _lastBrightnessTime = DateTime.UtcNow;
            }
            _hardwareService.SetBrightness(value);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hardwareService == null || txtVolumeVal == null) return;
            int val = (int)e.NewValue;
            txtVolumeVal.Text = $"{val}%";
            _hardwareService.SetVolume(val);
            UpdateMuteUI(); // Sincroniza o ícone (desmuta ao deslizar se necessário)
        }

        // ─── Toggles Click ────────────────────────────────────────────────────────
        private async void Toggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Border border)) return;
            string action = border.Tag?.ToString() ?? "";

            switch (action)
            {
                case "wifi":
                    _wifiOn = !_wifiOn;
                    UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
                    if (btnWifiMenu != null && icoWifiMenu != null)
                    {
                        UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
                    }
                    await _hardwareService.HandleToggleAsync("wifi", _wifiOn);
                    break;
                case "bluetooth":
                    _bluetoothOn = !_bluetoothOn; UpdateToggleButton(border, icoBluetooth, _bluetoothOn);
                    await _hardwareService.HandleToggleAsync("bluetooth", _bluetoothOn); break;
                case "airplane":
                    _airplaneOn = !_airplaneOn; UpdateToggleButton(border, icoAirplane, _airplaneOn);
                    await _hardwareService.HandleToggleAsync("airplane", _airplaneOn); break;
                case "accessibility":
                    _accessibilityOn = !_accessibilityOn; UpdateToggleButton(border, icoAccessibility, _accessibilityOn);
                    await _hardwareService.HandleToggleAsync("accessibility", _accessibilityOn); break;
                case "battery_saver":
                    _batterySaverOn = !_batterySaverOn; UpdateToggleButton(border, icoBatterySaver, _batterySaverOn);
                    await _hardwareService.HandleToggleAsync("battery_saver", _batterySaverOn); break;
                case "pomodoro_toggle":
                    _pomodoroVisible = !_pomodoroVisible;
                    UpdateToggleButton(border, icoPomodoroToggle, _pomodoroVisible);
                    pomodoroBorder.Visibility = _pomodoroVisible ? Visibility.Visible : Visibility.Collapsed;
                    UpdatePhysicalWindowWidth();
                    break;
                case "night_light":
                    _nightLightOn = !_nightLightOn; UpdateToggleButton(border, icoNightLight, _nightLightOn);
                    await _hardwareService.HandleToggleAsync("night_light", _nightLightOn); break;
                case "mobile_hotspot":
                    _mobileHotspotOn = !_mobileHotspotOn; UpdateToggleButton(border, icoMobileHotspot, _mobileHotspotOn);
                    await _hardwareService.HandleToggleAsync("mobile_hotspot", _mobileHotspotOn); break;
                case "nearby_share":
                    _nearbyShareOn = !_nearbyShareOn; UpdateToggleButton(border, icoNearbyShare, _nearbyShareOn);
                    await _hardwareService.HandleToggleAsync("nearby_share", _nearbyShareOn); break;
                case "cast":
                    _castOn = !_castOn; UpdateToggleButton(border, icoCast, _castOn);
                    await _hardwareService.HandleToggleAsync("cast", _castOn); break;
                case "project":
                    _projectOn = !_projectOn; UpdateToggleButton(border, icoProject, _projectOn);
                    await _hardwareService.HandleToggleAsync("project", _projectOn); break;
                case "file_picker":
                    CloseIsland(); OpenManualFilePicker(); break;
            }
        }

        private void UpdateToggleButton(Border border, TextBlock icon, bool isOn)
        {
            Color targetBg;
            Color targetIcon;
            
            if (isOn)
            {
                targetBg = _themeColor;
                double brightness = (0.299 * _themeColor.R + 0.587 * _themeColor.G + 0.114 * _themeColor.B) / 255;
                targetIcon = brightness > 0.6 ? Colors.Black : Colors.White;
            }
            else
            {
                var brush = this.Resources["IslandInactiveBrush"] as SolidColorBrush;
                targetBg = brush?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20);
                targetIcon = Colors.White;
            }

            var duration = TimeSpan.FromMilliseconds(250);

            // Anima fundo
            AnimateColor(border, Border.BackgroundProperty, targetBg, duration);

            // Anima ícone
            AnimateColor(icon, TextBlock.ForegroundProperty, targetIcon, duration);

            // Anima borda
            Color targetBorder = isOn ? _themeColor : Colors.Transparent;
            AnimateColor(border, Border.BorderBrushProperty, targetBorder, duration);
        }

        private void OpenManualFilePicker()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Selecionar ficheiro para Dynamic Island", Filter = "Todos os ficheiros (*.*)|*.*" };
                if (dlg.ShowDialog(this) != true) return;

                _pendingDropFilePath = dlg.FileName;
                txtDropFileName.Text = Path.GetFileName(dlg.FileName);
                ShowDropPanel();
            }
            catch (Exception ex) { Debug.WriteLine($"[FilePicker] {ex.Message}"); }
        }

        // ─── Drag & Drop NATIVO PERFEITO (Focado apenas no Border) ──────────────────
        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Link; ActivateDropMode(); }
            else { e.Effects = DragDropEffects.None; }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Link;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (_dragLeaveTimer == null)
            {
                _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DRAGLEAVE_DELAY_MS) };
                _dragLeaveTimer.Tick += DragLeaveTimer_Tick;
            }
            _dragLeaveTimer.Start();
            e.Handled = true;
        }

        private void DragLeaveTimer_Tick(object? sender, EventArgs e)
        {
            _dragLeaveTimer!.Stop();
            Point mousePos = Mouse.GetPosition(this);

            // Se saiu da área física do Border visível, fecha a zona de drop
            bool inside = mousePos.X >= 10 &&
                          mousePos.X <= 10 + islandBorder.ActualWidth &&
                          mousePos.Y >= (this.Height - 10 - islandBorder.ActualHeight) &&
                          mousePos.Y <= this.Height - 10;

            if (!inside) DeactivateDropMode();
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files?.Length > 0)
                {
                    _pendingDropFilePath = files[0];
                    txtDropFileName.Text = Path.GetFileName(files[0]);
                    ShowDropPanel();
                }
            }
            _isDraggingFile = false; e.Handled = true;
        }

        private void ExcelAction_Click(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingDropFilePath))
            {
                _excelService.SendFileToExcel(_pendingDropFilePath); _pendingDropFilePath = null;
            }
            DeactivateDropMode();
        }

        private void CancelDrop_Click(object sender, MouseButtonEventArgs e)
        {
            _pendingDropFilePath = null; DeactivateDropMode();
        }

        private void OpenSettings_Click(object sender, MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"[Settings] {ex.Message}"); }
        }

        // ─── Atualização Sincronizada de Mídia ─────────────────────────────────────
        private async Task UpdateMediaInfoAsync()
        {
            if (Interlocked.CompareExchange(ref _mediaUpdateInProgress, 1, 0) != 0)
                return;

            try
            {
                var state = await _mediaService.GetCurrentStateAsync();
                _currentMode = state.Mode;

                BitmapImage? decodedThumbnail = null;
                if (state.Mode == "music" && !string.IsNullOrEmpty(state.Thumbnail))
                {
                    try
                    {
                        decodedThumbnail = await Task.Run(() =>
                        {
                            byte[] binaryData = Convert.FromBase64String(state.Thumbnail);
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = new MemoryStream(binaryData);
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            bitmap.EndInit();
                            bitmap.Freeze(); // Permite uso cross-thread no WPF
                            return bitmap;
                        });
                    }
                    catch (Exception imgEx)
                    {
                        Debug.WriteLine($"[ImageDecodeBackgroundError] {imgEx.Message}");
                    }
                }

                this.Dispatcher.Invoke(() =>
                {
                    // Verifica se o conteúdo compacto mudou para invalidar o cache da largura
                    bool contentChanged = false;
                    if (txtMiniTitle.Text != state.Title) contentChanged = true;
                    if (txtMiniSubtitle.Text != state.Subtitle) contentChanged = true;

                    string expectedPlayGlyph = state.IsPlaying ? "\uE769" : "\uE768";
                    if (state.Mode == "music")
                    {
                        if (miniMediaControls.Visibility != Visibility.Visible) contentChanged = true;
                        if (txtMiniPlayIcon.Text != expectedPlayGlyph) contentChanged = true;
                        
                        bool hasThumb = decodedThumbnail != null;
                        if (hasThumb && miniThumbBorder.Visibility != Visibility.Visible) contentChanged = true;
                        if (!hasThumb && txtMiniIcon.Visibility != Visibility.Visible) contentChanged = true;
                    }
                    else
                    {
                        if (miniMediaControls.Visibility != Visibility.Collapsed) contentChanged = true;
                        if (txtMiniIcon.Text != "\uE990" || txtMiniIcon.Visibility != Visibility.Visible) contentChanged = true;
                    }

                    if (contentChanged)
                    {
                        _cachedCompactWidth = -1;
                    }

                    if (state.Mode == "music")
                    {
                        miniMediaControls.Visibility = Visibility.Visible;
                        
                        AnimateTextChange(txtMiniTitle, state.Title);
                        AnimateTextChange(txtMiniSubtitle, state.Subtitle);
                        txtMiniPlayIcon.Text = expectedPlayGlyph;

                        if (decodedThumbnail != null)
                        {
                            try
                            {
                                if (miniThumbBorder.Background is ImageBrush brush && brush.ImageSource is BitmapImage currentImg && currentImg == decodedThumbnail)
                                {
                                    // Mesmo thumbnail, nada a fazer
                                }
                                else
                                {
                                    AnimateThumbnailChange(decodedThumbnail);
                                }
                            }
                            catch (Exception imgEx)
                            {
                                Debug.WriteLine($"[ImageLoadError] {imgEx.Message}");
                                UseDefaultMediaIcon();
                            }
                        }
                        else
                        {
                            UseDefaultMediaIcon();
                        }
                    }
                    else
                    {
                        miniMediaControls.Visibility = Visibility.Collapsed;
                        AnimateTextChange(txtMiniTitle, state.Title);
                        AnimateTextChange(txtMiniSubtitle, state.Subtitle);

                        miniThumbBorder.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
                        miniThumbBorder.Visibility = Visibility.Collapsed;
                        txtMiniIcon.Visibility = Visibility.Visible;
                        txtMiniIcon.Text = "\uE990"; 
                    }

                    if (!_isIslandOpen && !_isDropZoneActive)
                    {
                        double targetW = GetDynamicCompactWidth();
                        AnimateBorder(targetW, _miniBaseHeightLogica);
                    }
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[UpdateMedia] {ex.Message}"); }
            finally { Interlocked.Exchange(ref _mediaUpdateInProgress, 0); }
        }

        private void UseDefaultMediaIcon()
        {
            miniThumbBorder.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            miniThumbBorder.Visibility = Visibility.Collapsed;
            txtMiniIcon.Visibility = Visibility.Visible;
            txtMiniIcon.Text = "\uE958";
        }

        // ─── Controles de Mídia ──────────────────────────────────────────────────
        private async void PlayPause_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync("playPause"); await UpdateMediaInfoAsync(); }

        private async void Next_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync("next"); await UpdateMediaInfoAsync(); }

        private async void Previous_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync("previous"); await UpdateMediaInfoAsync(); }

        private async void Shuffle_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync("shuffle"); await UpdateMediaInfoAsync(); }

        private async void Repeat_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync("repeat"); await UpdateMediaInfoAsync(); }

        // ─── Hooks do Win32 contra Win+D ──────────────────────────────────────────
        private void SetupWindowHook()
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyWindowStyles();

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);

            EnsureTopmost();

            this.StateChanged += (s, e) =>
            {
                if (this.WindowState == WindowState.Minimized && this.Visibility == Visibility.Visible)
                {
                    this.WindowState = WindowState.Normal; EnsureTopmost();
                }
            };
        }

        private void ApplyWindowStyles()
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                    exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[ApplyWindowStyles] {ex.Message}"); }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
            {
                var obj = Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                if (obj != null)
                {
                    WINDOWPOS wp = (WINDOWPOS)obj;
                    bool modified = false;

                    if ((wp.flags & SWP_HIDEWINDOW) != 0 && this.Visibility == Visibility.Visible)
                    {
                        wp.flags &= ~SWP_HIDEWINDOW; wp.flags |= SWP_SHOWWINDOW;
                        modified = true;
                    }

                    if (this.Visibility == Visibility.Visible && wp.hwndInsertAfter != HWND_TOPMOST)
                    {
                        wp.hwndInsertAfter = HWND_TOPMOST;
                        modified = true;
                    }

                    if (modified)
                    {
                        Marshal.StructureToPtr(wp, lParam, false);
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        private void EnsureTopmost()
        {
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private bool IsFullScreen()
        {
            try
            {
                SHQueryUserNotificationState(out var state);
                return state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                             or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
            }
            catch { return false; }
        }

        // ─── LÓGICA DE MUDO E APARÊNCIA DINÂMICA ──────────────────────────────
        private void MuteToggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (_hardwareService == null) return;
            e.Handled = true;
            _hardwareService.ToggleMute();
            UpdateMuteUI();
        }

        private void UpdateMuteUI()
        {
            if (_hardwareService == null || txtVolumeIcon == null) return;
            bool isMuted = _hardwareService.IsMuted();
            txtVolumeIcon.Text = isMuted ? "\uE74F" : "\uE995";
            txtVolumeIcon.Opacity = isMuted ? 0.4 : 0.8;
        }

        private void AppearanceSettings_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            
            // Torna o painel visível temporariamente com opacidade zero para o Measure funcionar
            appearancePanel.Opacity = 0;
            appearancePanel.Visibility = Visibility.Visible;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            // Mede a altura ideal com base no conteúdo para evitar espaço sobrando no topo ou cortes
            appearancePanel.UpdateLayout();
            appearancePanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double appearanceHeight = appearancePanel.DesiredSize.Height;

            miniIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double miniHeight = miniIslandPanel.DesiredSize.Height;

            // Altura total = altura do painel de cores + pílula compacta + separador (6px) + padding (13px) = 19px + 4px (Detail Depth)
            double totalHeight = appearanceHeight + miniHeight + 19 + 4.0;

            // Limita a altura para não exceder o limite máximo da ilha
            totalHeight = Math.Min(totalHeight, ISLAND_OPEN_H);

            // Fade-out do painel atual, fade-in do próximo
            AnimatePanelOut(expandedIslandPanel, () =>
            {
                AnimatePanelIn(appearancePanel, slideDistance: 12, delayMs: 0);
            });

            AnimateBorder(ISLAND_OPEN_W + 4.0, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Detail);
        }

        private void BackToMainPanel_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            
            _isWifiPanelTransitioning = false; // NOVO: Remove proteção ao sair do WiFi
            _wifiTransitionTimer?.Stop();       // NOVO
            StopWifiAutoRefresh();             // NOVO
            
            // Fade-out dos sub-painéis, fade-in do painel principal
            AnimatePanelOut(appearancePanel);
            AnimatePanelOut(wifiPanel);
            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            
            AnimatePanelIn(expandedIslandPanel, slideDistance: 12, delayMs: 100);

            if (btnWifi != null && icoWifi != null)
            {
                UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
            }
            if (btnWifiMenu != null && icoWifiMenu != null)
            {
                UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
            }

            // Mede a altura ideal dinamicamente para evitar cortes no conteúdo da pílula (Row 2)
            expandedIslandPanel.UpdateLayout();
            expandedIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double expandedHeight = expandedIslandPanel.DesiredSize.Height;

            miniIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double miniHeight = miniIslandPanel.DesiredSize.Height;

            double totalHeight = expandedHeight + miniHeight + 19;
            totalHeight = Math.Min(totalHeight, ISLAND_OPEN_H);

            AnimateBorder(ISLAND_OPEN_W, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Expanded);
        }

        private void SelectColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                e.Handled = true;
                string hexColor = border.Tag?.ToString() ?? "#FFF6A733";
                ApplyThemeColor(hexColor);
                SaveConfig();
            }
        }

        private void SelectBgColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                e.Handled = true;
                string hexColor = border.Tag?.ToString() ?? "#FF000000";
                ApplyBgColor(hexColor);
                SaveConfig();
            }
        }

        private void SelectInactColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                e.Handled = true;
                string hexColor = border.Tag?.ToString() ?? "#FF1E1E20";
                ApplyInactiveColor(hexColor);
                SaveConfig();
            }
        }

        private void ApplyAccentHex_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            string hex = txtAccentHex.Text.Trim();
            if (IsValidHexColor(hex))
            {
                ApplyThemeColor(hex);
                SaveConfig();
            }
            else
            {
                MessageBox.Show("Código hexadecimal de cor inválido. Ex: #FFF6A733", "Erro de Cor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyBgHex_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            string hex = txtBgHex.Text.Trim();
            if (IsValidHexColor(hex))
            {
                ApplyBgColor(hex);
                SaveConfig();
            }
            else
            {
                MessageBox.Show("Código hexadecimal de cor inválido. Ex: #FF000000", "Erro de Cor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyInactHex_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            string hex = txtInactHex.Text.Trim();
            if (IsValidHexColor(hex))
            {
                ApplyInactiveColor(hex);
                SaveConfig();
            }
            else
            {
                MessageBox.Show("Código hexadecimal de cor inválido. Ex: #FF1E1E20", "Erro de Cor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private bool IsValidHexColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex[0] != '#') return false;
            if (hex.Length != 7 && hex.Length != 9) return false;
            
            for (int i = 1; i < hex.Length; i++)
            {
                char c = hex[i];
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'A' && c <= 'F') ||
                      (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private string GetConfigPath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIslandWindows");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "island_config.json");
        }

        private void LoadConfig()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<IslandConfig>(json);
                    if (cfg != null)
                    {
                        _config = cfg;
                        ApplyThemeColor(_config.ThemeColor);
                        ApplyBgColor(_config.BackgroundColor);
                        ApplyInactiveColor(_config.InactiveColor);
                        return;
                    }
                }
                ApplyThemeColor("#FFF6A733");
                ApplyBgColor("#FF000000");
                ApplyInactiveColor("#FF1E1E20");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadConfig] {ex.Message}");
                ApplyThemeColor("#FFF6A733");
                ApplyBgColor("#FF000000");
                ApplyInactiveColor("#FF1E1E20");
            }
        }

        private void SaveConfig()
        {
            try
            {
                string path = GetConfigPath();
                string json = JsonSerializer.Serialize(_config);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveConfig] {ex.Message}");
            }
        }

        private void ConfigureStartup()
        {
            try
            {
                string keyName = "DynamicIsland";
                string? exePath = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(exePath))
                {
                    using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null)
                        {
                            string value = $"\"{exePath}\"";
                            object? currentValue = key.GetValue(keyName);
                            if (currentValue == null || currentValue.ToString() != value)
                            {
                                key.SetValue(keyName, value);
                                Debug.WriteLine($"[Startup] Registro de inicialização atualizado para: {value}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] Erro ao configurar inicialização: {ex.Message}");
            }
        }

        private void ApplyThemeColor(string hexColor)
        {
            try
            {
                _config.ThemeColor = hexColor;
                var colorObj = ColorConverter.ConvertFromString(hexColor);
                if (colorObj != null)
                {
                    _themeColor = (Color)colorObj;
                    this.Resources["ThemeAccentBrush"] = new SolidColorBrush(_themeColor);
                    
                    var trackColor = Color.FromRgb(_themeColor.R, _themeColor.G, _themeColor.B);
                    this.Resources["ThemeAccentTrackBrush"] = new SolidColorBrush(trackColor);

                    txtAccentHex.Text = hexColor;
                    UpdateColorSelectionUI(hexColor);
                    if (!_isInitializing) InitializeTogglesUI();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyThemeColor] {ex.Message}");
            }
        }

        private void ApplyBgColor(string hexColor)
        {
            try
            {
                _config.BackgroundColor = hexColor;
                var colorObj = ColorConverter.ConvertFromString(hexColor);
                if (colorObj != null)
                {
                    var color = (Color)colorObj;
                    this.Resources["IslandBackgroundBrush"] = new SolidColorBrush(color);
                    txtBgHex.Text = hexColor;
                    UpdateBgSelectionUI(hexColor);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyBgColor] {ex.Message}");
            }
        }

        private void ApplyInactiveColor(string hexColor)
        {
            try
            {
                _config.InactiveColor = hexColor;
                var colorObj = ColorConverter.ConvertFromString(hexColor);
                if (colorObj != null)
                {
                    var color = (Color)colorObj;
                    this.Resources["IslandInactiveBrush"] = new SolidColorBrush(color);
                    txtInactHex.Text = hexColor;
                    UpdateInactSelectionUI(hexColor);
                    if (!_isInitializing) InitializeTogglesUI();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyInactiveColor] {ex.Message}");
            }
        }

        private void UpdateColorSelectionUI(string hexColor)
        {
            if (colorCoral == null) return;
            colorCoral.BorderThickness = new Thickness(0);
            colorBlue.BorderThickness = new Thickness(0);
            colorPurple.BorderThickness = new Thickness(0);
            colorGreen.BorderThickness = new Thickness(0);
            colorRed.BorderThickness = new Thickness(0);

            var selectedBrush = Brushes.White;
            var thick = new Thickness(2);

            if (hexColor.Equals("#FFF6A733", StringComparison.OrdinalIgnoreCase)) { colorCoral.BorderBrush = selectedBrush; colorCoral.BorderThickness = thick; }
            else if (hexColor.Equals("#FF0078D4", StringComparison.OrdinalIgnoreCase)) { colorBlue.BorderBrush = selectedBrush; colorBlue.BorderThickness = thick; }
            else if (hexColor.Equals("#FF8660A9", StringComparison.OrdinalIgnoreCase)) { colorPurple.BorderBrush = selectedBrush; colorPurple.BorderThickness = thick; }
            else if (hexColor.Equals("#FF107C41", StringComparison.OrdinalIgnoreCase)) { colorGreen.BorderBrush = selectedBrush; colorGreen.BorderThickness = thick; }
            else if (hexColor.Equals("#FFE81123", StringComparison.OrdinalIgnoreCase)) { colorRed.BorderBrush = selectedBrush; colorRed.BorderThickness = thick; }
        }

        private void UpdateBgSelectionUI(string hexColor)
        {
            if (bgBlack == null) return;
            bgBlack.BorderThickness = new Thickness(0);
            bgDarkGray.BorderThickness = new Thickness(0);
            bgBlue.BorderThickness = new Thickness(0);
            bgPurple.BorderThickness = new Thickness(0);
            bgGreen.BorderThickness = new Thickness(0);

            var selectedBrush = Brushes.White;
            var thick = new Thickness(2);

            if (hexColor.Equals("#FF000000", StringComparison.OrdinalIgnoreCase)) { bgBlack.BorderBrush = selectedBrush; bgBlack.BorderThickness = thick; }
            else if (hexColor.Equals("#FF1A1A1A", StringComparison.OrdinalIgnoreCase)) { bgDarkGray.BorderBrush = selectedBrush; bgDarkGray.BorderThickness = thick; }
            else if (hexColor.Equals("#FF0D1B2A", StringComparison.OrdinalIgnoreCase)) { bgBlue.BorderBrush = selectedBrush; bgBlue.BorderThickness = thick; }
            else if (hexColor.Equals("#FF1E152A", StringComparison.OrdinalIgnoreCase)) { bgPurple.BorderBrush = selectedBrush; bgPurple.BorderThickness = thick; }
            else if (hexColor.Equals("#FF0A1C16", StringComparison.OrdinalIgnoreCase)) { bgGreen.BorderBrush = selectedBrush; bgGreen.BorderThickness = thick; }
        }

        private void UpdateInactSelectionUI(string hexColor)
        {
            if (inactGray == null) return;
            inactGray.BorderThickness = new Thickness(0);
            inactDark.BorderThickness = new Thickness(0);
            inactBlue.BorderThickness = new Thickness(0);
            inactPurple.BorderThickness = new Thickness(0);
            inactGreen.BorderThickness = new Thickness(0);

            var selectedBrush = Brushes.White;
            var thick = new Thickness(2);

            if (hexColor.Equals("#FF1E1E20", StringComparison.OrdinalIgnoreCase)) { inactGray.BorderBrush = selectedBrush; inactGray.BorderThickness = thick; }
            else if (hexColor.Equals("#FF141416", StringComparison.OrdinalIgnoreCase)) { inactDark.BorderBrush = selectedBrush; inactDark.BorderThickness = thick; }
            else if (hexColor.Equals("#FF1B2A47", StringComparison.OrdinalIgnoreCase)) { inactBlue.BorderBrush = selectedBrush; inactBlue.BorderThickness = thick; }
            else if (hexColor.Equals("#FF322348", StringComparison.OrdinalIgnoreCase)) { inactPurple.BorderBrush = selectedBrush; inactPurple.BorderThickness = thick; }
            else if (hexColor.Equals("#FF142D24", StringComparison.OrdinalIgnoreCase)) { inactGreen.BorderBrush = selectedBrush; inactGreen.BorderThickness = thick; }
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _openDelayTimer?.Stop();
            _closeDelayTimer?.Stop();
            _fullscreenTimer?.Stop();
            _mediaTimer?.Stop();
            _dragLeaveTimer?.Stop();
            _wifiTransitionTimer?.Stop();
            _wifiAutoRefreshTimer?.Stop();
            _openDelayTimer = _closeDelayTimer = _fullscreenTimer = _mediaTimer = _dragLeaveTimer = _wifiTransitionTimer = _wifiAutoRefreshTimer = null;

            if (_mediaService != null)
            {
                _mediaService.StateChanged -= MediaService_StateChanged;
                _mediaService.Dispose();
            }

            if (_hardwareService != null)
            {
                _hardwareService.Dispose();
            }

            if (_pomodoroService != null)
            {
                try
                {
                    _pomodoroService.Tick -= PomodoroService_Tick;
                    _pomodoroService.StateChanged -= PomodoroService_StateChanged;
                    _pomodoroService.Dispose();
                }
                catch { }
                _pomodoroService = null;
            }

            if (_pomodoroCloseTimer != null)
            {
                try { _pomodoroCloseTimer.Stop(); } catch { }
                _pomodoroCloseTimer = null;
            }

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(WndProc); _hwndSource.Dispose(); } catch { }
                _hwndSource = null;
            }
            GC.SuppressFinalize(this);
        }

        protected override void OnClosed(EventArgs e) { Dispose(); base.OnClosed(e); }

        private void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_hwnd != IntPtr.Zero)
            {
                try
                {
                    int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                    exStyle &= ~WS_EX_NOACTIVATE;
                    SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
                    this.Activate();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GotFocus] {ex.Message}");
                }
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_hwnd != IntPtr.Zero)
            {
                try
                {
                    int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                    exStyle |= WS_EX_NOACTIVATE;
                    SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LostFocus] {ex.Message}");
                }
            }
        }

        // ─── LÓGICA DO MENU DE WI-FI INTERNO ──────────────────────────────────────────
        private void UpdateWifiToggleHeaderUI()
        {
            if (btnWifiToggleHeader == null || wifiSwitchIndicator == null) return;
            
            // Configura TranslateTransform se necessário
            if (!(wifiSwitchIndicator.RenderTransform is TranslateTransform))
                wifiSwitchIndicator.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)wifiSwitchIndicator.RenderTransform;
            
            // A pílula tem 44px de largura, o indicador 16px, margem 4px
            // Posição esquerda: 0, Posição direita: 20px
            double targetX = _wifiOn ? 20 : 0;
            Color targetBg = _wifiOn ? _themeColor : 
                ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color 
                 ?? Color.FromRgb(0x1E, 0x1E, 0x20));
            
            // Desliza com spring suave
            var slideAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
            
            // Anima a cor de fundo do switch container
            if (!(btnWifiToggleHeader.Background is SolidColorBrush switchBg) || switchBg.IsFrozen)
            {
                switchBg = new SolidColorBrush(
                    (btnWifiToggleHeader.Background as SolidColorBrush)?.Color ?? Colors.Gray);
                btnWifiToggleHeader.Background = switchBg;
            }
            switchBg.BeginAnimation(SolidColorBrush.ColorProperty, 
                new ColorAnimation(targetBg, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            
            // Fixa o indicador à esquerda para que TranslateTransform funcione corretamente
            wifiSwitchIndicator.HorizontalAlignment = HorizontalAlignment.Left;
        }

        private async void WifiToggleHeader_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _wifiOn = !_wifiOn;
            UpdateWifiToggleHeaderUI();
            
            // Liga/Desliga o rádio
            await _hardwareService.HandleToggleAsync("wifi", _wifiOn);
            
            // Atualiza a lista
            _ = RefreshWifiListAsync();
        }

        private async Task RefreshWifiListAsync()
        {
            try
            {
                if (wifiListContainer == null) return;
                wifiListContainer.Children.Clear();

                if (!_wifiOn)
                {
                    var txtOff = new TextBlock
                    {
                        Style = this.Resources["FluentTextSec"] as Style,
                        Text = "O Wi-Fi está desativado.",
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    };
                    wifiListContainer.Children.Add(txtOff);
                    AdjustWifiPanelHeight();
                    return;
                }

                var txtScanning = new TextBlock
                {
                    Style = this.Resources["FluentTextSec"] as Style,
                    Text = "Procurando redes...",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                wifiListContainer.Children.Add(txtScanning);
                AdjustWifiPanelHeight();

                var networks = await _hardwareService.GetAvailableNetworksAsync();

                wifiListContainer.Children.Clear();

                if (networks == null || networks.Count == 0)
                {
                    var txtNone = new TextBlock
                    {
                        Style = this.Resources["FluentTextSec"] as Style,
                        Text = "Nenhuma rede encontrada.",
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    };
                    wifiListContainer.Children.Add(txtNone);
                    AdjustWifiPanelHeight();
                    return;
                }

                RenderWifiNetworks(networks);
                AdjustWifiPanelHeight();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RefreshWifiListError] {ex.Message}");
            }
        }

        private void RenderWifiNetworks(System.Collections.Generic.List<HardwareService.WiFiNetworkInfo> networks)
        {
            if (wifiListContainer == null) return;

            string? connectedSsid = _hardwareService.GetConnectedNetworkSsid();

            // Ordenar: conectada primeiro, depois por força de sinal
            var sorted = networks
                .OrderByDescending(n => n.Ssid == connectedSsid)
                .ThenByDescending(n => n.SignalBars)
                .ToList();

            int index = 0;
            foreach (var net in sorted)
            {
                bool isConnected = net.Ssid == connectedSsid;
                var normalBrushColor = isConnected
                    ? Color.FromArgb(0x33, _themeColor.R, _themeColor.G, _themeColor.B)
                    : ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20));

                var itemBorder = new Border
                {
                    Background = new SolidColorBrush(normalBrushColor),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderBrush = isConnected
                        ? new SolidColorBrush(Color.FromArgb(0x66, _themeColor.R, _themeColor.G, _themeColor.B))
                        : Brushes.Transparent,
                    BorderThickness = isConnected ? new Thickness(1) : new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = net
                };

                itemBorder.MouseEnter += (s, ev) => 
                {
                    var hoverColor = Color.FromRgb(0x2D, 0x2D, 0x30);
                    if (!(itemBorder.Background is SolidColorBrush bg) || bg.IsFrozen)
                    {
                        bg = new SolidColorBrush((itemBorder.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
                        itemBorder.Background = bg;
                    }
                    bg.BeginAnimation(SolidColorBrush.ColorProperty, 
                        new ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(100))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                };

                itemBorder.MouseLeave += (s, ev) => 
                {
                    bool isConn = ((HardwareService.WiFiNetworkInfo)itemBorder.Tag).Ssid == connectedSsid;
                    var normColor = isConn 
                        ? Color.FromArgb(0x33, _themeColor.R, _themeColor.G, _themeColor.B)
                        : ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20));
                    
                    if (itemBorder.Background is SolidColorBrush bg && !bg.IsFrozen)
                    {
                        bg.BeginAnimation(SolidColorBrush.ColorProperty, 
                            new ColorAnimation(normColor, TimeSpan.FromMilliseconds(150))
                            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    }
                };
                
                itemBorder.MouseLeftButtonDown += WifiNetworkItem_Click;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                
                var txtSsid = new TextBlock
                {
                    Style = this.Resources["FluentText"] as Style,
                    Text = net.Ssid,
                    FontSize = 12,
                    FontWeight = isConnected ? FontWeights.Bold : FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                textStack.Children.Add(txtSsid);

                if (isConnected)
                {
                    var connLabel = new TextBlock
                    {
                        Style = this.Resources["FluentTextSec"] as Style,
                        Text = "Conectado",
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(_themeColor),
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    textStack.Children.Add(connLabel);
                }

                Grid.SetColumn(textStack, 0);
                grid.Children.Add(textStack);

                bool isSecure = !net.SecurityKind.Equals("None", StringComparison.OrdinalIgnoreCase) && 
                                !net.SecurityKind.Equals("Open", StringComparison.OrdinalIgnoreCase);
                if (isSecure)
                {
                    var txtLock = new TextBlock
                    {
                        Style = this.Resources["FluentIcon"] as Style,
                        Text = "\uE72E",
                        FontSize = 12,
                        Foreground = Brushes.LightGray,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 8, 0)
                    };
                    Grid.SetColumn(txtLock, 1);
                    grid.Children.Add(txtLock);
                }

                var signalIndicator = CreateSignalBarsIndicator(net.SignalBars);
                Grid.SetColumn(signalIndicator, 2);
                grid.Children.Add(signalIndicator);

                itemBorder.Child = grid;
                
                // Adiciona com animação escalonada
                AddNetworkItemWithAnimation(itemBorder, index++);
            }
        }

        private UIElement CreateSignalBarsIndicator(int bars)
        {
            var panel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0) 
            };
            
            Color[] barColors = bars switch
            {
                >= 3 => new[] { 
                    Color.FromRgb(0x10, 0x7C, 0x41), // Verde
                    Color.FromRgb(0x10, 0x7C, 0x41), 
                    Color.FromRgb(0x10, 0x7C, 0x41), 
                    Color.FromRgb(0x10, 0x7C, 0x41) 
                },
                2 => new[] { 
                    Color.FromRgb(0xF6, 0xA7, 0x33), // Amarelo/Accent
                    Color.FromRgb(0xF6, 0xA7, 0x33), 
                    Color.FromRgb(0x3A, 0x3A, 0x3C), // Inativo
                    Color.FromRgb(0x3A, 0x3A, 0x3C) 
                },
                _ => new[] { 
                    Color.FromRgb(0xE8, 0x11, 0x23), // Vermelho
                    Color.FromRgb(0x3A, 0x3A, 0x3C), 
                    Color.FromRgb(0x3A, 0x3A, 0x3C), 
                    Color.FromRgb(0x3A, 0x3A, 0x3C) 
                }
            };
            
            int[] heights = { 6, 10, 14, 18 };
            for (int i = 0; i < 4; i++)
            {
                panel.Children.Add(new Border
                {
                    Width = 3,
                    Height = heights[i],
                    CornerRadius = new CornerRadius(1.5),
                    Background = new SolidColorBrush(i < bars ? barColors[i] : Color.FromRgb(0x3A, 0x3A, 0x3C)),
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                });
            }
            return panel;
        }

        private void AddNetworkItemWithAnimation(UIElement item, int index)
        {
            item.Opacity = 0;
            item.RenderTransform = new TranslateTransform(0, 15);
            wifiListContainer.Children.Add(item);
            
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(index * 50), // Escalonamento
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            var slideUp = new DoubleAnimation(15, 0, TimeSpan.FromMilliseconds(250))
            {
                BeginTime = TimeSpan.FromMilliseconds(index * 50),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            item.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ((TranslateTransform)item.RenderTransform).BeginAnimation(
                TranslateTransform.YProperty, slideUp);
        }

        private void WifiNetworkItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border item && item.Tag is HardwareService.WiFiNetworkInfo net)
            {
                e.Handled = true;
                _selectedWifiNetwork = net;

                bool isSecure = !net.SecurityKind.Equals("None", StringComparison.OrdinalIgnoreCase) && 
                                !net.SecurityKind.Equals("Open", StringComparison.OrdinalIgnoreCase);
                
                if (!isSecure)
                {
                    _ = ConnectToWifiNetwork(net, "");
                }
                else
                {
                    txtWifiPasswordPrompt.Text = $"Introduza a palavra-passe para \"{net.Ssid}\"";
                    txtWifiPassword.Text = "";
                    wifiPasswordPanel.Visibility = Visibility.Visible;
                    txtWifiPassword.Focus();
                }
            }
        }

        private void CancelWifiConnection_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            _selectedWifiNetwork = null;
            AdjustWifiPanelHeight();
        }

        private void ConnectWifi_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_selectedWifiNetwork == null) return;

            string password = txtWifiPassword.Text;
            wifiPasswordPanel.Visibility = Visibility.Collapsed;

            _ = ConnectToWifiNetwork(_selectedWifiNetwork, password);
        }

        private async Task ConnectToWifiNetwork(HardwareService.WiFiNetworkInfo net, string password)
        {
            if (wifiListContainer == null) return;

            wifiListContainer.Children.Clear();
            wifiListContainer.Children.Add(CreateConnectionSpinner(net.Ssid));
            AdjustWifiPanelHeight();

            bool success = false;
            if (net.RawNetwork != null)
            {
                success = await _hardwareService.ConnectToNetworkAsync(net.RawNetwork, password);
            }

            wifiListContainer.Children.Clear();
            wifiListContainer.Children.Add(CreateConnectionResult(net.Ssid, success));
            AdjustWifiPanelHeight();

            await Task.Delay(3000);

            _ = RefreshWifiListAsync();
        }

        /// <summary>
        /// Cria um spinner circular animado para indicar progresso de conexão.
        /// </summary>
        private UIElement CreateConnectionSpinner(string ssid)
        {
            var stack = new StackPanel 
            { 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 0) 
            };
            
            // Anel giratório usando um Arc/Ellipse com animação de rotação
            var spinner = new Border
            {
                Width = 24, Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(_themeColor),
                BorderThickness = new Thickness(2.5),
                Opacity = 0.8,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                // Clip para criar efeito de arco parcial
                Clip = new RectangleGeometry(new Rect(0, 0, 24, 12))
            };
            
            var rotation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            ((RotateTransform)spinner.RenderTransform).BeginAnimation(
                RotateTransform.AngleProperty, rotation);
            
            stack.Children.Add(spinner);
            stack.Children.Add(new TextBlock
            {
                Style = this.Resources["FluentTextSec"] as Style,
                Text = $"Conectando a \"{ssid}\"...",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
            
            return stack;
        }

        /// <summary>
        /// Cria um indicador de sucesso (✓ verde) ou erro (✗ vermelho) com animação.
        /// </summary>
        private UIElement CreateConnectionResult(string ssid, bool success)
        {
            var stack = new StackPanel 
            { 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 25, 0, 0) 
            };
            
            var iconBorder = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(success 
                    ? Color.FromArgb(0x33, 0x10, 0x7C, 0x41) 
                    : Color.FromArgb(0x33, 0xE8, 0x11, 0x23)),
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.5, 0.5)
            };
            
            iconBorder.Child = new TextBlock
            {
                Style = this.Resources["FluentIcon"] as Style,
                Text = success ? "\uE73E" : "\uE711",  // Check ou X
                FontSize = 16,
                Foreground = new SolidColorBrush(success 
                    ? Color.FromRgb(0x10, 0x7C, 0x41) 
                    : Color.FromRgb(0xE8, 0x11, 0x23)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            
            // Animação de escala: pop-in
            var scaleX = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
            var scaleY = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
            
            ((ScaleTransform)iconBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            ((ScaleTransform)iconBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            
            stack.Children.Add(iconBorder);
            stack.Children.Add(new TextBlock
            {
                Style = this.Resources["FluentText"] as Style,
                Text = success ? $"Conectado a \"{ssid}\"" : $"Falha ao conectar a \"{ssid}\"",
                FontSize = 11,
                Foreground = new SolidColorBrush(success 
                    ? Color.FromRgb(0x10, 0x7C, 0x41) 
                    : Color.FromRgb(0xE8, 0x11, 0x23)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
            
            return stack;
        }

        private void WifiMenuOpen_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            // Torna o painel visível temporariamente com opacidade zero para o Measure funcionar
            wifiPanel.Opacity = 0;
            wifiPanel.Visibility = Visibility.Visible;

            AnimatePanelOut(expandedIslandPanel, () =>
            {
                AnimatePanelIn(wifiPanel, slideDistance: 12, delayMs: 0);
            });

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            // ATIVA PROTEÇÃO: Impede o fechamento por MouseLeave durante 3 segundos
            ActivateWifiTransitionProtection();

            // Inicia a auto-atualização periódica das redes Wi-Fi
            StartWifiAutoRefresh();

            UpdateWifiToggleHeaderUI();
            _ = RefreshWifiListAsync();
        }

        /// <summary>
        /// Ativa uma proteção temporária de 3 segundos que impede o fechamento da ilha
        /// por MouseLeave. Necessário porque a transição para o painel Wi-Fi redimensiona
        /// o islandBorder, fazendo o mouse ficar fora da área e disparando MouseLeave.
        /// </summary>
        private void ActivateWifiTransitionProtection()
        {
            // Para o timer de fechamento que possa estar ativo
            _closeDelayTimer?.Stop();
            
            _isWifiPanelTransitioning = true;

            // Para timer anterior se estiver rodando (caso de cliques rápidos)
            _wifiTransitionTimer?.Stop();

            if (_wifiTransitionTimer == null)
            {
                _wifiTransitionTimer = new DispatcherTimer 
                { 
                    Interval = TimeSpan.FromMilliseconds(3000) 
                };
                _wifiTransitionTimer.Tick += (s, ev) =>
                {
                    _wifiTransitionTimer?.Stop();
                    _isWifiPanelTransitioning = false;
                    
                    // Se o mouse já saiu da ilha durante a proteção, agora sim inicia o fechamento
                    if (!islandBorder.IsMouseOver && _isIslandOpen)
                    {
                        _closeDelayTimer?.Stop();
                        if (_closeDelayTimer != null)
                        {
                            _closeDelayTimer.Interval = TimeSpan.FromMilliseconds(HOVER_CLOSE_DELAY_MS);
                            _closeDelayTimer.Start();
                        }
                    }
                };
            }
            _wifiTransitionTimer.Start();
        }

        // Iniciar quando o painel Wi-Fi abre:
        private void StartWifiAutoRefresh()
        {
            _wifiAutoRefreshTimer?.Stop();
            _wifiAutoRefreshTimer = new DispatcherTimer 
            { 
                Interval = TimeSpan.FromSeconds(15) // Refresh a cada 15 segundos
            };
            _wifiAutoRefreshTimer.Tick += async (s, ev) =>
            {
                if (wifiPanel.Visibility == Visibility.Visible && _wifiOn)
                {
                    await RefreshWifiListAsync();
                }
                else
                {
                    _wifiAutoRefreshTimer?.Stop();
                }
            };
            _wifiAutoRefreshTimer.Start();
        }

        // Parar quando o painel Wi-Fi fecha:
        private void StopWifiAutoRefresh()
        {
            _wifiAutoRefreshTimer?.Stop();
            _wifiAutoRefreshTimer = null;
        }

        private async void WifiRefresh_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            
            // Animação de rotação no ícone de refresh
            if (icoWifiRefresh != null)
            {
                var rotation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                ((RotateTransform)icoWifiRefresh.RenderTransform)
                    .BeginAnimation(RotateTransform.AngleProperty, rotation);
            }
            
            await RefreshWifiListAsync();
        }

        private void AdjustWifiPanelHeight()
        {
            if (wifiPanel == null || miniIslandPanel == null) return;

            // Mede a altura do painel com base no conteúdo
            wifiPanel.UpdateLayout();
            wifiPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double wifiHeight = wifiPanel.DesiredSize.Height;

            miniIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double miniHeight = miniIslandPanel.DesiredSize.Height;

            // Altura final = wifiHeight + miniHeight + separador (6px) + padding (13px) = 19px + 4px (Detail Depth)
            double totalHeight = wifiHeight + miniHeight + 19 + 4.0;

            // Limita ao tamanho máximo do painel aberto
            totalHeight = Math.Min(totalHeight, ISLAND_OPEN_H);

            AnimateBorder(ISLAND_OPEN_W + 4.0, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Detail);
        }

        private void PomodoroService_Tick(object? sender, PomodoroTickEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    int mins = e.RemainingSeconds / 60;
                    int secs = e.RemainingSeconds % 60;
                    string formattedTime = $"{mins:D2}:{secs:D2}";
                    
                    txtPomodoroTime.Text = formattedTime;
                    txtPomodoroTimeExpanded.Text = formattedTime;

                    if (_pomodoroService != null)
                    {
                        icoPomodoroPlayPause.Text = _pomodoroService.IsRunning ? "\uE769" : "\uE768";

                        if (!_pomodoroService.IsRunning && _pomodoroService.CurrentState != PomodoroState.Stopped)
                        {
                            pomodoroBorder.Opacity = 0.6;
                        }
                        else
                        {
                            pomodoroBorder.Opacity = 1.0;
                        }
                    }

                    UpdatePomodoroVisual(e.State, e.RemainingSeconds);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PomodoroUI_Tick] Erro: {ex.Message}");
                }
            });
        }

        private void PomodoroService_StateChanged(object? sender, PomodoroStateChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    Debug.WriteLine($"[PomodoroUI_State] Estado mudou para: {e.NewState}");
                    if (_pomodoroService != null)
                    {
                        icoPomodoroPlayPause.Text = _pomodoroService.IsRunning ? "\uE769" : "\uE768";
                        UpdatePomodoroVisual(e.NewState, _pomodoroService.RemainingSeconds);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PomodoroUI_StateChanged] Erro: {ex.Message}");
                }
            });
        }

        private void UpdatePomodoroVisual(PomodoroState state, int remainingSeconds)
        {
            try
            {
                if (state == PomodoroState.Stopped)
                {
                    pomodoroBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1E1E20"));
                    return;
                }

                // Degradê vertical (de cima para baixo: preto -> cor do estado)
                LinearGradientBrush brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };

                // Topo escuro comum para todos os estados
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF0E0E10"), 0.0));
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF151518"), 0.45));

                if (state == PomodoroState.Working)
                {
                    // Base azul
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF0078D4"), 1.0));
                }
                else if (state == PomodoroState.OnBreak)
                {
                    if (remainingSeconds <= 30)
                    {
                        // Base vermelha crítica
                        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFE81123"), 1.0));

                        if (remainingSeconds % 2 == 0)
                        {
                            pomodoroBorder.Opacity = 0.8;
                        }
                        else
                        {
                            pomodoroBorder.Opacity = 1.0;
                        }
                    }
                    else
                    {
                        // Base verde
                        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF2EA043"), 1.0));
                    }
                }

                pomodoroBorder.Background = brush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdatePomodoroVisual] Erro ao aplicar visual: {ex.Message}");
                pomodoroBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1E1E20"));
            }
        }

        private void UpdatePhysicalWindowWidth()
        {
            if (!_pomodoroVisible)
            {
                this.Width = 430;
            }
            else
            {
                this.Width = _isPomodoroExpanded ? 620 : 550;
            }
        }

        private void TogglePomodoroExpansion()
        {
            if (_pomodoroService == null) return;

            var duration = TimeSpan.FromMilliseconds(250);
            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (!_isPomodoroExpanded)
            {
                _isPomodoroExpanded = true;
                UpdatePhysicalWindowWidth();

                txtWorkTimeInput.Text = $"{_pomodoroService.WorkTimeMinutes:D2}:00";
                txtBreakTimeInput.Text = $"{_pomodoroService.BreakTimeMinutes:D2}:00";

                pomodoroCompactGrid.Visibility = Visibility.Collapsed;
                pomodoroExpandedGrid.Visibility = Visibility.Visible;

                var animWidth = new DoubleAnimation(90, 170, duration) { EasingFunction = easing };
                pomodoroBorder.BeginAnimation(WidthProperty, animWidth);

                var animHeight = new DoubleAnimation(43, 130, duration) { EasingFunction = easing };
                pomodoroBorder.BeginAnimation(HeightProperty, animHeight);

                pomodoroBorder.CornerRadius = new CornerRadius(20);

                var animFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                pomodoroExpandedGrid.BeginAnimation(OpacityProperty, animFade);
            }
            else
            {
                _isPomodoroExpanded = false;

                pomodoroExpandedGrid.BeginAnimation(OpacityProperty, null);
                pomodoroExpandedGrid.Opacity = 0;
                pomodoroExpandedGrid.Visibility = Visibility.Collapsed;
                
                pomodoroCompactGrid.Visibility = Visibility.Visible;
                pomodoroCompactGrid.Opacity = 1;

                var animWidth = new DoubleAnimation(170, 90, duration) { EasingFunction = easing };
                animWidth.Completed += (s, e) =>
                {
                    UpdatePhysicalWindowWidth();
                };
                pomodoroBorder.BeginAnimation(WidthProperty, animWidth);

                var animHeight = new DoubleAnimation(130, 43, duration) { EasingFunction = easing };
                pomodoroBorder.BeginAnimation(HeightProperty, animHeight);

                pomodoroBorder.CornerRadius = new CornerRadius(21.5);
            }
        }

        private void TxtWorkTimeInput_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox_LostFocus(sender, e);
            ApplyWorkTime();
            if (!pomodoroBorder.IsMouseOver)
            {
                _pomodoroCloseTimer?.Stop();
                _pomodoroCloseTimer?.Start();
            }
        }

        private void TxtBreakTimeInput_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox_LostFocus(sender, e);
            ApplyBreakTime();
            if (!pomodoroBorder.IsMouseOver)
            {
                _pomodoroCloseTimer?.Stop();
                _pomodoroCloseTimer?.Start();
            }
        }

        private void TxtTimeInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private int ParseInputTimeToMinutes(string input, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;
            try
            {
                input = input.Trim();
                if (input.Contains(":"))
                {
                    string[] parts = input.Split(':');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int mins))
                    {
                        return mins;
                    }
                }
                else
                {
                    if (int.TryParse(input, out int mins))
                    {
                        return mins;
                    }
                }
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private void ApplyWorkTime()
        {
            if (_pomodoroService == null) return;
            int currentMinutes = _pomodoroService.WorkTimeMinutes;
            int minutes = ParseInputTimeToMinutes(txtWorkTimeInput.Text, currentMinutes);
            
            if (minutes > 0 && minutes <= 180)
            {
                _pomodoroService.WorkTimeMinutes = minutes;
                txtWorkTimeInput.Text = $"{minutes:D2}:00";
            }
            else
            {
                txtWorkTimeInput.Text = $"{currentMinutes:D2}:00";
            }
        }

        private void ApplyBreakTime()
        {
            if (_pomodoroService == null) return;
            int currentMinutes = _pomodoroService.BreakTimeMinutes;
            int minutes = ParseInputTimeToMinutes(txtBreakTimeInput.Text, currentMinutes);
            
            if (minutes > 0 && minutes <= 60)
            {
                _pomodoroService.BreakTimeMinutes = minutes;
                txtBreakTimeInput.Text = $"{minutes:D2}:00";
            }
            else
            {
                txtBreakTimeInput.Text = $"{currentMinutes:D2}:00";
            }
        }

        private void PomodoroBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                _pomodoroService?.Toggle();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickLeft] Erro: {ex.Message}");
            }
        }

        private void PomodoroBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                TogglePomodoroExpansion();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickRight] Erro: {ex.Message}");
            }
        }

        private void PomodoroBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isPomodoroExpanded)
            {
                _pomodoroCloseTimer?.Stop();
                _pomodoroCloseTimer?.Start();
            }
        }

        private void PomodoroCloseTimer_Tick(object? sender, EventArgs e)
        {
            _pomodoroCloseTimer?.Stop();
            if (_isPomodoroExpanded && !pomodoroBorder.IsMouseOver)
            {
                // Limpa o foco do teclado de forma segura para disparar o LostFocus e aplicar as configurações
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
                Keyboard.ClearFocus();

                TogglePomodoroExpansion();
            }
        }

        private void PomodoroPlayPause_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                _pomodoroService?.Toggle();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickPlayPause] Erro: {ex.Message}");
            }
        }

        private void PomodoroReset_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                _pomodoroService?.Reset();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickReset] Erro: {ex.Message}");
            }
        }
        /// <summary>
        /// Aplica os ajustes de hierarquia de profundidade ao island.
        /// Chamado quando o estado muda para um subnível (ex: expandido → detalhe) de acordo com WWDC 2025.
        /// </summary>
        private void ApplyDepthHierarchy(IslandDepth depth)
        {
            double targetOpacity = depth switch
            {
                IslandDepth.Compact  => 0.90,
                IslandDepth.Expanded => 0.92,
                IslandDepth.Detail   => 0.96,  // mais opaco = mais "presente"
                _                    => 0.90
            };

            islandBorder.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    public enum IslandDepth { Compact, Expanded, Detail }
}