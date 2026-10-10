using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.IO;
using System.Text.Json;

namespace DynamicIslandWindows.Services
{
    public class AppHotkeyRule
    {
        public string ProcessName { get; set; } = string.Empty;     // Nome do processo (ex: "notepad")
        public string Shortcut { get; set; } = string.Empty;        // Atalho (ex: "Shift+F")
        public string ActionType { get; set; } = "ShowNotification"; // Ação: "ShowNotification", "ExecuteCommand"
        public string ActionParameter { get; set; } = string.Empty; // Texto da notificação ou comando a rodar
    }

    public class AppHotkeyTriggeredEventArgs : EventArgs
    {
        public AppHotkeyRule Rule { get; }
        public AppHotkeyTriggeredEventArgs(AppHotkeyRule rule)
        {
            Rule = rule;
        }
    }

    public class AppHotkeyService : IDisposable
    {
        // P/Invokes para Hook Global
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // P/Invokes para obter a janela e processo ativo
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, [Out] StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // P/Invokes para injeção de caracteres Unicode
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const int LLKHF_INJECTED = 0x10;
        // Tecla virtual não atribuída: injetada entre o Alt pressionado e sua liberação para que
        // o Windows/app não interprete o "Alt solto sozinho" como ativação da barra de menus
        private const ushort VK_MENU_SUPPRESS = 0xE8;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        // Modificadores Virtuais de Teclado
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private readonly List<AppHotkeyRule> _rules = new List<AppHotkeyRule>();
        private bool _disposed = false;

        public event EventHandler<AppHotkeyTriggeredEventArgs>? HotkeyTriggered;

        public AppHotkeyService()
        {
            _proc = HookCallback;
            _hookId = SetHook(_proc);
            LoadOrCreateConfig();
        }

        private string GetConfigPath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIslandWindows");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "app_hotkeys.json");
        }

        private void LoadOrCreateConfig()
        {
            string path = GetConfigPath();
            bool configChanged = false;

            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loadedRules = JsonSerializer.Deserialize<List<AppHotkeyRule>>(json);
                    if (loadedRules != null)
                    {
                        _rules.AddRange(loadedRules);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyServiceLoad] Erro ao ler config: {ex.Message}");
            }

            // Obs.: não há regras de exemplo para Bloco de Notas/Chrome. Elas consumiam teclas de uso
            // comum (Shift+F engolia o "F" maiúsculo no Bloco de Notas). Adicione regras próprias no JSON.

            // Garante que todas as 15 regras do Notion existam no arquivo
            var notionRules = new List<(string shortcut, string parameter)>
            {
                ("Shift+Space", "\\space "),
                ("Shift+*", "\\times "),
                ("Shift+/", "\\frac {}{}"),
                ("Shift++", "\\sqrt "),
                ("Alt+a", "\\alpha "),
                ("Alt+b", "\\beta "),
                ("Alt+g", "\\gamma "),
                ("Alt+d", "\\Delta "),
                ("Alt+l", "\\lambda "), 
                ("Alt+m", "\\mu "),
                ("Alt+n", "\\nu "),
                ("Alt+p", "\\pi "),
                ("Alt+f", "\\phi "),
                ("Alt+s", "\\Sigma "),
                ("Alt+o", "\\Omega ")
            };

