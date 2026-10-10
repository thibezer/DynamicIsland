using System;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Contrato de serviço para leitura e controle de brilho via WmiMonitorBrightness.
    /// </summary>
    public interface IBrightnessService : IDisposable
    {
        int GetCurrentBrightness();
        void SetBrightness(int brightness);
    }
}
