using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicIslandWindows.Services;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow
    {
        // ─── BOLHA DO POMODORO ───────────────────────────────────────────────────

        private void PomodoroService_Tick(object? sender, PomodoroTickEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    int mins = e.RemainingSeconds / 60;
                    int secs = e.RemainingSeconds % 60;
                    string formattedTime = $"{mins:D2}:{secs:D2}";
                    
                    txtPomodoroTime.Text = formattedTime;
                    txtPomodoroTimeExpanded.Text = formattedTime;

                    if (_pomodoroService != null)
                    {
                        icoPomodoroPlayPause.Text = _pomodoroService.IsRunning ? "\uE769" : "\uE768";

                        // Mantém a bolha sempre 100% opaca e sólida; atenua apenas o texto se pausado
                        bool isPaused = !_pomodoroService.IsRunning && _pomodoroService.CurrentState != PomodoroState.Stopped;
                        double textOpacity = isPaused ? 0.65 : 1.0;
                        txtPomodoroTime.Opacity = textOpacity;
                        txtPomodoroTimeExpanded.Opacity = textOpacity;
                        pomodoroBorder.Opacity = 1.0;
                    }

                    UpdatePomodoroVisual(e.State, e.RemainingSeconds);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PomodoroUI_Tick] Erro: {ex.Message}");
                }
            });
        }

        private void PomodoroService_StateChanged(object? sender, PomodoroStateChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    Debug.WriteLine($"[PomodoroUI_State] Estado mudou para: {e.NewState}");
                    if (_pomodoroService != null)
                    {
                        icoPomodoroPlayPause.Text = _pomodoroService.IsRunning ? "\uE769" : "\uE768";
                        bool isPaused = !_pomodoroService.IsRunning && _pomodoroService.CurrentState != PomodoroState.Stopped;
                        double textOpacity = isPaused ? 0.65 : 1.0;
                        txtPomodoroTime.Opacity = textOpacity;
                        txtPomodoroTimeExpanded.Opacity = textOpacity;
                        pomodoroBorder.Opacity = 1.0;

                        UpdatePomodoroVisual(e.NewState, _pomodoroService.RemainingSeconds);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PomodoroUI_StateChanged] Erro: {ex.Message}");
                }
            });
        }

        private void UpdatePomodoroVisual(PomodoroState state, int remainingSeconds)
        {
            try
            {
                pomodoroBorder.Opacity = 1.0; // Sempre 100% sólido

                if (state == PomodoroState.Stopped)
                {
                    pomodoroBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1E1E20"));
                    txtPomodoroTime.Opacity = 1.0;
                    txtPomodoroTimeExpanded.Opacity = 1.0;
                    return;
                }

                // Degradê vertical (de cima para baixo: preto -> cor do estado)
                LinearGradientBrush brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };

                // Topo escuro comum para todos os estados
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF0E0E10"), 0.0));
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF151518"), 0.45));

                if (state == PomodoroState.Working)
                {
                    // Base azul
                    brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF0078D4"), 1.0));
                }
                else if (state == PomodoroState.OnBreak)
                {
                    if (remainingSeconds <= 30)
                    {
                        // Base vermelha crítica
                        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFE81123"), 1.0));

                        // Pulsação suave apenas no texto (mantendo a cápsula sólida)
                        if (remainingSeconds % 2 == 0)
                        {
                            txtPomodoroTime.Opacity = 0.6;
                            txtPomodoroTimeExpanded.Opacity = 0.6;
                        }
                        else
                        {
                            txtPomodoroTime.Opacity = 1.0;
                            txtPomodoroTimeExpanded.Opacity = 1.0;
                        }
                    }
                    else
                    {
                        // Base verde
                        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF2EA043"), 1.0));
                    }
                }

                pomodoroBorder.Background = brush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdatePomodoroVisual] Erro ao aplicar visual: {ex.Message}");
                pomodoroBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1E1E20"));
            }
        }

        private void UpdatePhysicalWindowWidth()
        {
            if (!_pomodoroVisible)
            {
                this.Width = 430;
            }
            else
            {
                this.Width = _isPomodoroExpanded ? 580 : 550;
            }
        }

        private void TogglePomodoroExpansion()
        {
            if (_pomodoroService == null) return;

            var duration = TimeSpan.FromMilliseconds(250);
            var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (!_isPomodoroExpanded)
            {
                _isPomodoroExpanded = true;
                UpdatePhysicalWindowWidth();

                txtWorkTimeInput.Text = $"{_pomodoroService.WorkTimeMinutes:D2}:00";
                txtBreakTimeInput.Text = $"{_pomodoroService.BreakTimeMinutes:D2}:00";

                pomodoroCompactGrid.Visibility = Visibility.Collapsed;
                pomodoroExpandedGrid.Visibility = Visibility.Visible;

                // Só "To": parte do tamanho atual, sem saltos se o clique interromper a animação anterior
                var animWidth = new DoubleAnimation(158, duration) { EasingFunction = easing };
                pomodoroBorder.BeginAnimation(WidthProperty, animWidth);

                var animHeight = new DoubleAnimation(78, duration) { EasingFunction = easing };
                pomodoroBorder.BeginAnimation(HeightProperty, animHeight);

                pomodoroBorder.CornerRadius = new CornerRadius(18);

                var animFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                pomodoroExpandedGrid.BeginAnimation(OpacityProperty, animFade);
            }
            else
            {
                _isPomodoroExpanded = false;

                pomodoroExpandedGrid.BeginAnimation(OpacityProperty, null);
                pomodoroExpandedGrid.Opacity = 0;
                pomodoroExpandedGrid.Visibility = Visibility.Collapsed;
                
                pomodoroCompactGrid.Visibility = Visibility.Visible;
                pomodoroCompactGrid.Opacity = 1;

                var animWidth = new DoubleAnimation(90, duration) { EasingFunction = easing };
                animWidth.Completed += (s, e) =>
                {
                    // Se o usuário reabriu durante a animação, não encolhe a janela
                    if (!_isPomodoroExpanded) UpdatePhysicalWindowWidth();
                };
                pomodoroBorder.BeginAnimation(WidthProperty, animWidth);

                var animHeight = new DoubleAnimation(43, duration) { EasingFunction = easing };
                pomodoroBorder.BeginAnimation(HeightProperty, animHeight);

                pomodoroBorder.CornerRadius = new CornerRadius(21.5);
            }
        }

        private void TxtWorkTimeInput_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox_LostFocus(sender, e);
            ApplyWorkTime();
            if (!pomodoroBorder.IsMouseOver)
            {
                _pomodoroCloseTimer?.Stop();
                _pomodoroCloseTimer?.Start();
            }
        }

        private void TxtBreakTimeInput_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox_LostFocus(sender, e);
            ApplyBreakTime();
            if (!pomodoroBorder.IsMouseOver)
            {
                _pomodoroCloseTimer?.Stop();
                _pomodoroCloseTimer?.Start();
            }
        }

        private void TxtTimeInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private int ParseInputTimeToMinutes(string input, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;
            try
            {
                input = input.Trim();
                if (input.Contains(":"))
                {
                    string[] parts = input.Split(':');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int mins))
                    {
                        return mins;
                    }
                }
                else
                {
                    if (int.TryParse(input, out int mins))
                    {
                        return mins;
                    }
                }
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private void ApplyWorkTime()
        {
            if (_pomodoroService == null) return;
            int currentMinutes = _pomodoroService.WorkTimeMinutes;
            int minutes = ParseInputTimeToMinutes(txtWorkTimeInput.Text, currentMinutes);
            
            if (minutes > 0 && minutes <= 180)
            {
                _pomodoroService.WorkTimeMinutes = minutes;
                txtWorkTimeInput.Text = $"{minutes:D2}:00";
            }
            else
            {
                txtWorkTimeInput.Text = $"{currentMinutes:D2}:00";
            }
        }

        private void ApplyBreakTime()
        {
            if (_pomodoroService == null) return;
            int currentMinutes = _pomodoroService.BreakTimeMinutes;
            int minutes = ParseInputTimeToMinutes(txtBreakTimeInput.Text, currentMinutes);
            
            if (minutes > 0 && minutes <= 60)
            {
                _pomodoroService.BreakTimeMinutes = minutes;
                txtBreakTimeInput.Text = $"{minutes:D2}:00";
            }
            else
            {
                txtBreakTimeInput.Text = $"{currentMinutes:D2}:00";
            }
        }

        private void PomodoroBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                _pomodoroService?.Toggle();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickLeft] Erro: {ex.Message}");
            }
        }

        private void PomodoroBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                TogglePomodoroExpansion();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickRight] Erro: {ex.Message}");
            }
        }

        private void PomodoroBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isPomodoroExpanded)
            {
                _pomodoroCloseTimer?.Stop();
                _pomodoroCloseTimer?.Start();
            }
        }

        private void PomodoroCloseTimer_Tick(object? sender, EventArgs e)
        {
            _pomodoroCloseTimer?.Stop();
            if (_isPomodoroExpanded && !pomodoroBorder.IsMouseOver)
            {
                // Limpa o foco do teclado de forma segura para disparar o LostFocus e aplicar as configurações
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
                Keyboard.ClearFocus();

                TogglePomodoroExpansion();
            }
        }

        private void PomodoroPlayPause_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                _pomodoroService?.Toggle();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickPlayPause] Erro: {ex.Message}");
            }
        }

        private void PomodoroReset_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                _pomodoroService?.Reset();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PomodoroClickReset] Erro: {ex.Message}");
            }
        }

        private void DisposePomodoro()
        {
            if (_pomodoroService != null)
            {
                _pomodoroService.Tick -= PomodoroService_Tick;
                _pomodoroService.StateChanged -= PomodoroService_StateChanged;
                _pomodoroService.Dispose();
            }

            _pomodoroCloseTimer?.Stop();
            _pomodoroCloseTimer = null;
        }
    }
}
