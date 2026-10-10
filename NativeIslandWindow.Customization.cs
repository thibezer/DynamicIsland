using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow
    {
        // ─── APARÊNCIA, CORES E CONFIGURAÇÕES ──────────────────────────────────────

        private void AppearanceSettings_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            
            // Torna o painel visível temporariamente com opacidade zero para o Measure funcionar
            appearancePanel.Opacity = 0;
            appearancePanel.Visibility = Visibility.Visible;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            double totalHeight = CalculateContentHeight(appearancePanel, extraOffset: 4.0);

            // Cross-fade fluido: inicia fade-out do expandido e fade-in suave do painel de aparência
            AnimatePanelOut(expandedIslandPanel);
            AnimatePanelIn(appearancePanel, slideDistance: 12, delayMs: 40);

            AnimateBorder(ISLAND_OPEN_W + 4.0, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Detail);
        }

        private void BackToMainPanel_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            
            _isWifiPanelTransitioning = false; // Remove proteção ao sair do WiFi
            _wifiTransitionTimer?.Stop();
            StopWifiAutoRefresh();
            
            // Fade-out simultâneo dos sub-painéis e entrada suave do painel principal
            AnimatePanelOut(appearancePanel);
            AnimatePanelOut(wifiPanel);
            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            
            AnimatePanelIn(expandedIslandPanel, slideDistance: 12, delayMs: 40);

            if (btnWifi != null && icoWifi != null)
            {
                UpdateToggleButton(btnWifi, icoWifi, _wifiOn);
            }
            if (btnWifiMenu != null && icoWifiMenu != null)
            {
                UpdateToggleButton(btnWifiMenu, icoWifiMenu, _wifiOn);
            }

            double totalHeight = CalculateContentHeight(expandedIslandPanel);

            AnimateBorder(ISLAND_OPEN_W, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Expanded);
        }

        private void SelectColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                e.Handled = true;
                string hexColor = border.Tag?.ToString() ?? "#FFF6A733";
                ApplyThemeColor(hexColor);
                SaveConfig();
            }
        }

        private void SelectBgColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                e.Handled = true;
                string hexColor = border.Tag?.ToString() ?? "#FF000000";
                ApplyBgColor(hexColor);
                SaveConfig();
            }
        }

        private void SelectInactColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border)
            {
                e.Handled = true;
                string hexColor = border.Tag?.ToString() ?? "#FF1E1E20";
                ApplyInactiveColor(hexColor);
                SaveConfig();
            }
        }

        private void ApplyAccentHex_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            string hex = txtAccentHex.Text.Trim();
            if (IsValidHexColor(hex))
            {
                ApplyThemeColor(hex);
                SaveConfig();
            }
            else
            {
                MessageBox.Show("Código hexadecimal de cor inválido. Ex: #FFF6A733", "Erro de Cor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyBgHex_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            string hex = txtBgHex.Text.Trim();
            if (IsValidHexColor(hex))
            {
                ApplyBgColor(hex);
                SaveConfig();
            }
            else
            {
                MessageBox.Show("Código hexadecimal de cor inválido. Ex: #FF000000", "Erro de Cor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyInactHex_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            string hex = txtInactHex.Text.Trim();
            if (IsValidHexColor(hex))
            {
                ApplyInactiveColor(hex);
                SaveConfig();
            }
            else
            {
                MessageBox.Show("Código hexadecimal de cor inválido. Ex: #FF1E1E20", "Erro de Cor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private bool IsValidHexColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex[0] != '#') return false;
            if (hex.Length != 7 && hex.Length != 9) return false;
            
            for (int i = 1; i < hex.Length; i++)
            {
                char c = hex[i];
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'A' && c <= 'F') ||
                      (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private string GetConfigPath()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DynamicIslandWindows");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "island_config.json");
        }

        private void LoadConfig()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<IslandConfig>(json);
                    if (cfg != null)
                    {
                        _config = cfg;
                        ApplyThemeColor(_config.ThemeColor);
                        ApplyBgColor(_config.BackgroundColor);
                        ApplyInactiveColor(_config.InactiveColor);
                        return;
                    }
                }
                ApplyThemeColor("#FFF6A733");
                ApplyBgColor("#FF000000");
                ApplyInactiveColor("#FF1E1E20");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LoadConfig] {ex.Message}");
                ApplyThemeColor("#FFF6A733");
                ApplyBgColor("#FF000000");
                ApplyInactiveColor("#FF1E1E20");
            }
        }

        private void SaveConfig()
        {
            try
            {
                string path = GetConfigPath();
                string json = JsonSerializer.Serialize(_config);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SaveConfig] {ex.Message}");
            }
        }

        /// <summary>
        /// Sincroniza o registro de inicialização com o Windows com a opção StartWithWindows do island_config.json.
        /// Builds de Debug nunca se registram, para não deixar o executável de desenvolvimento no Run.
        /// </summary>
        public static void ConfigureStartup(bool enabled)
        {
#if DEBUG
            Debug.WriteLine("[Startup] Build de Debug: registro de inicialização ignorado.");
            return;
#else
            try
            {
                string keyName = "DynamicIsland";

                if (!enabled)
                {
                    using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                    if (runKey?.GetValue(keyName) != null)
                    {
                        runKey.DeleteValue(keyName, throwOnMissingValue: false);
                        Debug.WriteLine("[Startup] Registro de inicialização removido (StartWithWindows = false).");
                    }
                    return;
                }

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (!string.IsNullOrEmpty(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null)
                        {
                            string value = $"\"{exePath}\"";
                            object? currentValue = key.GetValue(keyName);
                            if (currentValue == null || currentValue.ToString() != value)
                            {
                                key.SetValue(keyName, value);
                                Debug.WriteLine($"[Startup] Registro de inicialização atualizado para: {value}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] Erro ao configurar inicialização: {ex.Message}");
            }
#endif
        }

        private void ApplyThemeColor(string hexColor)
        {
            try
            {
                _config.ThemeColor = hexColor;
                var colorObj = ColorConverter.ConvertFromString(hexColor);
                if (colorObj != null)
                {
                    _themeColor = (Color)colorObj;
                    this.Resources["ThemeAccentBrush"] = new SolidColorBrush(_themeColor);
                    
                    var trackColor = Color.FromRgb(_themeColor.R, _themeColor.G, _themeColor.B);
                    this.Resources["ThemeAccentTrackBrush"] = new SolidColorBrush(trackColor);

                    txtAccentHex.Text = hexColor;
                    UpdateColorSelectionUI(hexColor);
                    if (!_isInitializing) InitializeTogglesUI();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyThemeColor] {ex.Message}");
            }
        }

        private void ApplyBgColor(string hexColor)
        {
            try
            {
                _config.BackgroundColor = hexColor;
                var colorObj = ColorConverter.ConvertFromString(hexColor);
                if (colorObj != null)
                {
                    var color = (Color)colorObj;
                    color.A = 255; // Garante opacidade 100% opaca
                    this.Resources["IslandBackgroundBrush"] = new SolidColorBrush(color);
                    txtBgHex.Text = hexColor;
                    UpdateBgSelectionUI(hexColor);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyBgColor] {ex.Message}");
            }
        }

        private void ApplyInactiveColor(string hexColor)
        {
            try
            {
                _config.InactiveColor = hexColor;
                var colorObj = ColorConverter.ConvertFromString(hexColor);
                if (colorObj != null)
                {
                    var color = (Color)colorObj;
                    this.Resources["IslandInactiveBrush"] = new SolidColorBrush(color);
                    txtInactHex.Text = hexColor;
                    UpdateInactSelectionUI(hexColor);
                    if (!_isInitializing) InitializeTogglesUI();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyInactiveColor] {ex.Message}");
            }
        }

        private void UpdateColorSelectionUI(string hexColor)
        {
            if (colorCoral == null) return;
            colorCoral.BorderThickness = new Thickness(0);
            colorBlue.BorderThickness = new Thickness(0);
            colorPurple.BorderThickness = new Thickness(0);
            colorGreen.BorderThickness = new Thickness(0);
            colorRed.BorderThickness = new Thickness(0);

            var selectedBrush = Brushes.White;
            var thick = new Thickness(2);

            if (hexColor.Equals("#FFF6A733", StringComparison.OrdinalIgnoreCase)) { colorCoral.BorderBrush = selectedBrush; colorCoral.BorderThickness = thick; }
            else if (hexColor.Equals("#FF0078D4", StringComparison.OrdinalIgnoreCase)) { colorBlue.BorderBrush = selectedBrush; colorBlue.BorderThickness = thick; }
            else if (hexColor.Equals("#FF8660A9", StringComparison.OrdinalIgnoreCase)) { colorPurple.BorderBrush = selectedBrush; colorPurple.BorderThickness = thick; }
            else if (hexColor.Equals("#FF107C41", StringComparison.OrdinalIgnoreCase)) { colorGreen.BorderBrush = selectedBrush; colorGreen.BorderThickness = thick; }
            else if (hexColor.Equals("#FFE81123", StringComparison.OrdinalIgnoreCase)) { colorRed.BorderBrush = selectedBrush; colorRed.BorderThickness = thick; }
        }

        private void UpdateBgSelectionUI(string hexColor)
        {
            if (bgBlack == null) return;
            bgBlack.BorderThickness = new Thickness(0);
            bgDarkGray.BorderThickness = new Thickness(0);
            bgBlue.BorderThickness = new Thickness(0);
            bgPurple.BorderThickness = new Thickness(0);
            bgGreen.BorderThickness = new Thickness(0);

            var selectedBrush = Brushes.White;
            var thick = new Thickness(2);

            if (hexColor.Equals("#FF000000", StringComparison.OrdinalIgnoreCase)) { bgBlack.BorderBrush = selectedBrush; bgBlack.BorderThickness = thick; }
            else if (hexColor.Equals("#FF1A1A1A", StringComparison.OrdinalIgnoreCase)) { bgDarkGray.BorderBrush = selectedBrush; bgDarkGray.BorderThickness = thick; }
            else if (hexColor.Equals("#FF0D1B2A", StringComparison.OrdinalIgnoreCase)) { bgBlue.BorderBrush = selectedBrush; bgBlue.BorderThickness = thick; }
            else if (hexColor.Equals("#FF1E152A", StringComparison.OrdinalIgnoreCase)) { bgPurple.BorderBrush = selectedBrush; bgPurple.BorderThickness = thick; }
            else if (hexColor.Equals("#FF0A1C16", StringComparison.OrdinalIgnoreCase)) { bgGreen.BorderBrush = selectedBrush; bgGreen.BorderThickness = thick; }
        }

        private void UpdateInactSelectionUI(string hexColor)
        {
            if (inactGray == null) return;
            inactGray.BorderThickness = new Thickness(0);
            inactDark.BorderThickness = new Thickness(0);
            inactBlue.BorderThickness = new Thickness(0);
            inactPurple.BorderThickness = new Thickness(0);
            inactGreen.BorderThickness = new Thickness(0);

            var selectedBrush = Brushes.White;
            var thick = new Thickness(2);

            if (hexColor.Equals("#FF1E1E20", StringComparison.OrdinalIgnoreCase)) { inactGray.BorderBrush = selectedBrush; inactGray.BorderThickness = thick; }
            else if (hexColor.Equals("#FF141416", StringComparison.OrdinalIgnoreCase)) { inactDark.BorderBrush = selectedBrush; inactDark.BorderThickness = thick; }
            else if (hexColor.Equals("#FF1B2A47", StringComparison.OrdinalIgnoreCase)) { inactBlue.BorderBrush = selectedBrush; inactBlue.BorderThickness = thick; }
            else if (hexColor.Equals("#FF322348", StringComparison.OrdinalIgnoreCase)) { inactPurple.BorderBrush = selectedBrush; inactPurple.BorderThickness = thick; }
            else if (hexColor.Equals("#FF142D24", StringComparison.OrdinalIgnoreCase)) { inactGreen.BorderBrush = selectedBrush; inactGreen.BorderThickness = thick; }
        }

        private void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            SetWindowActivateState(allowActivation: true);
            this.Activate();
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SetWindowActivateState(allowActivation: false);
        }

        private void SetWindowActivateState(bool allowActivation)
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                if (allowActivation)
                {
                    exStyle &= ~WS_EX_NOACTIVATE;
                }
                else
                {
                    exStyle |= WS_EX_NOACTIVATE;
                }
                SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);
                EnsureTopmost();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetWindowActivateState] {ex.Message}");
            }
        }

        /// <summary>
        /// Aplica os ajustes de hierarquia de profundidade ao island.
        /// Chamado quando o estado muda para um subnível (ex: expandido → detalhe) de acordo com WWDC 2025.
        /// </summary>
        private void ApplyDepthHierarchy(IslandDepth depth)
        {
            double targetOpacity = 1.0; // Fundo completamente opaco

            islandBorder.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    public enum IslandDepth { Compact, Expanded, Detail }
}
