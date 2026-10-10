using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Serviço de gerenciamento de redes Wi-Fi via Windows Runtime (Windows.Devices.WiFi).
    /// Responsabilidade Única (SRP): Varredura assíncrona de redes, conexão segura e verificação do SSID conectado.
    /// </summary>
    public class WiFiService : IWiFiService
    {
        private WiFiAdapter? _wifiAdapter;
        private readonly SemaphoreSlim _adapterLock = new(1, 1);
        private bool _disposed = false;

        /// <summary>
        /// Obtém (e cria uma única vez) o adaptador Wi-Fi. Serializado para que varreduras
        /// simultâneas não criem adaptadores duplicados.
        /// </summary>
        private async Task<WiFiAdapter?> EnsureAdapterAsync()
        {
            if (_wifiAdapter != null) return _wifiAdapter;

            await _adapterLock.WaitAsync();
            try
            {
                if (_wifiAdapter != null || _disposed) return _wifiAdapter;

                var access = await WiFiAdapter.RequestAccessAsync();
                if (access != WiFiAccessStatus.Allowed)
                {
                    Debug.WriteLine("[WiFiService.EnsureAdapterAsync] Acesso ao adaptador negado.");
                    return null;
                }

                var devices = await DeviceInformation.FindAllAsync(WiFiAdapter.GetDeviceSelector());
                if (devices.Count == 0)
                {
                    Debug.WriteLine("[WiFiService.EnsureAdapterAsync] Nenhum adaptador Wi-Fi encontrado no sistema.");
                    return null;
                }

                _wifiAdapter = await WiFiAdapter.FromIdAsync(devices[0].Id);
                return _wifiAdapter;
            }
            finally
            {
                _adapterLock.Release();
            }
        }

        public async Task<List<HardwareService.WiFiNetworkInfo>> GetAvailableNetworksAsync()
        {
            var list = new List<HardwareService.WiFiNetworkInfo>();
            if (_disposed) return list;

            try
            {
                var adapter = await EnsureAdapterAsync();
                if (adapter == null) return list;

                await adapter.ScanAsync();

                if (adapter.NetworkReport != null)
                {
                    foreach (var net in adapter.NetworkReport.AvailableNetworks)
                    {
                        if (string.IsNullOrEmpty(net.Ssid)) continue;
                        list.Add(new HardwareService.WiFiNetworkInfo
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
                Debug.WriteLine($"[WiFiService.GetAvailableNetworksAsync] Erro no scan de WiFi: {ex.Message}");
            }

            // Deduplicação: agrupa por SSID e mantém o AP com melhor sinal
            var deduped = list
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .OrderByDescending(n => n.SignalBars)
                .ToList();

            return deduped;
        }

        public async Task<bool> ConnectToNetworkAsync(object rawNetwork, string password)
        {
            if (_disposed) return false;

            try
            {
                if (rawNetwork is not WiFiAvailableNetwork net) return false;
                var adapter = await EnsureAdapterAsync();
                if (adapter == null) return false;

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
                Debug.WriteLine($"[WiFiService.ConnectToNetworkAsync] Erro ao conectar ao WiFi: {ex.Message}");
                return false;
            }
        }

        public async Task<string?> GetConnectedNetworkSsidAsync()
        {
            if (_disposed) return null;

            try
            {
                var adapter = await EnsureAdapterAsync();
                if (adapter?.NetworkAdapter == null) return null;

                // Limite de espera: a consulta do perfil pode demorar em alguns drivers
                var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(500));
                return profile?.ProfileName;
            }
            catch (TimeoutException)
            {
                Debug.WriteLine("[WiFiService.GetConnectedNetworkSsidAsync] Tempo esgotado ao consultar rede conectada.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WiFiService.GetConnectedNetworkSsidAsync] Erro ao consultar rede conectada: {ex.Message}");
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _wifiAdapter = null;
            GC.SuppressFinalize(this);
        }
    }
}
