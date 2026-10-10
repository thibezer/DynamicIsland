using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DynamicIslandWindows
{
    public enum BackdropType : int
    {
        Default = 0,
        Mica = 2,
        Acrylic = 3,
        MicaAlt = 4
    }

    public enum CornerPreference : int
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3
    }

    [Flags]
    public enum RainbowFlags
    {
        None = 0,
        Title = 1 << 0,
        Border = 1 << 1,
        Text = 1 << 2
    }

    public class WindowEffectManager
    {
        // Constantes do DWM
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);

        private DispatcherTimer? _rainbowTimer;
        private double _currentHue = 0;
        private IntPtr _windowHandle;
        private RainbowFlags _rainbowFlags;
        private float _rainbowSpeed;

        // Obtém o HWND de uma Window do WPF
        private static IntPtr GetHandle(Window window)
        {
            return new WindowInteropHelper(window).EnsureHandle();
        }

        public static void ApplyBackdrop(Window window, BackdropType type)
        {
            IntPtr hwnd = GetHandle(window);
            int val = (int)type;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref val, sizeof(int));
        }

        public static void SetCornerPreference(Window window, CornerPreference preference)
        {
            IntPtr hwnd = GetHandle(window);
            int val = (int)preference;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref val, sizeof(int));
        }

        public static void SetImmersiveDarkMode(Window window, bool enabled)
        {
            IntPtr hwnd = GetHandle(window);
            int val = enabled ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref val, sizeof(int));
        }

        public static void SetCustomColors(Window window, int? border = null, int? caption = null, int? text = null)
        {
            IntPtr hwnd = GetHandle(window);
            int val;
            
            if (border.HasValue) { val = border.Value; DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref val, sizeof(int)); }
            if (caption.HasValue) { val = caption.Value; DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref val, sizeof(int)); }
            if (text.HasValue) { val = text.Value; DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref val, sizeof(int)); }
        }

        // Ativa o Efeito Rainbow nativo via DispatcherTimer
        public void EnableRainbow(Window window, RainbowFlags flags, float speed = 1.0f)
        {
            DisableRainbow();

            _windowHandle = GetHandle(window);
            _rainbowFlags = flags;
            _rainbowSpeed = speed;

            _rainbowTimer = new DispatcherTimer(DispatcherPriority.Render);
            _rainbowTimer.Interval = TimeSpan.FromMilliseconds(32); // ~30 FPS
            _rainbowTimer.Tick += RainbowTimer_Tick;
            _rainbowTimer.Start();
        }

        public void DisableRainbow()
        {
            if (_rainbowTimer != null)
            {
                _rainbowTimer.Stop();
                _rainbowTimer = null;
            }
        }

        private void RainbowTimer_Tick(object? sender, EventArgs e)
        {
            if (_windowHandle == IntPtr.Zero) return;

            _currentHue = (_currentHue + (_rainbowSpeed * 2.0)) % 360.0;
            int colorBGR = HSLToBGR((float)_currentHue, 1.0f, 0.5f);

            int attrVal = colorBGR;
            if (_rainbowFlags.HasFlag(RainbowFlags.Border))
                DwmSetWindowAttribute(_windowHandle, DWMWA_BORDER_COLOR, ref attrVal, sizeof(int));
            if (_rainbowFlags.HasFlag(RainbowFlags.Title))
                DwmSetWindowAttribute(_windowHandle, DWMWA_CAPTION_COLOR, ref attrVal, sizeof(int));
            if (_rainbowFlags.HasFlag(RainbowFlags.Text))
                DwmSetWindowAttribute(_windowHandle, DWMWA_TEXT_COLOR, ref attrVal, sizeof(int));
        }

        // Conversão de HSL para BGR (formato que o DWM espera: 0x00BBGGRR)
        private static int HSLToBGR(float h, float s, float l)
        {
            float c = (1.0f - Math.Abs(2.0f * l - 1.0f)) * s;
            float x = c * (1.0f - Math.Abs((h / 60.0f) % 2.0f - 1.0f));
            float m = l - c / 2.0f;

            float r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }

            byte red = (byte)((r + m) * 255);
            byte green = (byte)((g + m) * 255);
            byte blue = (byte)((b + m) * 255);

            // Retorna no formato Win32 COLORREF / BGR
            return (blue << 16) | (green << 8) | red;
        }
    }
}
