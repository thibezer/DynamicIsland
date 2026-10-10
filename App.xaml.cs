using System;
using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;

namespace DynamicIslandWindows;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
    // Local\ = uma instância por sessão de usuário (com Global\, um segundo usuário logado não conseguia abrir o app)
    private const string MutexName = @"Local\DynamicIslandWindows_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Já existe outra instância ativa do aplicativo. Fecha silenciosamente.
            _mutex.Dispose();
            _mutex = null;
            Current.Shutdown();
            return;
        }

        // O registro de inicialização com o Windows é sincronizado pela janela, após carregar a configuração
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            DynamicIslandWindows.Services.NightLightService.RestoreDefaultGammaRamp();
        }
        catch { }

        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
            _mutex = null;
        }
        base.OnExit(e);
    }
}

