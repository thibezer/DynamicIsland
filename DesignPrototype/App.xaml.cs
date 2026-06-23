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
    private const string MutexName = @"Global\DynamicIslandWindows_SingleInstance_Mutex";

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

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

