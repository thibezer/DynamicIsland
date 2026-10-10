using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace DynamicIslandWindows.Services
{
    public enum PomodoroState
    {
        Stopped,
        Working,
        OnBreak
    }

    public class PomodoroStateChangedEventArgs : EventArgs
    {
        public PomodoroState NewState { get; }
        public PomodoroStateChangedEventArgs(PomodoroState newState)
        {
            NewState = newState;
        }
    }

    public class PomodoroTickEventArgs : EventArgs
    {
        public int RemainingSeconds { get; }
        public PomodoroState State { get; }
        public PomodoroTickEventArgs(int remainingSeconds, PomodoroState state)
        {
            RemainingSeconds = remainingSeconds;
            State = state;
        }
    }

    public class PomodoroService : IDisposable
    {
        private int _workTimeSeconds = 25 * 60; // 25 minutos
        private int _breakTimeSeconds = 5 * 60;  // 5 minutos

        private DispatcherTimer? _timer;
        private int _remainingSeconds = 25 * 60;
        private PomodoroState _state = PomodoroState.Stopped;
        private bool _disposed = false;
        private DateTime _endUtc;

        public event EventHandler<PomodoroTickEventArgs>? Tick;
        public event EventHandler<PomodoroStateChangedEventArgs>? StateChanged;

        public int RemainingSeconds => _remainingSeconds;
        public PomodoroState CurrentState => _state;
        public bool IsRunning => _timer?.IsEnabled ?? false;

        public int WorkTimeMinutes
        {
            get => _workTimeSeconds / 60;
            set
            {
                if (value > 0 && value <= 180 && value * 60 != _workTimeSeconds)
                {
                    _workTimeSeconds = value * 60;
                    // Ciclo em andamento não é reiniciado: o novo valor vale a partir do próximo ciclo
                    if (_state == PomodoroState.Stopped)
                    {
                        _remainingSeconds = _workTimeSeconds;
                        OnTick(_remainingSeconds, _state);
                    }
                }
            }
        }

        public int BreakTimeMinutes
        {
            get => _breakTimeSeconds / 60;
            set
            {
                if (value > 0 && value <= 60)
                {
                    _breakTimeSeconds = value * 60;
                }
            }
        }

        public PomodoroService()
        {
            try
            {
                _timer = new DispatcherTimer
                {
                    // Intervalo curto: o tempo restante é calculado pelo relógio, o timer só dispara a checagem
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _timer.Tick += Timer_Tick;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroInit] Erro ao criar timer: {ex.Message}");
            }
        }

        public void Start()
        {
            if (_disposed) return;
            try
            {
                if (_state == PomodoroState.Stopped)
                {
                    _state = PomodoroState.Working;
                    _remainingSeconds = _workTimeSeconds;
                    OnStateChanged(_state);
                }

                _endUtc = DateTime.UtcNow.AddSeconds(_remainingSeconds);
                _timer?.Start();
                OnTick(_remainingSeconds, _state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroStart] Erro: {ex.Message}");
            }
        }

        public void Pause()
        {
            if (_disposed) return;
            try
            {
                if (IsRunning) _remainingSeconds = RemainingFromClock();
                _timer?.Stop();
                OnTick(_remainingSeconds, _state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroPause] Erro: {ex.Message}");
            }
        }

        public void Toggle()
        {
            if (IsRunning)
            {
                Pause();
            }
            else
            {
                Start();
            }
        }

        public void Reset()
        {
            if (_disposed) return;
            try
            {
                _timer?.Stop();
                _state = PomodoroState.Stopped;
                _remainingSeconds = _workTimeSeconds;
                OnStateChanged(_state);
                OnTick(_remainingSeconds, _state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroReset] Erro: {ex.Message}");
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_disposed) return;
            try
            {
                int remaining = RemainingFromClock();
                if (remaining > 0)
                {
                    if (remaining != _remainingSeconds)
                    {
                        _remainingSeconds = remaining;
                        OnTick(_remainingSeconds, _state);
                    }
                }
                else
                {
                    // Transição automática de estados, sem exibir 00:00 por um tick extra
                    if (_state == PomodoroState.Working)
                    {
                        _state = PomodoroState.OnBreak;
                        _remainingSeconds = _breakTimeSeconds;
                    }
                    else
                    {
                        _state = PomodoroState.Working;
                        _remainingSeconds = _workTimeSeconds;
                    }
                    _endUtc = DateTime.UtcNow.AddSeconds(_remainingSeconds);
                    OnStateChanged(_state);
                    OnTick(_remainingSeconds, _state);
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroTick] Erro no tick do temporizador: {ex.Message}");
            }
        }

        private int RemainingFromClock() =>
            Math.Max(0, (int)Math.Ceiling((_endUtc - DateTime.UtcNow).TotalSeconds));

        protected virtual void OnTick(int remainingSeconds, PomodoroState state)
        {
            try
            {
                Tick?.Invoke(this, new PomodoroTickEventArgs(remainingSeconds, state));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroEventTick] Erro ao disparar evento: {ex.Message}");
            }
        }

        protected virtual void OnStateChanged(PomodoroState newState)
        {
            try
            {
                StateChanged?.Invoke(this, new PomodoroStateChangedEventArgs(newState));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroEventState] Erro ao disparar evento: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _timer?.Stop();
                _timer = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroDispose] Erro ao descartar recursos: {ex.Message}");
            }
        }
    }
}
