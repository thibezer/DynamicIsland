using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIslandWindows.Services
{
    public enum PipTriggerOutcome
    {
        Success,
        MovedExisting,
        ExtensionNotConnected,
        NoYouTubeTabFound,
        WindowNotDetected,
        Error
    }

    /// <summary>
    /// Gerencia a comunicação local (WebSocket) com a extensão do Chrome para ativar o Picture-in-Picture
    /// do YouTube e reposiciona a janela nativa de PiP do navegador via Win32 para a coordenada escolhida.
    /// </summary>
    public class YouTubePipService : IDisposable
    {
        public const int DefaultPort = 49152;
        private readonly string _listenerPrefix = $"http://127.0.0.1:{DefaultPort}/";

        private HttpListener? _httpListener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
        private TaskCompletionSource<string>? _pendingCommandResponse;
        private readonly object _responseLock = new();
        private bool _disposed = false;

        public bool IsExtensionConnected
        {
            get
            {
                foreach (var ws in _clients.Values)
                {
                    if (ws.State == WebSocketState.Open) return true;
                }
                return false;
            }
        }

        public void Start()
        {
            if (_disposed || _httpListener != null) return;

            try
            {
                _cts = new CancellationTokenSource();
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add(_listenerPrefix);
                _httpListener.Start();

                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
                IslandLogger.Log("PIP_SERVER_START", $"Servidor WebSocket PiP iniciado em {_listenerPrefix}");
            }
            catch (Exception ex)
            {
                IslandLogger.Log("PIP_SERVER_ERROR", "Falha ao iniciar servidor WebSocket PiP", ex);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _httpListener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[YouTubePipService.Accept] {ex.Message}");
                    continue;
                }

                if (context.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketClientAsync(context, ct), ct);
                }
                else
                {
                    // Endpoint simples de health-check HTTP
                    try
                    {
                        byte[] body = Encoding.UTF8.GetBytes("{\"service\":\"DynamicIsland.PiP\",\"ok\":true}");
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body, ct);
                        context.Response.Close();
                    }
                    catch { }
                }
            }
        }

        private async Task HandleWebSocketClientAsync(HttpListenerContext context, CancellationToken ct)
        {
            Guid clientId = Guid.NewGuid();
            WebSocket? ws = null;
            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                ws = wsContext.WebSocket;
                _clients[clientId] = ws;
                IslandLogger.Log("PIP_EXT_CONNECTED", $"Extensão Chrome conectada ({clientId}).");

                var buffer = new byte[4096];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string message = Encoding.UTF8.GetString(ms.ToArray());
                    HandleIncomingMessage(ws, message, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YouTubePipService.WS] {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                if (ws != null)
                {
                    try { ws.Dispose(); } catch { }
                }
            }
        }

        private void HandleIncomingMessage(WebSocket ws, string message, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp))
                {
                    string? msgType = typeProp.GetString();
                    if (msgType == "ping")
                    {
                        _ = SendJsonAsync(ws, "{\"type\":\"pong\"}", ct);
                        return;
                    }
                    if (msgType == "pip_result")
                    {
                        lock (_responseLock)
                        {
                            _pendingCommandResponse?.TrySetResult(message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YouTubePipService.Parse] {ex.Message}");
            }
        }

        private static async Task SendJsonAsync(WebSocket ws, string json, CancellationToken ct)
        {
            if (ws.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        /// <summary>
        /// Abre o Picture-in-Picture do YouTube via extensão do Chrome (ou reutiliza a janela PiP já aberta)
        /// e a posiciona centralizada em (targetCenterX, targetCenterY) em pixels físicos de tela.
        /// </summary>
        public async Task<PipTriggerOutcome> OpenOrMovePipAsync(int targetCenterX, int targetCenterY)
        {
            // 1. Se já existe uma janela PiP do Chrome aberta, apenas a move para a nova posição
            IntPtr existingPip = FindExistingChromePipWindow();
            if (existingPip != IntPtr.Zero)
            {
                MoveWindowCenteredClamped(existingPip, targetCenterX, targetCenterY);
                IslandLogger.Log("PIP_MOVED_EXISTING", $"Janela PiP existente movida para ({targetCenterX}, {targetCenterY}).");
                return PipTriggerOutcome.MovedExisting;
            }

            // 2. Verifica se há extensão conectada
            WebSocket? activeSocket = null;
            foreach (var kv in _clients)
            {
                if (kv.Value.State == WebSocketState.Open)
                {
                    activeSocket = kv.Value;
                    break;
                }
                else
                {
                    _clients.TryRemove(kv.Key, out _);
                }
            }

            if (activeSocket == null)
            {
                IslandLogger.Log("PIP_NO_EXTENSION", "Nenhuma extensão Chrome conectada ao servidor PiP.");
                return PipTriggerOutcome.ExtensionNotConnected;
            }

            // 3. Captura as janelas candidatas existentes antes de disparar o PiP
            HashSet<IntPtr> beforeHwnds = GetChromePipCandidateHwnds();

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_responseLock)
            {
                _pendingCommandResponse = tcs;
            }

            try
            {
                await SendJsonAsync(activeSocket, "{\"action\":\"pip\"}", _cts?.Token ?? CancellationToken.None);

                // Aguarda resposta da extensão (máximo 2.5s) em paralelo com a detecção da janela
                var watchTask = WaitForNewChromePipWindowAsync(beforeHwnds, TimeSpan.FromMilliseconds(2800));
                var extTimeoutTask = Task.Delay(2500);
                await Task.WhenAny(tcs.Task, extTimeoutTask);

                if (tcs.Task.IsCompletedSuccessfully)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(tcs.Task.Result);
                        if (doc.RootElement.TryGetProperty("ok", out var okProp) && !okProp.GetBoolean())
                        {
                            string? reason = doc.RootElement.TryGetProperty("error", out var errProp)
                                ? errProp.GetString()
                                : "unknown";
                            IslandLogger.Log("PIP_EXT_REJECTED", $"Extensão retornou erro: {reason}");
                            if (reason == "no_youtube_tab")
                                return PipTriggerOutcome.NoYouTubeTabFound;
                            return PipTriggerOutcome.Error;
                        }
                    }
                    catch { }
                }

                IntPtr newPipHwnd = await watchTask;
                if (newPipHwnd != IntPtr.Zero)
                {
                    // Pequeno delay para o Chrome finalizar o layout inicial do frame PiP antes de mover
                    await Task.Delay(35);
                    MoveWindowCenteredClamped(newPipHwnd, targetCenterX, targetCenterY);
                    IslandLogger.Log("PIP_OPENED_AND_MOVED", $"PiP aberto e movido para ({targetCenterX}, {targetCenterY}).");
                    return PipTriggerOutcome.Success;
                }

                IslandLogger.Log("PIP_WINDOW_NOT_FOUND", "Comando enviado, mas a janela PiP do Chrome não foi detectada a tempo.");
                return PipTriggerOutcome.WindowNotDetected;
            }
            catch (Exception ex)
            {
                IslandLogger.Log("PIP_TRIGGER_ERROR", "Erro ao acionar PiP do YouTube", ex);
                return PipTriggerOutcome.Error;
            }
            finally
            {
                lock (_responseLock)
                {
                    if (_pendingCommandResponse == tcs)
                        _pendingCommandResponse = null;
                }
            }
        }

        private static async Task<IntPtr> WaitForNewChromePipWindowAsync(HashSet<IntPtr> beforeHwnds, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                IntPtr found = FindNewChromePipWindow(beforeHwnds);
                if (found != IntPtr.Zero)
                    return found;

                await Task.Delay(25);
            }
            return IntPtr.Zero;
        }

        // ─── Detecção e Posicionamento Win32 da Janela PiP do Chrome ──────────────

        private static IntPtr FindExistingChromePipWindow()
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (IsLikelyChromePipWindow(hwnd, requirePipTitle: true))
                {
                    result = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static IntPtr FindNewChromePipWindow(HashSet<IntPtr> beforeHwnds)
        {
            IntPtr fallback = IntPtr.Zero;
            IntPtr exact = IntPtr.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (beforeHwnds.Contains(hwnd)) return true;

                if (IsLikelyChromePipWindow(hwnd, requirePipTitle: true))
                {
                    exact = hwnd;
                    return false;
                }
                if (fallback == IntPtr.Zero && IsLikelyChromePipWindow(hwnd, requirePipTitle: false))
                {
                    fallback = hwnd;
                }
                return true;
            }, IntPtr.Zero);

            return exact != IntPtr.Zero ? exact : fallback;
        }

        private static HashSet<IntPtr> GetChromePipCandidateHwnds()
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((hwnd, _) =>
            {
                if (IsLikelyChromePipWindow(hwnd, requirePipTitle: false))
                    set.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        private static bool IsLikelyChromePipWindow(IntPtr hwnd, bool requirePipTitle)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TOPMOST) == 0) return false;

            var classSb = new StringBuilder(64);
            GetClassName(hwnd, classSb, classSb.Capacity);
            string className = classSb.ToString();
            if (!className.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return false;

            if (!GetWindowRect(hwnd, out RECT rect)) return false;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            // Tamanho típico da janela PiP do Chrome (evita popups minúsculos ou janelas maximizadas)
            if (w < 180 || h < 100 || w > 1200 || h > 900) return false;

            // Verifica se pertence ao processo chrome.exe
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsChromeProcess(pid)) return false;

            if (requirePipTitle)
            {
                int len = GetWindowTextLength(hwnd);
                if (len > 0)
                {
                    var titleSb = new StringBuilder(len + 1);
                    GetWindowText(hwnd, titleSb, titleSb.Capacity);
                    string title = titleSb.ToString();
                    if (title.Contains("Picture in picture", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Picture-in-picture", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Imagem em imagem", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Imagem dentro", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            return true;
        }

        private static bool IsChromeProcess(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void MoveWindowCenteredClamped(IntPtr hwnd, int targetCenterX, int targetCenterY)
        {
            if (!GetWindowRect(hwnd, out RECT rect)) return;

            int width = Math.Max(200, rect.Right - rect.Left);
            int height = Math.Max(120, rect.Bottom - rect.Top);

            int desiredX = targetCenterX - (width / 2);
            int desiredY = targetCenterY - (height / 2);

            // Garante que a janela fique dentro da área de trabalho do monitor onde o usuário soltou
            IntPtr hMonitor = MonitorFromPoint(new POINT { X = targetCenterX, Y = targetCenterY }, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    RECT work = mi.rcWork;
                    desiredX = Math.Clamp(desiredX, work.Left, Math.Max(work.Left, work.Right - width));
                    desiredY = Math.Clamp(desiredY, work.Top, Math.Max(work.Top, work.Bottom - height));
                }
            }

            SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                desiredX,
                desiredY,
                0,
                0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts?.Cancel(); } catch { }
            foreach (var ws in _clients.Values)
            {
                try { ws.Dispose(); } catch { }
            }
            _clients.Clear();
            try { _httpListener?.Stop(); _httpListener?.Close(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }

        // ─── P/Invoke Win32 ──────────────────────────────────────────────────────

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }
}
