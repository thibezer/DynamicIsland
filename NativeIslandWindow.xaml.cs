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
        private const int  MEDIA_TIMER_S         = 2;
        private const int  DRAGLEAVE_DELAY_MS    = 150;

        // Dimensões físicas lógicas finais do widget (sincronizadas e sem saltos)
        private const double ISLAND_OPEN_W       = 410;
        private const double ISLAND_OPEN_H       = 396;
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
            
            _mediaService.StateChanged += async (s, e) => await UpdateMediaInfoAsync();
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

            // Sincronizar Hardware
            try
            {
                int vol = _hardwareService.GetCurrentVolume();
                int bri = _hardwareService.GetCurrentBrightness();
                
                brightnessSlider.Value = bri;
                volumeSlider.Value = vol;
                txtBrightnessVal.Text = $"{bri}%";
                txtVolumeVal.Text = $"{vol}%";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HardwareInit] {ex.Message}");
            }

            InitializeTogglesUI();
            SetupHoverTimer();

            await _mediaService.InitializeAsync();
            await UpdateMediaInfoAsync();

            _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MEDIA_TIMER_S) };
            _mediaTimer.Tick += async (s, ev) => await UpdateMediaInfoAsync();
            _mediaTimer.Start();
        }

        private void InitializeTogglesUI()
        {
            UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
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
            bool fullScreen = IsFullScreen();
            var targetVisibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
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
            double idealWidth = miniIslandPanel.DesiredSize.Width + 60;
            return Math.Clamp(idealWidth, 200, 380);
        }

        // ─── Motor de Animação por Hardware Puro ──────────────────────────────────
        // Executado diretamente pela GPU, sem recalcular buffers de janela do SO.
        private void AnimateBorder(double targetWidth, double targetHeight)
        {
            var duration = TimeSpan.FromMilliseconds(300);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var widthAnim = new DoubleAnimation(islandBorder.Width, targetWidth, duration) { EasingFunction = easing };
            var heightAnim = new DoubleAnimation(islandBorder.Height, targetHeight, duration) { EasingFunction = easing };

            islandBorder.BeginAnimation(Border.WidthProperty, widthAnim);
            islandBorder.BeginAnimation(Border.HeightProperty, heightAnim);
        }

        private void OpenIsland()
        {
            if (_isDropZoneActive) DeactivateDropMode();
            _isIslandOpen = true;
            _hoverCounter = 0;

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

            expandedIslandPanel.Visibility = Visibility.Collapsed;
            islandSeparator.Visibility = Visibility.Collapsed;
            dropZonePanel.Visibility = Visibility.Collapsed;
            miniIslandPanel.Visibility = Visibility.Visible;

            double dynamicWidth = GetDynamicCompactWidth();
            AnimateBorder(dynamicWidth, _miniBaseHeightLogica);
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

            if (islandBorder != null)
            {
                var dropShadow = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x4C, 0xC2, 0xFF),
                    ShadowDepth = 0,
                    Opacity = 0.6,
                    BlurRadius = 25
                };
                islandBorder.Effect = dropShadow;
                islandBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

                DoubleAnimation blurAnim = new DoubleAnimation(15, 35, new Duration(TimeSpan.FromSeconds(1.5))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                dropShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);

                DoubleAnimation opacityAnim = new DoubleAnimation(0.3, 0.8, new Duration(TimeSpan.FromSeconds(1.5))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                dropShadow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);
            }

            AnimateBorder(DROPZONE_W, DROPZONE_H);
        }

        // ─── Sliders ──────────────────────────────────────────────────────────────
        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hardwareService == null || txtBrightnessVal == null) return;
            int val = (int)e.NewValue;
            txtBrightnessVal.Text = $"{val}%";
            _hardwareService.SetBrightness(val);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hardwareService == null || txtVolumeVal == null) return;
            int val = (int)e.NewValue;
            txtVolumeVal.Text = $"{val}%";
            _hardwareService.SetVolume(val);
        }

        // ─── Toggles Click ────────────────────────────────────────────────────────
        private async void Toggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Border border)) return;
            string action = border.Tag?.ToString() ?? "";

            switch (action)
            {
                case "wifi":
                    _wifiOn = !_wifiOn; UpdateToggleButton(border, icoWifi, _wifiOn);
                    await _hardwareService.HandleToggleAsync("wifi", _wifiOn); break;
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
                border.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF6, 0xA7, 0x33));
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF6, 0xA7, 0x33));
                icon.Foreground = new SolidColorBrush(Color.FromRgb(0xF6, 0xA7, 0x33));
            }
            else
            {
                border.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x20));
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
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
                        txtMiniTitle.Text = "Dynamic Island";
                        txtMiniSubtitle.Text = "Notificações ativas";

                        miniThumbBorder.Background = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
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
            miniThumbBorder.Background = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
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

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _hoverTimer?.Stop(); _mediaTimer?.Stop(); _dragLeaveTimer?.Stop();
            _hoverTimer = _mediaTimer = _dragLeaveTimer = null;

            if (_hwndSource != null)
            {
                try { _hwndSource.RemoveHook(WndProc); _hwndSource.Dispose(); } catch { }
                _hwndSource = null;
            }
            GC.SuppressFinalize(this);
        }

        protected override void OnClosed(EventArgs e) { Dispose(); base.OnClosed(e); }
    }
}