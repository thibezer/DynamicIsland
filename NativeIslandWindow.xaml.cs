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
        private const int  HOVER_TICKS_TO_OPEN  = 4;
        private const int  HOVER_TICKS_TO_CLOSE = 3;
        private const int  HOVER_TIMER_MS        = 100;
        private const int  MEDIA_TIMER_S         = 10;
        private const int  DRAGLEAVE_DELAY_MS    = 150;

        // Dimensões físicas lógicas finais do widget (sincronizadas e sem saltos)
        private const double ISLAND_OPEN_W       = 410;
        private const double ISLAND_OPEN_H       = 374;
        private const double ISLAND_APPEARANCE_H = 300;
        private const double DROPZONE_W          = 320;
        private const double DROPZONE_H          = 200;

        // ─── Estado Interno ───────────────────────────────────────────────────────
        private DispatcherTimer? _hoverTimer;
        private DispatcherTimer? _mediaTimer;
        private DispatcherTimer? _dragLeaveTimer;

        private bool   _isIslandOpen       = false;
        private bool   _isDropZoneActive   = false;
        private bool   _isDraggingFile     = false;
        private string _currentMode        = "notification";
        private int    _hoverCounter       = 0;
        private string? _pendingDropFilePath = null;
        private double _dpiX = 1.0, _dpiY = 1.0;
        private int _mediaUpdateInProgress = 0;
        private bool _disposed = false;
        private double _miniBaseHeightLogica = 43; 
        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private int _topmostTickCounter = 0;
        private const int TOPMOST_REFRESH_TICKS = 10;
        private int _fullScreenCheckCounter = 0;
        private bool _isFullScreenCached = false;

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
        private bool _liveCaptionsOn = false;
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
            this.Width = 430;
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

            InitializeTogglesUI();
            _isFullScreenCached = IsFullScreen();
            SetupHoverTimer();

            await _mediaService.InitializeAsync();
            await UpdateMediaInfoAsync();

            _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MEDIA_TIMER_S) };
            _mediaTimer.Tick += async (s, ev) => await UpdateMediaInfoAsync();
            _mediaTimer.Start();
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
            UpdateToggleButton(btnLiveCaptions, icoLiveCaptions, _liveCaptionsOn);
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

        // ─── Hover Engine (Hit Test ultra leve em coordenadas lógicas) ─────────
        private void SetupHoverTimer()
        {
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HOVER_TIMER_MS) };
            _hoverTimer.Tick += HoverTimer_Tick;
            _hoverTimer.Start();
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (++_fullScreenCheckCounter >= 5)
            {
                _fullScreenCheckCounter = 0;
                _isFullScreenCached = IsFullScreen();
            }

            var targetVisibility = _isFullScreenCached ? Visibility.Collapsed : Visibility.Visible;
            if (this.Visibility != targetVisibility)
            {
                this.Visibility = targetVisibility;
            }

            if (this.Visibility == Visibility.Visible)
            {
                if (++_topmostTickCounter >= TOPMOST_REFRESH_TICKS)
                {
                    _topmostTickCounter = 0;
                    EnsureTopmost();
                }
            }

            // Captura posição do rato relativa à janela (Independente de DPI ou resoluções complexas)
            Point mousePos = Mouse.GetPosition(this);
            
            // Verifica com precisão matemática se o rato está sobre o Border visível real
            bool inZone = mousePos.X >= 10 &&
                          mousePos.X <= 10 + islandBorder.ActualWidth &&
                          mousePos.Y >= (this.Height - 10 - islandBorder.ActualHeight) &&
                          mousePos.Y <= this.Height - 10;

            if (!_isIslandOpen && _currentMode == "notification" && inZone && !_isDraggingFile && !_isDropZoneActive)
            {
                if (++_hoverCounter >= HOVER_TICKS_TO_OPEN) { OpenIsland(); _hoverCounter = 0; }
            }
            else if ((_isIslandOpen || _isDropZoneActive) && !inZone)
            {
                int ticksToClose = _isDropZoneActive ? 30 : HOVER_TICKS_TO_CLOSE;
                if (++_hoverCounter >= ticksToClose) 
                { 
                    if (_isIslandOpen) CloseIsland();
                    if (_isDropZoneActive) DeactivateDropMode();
                    _hoverCounter = 0; 
                }
            }
            else
            {
                if (!inZone || ((_isIslandOpen || _isDropZoneActive) && inZone)) _hoverCounter = 0;
            }
        }

        private double GetDynamicCompactWidth()
        {
            if (miniIslandPanel == null) return 340;
            miniIslandPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double idealWidth = miniIslandPanel.DesiredSize.Width + 30;
            return Math.Clamp(idealWidth, 200, 380);
        }

        // ─── Motor de Animação por Hardware Puro ──────────────────────────────────
        // Executado diretamente pela GPU, sem recalcular buffers de janela do SO.
        private void AnimateBorder(double targetWidth, double targetHeight, Action? onCompleted = null)
        {
            var duration = TimeSpan.FromMilliseconds(300);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var widthAnim = new DoubleAnimation(islandBorder.Width, targetWidth, duration) { EasingFunction = easing };
            var heightAnim = new DoubleAnimation(islandBorder.Height, targetHeight, duration) { EasingFunction = easing };

            if (onCompleted != null)
            {
                heightAnim.Completed += (s, e) => onCompleted();
            }

            islandBorder.BeginAnimation(Border.WidthProperty, widthAnim);
            islandBorder.BeginAnimation(Border.HeightProperty, heightAnim);
        }

        private void OpenIsland()
        {
            if (_isDropZoneActive) DeactivateDropMode();
            _isIslandOpen = true;
            _hoverCounter = 0;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            if (txtMiniTitle != null) txtMiniTitle.MaxWidth = 260;
            if (txtMiniSubtitle != null) txtMiniSubtitle.MaxWidth = 260;

            miniIslandPanel.Visibility = Visibility.Visible;
            islandSeparator.Visibility = Visibility.Visible;
            dropZonePanel.Visibility = Visibility.Collapsed;
            expandedIslandPanel.Visibility = Visibility.Visible;

            AnimateBorder(ISLAND_OPEN_W, ISLAND_OPEN_H);
        }

        private void CloseIsland()
        {
            _isIslandOpen = false;
            _hoverCounter = 0;

            if (txtMiniTitle != null) txtMiniTitle.MaxWidth = 220;
            if (txtMiniSubtitle != null) txtMiniSubtitle.MaxWidth = 220;

            expandedIslandPanel.Visibility = Visibility.Collapsed;
            appearancePanel.Visibility = Visibility.Collapsed;
            wifiPanel.Visibility = Visibility.Collapsed;
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
            });
        }

        private void ActivateDropMode()
        {
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
                islandBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            }

            CloseIsland();
        }

        private void ShowDropPanel()
        {
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
                case "live_captions":
                    _liveCaptionsOn = !_liveCaptionsOn; UpdateToggleButton(border, icoLiveCaptions, _liveCaptionsOn);
                    await _hardwareService.HandleToggleAsync("live_captions", _liveCaptionsOn); break;
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
            if (isOn)
            {
                border.Background = new SolidColorBrush(_themeColor);
                border.BorderBrush = new SolidColorBrush(_themeColor);
                
                // Escolhe ícone preto ou branco dependendo do contraste da cor do tema
                double brightness = (0.299 * _themeColor.R + 0.587 * _themeColor.G + 0.114 * _themeColor.B) / 255;
                icon.Foreground = brightness > 0.6 ? Brushes.Black : Brushes.White;
            }
            else
            {
                var brush = this.Resources["IslandInactiveBrush"] as Brush;
                border.Background = brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));
                border.BorderBrush = Brushes.Transparent;
                icon.Foreground = Brushes.White;
            }
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
            _isDraggingFile = false; _hoverCounter = -10; e.Handled = true;
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

                this.Dispatcher.Invoke(() =>
                {
                    if (state.Mode == "music")
                    {
                        miniMediaControls.Visibility = Visibility.Visible;
                        
                        txtMiniTitle.Text = state.Title;
                        txtMiniSubtitle.Text = state.Subtitle;

                        string playGlyph = state.IsPlaying ? "\uE769" : "\uE768";
                        txtMiniPlayIcon.Text = playGlyph;

                        if (!string.IsNullOrEmpty(state.Thumbnail))
                        {
                            try
                            {
                                byte[] binaryData = Convert.FromBase64String(state.Thumbnail);
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.StreamSource = new System.IO.MemoryStream(binaryData);
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                bitmap.EndInit();
                                bitmap.Freeze();

                                miniThumbBorder.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                                miniThumbBorder.Visibility = Visibility.Visible;
                                txtMiniIcon.Visibility = Visibility.Collapsed;
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
                        txtMiniTitle.Text = state.Title;
                        txtMiniSubtitle.Text = state.Subtitle;

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
                    if ((wp.flags & SWP_HIDEWINDOW) != 0 && this.Visibility == Visibility.Visible)
                    {
                        wp.flags &= ~SWP_HIDEWINDOW; wp.flags |= SWP_SHOWWINDOW;
                        Marshal.StructureToPtr(wp, lParam, false);
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
                             or QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
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
            expandedIslandPanel.Visibility = Visibility.Collapsed;
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

            // Altura total = altura do painel de cores + pílula compacta + separador (6px) + padding (13px) + margem superior desejada (10px) = 29px
            double totalHeight = appearanceHeight + miniHeight + 29;

            // Limita a altura para não exceder o limite máximo da ilha
            totalHeight = Math.Min(totalHeight, ISLAND_OPEN_H);

            AnimateBorder(ISLAND_OPEN_W, totalHeight);
        }

        private void BackToMainPanel_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            appearancePanel.Visibility = Visibility.Collapsed;
            wifiPanel.Visibility = Visibility.Collapsed;
            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            expandedIslandPanel.Visibility = Visibility.Visible;
            if (btnWifi != null && icoWifi != null)
            {
                UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
            }
            if (btnWifiMenu != null && icoWifiMenu != null)
            {
                UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
            }
            AnimateBorder(ISLAND_OPEN_W, ISLAND_OPEN_H);
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
            if (string.IsNullOrEmpty(hex)) return false;
            if (hex[0] != '#') return false;
            return hex.Length == 7 || hex.Length == 9;
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "island_config.json");
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
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "island_config.json");
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
                    InitializeTogglesUI();
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
                    InitializeTogglesUI();
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
            _hoverTimer?.Stop(); _mediaTimer?.Stop(); _dragLeaveTimer?.Stop();
            _hoverTimer = _mediaTimer = _dragLeaveTimer = null;

            if (_mediaService != null)
            {
                _mediaService.StateChanged -= MediaService_StateChanged;
                _mediaService.Dispose();
            }

            if (_hardwareService != null)
            {
                _hardwareService.Dispose();
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
            
            if (_wifiOn)
            {
                btnWifiToggleHeader.Background = new SolidColorBrush(_themeColor);
                wifiSwitchIndicator.HorizontalAlignment = HorizontalAlignment.Right;
            }
            else
            {
                var brush = this.Resources["IslandInactiveBrush"] as Brush;
                btnWifiToggleHeader.Background = brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));
                wifiSwitchIndicator.HorizontalAlignment = HorizontalAlignment.Left;
            }
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

        private void RenderWifiNetworks(System.Collections.Generic.List<HardwareService.WiFiNetworkInfo> networks)
        {
            if (wifiListContainer == null) return;

            foreach (var net in networks)
            {
                var itemBorder = new Border
                {
                    Background = this.Resources["IslandInactiveBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20)),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Cursor = Cursors.Hand,
                    Tag = net
                };

                itemBorder.MouseEnter += (s, ev) => { itemBorder.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)); };
                itemBorder.MouseLeave += (s, ev) => { itemBorder.Background = this.Resources["IslandInactiveBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20)); };
                itemBorder.MouseLeftButtonDown += WifiNetworkItem_Click;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txtSsid = new TextBlock
                {
                    Style = this.Resources["FluentText"] as Style,
                    Text = net.Ssid,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(txtSsid, 0);
                grid.Children.Add(txtSsid);

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

                var txtSignal = new TextBlock
                {
                    Style = this.Resources["FluentIcon"] as Style,
                    Text = GetWifiIconGlyph(net.SignalBars),
                    FontSize = 14,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txtSignal, 2);
                grid.Children.Add(txtSignal);

                itemBorder.Child = grid;
                wifiListContainer.Children.Add(itemBorder);
            }
        }

        private string GetWifiIconGlyph(int bars)
        {
            switch (bars)
            {
                case 0: return "\uE701";
                case 1: return "\uE701";
                case 2: return "\uE702";
                case 3: return "\uE703";
                case 4: return "\uE704";
                default: return "\uE704";
            }
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
            var txtConnecting = new TextBlock
            {
                Style = this.Resources["FluentTextSec"] as Style,
                Text = $"A ligar a \"{net.Ssid}\"...",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            };
            wifiListContainer.Children.Add(txtConnecting);
            AdjustWifiPanelHeight();

            bool success = false;
            if (net.RawNetwork != null)
            {
                success = await _hardwareService.ConnectToNetworkAsync(net.RawNetwork, password);
            }

            if (success)
            {
                MessageBox.Show($"Ligado com sucesso a \"{net.Ssid}\"!", "Wi-Fi", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Não foi possível ligar a \"{net.Ssid}\". Verifique a palavra-passe.", "Erro Wi-Fi", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            _ = RefreshWifiListAsync();
        }

        private void WifiMenuOpen_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            expandedIslandPanel.Visibility = Visibility.Collapsed;
            wifiPanel.Visibility = Visibility.Visible;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            UpdateWifiToggleHeaderUI();
            _ = RefreshWifiListAsync();
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

            // Altura final = wifiHeight + miniHeight + separador (10) + margem do border (12*2 = 24)
            double totalHeight = wifiHeight + miniHeight + 34;

            // Limita ao tamanho máximo do painel aberto
            totalHeight = Math.Min(totalHeight, ISLAND_OPEN_H);

            AnimateBorder(ISLAND_OPEN_W, totalHeight);
        }
    }
}