            foreach (var item in notionRules)
            {
                bool exists = _rules.Exists(r => r.ProcessName.Equals("notion", StringComparison.OrdinalIgnoreCase) && r.Shortcut.Equals(item.shortcut, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    _rules.Add(new AppHotkeyRule 
                    { 
                        ProcessName = "notion", 
                        Shortcut = item.shortcut, 
                        ActionType = "InjectText", 
                        ActionParameter = item.parameter 
                    });
                    configChanged = true;
                }
            }

            if (configChanged)
            {
                SaveConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                string path = GetConfigPath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_rules, options);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyServiceSave] {ex.Message}");
            }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName!), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                // KBDLLHOOKSTRUCT: vkCode (offset 0), scanCode (4), flags (8)
                int vkCode = Marshal.ReadInt32(lParam);
                int flags = Marshal.ReadInt32(lParam, 8);

                // Ignora teclas sintéticas (inclusive as que o próprio SendText injeta)
                if ((flags & LLKHF_INJECTED) == 0)
                {
                    try
                    {
                        if (CheckHotkey(vkCode))
                        {
                            return (IntPtr)1; // Consome a tecla original
                        }
                    }
                    catch (Exception ex)
                    {
                        // Exceção dentro do hook derrubaria o processo: apenas registra
                        Debug.WriteLine($"[HotkeyHook] {ex.Message}");
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void AddKeyInput(List<INPUT> inputs, ushort vk, uint flags)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }

        public static void SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Verifica o estado físico atual das teclas modificadoras
            bool shiftPressionado = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool ctrlPressionado = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool altPressionado = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool winPressionado = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            var inputs = new List<INPUT>();

            // Solta temporariamente os modificadores físicos para evitar que o Windows interprete o texto digitado como atalhos/comandos de menu
            if (altPressionado)
            {
                AddKeyInput(inputs, VK_MENU_SUPPRESS, 0);
                AddKeyInput(inputs, VK_MENU_SUPPRESS, KEYEVENTF_KEYUP);
                AddKeyInput(inputs, VK_MENU, KEYEVENTF_KEYUP);
            }
            if (ctrlPressionado) AddKeyInput(inputs, VK_CONTROL, KEYEVENTF_KEYUP);
            if (shiftPressionado) AddKeyInput(inputs, VK_SHIFT, KEYEVENTF_KEYUP);
            if (winPressionado) AddKeyInput(inputs, VK_LWIN, KEYEVENTF_KEYUP);

            // Injeta o texto caracter por caractere
            foreach (char c in text)
            {
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                });
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                });
            }

            // Restaura o estado físico original das teclas modificadoras
            if (winPressionado) AddKeyInput(inputs, VK_LWIN, 0);
            if (shiftPressionado) AddKeyInput(inputs, VK_SHIFT, 0);
            if (ctrlPressionado) AddKeyInput(inputs, VK_CONTROL, 0);
            if (altPressionado)
            {
                AddKeyInput(inputs, VK_MENU, 0);
                // Quando o usuário soltar o Alt físico, ele não estará "sozinho" (não abre o menu)
                AddKeyInput(inputs, VK_MENU_SUPPRESS, 0);
                AddKeyInput(inputs, VK_MENU_SUPPRESS, KEYEVENTF_KEYUP);
            }

            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        // Cache por PID (e não por HWND, que pode ser reutilizado por outro processo)
        private uint _cachedProcessId = 0;
        private string _cachedProcessName = string.Empty;

        private string GetActiveProcessName(IntPtr foregroundWindow)
        {
            if (foregroundWindow == IntPtr.Zero) return string.Empty;

            GetWindowThreadProcessId(foregroundWindow, out uint processId);
            if (processId == 0) return string.Empty;
            if (processId == _cachedProcessId) return _cachedProcessName;

            _cachedProcessId = processId;

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    {
                        _cachedProcessName = Path.GetFileNameWithoutExtension(sb.ToString());
                        return _cachedProcessName;
                    }
                }
                catch
                {
                    // Silencia e tenta fallback caso ocorra algum erro
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }

            // Fallback caso OpenProcess retorne IntPtr.Zero (ex: permissões elevadas ou processo protegido)
            try
            {
                using (Process proc = Process.GetProcessById((int)processId))
                {
                    _cachedProcessName = proc.ProcessName;
                    return _cachedProcessName;
                }
            }
            catch
            {
                return _cachedProcessName = string.Empty;
            }
        }

        private bool CheckHotkey(int vkCode)
        {
            // Verifica os modificadores ativos atualmente
            bool isShift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            bool isCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool isAlt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            bool isWin = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            // Obtém a janela em primeiro plano
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return false;

            string activeProcessName = GetActiveProcessName(foregroundWindow);
            if (string.IsNullOrEmpty(activeProcessName)) return false;

            foreach (var rule in _rules)
            {
                if (string.Equals(rule.ProcessName, activeProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    if (MatchShortcut(rule.Shortcut, vkCode, isShift, isCtrl, isAlt, isWin))
                    {
                        if (rule.ActionType.Equals("InjectText", StringComparison.OrdinalIgnoreCase))
                        {
                            string textToInject = rule.ActionParameter;
                            System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(50);
                                SendText(textToInject);
                            });
                        }

                        // Atalho corresponde e aplicativo corresponde!
                        HotkeyTriggered?.Invoke(this, new AppHotkeyTriggeredEventArgs(rule));
                        return true; // Consome a tecla original
                    }
                }
            }
            return false;
        }

        private bool MatchShortcut(string shortcutStr, int vkCode, bool isShift, bool isCtrl, bool isAlt, bool isWin)
        {
            bool reqShift = false;
            bool reqCtrl = false;
            bool reqAlt = false;
            bool reqWin = false;
            string keyName = "";

            // A própria tecla pode ser "+" (ex.: "Shift++"); um Split('+') simples a perderia
            string modifiersPart = shortcutStr;
            if (shortcutStr.EndsWith("++") || shortcutStr.Trim() == "+")
            {
                keyName = "+";
                modifiersPart = shortcutStr.TrimEnd().Substring(0, Math.Max(0, shortcutStr.TrimEnd().Length - 1));
            }

            foreach (var part in modifiersPart.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "shift") reqShift = true;
                else if (p == "ctrl" || p == "control") reqCtrl = true;
                else if (p == "alt") reqAlt = true;
                else if (p == "win" || p == "windows") reqWin = true;
                else keyName = p;
            }

            // Tolera Alt Gr: Se a regra requer Alt (reqAlt == true) e o Alt físico está ativo (isAlt == true),
            // permitimos que o Ctrl físico (isCtrl) seja verdadeiro (pois Alt Gr envia Ctrl+Alt).
            bool ctrlMatches = (isCtrl == reqCtrl) || (isCtrl && isAlt && reqAlt && !reqCtrl);
            if (!ctrlMatches) return false;

            if (isAlt != reqAlt) return false;
            if (isWin != reqWin) return false;

            if (keyName == "*")
            {
                if (vkCode == 0x6A) return true; // Multiply do teclado numérico (não exige Shift)
                if (vkCode == 0x38 && isShift) return true; // Tecla 8 alfanumérica com Shift
                return false;
            }

            if (keyName == "+")
            {
                if (vkCode == 0x6B && !isShift) return true; // Tecla + do teclado numérico (sem Shift)
                if (vkCode == 0xBB && isShift) return true;  // Tecla =/+ alfanumérica com Shift
                return false;
            }

            if (keyName == "/")
            {
                if (vkCode == 0x6F) return true; // DIVIDE do teclado numérico (não exige Shift)
                if ((vkCode == 0xBF || vkCode == 0xC1) && isShift == reqShift) return true; // OEM_2 ou ABNT_C1 sem Shift
                return false;
            }

            if (isShift != reqShift) return false;

            int targetVk = ParseKeyNameToVk(keyName);
            return vkCode == targetVk;
        }

        private int ParseKeyNameToVk(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return 0;
            keyName = keyName.ToLower();

            if (keyName == "espaço" || keyName == "espaco" || keyName == "space") return 0x20;
            if (keyName == "*") return 0x38;
            if (keyName == "/") return 0xBF;
            if (keyName == "+") return 0xBB;

            // Mapeamentos comuns de teclas
            if (keyName.Length == 1)
            {
                char c = keyName[0];
                if (c >= 'a' && c <= 'z') return 0x41 + (c - 'a');
                if (c >= '0' && c <= '9') return 0x30 + (c - '0');
            }

            // Teclas especiais e de função
            switch (keyName)
            {
                case "f1": return 0x70;
                case "f2": return 0x71;
                case "f3": return 0x72;
                case "f4": return 0x73;
                case "f5": return 0x74;
                case "f6": return 0x75;
                case "f7": return 0x76;
                case "f8": return 0x77;
                case "f9": return 0x78;
                case "f10": return 0x79;
                case "f11": return 0x7A;
                case "f12": return 0x7B;
                case "enter": return 0x0D;
                case "tab": return 0x09;
                case "escape": return 0x1B;
                case "backspace": return 0x08;
                case "delete": return 0x2E;
                case "insert": return 0x2D;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pgup": case "pageup": return 0x21;
                case "pgdn": case "pagedown": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
            }

            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}
