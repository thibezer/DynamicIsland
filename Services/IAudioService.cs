using System;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Contrato de serviço para manipulação de áudio do sistema via CoreAudio / NAudio.
    /// </summary>
    public interface IAudioService : IDisposable
    {
        int GetCurrentVolume();
        void SetVolume(int volume);
        bool IsMuted();
        void ToggleMute();
    }
}
