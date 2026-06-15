using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Devices.Enumeration;

namespace DynamicIslandWindows.Services
{
    public class HardwareService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP   = 0x0002;

        private MMDeviceEnumerator? _deviceEnumerator;
        private MMDevice? _defaultAudioDevice;
        private readonly object _audioLock = new();
        private bool _disposed = false;

        private readonly object _brightnessWriteLock = new();
        private int _pendingBrightness = -1;
        private bool _isWritingBrightness = false;

        private MMDevice? GetDefaultAudioDevice()
        {
            lock (_audioLock)
            {
                try
                {
                    if (_deviceEnumerator == null)
                    {
                        _deviceEnumerator = new MMDeviceEnumerator();
                    }
                    if (_defaultAudioDevice == null)
                    {
                        _defaultAudioDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    }
                    return _defaultAudioDevice;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioDevice] Erro ao obter endpoint: {ex.Message}");
                    CleanupAudioDevice();
                    return null;
                }
            }
        }

        private void CleanupAudioDevice()
        {
            lock (_audioLock)
            {
                _defaultAudioDevice?.Dispose();
                _defaultAudioDevice = null;
                _deviceEnumerator?.Dispose();
                _deviceEnumerator = null;
            }
        }

        public int GetCurrentVolume()
        {
            try
            {
                var device = GetDefaultAudioDevice();
                if (device != null)
                {
                    return (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                }
                return 50;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeGet] {ex.Message}");
                CleanupAudioDevice();
                return 50;
            }
        }

        public void SetVolume(int volume)
        {
            try
            {
                var device = GetDefaultAudioDevice();
                if (device != null)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume / 100f, 0f, 1f);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeSet] {ex.Message}");
                CleanupAudioDevice();
            }
        }

        public bool IsMuted()
        {
            try
            {
                var device = GetDefaultAudioDevice();
                return device?.AudioEndpointVolume.Mute ?? false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMutedGet] {ex.Message}");
                CleanupAudioDevice();
                return false;
            }
        }

        public void ToggleMute()
        {
            try
            {
                var device = GetDefaultAudioDevice();
                if (device != null)
                {
                    device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeMuteToggle] {ex.Message}");
                CleanupAudioDevice();
            }
        }

        public int GetCurrentBrightness()
        {
            try
            {
                // BLINDAGEM: Executa fora da Thread Principal, com limite de espera (timeout) seguro
                var task = Task.Run(() =>
                {
                    using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                    using var instances = searcher.Get();
                    foreach (ManagementObject o in instances)
                    {
                        using (o)
                        {
                            return Convert.ToInt32(o.GetPropertyValue("CurrentBrightness"));
                        }
                    }
                    return 70;
                });

                // Espera no máximo 500ms. Se o Windows WMI travar, retorna 70 em vez de congelar o seu Widget.
                if (task.Wait(TimeSpan.FromMilliseconds(500))) return task.Result;
                return 70;
            }
            catch { return 70; }
        }

        public void SetBrightness(int brightness)
        {
            lock (_brightnessWriteLock)
            {
                _pendingBrightness = Math.Clamp(brightness, 0, 100);
                if (_isWritingBrightness)
                {
                    return;
                }
                _isWritingBrightness = true;
            }

            // BLINDAGEM: Não bloqueia o arrastar da barra no HTML / UI e executa de forma serializada coalescida
            Task.Run(() =>
            {
                while (true)
                {
                    int targetValue;
                    lock (_brightnessWriteLock)
                    {
                        targetValue = _pendingBrightness;
                        _pendingBrightness = -1;
                        if (targetValue == -1)
                        {
                            _isWritingBrightness = false;
                            break;
                        }
                    }

                    try
                    {
                        byte target = (byte)targetValue;
                        using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                        using var instances = searcher.Get();
                        foreach (ManagementObject o in instances)
                        {
                            using (o)
                            {
                                o.InvokeMethod("WmiSetBrightness", new object[] { 1, target });
                            }
                        }
                    }
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"[Brightness] Erro ao gravar brilho: {ex.Message}"); 
                    }
                }
            });
        }

        public async Task HandleToggleAsync(string setting, bool state)
        {
            switch (setting)
            {
                case "wifi":         await ToggleRadioAsync(RadioKind.WiFi, state);      break;
                case "bluetooth":    await ToggleRadioAsync(RadioKind.Bluetooth, state); break;
                case "airplane":     await ToggleAirplaneModeAsync(state);               break;
                case "open_settings":  OpenUri("ms-settings:");                          break;
                case "accessibility":  OpenUri("ms-settings:easeofaccess-display");      break;
                case "battery_saver":  OpenUri("ms-settings:batterysaver");              break;
                case "live_captions":  TriggerLiveCaptionsShortcut();                    break;
                case "night_light":    SetNightLight(state);                             break;
                case "mobile_hotspot": OpenUri("ms-settings:network-mobilehotspot");     break;
                case "nearby_share":   OpenUri("ms-settings:crossdevice");               break;
                case "cast":           OpenUri("ms-settings-connectabledevices:devicediscovery"); break;
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
                var target = state ? RadioState.On : RadioState.Off;
                foreach (var radio in radios)
                    if (radio.Kind == kind) await radio.SetStateAsync(target);
            }
            catch (Exception ex) { Debug.WriteLine($"[Radio] {ex.Message}"); }
        }

        private async Task ToggleAirplaneModeAsync(bool airplaneModeOn)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
                var radios = await Radio.GetRadiosAsync();
                var target = airplaneModeOn ? RadioState.Off : RadioState.On;
                foreach (var radio in radios) await radio.SetStateAsync(target);
            }
            catch (Exception ex) { Debug.WriteLine($"[AirplaneMode] {ex.Message}"); }
        }

        private void OpenUri(string uri)
        {
            try { Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"[OpenUri] {ex.Message}"); }
        }

        private void TriggerLiveCaptionsShortcut()
        {
            try
            {
                keybd_event(0x5B, 0, KEYEVENTF_KEYDOWN, 0); // VK_LWIN
                keybd_event(0xA2, 0, KEYEVENTF_KEYDOWN, 0); // VK_LCONTROL
                keybd_event(0x4C, 0, KEYEVENTF_KEYDOWN, 0); // L
                keybd_event(0x4C, 0, KEYEVENTF_KEYUP,   0);
                keybd_event(0xA2, 0, KEYEVENTF_KEYUP,   0);
                keybd_event(0x5B, 0, KEYEVENTF_KEYUP,   0);
            }
            catch (Exception ex) { Debug.WriteLine($"[Shortcut] {ex.Message}"); }
        }

        // P/Invoke para GammaRamp (Luz Noturna Direta)
        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref Ramp lpRamp);

        [DllImport("gdi32.dll")]
        private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref Ramp lpRamp);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
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
        private static bool _isNightLightActive = false;

        public void SetNightLight(bool active)
        {
            IntPtr hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return;

            try
            {
                if (active)
                {
                    // Salva a rampa original
                    if (!_isNightLightActive)
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

                    SetDeviceGammaRamp(hdc, ref warmRamp);
                    _isNightLightActive = true;
                }
                else
                {
                    // Restaura a rampa original
                    if (_originalRamp.HasValue)
                    {
                        var ramp = _originalRamp.Value;
                        SetDeviceGammaRamp(hdc, ref ramp);
                    }
                    else
                    {
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
                    _isNightLightActive = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NightLightGamma] {ex.Message}");
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        // LÓGICA DE WI-FI (APIs NATIVAS DO WINDOWS RUNTIME)
        public class WiFiNetworkInfo
        {
            public string Ssid { get; set; } = string.Empty;
            public int SignalBars { get; set; }
            public string Bssid { get; set; } = string.Empty;
            public string SecurityKind { get; set; } = string.Empty;
            public object? RawNetwork { get; set; } // WiFiAvailableNetwork
        }

        public async Task<List<WiFiNetworkInfo>> GetAvailableNetworksAsync()
        {
            var list = new List<WiFiNetworkInfo>();
            try
            {
                var access = await WiFiAdapter.RequestAccessAsync();
                if (access != WiFiAccessStatus.Allowed)
                {
                    Debug.WriteLine("[WiFi] Acesso ao adaptador negado.");
                    return list;
                }

                var deviceSelector = WiFiAdapter.GetDeviceSelector();
                var devices = await DeviceInformation.FindAllAsync(deviceSelector);
                if (devices.Count == 0)
                {
                    Debug.WriteLine("[WiFi] Nenhum adaptador Wi-Fi encontrado no sistema.");
                    return list;
                }

                var adapter = await WiFiAdapter.FromIdAsync(devices[0].Id);
                await adapter.ScanAsync();

                if (adapter.NetworkReport != null)
                {
                    foreach (var net in adapter.NetworkReport.AvailableNetworks)
                    {
                        if (string.IsNullOrEmpty(net.Ssid)) continue;
                        list.Add(new WiFiNetworkInfo
                        {
                            Ssid = net.Ssid,
                            SignalBars = net.SignalBars,
                            Bssid = net.Bssid,
                            SecurityKind = net.SecuritySettings.NetworkEncryptionType.ToString(),
                            RawNetwork = net
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WiFiScan] {ex.Message}");
            }
            return list;
        }

        public async Task<bool> ConnectToNetworkAsync(object rawNetwork, string password)
        {
            try
            {
                if (rawNetwork is not WiFiAvailableNetwork net) return false;

                var deviceSelector = WiFiAdapter.GetDeviceSelector();
                var devices = await DeviceInformation.FindAllAsync(deviceSelector);
                if (devices.Count == 0) return false;

                var adapter = await WiFiAdapter.FromIdAsync(devices[0].Id);
                
                var credential = new Windows.Security.Credentials.PasswordCredential();
                if (!string.IsNullOrEmpty(password))
                {
                    credential.Password = password;
                }

                var connectionResult = await adapter.ConnectAsync(net, WiFiReconnectionKind.Automatic, credential);
                return connectionResult.ConnectionStatus == WiFiConnectionStatus.Success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WiFiConnect] {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CleanupAudioDevice();
            GC.SuppressFinalize(this);
        }
    }
}
