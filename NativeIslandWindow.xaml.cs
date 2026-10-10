using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST    = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOZORDER   = 0x0004;
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
        private const int WM_DISPLAYCHANGE     = 0x007E;
        private const int WM_SETTINGCHANGE     = 0x001A;
        private const int WM_DPICHANGED        = 0x02E0;

        private const uint SPI_GETWORKAREA     = 0x0030;
        private const uint SPI_SETWORKAREA     = 0x002F;
        private const int  SM_CXSCREEN         = 0;
        private const int  SM_CYSCREEN         = 1;

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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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
        private CancellationTokenSource? _displayChangeCts;

        private bool _dinoEasterEggActive = false;

        private bool   _isIslandOpen       = false;
        private bool   _isDropZoneActive   = false;
        private bool   _isDraggingFile     = false;
        private MediaMode _currentMode     = MediaMode.Notification;
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
        private bool _suppressVolumeWrite = false;
        private bool _isDraggingSlider = false;
        private Slider? _activeDraggingSlider = null;

        // Configuração
        private IslandConfig _config = new IslandConfig();
        private Color _themeColor = Color.FromRgb(0xF6, 0xA7, 0x33);

        public class IslandConfig
        {
            public string ThemeColor { get; set; } = "#FFF6A733";
            public string BackgroundColor { get; set; } = "#FF000000";
            public string InactiveColor { get; set; } = "#FF1E1E20";
            /// <summary>Registra o app na inicialização do Windows (HKCU\...\Run). false remove o registro.</summary>
            public bool StartWithWindows { get; set; } = true;
        }

        // Estados dos Toggles (apenas os que refletem um estado real; os demais só abrem as Configurações)
        private bool _wifiOn = true;
        private bool _bluetoothOn = true;
        private bool _airplaneOn = false;
        private bool _pomodoroVisible = true;
        private bool _isPomodoroExpanded = false;
        private bool _nightLightOn = false;
        private HardwareService.WiFiNetworkInfo? _selectedWifiNetwork;

        // ─── Serviços ────────────────────────────────────────────────────────────
        private readonly ExcelService _excelService;
        private readonly HardwareService _hardwareService;
        private readonly MediaNotificationService _mediaService;
        private AppHotkeyService? _hotkeyService;
        private DispatcherTimer? _hotkeyNotificationTimer;

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
                Debug.WriteLine($"[PomodoroInit] {ex.Message}");
            }

            SetupTimersAndEvents();
            
            Loaded += Window_Loaded;
        }

        // ─── Ciclo de Vida e Inicialização ───────────────────────────────────────
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                SetupWindowHook();
                LoadConfig();
                ConfigureStartup(_config.StartWithWindows);
                SetupAppHotkeys();

                RecalculateDimensions();
                UpdateWindowPositionAndSize();

                SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
                NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged;

                // Garante que a pílula compacta comece na largura adaptativa exata
                double startingWidth = GetDynamicCompactWidth();
                islandBorder.Width = startingWidth;
                islandBorder.Height = _miniBaseHeightLogica;

                // Inicializa hardware e sliders (leitura fora da thread de UI: WMI pode demorar)
                int vol = _hardwareService.GetCurrentVolume();
                int bri = await Task.Run(() => _hardwareService.GetCurrentBrightness());

                brightnessSlider.Value = bri;
                volumeSlider.Value = vol;
                txtBrightnessVal.Text = $"{bri}%";
                txtVolumeVal.Text = $"{vol}%";
                UpdateMuteUI();

                // Sincroniza estado real dos rádios (Wi-Fi e Bluetooth) com o Windows
                _wifiOn = await _hardwareService.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.WiFi) ?? true;
                _bluetoothOn = await _hardwareService.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth) ?? true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareInit] {ex.Message}");
            }

            // Sempre libera os sliders/toggles, mesmo que alguma leitura de hardware tenha falhado
            _isInitializing = false;

            try
            {
                InitializeTogglesUI();

                // Inicia o timer do Pomodoro com o estado inicial
                if (_pomodoroService != null)
                {
                    txtPomodoroTime.Text = $"{_pomodoroService.RemainingSeconds / 60:D2}:{_pomodoroService.RemainingSeconds % 60:D2}";
                    txtPomodoroTimeExpanded.Text = txtPomodoroTime.Text;
                }

                // Mídia/notificações por último: o pedido de acesso às notificações pode demorar
                // e não deve atrasar a inicialização de sliders e toggles
                await _mediaService.InitializeAsync();
                await UpdateMediaInfoAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaInit] {ex.Message}");
            }
        }

        // ─── Atalhos por Aplicativo ───────────────────────────────────────────────
        private void SetupAppHotkeys()
        {
            try
            {
                // O hook de teclado de baixo nível precisa ser instalado na thread de UI (com loop de mensagens)
                _hotkeyService = new AppHotkeyService();
                _hotkeyService.HotkeyTriggered += HotkeyService_HotkeyTriggered;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyInit] {ex.Message}");
            }
        }

        private void HotkeyService_HotkeyTriggered(object? sender, AppHotkeyTriggeredEventArgs e)
        {
            // Chamado de dentro do hook de teclado: nada de trabalho pesado aqui, só agenda na UI
            var rule = e.Rule;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                if (rule.ActionType.Equals("ShowNotification", StringComparison.OrdinalIgnoreCase))
                {
                    ShowHotkeyNotification(rule.ActionParameter);
                }
                else if (rule.ActionType.Equals("ExecuteCommand", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(rule.ActionParameter))
                {
                    try { Process.Start(new ProcessStartInfo(rule.ActionParameter) { UseShellExecute = true }); }
                    catch (Exception ex) { Debug.WriteLine($"[HotkeyExecute] {ex.Message}"); }
                }
                // "InjectText" já é tratado pelo próprio serviço
            }));
        }

        private void ShowHotkeyNotification(string text)
        {
            if (_isIslandOpen || _isDropZoneActive || string.IsNullOrEmpty(text)) return;

            AnimateTextChange(txtMiniTitle, text);
            AnimateTextChange(txtMiniSubtitle, "Atalho");
            _cachedCompactWidth = CalculateCompactWidth(text, "Atalho", miniMediaControls.Visibility == Visibility.Visible);
            AnimateBorder(_cachedCompactWidth, _miniBaseHeightLogica);

            // Após alguns segundos, volta a exibir a mídia/notificação atual
            _hotkeyNotificationTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _hotkeyNotificationTimer.Stop();
            _hotkeyNotificationTimer.Tick -= HotkeyNotificationTimer_Tick;
            _hotkeyNotificationTimer.Tick += HotkeyNotificationTimer_Tick;
            _hotkeyNotificationTimer.Start();
        }

        private async void HotkeyNotificationTimer_Tick(object? sender, EventArgs e)
        {
            _hotkeyNotificationTimer?.Stop();
            _cachedCompactWidth = -1;
            await UpdateMediaInfoAsync();
        }

        private void DisposeHotkeys()
        {
            _hotkeyNotificationTimer?.Stop();
            _hotkeyNotificationTimer = null;
            if (_hotkeyService != null)
            {
                _hotkeyService.HotkeyTriggered -= HotkeyService_HotkeyTriggered;
                _hotkeyService.Dispose();
                _hotkeyService = null;
            }
        }

        private void InitializeTogglesUI()
        {
            UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
            if (btnWifiMenu != null && icoWifiMenu != null)
            {
                UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
            }
            UpdateToggleButton(btnBluetooth, icoBluetooth, _bluetoothOn);
            UpdateToggleButton(btnAirplane, icoAirplane, _airplaneOn);
            UpdateToggleButton(btnPomodoroToggle, icoPomodoroToggle, _pomodoroVisible);
            UpdateToggleButton(btnNightLight, icoNightLight, _nightLightOn);

            // Atalhos para as Configurações: sempre no visual "desligado"
            UpdateToggleButton(btnAccessibility, icoAccessibility, false);
            UpdateToggleButton(btnBatterySaver, icoBatterySaver, false);
            UpdateToggleButton(btnMobileHotspot, icoMobileHotspot, false);
            UpdateToggleButton(btnNearbyShare, icoNearbyShare, false);
            UpdateToggleButton(btnCast, icoCast, false);
            UpdateToggleButton(btnProject, icoProject, false);
        }

        // ─── Posicionamento e Resolução de Tela ──────────────────────────────────
        private RECT GetScreenWorkArea()
        {
            RECT workArea = new RECT();
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0);
            return workArea;
        }

        private void RecalculateDimensions()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiX = dpi.DpiScaleX;
            _dpiY = dpi.DpiScaleY;

            double barraFisica = (_dpiY > 1.1) ? 59.0 : 47.0;
            double pilulaFisica = barraFisica - 4.0;
            _miniBaseHeightLogica = pilulaFisica / _dpiY;
        }

        private void UpdateWindowPositionAndSize()
        {
            try
            {
                var workArea = SystemParameters.WorkArea;
                this.Left = workArea.Left + 5;
                this.Top = SystemParameters.PrimaryScreenHeight - this.Height + 10 - (2.0 / _dpiY);

                UpdatePhysicalWindowWidth();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Position] {ex.Message}");
            }
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            OnDisplayOrScreenChanged();
        }

        private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            if (_disposed) return;
            // Assíncrono: este evento vem de thread de background e Invoke pode travar no encerramento
            Dispatcher.BeginInvoke(new Action(UpdateDinoState));
        }

        private void OnDisplayOrScreenChanged()
        {
            _displayChangeCts?.Cancel();
            _displayChangeCts?.Dispose();
            _displayChangeCts = new CancellationTokenSource();
            var token = _displayChangeCts.Token;

            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(200, token);
                    if (token.IsCancellationRequested) return;

                    RecalculateDimensions();
                    UpdateWindowPositionAndSize();
                    EnsureTopmost();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DisplayChange] Erro ao reposicionar janela: {ex.Message}");
                }
            });
        }

        // ─── Interação por Clique (Abertura Forçada) e Scroll de Volume na Pílula ──
        private void IslandBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            EnsureTopmost();
            if (!_isIslandOpen && !_isDropZoneActive)
            {
                OpenIsland();
                e.Handled = true;
            }
        }

        private void IslandBorder_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSlider && _activeDraggingSlider != null)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    UpdateSliderFromPoint(_activeDraggingSlider, e.GetPosition(_activeDraggingSlider));
                    e.Handled = true;
                }
                else
                {
                    _isDraggingSlider = false;
                    if (_activeDraggingSlider.IsMouseCaptured)
                    {
                        _activeDraggingSlider.ReleaseMouseCapture();
                    }
                    _activeDraggingSlider = null;
                }
            }
        }

        private void IslandBorder_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingSlider && _activeDraggingSlider != null)
            {
                _isDraggingSlider = false;
                if (_activeDraggingSlider.IsMouseCaptured)
                {
                    _activeDraggingSlider.ReleaseMouseCapture();
                }
                _activeDraggingSlider = null;
                e.Handled = true;
            }
        }

        private void IslandBorder_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Se o evento foi originado sobre um Slider, o Slider_PreviewMouseWheel já trata
            if (e.OriginalSource is DependencyObject dep && (dep is Slider || FindVisualParent<Slider>(dep) != null))
            {
                return;
            }

            // Quando sobre a pílula compacta ou tocando música, rolar ajusta o volume do sistema
            if (_currentMode == MediaMode.Music || !_isIslandOpen)
            {
                if (_hardwareService != null)
                {
                    int currentVol = _hardwareService.GetCurrentVolume();
                    int step = (e.Delta > 0) ? 2 : -2;
                    int newVol = Math.Clamp(currentVol + step, 0, 100);

                    _hardwareService.SetVolume(newVol);

                    if (volumeSlider != null)
                    {
                        // Já aplicado acima; evita que o ValueChanged do slider grave de novo
                        _suppressVolumeWrite = true;
                        try { volumeSlider.Value = newVol; }
                        finally { _suppressVolumeWrite = false; }
                    }
                    if (txtVolumeVal != null)
                    {
                        txtVolumeVal.Text = $"{newVol}%";
                    }
                    UpdateMuteUI();
                    e.Handled = true;
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
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

            EnsureTopmost();
            _closeDelayTimer?.Stop();

            if (!_isIslandOpen && _currentMode == MediaMode.Notification && !_isDraggingFile && !_isDropZoneActive)
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
            if (islandBorder.IsMouseOver && !_isIslandOpen && !_isFullScreenCached)
            {
                OpenIsland();
            }
        }

        private void CloseDelayTimer_Tick(object? sender, EventArgs e)
        {
            _closeDelayTimer?.Stop();
            if (!islandBorder.IsMouseOver)
            {
                if (_isDropZoneActive)
                {
                    DeactivateDropMode();
                }
                else
                {
                    CloseIsland();
                }
            }
        }

        private void FullscreenTimer_Tick(object? sender, EventArgs e)
        {
            bool isFs = IsFullScreenRunning();
            if (isFs != _isFullScreenCached)
            {
                _isFullScreenCached = isFs;
                if (_isFullScreenCached)
                {
                    _openDelayTimer?.Stop();
                    _closeDelayTimer?.Stop();
                    if (_isIslandOpen) CloseIsland();
                    this.Visibility = Visibility.Collapsed;
                }
                else
                {
                    this.Visibility = Visibility.Visible;
                }
                EnsureTopmost();
            }
            else if (!_isFullScreenCached && this.Visibility == Visibility.Visible)
            {
                // Reafirma periodicamente que a Dynamic Island está no topo absoluto da barra de tarefas (Shell_TrayWnd)
                EnsureTopmost();
            }
        }

        // ─── Motor de Animação por Hardware Puro ──────────────────────────────────
        private void AnimateBorder(double targetWidth, double targetHeight)
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

            double currentWidth = islandBorder.RenderSize.Width;
            double currentHeight = islandBorder.RenderSize.Height;
            if (currentWidth <= 0) currentWidth = islandBorder.Width;
            if (currentHeight <= 0) currentHeight = islandBorder.Height;

            var widthDuration = widthSpring.GetSettleTime();
            var heightDuration = heightSpring.GetSettleTime();

            var widthAnim = new DoubleAnimation(currentWidth, targetWidth, widthDuration)
            {
                EasingFunction = widthSpring
            };

            var heightAnim = new DoubleAnimation(currentHeight, targetHeight, heightDuration)
            {
                EasingFunction = heightSpring
            };

            islandBorder.BeginAnimation(Border.WidthProperty,  widthAnim);
            islandBorder.BeginAnimation(Border.HeightProperty, heightAnim);
        }

        private static void AnimateColor(
            FrameworkElement element,
            DependencyProperty brushProperty,
            Color targetColor,
            TimeSpan duration)
        {
            if (element.GetValue(brushProperty) is not SolidColorBrush currentBrush || currentBrush.IsFrozen)
            {
                currentBrush = new SolidColorBrush(Colors.Transparent);
                element.SetValue(brushProperty, currentBrush);
            }

            var anim = new ColorAnimation
            {
                To = targetColor,
                Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private void OpenIsland()
        {
            EnsureTopmost();
            _isIslandOpen = true;

            expandedIslandPanel.Visibility = Visibility.Visible;
            islandSeparator.Visibility = Visibility.Visible;
            miniIslandPanel.Visibility = Visibility.Visible;
            dropZonePanel.Visibility = Visibility.Collapsed;
            appearancePanel.Visibility = Visibility.Collapsed;
            wifiPanel.Visibility = Visibility.Collapsed;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            double totalHeight = CalculateContentHeight(expandedIslandPanel);

            AnimateBorder(ISLAND_OPEN_W, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Expanded);
        }

        private double CalculateContentHeight(FrameworkElement? contentPanel, double extraOffset = 0.0)
        {
            if (contentPanel == null || miniIslandPanel == null) return ISLAND_OPEN_H;

            contentPanel.UpdateLayout();
            contentPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double contentHeight = contentPanel.DesiredSize.Height;

            miniIslandPanel.Measure(new Size(ISLAND_OPEN_W, double.PositiveInfinity));
            double miniHeight = miniIslandPanel.DesiredSize.Height;

            const double PanelSpacingAndPadding = 19.0;
            double totalHeight = contentHeight + miniHeight + PanelSpacingAndPadding + extraOffset;
            return Math.Min(totalHeight, ISLAND_OPEN_H);
        }

        private void CloseIsland()
        {
            _isIslandOpen = false;
            
            _isWifiPanelTransitioning = false;
            _wifiTransitionTimer?.Stop();
            StopWifiAutoRefresh();
            txtWifiPassword.Clear();
            wifiPasswordPanel.Visibility = Visibility.Collapsed;

            expandedIslandPanel.Visibility = Visibility.Collapsed;
            islandSeparator.Visibility = Visibility.Collapsed;
            dropZonePanel.Visibility = Visibility.Collapsed;
            appearancePanel.Visibility = Visibility.Collapsed;
            wifiPanel.Visibility = Visibility.Collapsed;

            miniIslandPanel.Visibility = Visibility.Visible;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Center;
            }

            double dynamicWidth = GetDynamicCompactWidth();
            AnimateBorder(dynamicWidth, _miniBaseHeightLogica);
            ApplyDepthHierarchy(IslandDepth.Compact);
            EnsureTopmost();
        }

        public void EnsureTopmost()
        {
            try
            {
                if (_hwnd != IntPtr.Zero && this.Visibility == Visibility.Visible)
                {
                    SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EnsureTopmost] {ex.Message}");
            }
        }

        // ─── Utilitários de Animação Avançada ────────────────────────────────────
        private void AnimatePanelIn(FrameworkElement? panel, double slideDistance = 15, int delayMs = 60)
        {
            if (panel == null) return;
            panel.Visibility = Visibility.Visible;
            panel.Opacity = 0;
            
            if (!(panel.RenderTransform is TranslateTransform))
                panel.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)panel.RenderTransform;
            translate.Y = slideDistance;
            
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : null
            };
            
            var slideUp = new DoubleAnimation(slideDistance, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : null
            };
            
            panel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        private void AnimatePanelOut(FrameworkElement? panel, Action? onCompleted = null)
        {
            if (panel == null)
            {
                onCompleted?.Invoke();
                return;
            }

            if (panel.Visibility != Visibility.Visible)
            {
                onCompleted?.Invoke();
                return;
            }

            double startOpacity = panel.Opacity > 0 ? panel.Opacity : 1.0;
            var fadeOut = new DoubleAnimation(startOpacity, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            
            fadeOut.Completed += (s, e) =>
            {
                panel.Visibility = Visibility.Collapsed;
                panel.Opacity = 1;
                onCompleted?.Invoke();
            };
            
            panel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

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
                    exStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
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
                        wp.flags &= ~SWP_HIDEWINDOW;
                        wp.flags |= SWP_SHOWWINDOW;
                        modified = true;
                    }

                    if (this.Visibility == Visibility.Visible)
                    {
                        if (wp.hwndInsertAfter != HWND_TOPMOST || (wp.flags & (int)SWP_NOZORDER) != 0)
                        {
                            wp.hwndInsertAfter = HWND_TOPMOST;
                            wp.flags &= ~(int)SWP_NOZORDER; // Remove SWP_NOZORDER para forçar aplicação do HWND_TOPMOST
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        Marshal.StructureToPtr(wp, lParam, false);
                        handled = true;
                    }
                }
            }
            else if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE || msg == WM_DPICHANGED)
            {
                OnDisplayOrScreenChanged();
            }

            return IntPtr.Zero;
        }

        private bool IsFullScreenRunning()
        {
            try
            {
                int hr = SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);
                return hr == 0 && (state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                                   state == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY);
            }
            catch { return false; }
        }

        // ─── Encerramento e Teardown Completo ────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged; } catch { }

            try
            {
                _displayChangeCts?.Cancel();
                _displayChangeCts?.Dispose();
            }
            catch { }
            _displayChangeCts = null;

            _openDelayTimer?.Stop();
            _closeDelayTimer?.Stop();
            _fullscreenTimer?.Stop();
            _mediaTimer?.Stop();
            _openDelayTimer = _closeDelayTimer = _fullscreenTimer = _mediaTimer = null;

            DisposeHotkeys();
            DisposeMedia();
            DisposeHardware();
            DisposeWifi();
            DisposePomodoro();
            DisposeDropZone();

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(WndProc); _hwndSource.Dispose(); } catch { }
                _hwndSource = null;
            }

            GC.SuppressFinalize(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            Dispose();
            base.OnClosed(e);
        }
    }
}