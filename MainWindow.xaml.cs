using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Media.Control;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIslandWindows
{
    public partial class MainWindow : Window
    {
        // Importações para simular atalhos de teclado nativos
        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const int KEYEVENTF_KEYDOWN = 0x0000;
        private const int KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        [DllImport("ole32.dll")]
        private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        public enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private System.Windows.Threading.DispatcherTimer? _hoverTimer;
        private System.Windows.Threading.DispatcherTimer? _mediaTimer;
        private System.Windows.Threading.DispatcherTimer? _dragLeaveTimer;
        private bool _isIslandOpen = false;
        private bool _isDropZoneActive = false;
        private bool _isDraggingFile = false;
        private string _currentMode = "notification";
        private double _miniIslandWidth = 300;
        private int _topmostTickCounter = 0;
        private int _hoverCounter = 0;
        private string? _pendingDropFilePath = null;

        public MainWindow()
        {
            InitializeComponent();
            this.Deactivated += Window_Deactivated;
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            // Força a janela a ficar no topo usando a API nativa
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // 0x0001 (NOSIZE) | 0x0002 (NOMOVE) | 0x0010 (NOACTIVATE)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); 
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Posicionar a janela no canto inferior esquerdo
            var desktopWorkingArea = SystemParameters.WorkArea;
            this.Left = desktopWorkingArea.Left;
            
            // CORREÇÃO: Usar PrimaryScreenHeight para ignorar o bloqueio da barra de tarefas
            // Isso permite que o WPF desça até o último pixel do monitor
            this.Top = SystemParameters.PrimaryScreenHeight - this.Height;

            // 2. Inicializar o WebView2
            await webView.EnsureCoreWebView2Async(null);
            
            // CRÍTICO: Desativa o drop target do Chromium para forçar o bubble up para a Window WPF
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.AllowExternalDrop = false;

            // Forçar o fundo do WebView2 a ser transparente
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            // 3. Registrar eventos
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

            // Desativar menus de contexto e outras interferências
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false; // Opcional, para evitar F12
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // 4. Carregar o arquivo HTML
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string htmlPath = Path.Combine(currentDirectory, "index.html");
            webView.CoreWebView2.Navigate(htmlPath);

            // 5. Ajuste inicial de layout
            UpdateUILayout();

            // Iniciar timers e listeners
            SetupHoverTimer();
            SetupMediaTimer();
            SetupNotificationListener();
            SetStartup();
        }

        private async void SetupNotificationListener()
        {
            try
            {
                var listener = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    listener.NotificationChanged += async (s, e) => 
                    {
                        // Se não houver música tocando, atualiza para mostrar a nova notificação
                        await UpdateMediaInfoAsync();
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao configurar listener de notificações: {ex.Message}");
            }
        }

        private void SetStartup()
        {
            try
            {
                string appName = "DynamicIsland";
                string appPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (key != null && key.GetValue(appName) == null)
                {
                    key.SetValue(appName, $"\"{appPath}\"");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao configurar inicialização: {ex.Message}");
            }
        }

        private void SetupMediaTimer()
        {
            _mediaTimer = new System.Windows.Threading.DispatcherTimer();
            _mediaTimer.Interval = TimeSpan.FromSeconds(2);
            _mediaTimer.Tick += async (s, e) => await UpdateMediaInfoAsync();
            _mediaTimer.Start();
            
            _ = UpdateMediaInfoAsync();
        }

        private async Task UpdateMediaInfoAsync()
        {
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = manager.GetCurrentSession();
                
                if (session != null)
                {
                    var info = await session.TryGetMediaPropertiesAsync();
                    string title = string.IsNullOrEmpty(info.Title) ? "Nenhuma música" : info.Title;
                    string artist = string.IsNullOrEmpty(info.Artist) ? "Nenhum artista" : info.Artist;
                    
                    string thumbnailBase64 = "";
                    if (info.Thumbnail != null)
                    {
                        try
                        {
                            var stream = await info.Thumbnail.OpenReadAsync();
                            var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
                            await reader.LoadAsync((uint)stream.Size);
                            byte[] bytes = new byte[stream.Size];
                            reader.ReadBytes(bytes);
                            thumbnailBase64 = Convert.ToBase64String(bytes);
                        }
                        catch { }
                    }

                    _currentMode = "music";
                    var msg = new { type = "ISLAND_CONTENT", mode = "music", title = title, subtitle = artist, thumbnail = thumbnailBase64 };
                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
                }
                else
                {
                    _currentMode = "notification";
                    var (notificationTitle, notificationContent) = await GetLatestNotificationAsync();
                    
                    var msg = new { type = "ISLAND_CONTENT", mode = "notification", title = notificationTitle, subtitle = notificationContent, thumbnail = "" };
                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro no UpdateMediaInfo: {ex.Message}");
            }
        }

        private async Task<(string title, string content)> GetLatestNotificationAsync()
        {
            string title = "Sem notificações";
            string content = "Tudo limpo por aqui";

            try
            {
                var listener = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();
                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                    if (notifications.Count > 0)
                    {
                        // Pegamos a última (mais recente na lista do Windows costuma ser a última ou a primeira dependendo da versão, 
                        // mas GetNotificationsAsync geralmente retorna na ordem de chegada)
                        var notification = notifications[notifications.Count - 1];
                        var bindings = notification.Notification.Visual.Bindings;
                        
                        if (bindings.Count > 0)
                        {
                            var textElements = bindings[0].GetTextElements();
                            if (textElements.Count >= 1) title = textElements[0].Text;
                            if (textElements.Count >= 2) content = textElements[1].Text;
                            
                            // Se o título for o nome do app (ex: WhatsApp), e tivermos 3 elementos, 
                            // o segundo costuma ser o contato e o terceiro a mensagem.
                            if (textElements.Count >= 3)
                            {
                                title = textElements[1].Text;
                                content = textElements[2].Text;
                            }
                        }
                    }
                }
            }
            catch { }

            return (title, content);
        }

        private void SetupHoverTimer()
        {
            _hoverTimer = new System.Windows.Threading.DispatcherTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(100);
            _hoverTimer.Tick += HoverTimer_Tick;
            _hoverTimer.Start();
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            if (GetCursorPos(out POINT p))
            {
                double dpiX = 1.0, dpiY = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                double logicalX = p.X / dpiX;
                double logicalY = p.Y / dpiY;
                
                // --- LÓGICA DE DETECÇÃO DO MOUSE (HIT-TEST) ---
                
                // 1. hitWidth/Height: Define a área "quente" onde o mouse ativa a ilha.
                double hitWidth = _isIslandOpen ? 400 : _miniIslandWidth + 20;
                double hitHeight = _isIslandOpen ? 600 : 60; 
                
                // 2. inIslandZone: Verifica se o cursor está dentro do retângulo visual
                // Consideramos a margem de 12px definida no XAML
                bool inIslandZone = logicalX >= this.Left + 12 && logicalX <= this.Left + 12 + hitWidth && 
                                   logicalY >= this.Top + (this.Height - 12 - hitHeight) && logicalY <= this.Top + this.Height - 12;

                // 3. Lógica de Hover (Abertura automática)
                if (!_isIslandOpen && _currentMode == "notification" && inIslandZone && !_isDraggingFile && !_isDropZoneActive)
                {
                    _hoverCounter++;
                    if (_hoverCounter >= 4)
                    {
                        OpenIsland();
                        _hoverCounter = 0;
                    }
                }
                // 4. Fechamento automático ao tirar o mouse
                else if (_isIslandOpen && !inIslandZone)
                {
                    _hoverCounter++;
                    if (_hoverCounter >= 3) // Se ficar fora por 300ms
                    {
                        CloseIsland();
                        _hoverCounter = 0;
                    }
                }
                else if (!inIslandZone)
                {
                    _hoverCounter = 0;
                }
                else if (_isIslandOpen && inIslandZone)
                {
                    _hoverCounter = 0;
                }
            }

            // --- VERIFICAÇÃO DE TELA CHEIA ---
            // Oculta a ilha se o usuário estiver em um jogo, apresentação ou vídeo em tela cheia
            if (IsFullScreen())
            {
                if (this.Visibility != Visibility.Collapsed) this.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (this.Visibility != Visibility.Visible) this.Visibility = Visibility.Visible;
            }

            // Garante que a janela fique sempre visível sobre a barra de tarefas
            _topmostTickCounter++;
            if (_topmostTickCounter >= 10 && this.Visibility == Visibility.Visible)
            {
                _topmostTickCounter = 0;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                // Força o estado TOPMOST via Win32 API
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
        }

        private void ActivateDropMode()
        {
            _isDraggingFile = true;
            _isDropZoneActive = true;
            
            UpdateUILayout();

            // Mostra o painel de opções no JS
            var msg = new { type = "SET_DROP_ZONE", active = true };
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
        }

        private void DeactivateDropMode()
        {
            _isDraggingFile = false;
            _isDropZoneActive = false;
            _dragLeaveTimer?.Stop();

            UpdateUILayout();

            // Fecha o painel no JS
            var msg = new { type = "SET_DROP_ZONE", active = false };
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Link;
                ActivateDropMode();
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Link;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            Debug.WriteLine("WPF: Arquivo solto (Drop na Window)");
            _dragLeaveTimer?.Stop();

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[]? files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    Debug.WriteLine($"WPF: Arquivo recebido: {files[0]}");
                    _pendingDropFilePath = files[0];

                    var msgFile = new { type = "FILE_RECEIVED", fileName = System.IO.Path.GetFileName(files[0]) };
                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msgFile));
                }
            }

            _isDraggingFile = false;
            _hoverCounter = -10; // Bloqueia hover temporariamente
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            _dragLeaveTimer = new System.Windows.Threading.DispatcherTimer();
            _dragLeaveTimer.Interval = TimeSpan.FromMilliseconds(150);
            _dragLeaveTimer.Tick += (s, args) =>
            {
                _dragLeaveTimer.Stop();
                if (GetCursorPos(out POINT cursorPos))
                {
                    double dpiX = 1.0, dpiY = 1.0;
                    var source = PresentationSource.FromVisual(this);
                    if (source?.CompositionTarget != null)
                    {
                        dpiX = source.CompositionTarget.TransformToDevice.M11;
                        dpiY = source.CompositionTarget.TransformToDevice.M22;
                    }
                    double lx = cursorPos.X / dpiX, ly = cursorPos.Y / dpiY;

                    bool insideWindow = lx >= this.Left && lx <= this.Left + this.Width &&
                                        ly >= this.Top && ly <= this.Top + this.Height;
                    if (!insideWindow)
                    {
                        DeactivateDropMode();
                    }
                }
            };
            _dragLeaveTimer.Start();
            e.Handled = true;
        }

        private bool IsFullScreen()
        {
            try
            {
                SHQueryUserNotificationState(out var state);
                return state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                       state == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY ||
                       state == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
            }
            catch { return false; }
        }

        private void OpenIsland()
        {
            _isIslandOpen = true;
            _hoverCounter = 0;
            
            UpdateUILayout();

            var msg = new { type = "SET_ISLAND_STATE", state = "open" };
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
        }

        private void CloseIsland()
        {
            _isIslandOpen = false;
            _hoverCounter = 0;
            
            UpdateUILayout();

            var msg = new { type = "SET_ISLAND_STATE", state = "closed" };
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
        }

        private void UpdateUILayout()
        {
            try
            {
                double targetWidth = _miniIslandWidth + 40;
                double targetHeight = 65;

                if (_isIslandOpen)
                {
                    targetWidth = 405;
                    targetHeight = 630;
                }
                else if (_isDropZoneActive)
                {
                    targetWidth = 350;
                    targetHeight = 480;
                }

                // Ajusta o tamanho do WebView2 físico
                webView.Width = targetWidth;
                webView.Height = targetHeight;

                // Ajusta o hit-test border para ser igual ao webView
                hitTestBorder.Width = targetWidth;
                hitTestBorder.Height = targetHeight;
                hitTestBorder.IsHitTestVisible = true;
                
                // Garante que o fundo da janela seja transparente para cliques fora dessas áreas
                mainGrid.Background = System.Windows.Media.Brushes.Transparent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar layout: {ex.Message}");
            }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            SendInitialHardwareValues();
        }

        private void SendInitialHardwareValues()
        {
            try
            {
                int currentVolume = 50;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    currentVolume = (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                }
                catch { }

                int currentBrightness = 70;
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightness");
                    using var instances = searcher.Get();
                    foreach (ManagementObject instance in instances)
                    {
                        currentBrightness = Convert.ToInt32(instance.GetPropertyValue("CurrentBrightness"));
                        break;
                    }
                }
                catch { }

                var msg = new { type = "INIT_VALUES", volume = currentVolume, brightness = currentBrightness };
                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
            }
            catch { }
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string messageStr = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(messageStr)) return;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(messageStr);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("type", out JsonElement typeElement))
                {
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "UPDATE_WIDTH")
                    {
                        _miniIslandWidth = root.GetProperty("width").GetDouble();
                        if (!_isIslandOpen)
                        {
                            hitTestBorder.Width = _miniIslandWidth;
                        }
                        return;
                    }

                    if (type == "TOGGLE_SETTING")
                    {
                        string? setting = root.GetProperty("setting").GetString();
                        bool state = root.GetProperty("state").GetBoolean();
                        if (setting != null) await HandleToggleAsync(setting, state);
                    }
                    else if (type == "SET_VOLUME")
                    {
                        if (int.TryParse(root.GetProperty("value").GetString(), out int value)) SetVolume(value);
                    }
                    else if (type == "SET_BRIGHTNESS")
                    {
                        if (int.TryParse(root.GetProperty("value").GetString(), out int value)) SetBrightness(value);
                    }
                    else if (type == "MEDIA_COMMAND")
                    {
                        await HandleMediaCommandAsync(root.GetProperty("command").GetString());
                    }
                    else if (type == "OPEN_ISLAND")
                    {
                        OpenIsland();
                    }
                    else if (type == "JS_READY")
                    {
                        // O JS avisou que está pronto. Agora enviamos os valores iniciais de volume/brilho.
                        SendInitialHardwareValues();
                    }
                    else if (type == "OPEN_FILE_PICKER")
                    {
                        OpenManualFilePicker();
                    }
                    else if (type == "CLOSE_DROP_ZONE")
                    {
                        DeactivateDropMode();
                    }
                    else if (type == "SET_DROP_ZONE")
                    {
                        bool active = root.GetProperty("active").GetBoolean();
                        if (active)
                        {
                            // JS detectou dragenter → traz hitTestBorder pra frente do WebView2
                            ActivateDropMode();
                        }
                        else
                        {
                            DeactivateDropMode();
                        }
                    }
                    else if (type == "FILE_ACTION")
                    {
                        string action = root.GetProperty("action").GetString() ?? "";
                        Debug.WriteLine($"JS: Ação de arquivo recebida: {action}");
                        
                        if (_pendingDropFilePath != null)
                        {
                            string filePath = _pendingDropFilePath;
                            _pendingDropFilePath = null;
                            ProcessDroppedFile(filePath, action);
                        }
                        else
                        {
                            Debug.WriteLine("C#: Erro - _pendingDropFilePath está nulo ao receber FILE_ACTION");
                            MessageBox.Show("Ocorreu um erro: o caminho do arquivo foi perdido. Por favor, tente arrastar o arquivo novamente.", "Erro Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        
                        // Fecha o painel de drop
                        _isDropZoneActive = false;
                        var closeMsg = new { type = "SET_DROP_ZONE", active = false };
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(closeMsg));
                    }
                }
            }
            catch { }
        }

        private void OpenManualFilePicker()
        {
            try
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog();
                openFileDialog.Title = "Selecionar arquivo para Dynamic Island";
                openFileDialog.Filter = "Todos os arquivos (*.*)|*.*";

                if (openFileDialog.ShowDialog() == true)
                {
                    string filePath = openFileDialog.FileName;
                    Debug.WriteLine($"C#: Arquivo selecionado manualmente: {filePath}");
                    
                    // Ativa o estado de drop mode (desativa hit-test do border, etc)
                    ActivateDropMode();

                    // Salva como pendente
                    _pendingDropFilePath = filePath;

                    // Fecha a ilha principal (caso ainda esteja aberta)
                    _isIslandOpen = false;
                    var closeIslandMsg = new { type = "SET_ISLAND_STATE", state = "closed" };
                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(closeIslandMsg));

                    // Abre o painel de drop com as opções
                    var msgFile = new { type = "FILE_RECEIVED", fileName = System.IO.Path.GetFileName(filePath) };
                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msgFile));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao abrir seletor de arquivos: {ex.Message}");
            }
        }

        // Método para ser chamado quando um arquivo é solto
        private void ProcessDroppedFile(string filePath, string action)
        {
            if (action == "excel")
            {
                SendFileToExcel(filePath);
            }
        }

        private void SendFileToExcel(string filePath)
        {
            try
            {
                // Usando Late Binding para evitar dependência direta de DLLs do Office
                Type? excelType = Type.GetTypeFromProgID("Excel.Application");
                if (excelType == null) return;

                // Em .NET Core, Marshal.GetActiveObject não existe. Usamos o helper via P/Invoke.
                Guid clsid;
                CLSIDFromProgID("Excel.Application", out clsid);
                GetActiveObject(ref clsid, IntPtr.Zero, out object excelApp);
                
                if (excelApp == null) return;

                object? activeSheet = excelApp.GetType().InvokeMember("ActiveSheet", System.Reflection.BindingFlags.GetProperty, null, excelApp, null);
                if (activeSheet == null) return;

                object? activeCell = excelApp.GetType().InvokeMember("ActiveCell", System.Reflection.BindingFlags.GetProperty, null, excelApp, null);
                if (activeCell == null) return;

                object? hyperlinks = activeSheet.GetType().InvokeMember("Hyperlinks", System.Reflection.BindingFlags.GetProperty, null, activeSheet, null);
                if (hyperlinks == null) return;

                // CORREÇÃO AQUI - Usando Missing.Value em vez de "" vazia
                object missing = System.Reflection.Missing.Value;
                
                // Argumentos: Anchor, Address, SubAddress, ScreenTip, TextToDisplay
                object[] args = new object[] { activeCell, filePath, missing, missing, Path.GetFileName(filePath) };
                
                hyperlinks.GetType().InvokeMember("Add", System.Reflection.BindingFlags.InvokeMethod, null, hyperlinks, args);
                
                Debug.WriteLine("Hiperlink criado no Excel com sucesso.");
                
                // Feedback visual de sucesso
                MessageBox.Show("Hiperlink criado no Excel com sucesso!", "Sucesso Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao enviar para o Excel: {ex.Message}");
                // Mostra um aviso caso a célula do Excel não esteja pronta
                MessageBox.Show($"Ocorreu um erro ao colar no Excel.\n\nCertifique-se de que:\n1. Há uma planilha aberta.\n2. A célula não está em modo de edição (piscando).\n\nErro técnico: {ex.Message}", "Aviso Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task HandleMediaCommandAsync(string? command)
        {
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = manager.GetCurrentSession();
                if (session == null) return;

                switch (command)
                {
                    case "playPause":
                        var info = session.GetPlaybackInfo();
                        if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            await session.TryPauseAsync();
                        else
                            await session.TryPlayAsync();
                        break;
                    case "next":
                        await session.TrySkipNextAsync();
                        break;
                }
                await UpdateMediaInfoAsync();
            }
            catch { }
        }

        private async Task HandleToggleAsync(string setting, bool state)
        {
            switch (setting)
            {
                case "wifi": await ToggleRadioAsync(RadioKind.WiFi, state); break;
                case "bluetooth": await ToggleRadioAsync(RadioKind.Bluetooth, state); break;
                case "airplane": await ToggleAirplaneModeAsync(state); break;
                case "open_settings": OpenUri("ms-settings:"); break;
                case "accessibility": OpenUri("ms-settings:easeofaccess-display"); break;
                case "battery_saver": OpenUri("ms-settings:batterysaver"); break;
                case "live_captions": TriggerLiveCaptionsShortcut(); break;
                case "night_light": OpenUri("ms-settings:nightlight"); break;
                case "mobile_hotspot": OpenUri("ms-settings:network-mobilehotspot"); break;
                case "nearby_share": OpenUri("ms-settings:crossdevice"); break;
                case "cast": OpenUri("ms-settings-connectabledevices:devicediscovery"); break;
                case "project":
                    try { Process.Start("DisplaySwitch.exe"); } catch { }
                    break;
            }
        }
        
        private async Task ToggleRadioAsync(RadioKind kind, bool state)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
                var radios = await Radio.GetRadiosAsync();
                foreach (var radio in radios)
                {
                    if (radio.Kind == kind) await radio.SetStateAsync(state ? RadioState.On : RadioState.Off);
                }
            }
            catch { }
        }

        private async Task ToggleAirplaneModeAsync(bool airplaneModeOn)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
                var radios = await Radio.GetRadiosAsync();
                var targetState = airplaneModeOn ? RadioState.Off : RadioState.On;
                foreach (var radio in radios) await radio.SetStateAsync(targetState);
            }
            catch { }
        }

        private void SetVolume(int volume)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume / 100f, 0.0f, 1.0f);
            }
            catch { }
        }

        private void SetBrightness(int brightness)
        {
            try
            {
                byte targetBrightness = (byte)Math.Clamp(brightness, 0, 100);
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                using var instances = searcher.Get();
                foreach (ManagementObject instance in instances) instance.InvokeMethod("WmiSetBrightness", new object[] { 1, targetBrightness });
            }
            catch { }
        }

        private void OpenUri(string uri)
        {
            try { Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true }); } catch { }
        }

        private void TriggerLiveCaptionsShortcut()
        {
            try
            {
                keybd_event(0x5B, 0, KEYEVENTF_KEYDOWN, 0);
                keybd_event(0xA2, 0, KEYEVENTF_KEYDOWN, 0);
                keybd_event(0x4C, 0, KEYEVENTF_KEYDOWN, 0);
                keybd_event(0x4C, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(0xA2, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(0x5B, 0, KEYEVENTF_KEYUP, 0);
            }
            catch { }
        }
    }
}