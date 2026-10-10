using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow
    {
        // ─── Sliders ──────────────────────────────────────────────────────────────
        // Motor de Arraste Contínuo e Scrubbing Fluido (Estilo iOS Control Center / Win11)
        private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider slider) return;
            _activeDraggingSlider = slider;
            _isDraggingSlider = true;
            slider.CaptureMouse();
            UpdateSliderFromPoint(slider, e.GetPosition(slider));
            e.Handled = true;
        }

        private void Slider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Slider slider) return;
            if (e.LeftButton == MouseButtonState.Pressed && (_isDraggingSlider && _activeDraggingSlider == slider || slider.IsMouseCaptured))
            {
                UpdateSliderFromPoint(slider, e.GetPosition(slider));
                e.Handled = true;
            }
        }

        private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider slider) return;
            _isDraggingSlider = false;
            _activeDraggingSlider = null;
            if (slider.IsMouseCaptured)
            {
                slider.ReleaseMouseCapture();
            }
            e.Handled = true;
        }

        private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Slider slider) return;
            double step = 2.0; // Ajuste fino de 2% por passo de scroll
            double delta = (e.Delta > 0) ? step : -step;
            slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            e.Handled = true;
        }

        private static void UpdateSliderFromPoint(Slider slider, Point point)
        {
            double width = slider.ActualWidth;
            if (width <= 0) return;

            double ratio = Math.Clamp(point.X / width, 0.0, 1.0);
            double targetVal = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
            slider.Value = Math.Round(targetVal);
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hardwareService == null || txtBrightnessVal == null) return;
            int val = (int)e.NewValue;
            txtBrightnessVal.Text = $"{val}%";
            if (_isInitializing) return; // Valor inicial lido do hardware não deve ser regravado
            // O BrightnessService já agrupa as gravações em background (só o último valor é aplicado)
            _hardwareService.SetBrightness(val);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_hardwareService == null || txtVolumeVal == null) return;
            int val = (int)e.NewValue;
            txtVolumeVal.Text = $"{val}%";
            if (_isInitializing) return; // Valor inicial lido do hardware não deve ser regravado
            if (!_suppressVolumeWrite) _hardwareService.SetVolume(val);
            UpdateMuteUI(); // Sincroniza o ícone (desmuta ao deslizar se necessário)
        }

        private void UpdateMuteUI()
        {
            if (_hardwareService == null || txtVolumeIcon == null) return;
            bool isMuted = _hardwareService.IsMuted();
            int currentVol = _hardwareService.GetCurrentVolume();

            if (isMuted || currentVol == 0)
            {
                txtVolumeIcon.Text = "\uE74F"; // Ícone de mudo
                txtVolumeIcon.Opacity = 0.5;
            }
            else if (currentVol < 33)
            {
                txtVolumeIcon.Text = "\uE992"; // Volume baixo
                txtVolumeIcon.Opacity = 0.8;
            }
            else if (currentVol < 66)
            {
                txtVolumeIcon.Text = "\uE993"; // Volume médio
                txtVolumeIcon.Opacity = 0.8;
            }
            else
            {
                txtVolumeIcon.Text = "\uE995"; // Volume alto
                txtVolumeIcon.Opacity = 0.8;
            }
        }

        private void MuteToggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (_hardwareService == null) return;
            _hardwareService.ToggleMute();
            UpdateMuteUI();
        }

        // ─── Toggles Click ────────────────────────────────────────────────────────
        private async void Toggle_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Border border)) return;
            string action = border.Tag?.ToString() ?? "";

            switch (action)
            {
                case "wifi":
                    await ToggleWifiAsync();
                    break;
                case "bluetooth":
                    _bluetoothOn = !_bluetoothOn; UpdateToggleButton(border, icoBluetooth, _bluetoothOn);
                    await _hardwareService.HandleToggleAsync("bluetooth", _bluetoothOn);
                    await SyncRadioStatesAsync(); // Reflete o estado real, inclusive se a ação falhou
                    break;
                case "airplane":
                    _airplaneOn = !_airplaneOn; UpdateToggleButton(border, icoAirplane, _airplaneOn);
                    if (!await _hardwareService.HandleToggleAsync("airplane", _airplaneOn))
                    {
                        _airplaneOn = !_airplaneOn; UpdateToggleButton(border, icoAirplane, _airplaneOn);
                    }
                    await SyncRadioStatesAsync(); // Wi-Fi e Bluetooth mudam junto com o modo avião
                    break;
                case "pomodoro_toggle":
                    _pomodoroVisible = !_pomodoroVisible;
                    UpdateToggleButton(border, icoPomodoroToggle, _pomodoroVisible);
                    pomodoroBorder.Visibility = _pomodoroVisible ? Visibility.Visible : Visibility.Collapsed;
                    UpdatePhysicalWindowWidth();
                    break;
                case "night_light":
                    _nightLightOn = !_nightLightOn; UpdateToggleButton(border, icoNightLight, _nightLightOn);
                    await _hardwareService.HandleToggleAsync("night_light", _nightLightOn);
                    if (_nightLightOn != _hardwareService.IsNightLightActive)
                    {
                        // Driver rejeitou a rampa de gama: volta o botão ao estado real
                        _nightLightOn = _hardwareService.IsNightLightActive;
                        UpdateToggleButton(border, icoNightLight, _nightLightOn);
                    }
                    break;
                // Estes botões apenas abrem a página correspondente das Configurações do Windows;
                // não há como ler/alterar o estado real, então não ficam "ligados"
                case "accessibility":
                case "battery_saver":
                case "mobile_hotspot":
                case "nearby_share":
                case "cast":
                case "project":
                    await _hardwareService.HandleToggleAsync(action, true);
                    break;
                case "file_picker":
                    CloseIsland(); OpenManualFilePicker(); break;
            }
        }

        private async Task ToggleWifiAsync()
        {
            _wifiOn = !_wifiOn;
            UpdateWifiToggleUI();
            await _hardwareService.HandleToggleAsync("wifi", _wifiOn);
            await SyncRadioStatesAsync();
        }

        /// <summary>Atualiza todos os controles de Wi-Fi (botão rápido, botão de menu e switch do painel).</summary>
        private void UpdateWifiToggleUI()
        {
            UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
            if (btnWifiMenu != null && icoWifiMenu != null)
            {
                UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
            }
            UpdateWifiToggleHeaderUI();
        }

        /// <summary>Lê o estado real dos rádios no Windows e sincroniza a UI.</summary>
        private async Task SyncRadioStatesAsync()
        {
            var wifi = await _hardwareService.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.WiFi);
            var bluetooth = await _hardwareService.GetRadioStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
            if (_disposed) return;

            if (wifi.HasValue && wifi.Value != _wifiOn)
            {
                _wifiOn = wifi.Value;
                UpdateWifiToggleUI();
            }
            if (bluetooth.HasValue && bluetooth.Value != _bluetoothOn)
            {
                _bluetoothOn = bluetooth.Value;
                UpdateToggleButton(btnBluetooth, icoBluetooth, _bluetoothOn);
            }
        }

        private void UpdateToggleButton(Border border, TextBlock icon, bool isOn)
        {
            Color targetBg;
            Color targetIcon;
            
            if (isOn)
            {
                targetBg = _themeColor;
                double brightness = (0.299 * _themeColor.R + 0.587 * _themeColor.G + 0.114 * _themeColor.B) / 255;
                targetIcon = brightness > 0.6 ? Colors.Black : Colors.White;
            }
            else
            {
                var brush = this.Resources["IslandInactiveBrush"] as SolidColorBrush;
                targetBg = brush?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20);
                targetIcon = Colors.White;
            }

            var duration = TimeSpan.FromMilliseconds(250);

            // Anima fundo
            AnimateColor(border, Border.BackgroundProperty, targetBg, duration);

            // Anima ícone
            AnimateColor(icon, TextBlock.ForegroundProperty, targetIcon, duration);

            // Anima borda
            Color targetBorder = isOn ? _themeColor : Colors.Transparent;
            AnimateColor(border, Border.BorderBrushProperty, targetBorder, duration);
        }

        private void OpenSettings_Click(object sender, MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"[Settings] {ex.Message}"); }
        }

        private void DisposeHardware()
        {
            if (_hardwareService != null)
            {
                _hardwareService.Dispose();
            }
        }
    }
}
