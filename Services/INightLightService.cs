using System;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Contrato de serviço para manipulação da temperatura de cor do monitor via GDI32 Gamma Ramp.
    /// </summary>
    public interface INightLightService : IDisposable
    {
        bool IsActive { get; }
        /// <returns>true se a rampa foi aplicada/restaurada com sucesso.</returns>
        bool SetNightLight(bool active);
        void RestoreOriginalRamp();
    }
}
