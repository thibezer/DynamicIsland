using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Serviço de gerenciamento de áudio do sistema utilizando NAudio CoreAudioApi.
    /// Responsabilidade Única (SRP): Leitura, alteração e mutação do volume mestre do endpoint padrão de reprodução.
    /// </summary>
    public class AudioService : IAudioService
    {
        private bool _disposed = false;

        // Enumerador reutilizado: criar um MMDeviceEnumerator (COM) a cada chamada é caro.
        // O endpoint padrão ainda é resolvido a cada chamada, então troca de dispositivo continua funcionando.
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly object _lock = new();

        private MMDevice GetDevice() => _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        public int GetCurrentVolume()
        {
            try
            {
                lock (_lock)
                {
                    using var device = GetDevice();
                    return (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService.GetCurrentVolume] Falha ao ler volume: {ex.Message}");
                return 50;
            }
        }

        public void SetVolume(int volume)
        {
            try
            {
                lock (_lock)
                {
                    using var device = GetDevice();
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume / 100f, 0f, 1f);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService.SetVolume] Falha ao definir volume: {ex.Message}");
            }
        }

        public bool IsMuted()
        {
            try
            {
                lock (_lock)
                {
                    using var device = GetDevice();
                    return device.AudioEndpointVolume.Mute;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService.IsMuted] Falha ao checar mudo: {ex.Message}");
                return false;
            }
        }

        public void ToggleMute()
        {
            try
            {
                lock (_lock)
                {
                    using var device = GetDevice();
                    device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioService.ToggleMute] Falha ao alternar mudo: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                try { _enumerator.Dispose(); } catch { }
            }
            GC.SuppressFinalize(this);
        }
    }
}
