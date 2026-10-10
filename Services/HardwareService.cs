using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Fachada unificada (Facade Pattern) para controle de hardware do sistema operacional.
    /// Delega para serviços especializados coesos (Audio, Brilho, Luz Noturna e WiFi)
    /// mantendo estabilidade de interface pública sem quebras de compatibilidade.
    /// </summary>
    public class HardwareService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP   = 0x0002;

        private readonly IAudioService _audioService;
        private readonly IBrightnessService _brightnessService;
        private readonly INightLightService _nightLightService;
        private readonly IWiFiService _wifiService;

        private bool _disposed = false;

        public HardwareService()
            : this(new AudioService(), new BrightnessService(), new NightLightService(), new WiFiService())
        {
        }

        public HardwareService(
            IAudioService audioService,
            IBrightnessService brightnessService,
            INightLightService nightLightService,
            IWiFiService wifiService)
        {
            _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
            _brightnessService = brightnessService ?? throw new ArgumentNullException(nameof(brightnessService));
            _nightLightService = nightLightService ?? throw new ArgumentNullException(nameof(nightLightService));
            _wifiService = wifiService ?? throw new ArgumentNullException(nameof(wifiService));
        }

        // ─── Áudio ────────────────────────────────────────────────────────────
        public int GetCurrentVolume() => _audioService.GetCurrentVolume();
        public void SetVolume(int volume) => _audioService.SetVolume(volume);
        public bool IsMuted() => _audioService.IsMuted();
        public void ToggleMute() => _audioService.ToggleMute();

        // ─── Brilho ───────────────────────────────────────────────────────────
        public int GetCurrentBrightness() => _brightnessService.GetCurrentBrightness();
        public void SetBrightness(int brightness) => _brightnessService.SetBrightness(brightness);

        // ─── Luz Noturna ──────────────────────────────────────────────────────
        public bool SetNightLight(bool active) => _nightLightService.SetNightLight(active);
        public bool IsNightLightActive => _nightLightService.IsActive;

        // ─── Wi-Fi ────────────────────────────────────────────────────────────
        public class WiFiNetworkInfo
        {
            public string Ssid { get; set; } = string.Empty;
            public int SignalBars { get; set; }
            public string Bssid { get; set; } = string.Empty;
            public string SecurityKind { get; set; } = string.Empty;
            public object? RawNetwork { get; set; }
        }

        public Task<List<WiFiNetworkInfo>> GetAvailableNetworksAsync() => _wifiService.GetAvailableNetworksAsync();
        public Task<bool> ConnectToNetworkAsync(object rawNetwork, string password) => _wifiService.ConnectToNetworkAsync(rawNetwork, password);
        public Task<string?> GetConnectedNetworkSsidAsync() => _wifiService.GetConnectedNetworkSsidAsync();

        // ─── Ações de Toggle Rápidas ──────────────────────────────────────────
        /// <summary>
        /// Executa a ação do toggle. Retorna false se a ação falhou (ex.: acesso ao rádio negado),
        /// para que a UI possa reverter o estado visual.
        /// </summary>
        public async Task<bool> HandleToggleAsync(string setting, bool state)
        {
            switch (setting)
            {
                case "wifi":           return await ToggleRadioAsync(RadioKind.WiFi, state);
                case "bluetooth":      return await ToggleRadioAsync(RadioKind.Bluetooth, state);
                case "airplane":       return await ToggleAirplaneModeAsync(state);
                case "night_light":    return SetNightLight(state);
                case "open_settings":  return OpenUri("ms-settings:");
                case "accessibility":  return OpenUri("ms-settings:easeofaccess-display");
                case "battery_saver":  return OpenUri("ms-settings:batterysaver");
                case "live_captions":  return TriggerLiveCaptionsShortcut();
                case "mobile_hotspot": return OpenUri("ms-settings:network-mobilehotspot");
                case "nearby_share":   return OpenUri("ms-settings:crossdevice");
                case "cast":           return OpenUri("ms-settings-connectabledevices:devicediscovery");
                case "project":
                    try { Process.Start(new ProcessStartInfo("DisplaySwitch.exe") { UseShellExecute = true }); return true; }
                    catch (Exception ex) { Debug.WriteLine($"[ProjectDisplaySwitch] {ex.Message}"); return false; }
            }
            return false;
        }

        /// <summary>Estado real do rádio, ou null se não foi possível consultá-lo.</summary>
        public async Task<bool?> GetRadioStateAsync(RadioKind kind)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
                var radios = await Radio.GetRadiosAsync();
                var radio = radios.FirstOrDefault(r => r.Kind == kind);
                if (radio != null)
                {
                    return radio.State == RadioState.On;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetRadioState] {ex.Message}");
            }
            return null;
        }

        private static async Task<bool> ToggleRadioAsync(RadioKind kind, bool state)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return false;
                var radios = await Radio.GetRadiosAsync();
                var target = state ? RadioState.On : RadioState.Off;
                bool found = false;
                bool ok = true;
                foreach (var radio in radios)
                {
                    if (radio.Kind == kind)
                    {
                        found = true;
                        ok &= await radio.SetStateAsync(target) == RadioAccessStatus.Allowed;
                    }
                }
                return found && ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadioToggle] {ex.Message}");
                return false;
            }
        }

        // Estado de cada rádio antes de ativar o modo avião, para restaurar exatamente o que estava ligado
        private readonly Dictionary<RadioKind, RadioState> _radioStatesBeforeAirplane = new();

        private async Task<bool> ToggleAirplaneModeAsync(bool airplaneModeOn)
        {
            try
            {
                if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return false;
                var radios = await Radio.GetRadiosAsync();
                bool ok = true;

                if (airplaneModeOn)
                {
                    _radioStatesBeforeAirplane.Clear();
                    foreach (var radio in radios)
                    {
                        _radioStatesBeforeAirplane[radio.Kind] = radio.State;
                        ok &= await radio.SetStateAsync(RadioState.Off) == RadioAccessStatus.Allowed;
                    }
                }
                else
                {
                    foreach (var radio in radios)
                    {
                        // Sem histórico (ex.: app reiniciado durante o modo avião), liga o rádio
                        var target = _radioStatesBeforeAirplane.TryGetValue(radio.Kind, out var previous)
                            ? previous
                            : RadioState.On;
                        if (target == RadioState.On)
                        {
                            ok &= await radio.SetStateAsync(RadioState.On) == RadioAccessStatus.Allowed;
                        }
                    }
                    _radioStatesBeforeAirplane.Clear();
                }
                return ok;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AirplaneModeToggle] {ex.Message}");
                return false;
            }
        }

        private static bool OpenUri(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenUri] {ex.Message}");
                return false;
            }
        }

        private static bool TriggerLiveCaptionsShortcut()
        {
            try
            {
                keybd_event(0x5B, 0, KEYEVENTF_KEYDOWN, 0); // VK_LWIN
                keybd_event(0xA2, 0, KEYEVENTF_KEYDOWN, 0); // VK_LCONTROL
                keybd_event(0x4C, 0, KEYEVENTF_KEYDOWN, 0); // L
                keybd_event(0x4C, 0, KEYEVENTF_KEYUP,   0);
                keybd_event(0xA2, 0, KEYEVENTF_KEYUP,   0);
                keybd_event(0x5B, 0, KEYEVENTF_KEYUP,   0);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LiveCaptionsShortcut] {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _audioService?.Dispose();
            _brightnessService?.Dispose();
            _nightLightService?.Dispose();
            _wifiService?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
