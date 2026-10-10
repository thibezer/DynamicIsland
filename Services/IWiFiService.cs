using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Contrato de serviço para varredura e conexão de redes Wi-Fi nativas via Windows.Devices.WiFi.
    /// </summary>
    public interface IWiFiService : IDisposable
    {
        Task<List<HardwareService.WiFiNetworkInfo>> GetAvailableNetworksAsync();
        Task<bool> ConnectToNetworkAsync(object rawNetwork, string password);
        Task<string?> GetConnectedNetworkSsidAsync();
    }
}
