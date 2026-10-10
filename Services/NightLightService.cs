using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Serviço de gerenciamento de Luz Noturna (Warm Color Temperature) via GDI32 SetDeviceGammaRamp.
    /// Responsabilidade Única (SRP): Ajuste de temperatura de cor e restauração estrita do Gamma Ramp original do monitor.
    /// </summary>
    public class NightLightService : INightLightService
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref Ramp lpRamp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref Ramp lpRamp);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct Ramp
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;
        }

        private static Ramp? _originalRamp;
        private bool _isNightLightActive = false;
        private bool _disposed = false;

        public bool IsActive => _isNightLightActive;

        public bool SetNightLight(bool active)
        {
            if (_disposed) return false;

            IntPtr hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                Debug.WriteLine("[NightLightService.SetNightLight] Não foi possível criar DC para o display.");
                return false;
            }

            try
            {
                if (active)
                {
                    // Salva a rampa original se ainda não salvou
                    if (!_isNightLightActive && !_originalRamp.HasValue)
                    {
                        var currentRamp = new Ramp
                        {
                            Red = new ushort[256],
                            Green = new ushort[256],
                            Blue = new ushort[256]
                        };

                        if (GetDeviceGammaRamp(hdc, ref currentRamp))
                        {
                            _originalRamp = currentRamp;
                        }
                    }

                    // Rampa de cor aquecida (Warm/Night Light)
                    var warmRamp = new Ramp
                    {
                        Red = new ushort[256],
                        Green = new ushort[256],
                        Blue = new ushort[256]
                    };

                    for (int i = 0; i < 256; i++)
                    {
                        ushort baseVal = (ushort)(i * 257); // Escala linear 0-65535
                        warmRamp.Red[i] = baseVal;
                        warmRamp.Green[i] = (ushort)(baseVal * 0.85); // Reduz verde em 15%
                        warmRamp.Blue[i] = (ushort)(baseVal * 0.60);  // Reduz azul em 40%
                    }

                    if (SetDeviceGammaRamp(hdc, ref warmRamp))
                    {
                        _isNightLightActive = true;
                        return true;
                    }

                    // Alguns drivers/Windows rejeitam rampas muito diferentes da linear
                    Debug.WriteLine($"[NightLightService.SetNightLight] SetDeviceGammaRamp rejeitado (erro {Marshal.GetLastWin32Error()}).");
                    return false;
                }

                RestoreOriginalRampInternal(hdc, allowLinearFallback: true);
                _isNightLightActive = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NightLightService.SetNightLight] Erro ao aplicar gamma ramp: {ex.Message}");
                return false;
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        public void RestoreOriginalRamp()
        {
            RestoreGammaRamp(allowLinearFallback: _isNightLightActive);
            _isNightLightActive = false;
        }

        /// <summary>
        /// Restaura a rampa salva antes da Luz Noturna. Sem rampa salva (Luz Noturna nunca ativada),
        /// não toca no gama, para não sobrescrever a calibração/perfil ICC do usuário.
        /// </summary>
        public static void RestoreDefaultGammaRamp() => RestoreGammaRamp(allowLinearFallback: false);

        private static void RestoreGammaRamp(bool allowLinearFallback)
        {
            if (!_originalRamp.HasValue && !allowLinearFallback) return;

            IntPtr hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return;

            try
            {
                RestoreOriginalRampInternal(hdc, allowLinearFallback);
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        private static void RestoreOriginalRampInternal(IntPtr hdc, bool allowLinearFallback)
        {
            try
            {
                if (_originalRamp.HasValue)
                {
                    var ramp = _originalRamp.Value;
                    SetDeviceGammaRamp(hdc, ref ramp);
                }
                else if (allowLinearFallback)
                {
                    // Rampa linear padrão de restauração
                    var linearRamp = new Ramp
                    {
                        Red = new ushort[256],
                        Green = new ushort[256],
                        Blue = new ushort[256]
                    };
                    for (int i = 0; i < 256; i++)
                    {
                        ushort val = (ushort)(i * 257);
                        linearRamp.Red[i] = val;
                        linearRamp.Green[i] = val;
                        linearRamp.Blue[i] = val;
                    }
                    SetDeviceGammaRamp(hdc, ref linearRamp);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NightLightService.RestoreOriginalRampInternal] Erro ao restaurar gamma: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // [BLOCKER 1 FIX]: Garante que a rampa original seja restaurada ao descartar o serviço
            if (_isNightLightActive)
            {
                RestoreOriginalRamp();
            }

            GC.SuppressFinalize(this);
        }
    }
}
