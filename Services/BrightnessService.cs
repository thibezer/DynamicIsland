using System;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Serviço de gerenciamento de brilho de tela via WMI (WmiMonitorBrightness).
    /// Responsabilidade Única (SRP): Leitura e gravação assíncrona do nível de brilho com proteção contra congelamento de UI.
    /// </summary>
    public class BrightnessService : IBrightnessService
    {
        private readonly object _brightnessWriteLock = new();
        private int _pendingBrightness = -1;
        private bool _isWritingBrightness = false;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed = false;

        public int GetCurrentBrightness()
        {
            try
            {
                // Executa fora da Thread Principal, com limite de espera seguro de 500ms
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

                if (task.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    return task.Result;
                }
                return 70;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrightnessService.GetCurrentBrightness] Erro ao ler brilho via WMI: {ex.Message}");
                return 70;
            }
        }

        public void SetBrightness(int brightness)
        {
            if (_disposed) return;

            lock (_brightnessWriteLock)
            {
                _pendingBrightness = Math.Clamp(brightness, 0, 100);
                if (_isWritingBrightness)
                {
                    return;
                }
                _isWritingBrightness = true;
            }

            var token = _cts.Token;
            Task.Run(() =>
            {
                while (true)
                {
                    if (token.IsCancellationRequested) break;

                    int targetValue;
                    lock (_brightnessWriteLock)
                    {
                        targetValue = _pendingBrightness;
                        _pendingBrightness = -1;
                        if (targetValue == -1 || token.IsCancellationRequested)
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
                            if (token.IsCancellationRequested) break;
                            using (o)
                            {
                                o.InvokeMethod("WmiSetBrightness", new object[] { 1, target });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[BrightnessService.SetBrightness] Erro ao gravar brilho: {ex.Message}");
                    }
                }
            }, token);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrightnessService.Dispose] {ex.Message}");
            }

            GC.SuppressFinalize(this);
        }
    }
}
