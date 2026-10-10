using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIslandWindows.Services;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow
    {
        // ─── LÓGICA DO MENU DE WI-FI INTERNO ──────────────────────────────────────────
        private void UpdateWifiToggleHeaderUI()
        {
            if (btnWifiToggleHeader == null || wifiSwitchIndicator == null) return;
            
            // Configura TranslateTransform se necessário
            if (!(wifiSwitchIndicator.RenderTransform is TranslateTransform))
                wifiSwitchIndicator.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)wifiSwitchIndicator.RenderTransform;
            
            // A pílula tem 44px de largura, o indicador 16px, margem 4px
            // Posição esquerda: 0, Posição direita: 20px
            double targetX = _wifiOn ? 20 : 0;
            Color targetBg = _wifiOn ? _themeColor : 
                ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color 
                 ?? Color.FromRgb(0x1E, 0x1E, 0x20));
            
            // Desliza com spring suave
            var slideAnim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, slideAnim);
            
            // Anima a cor de fundo do switch container
            if (!(btnWifiToggleHeader.Background is SolidColorBrush switchBg) || switchBg.IsFrozen)
            {
                switchBg = new SolidColorBrush(
                    (btnWifiToggleHeader.Background as SolidColorBrush)?.Color ?? Colors.Gray);
                btnWifiToggleHeader.Background = switchBg;
            }
            switchBg.BeginAnimation(SolidColorBrush.ColorProperty, 
                new ColorAnimation(targetBg, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            
            // Fixa o indicador à esquerda para que TranslateTransform funcione corretamente
            wifiSwitchIndicator.HorizontalAlignment = HorizontalAlignment.Left;
        }

        private async void WifiToggleHeader_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            // Liga/Desliga o rádio e sincroniza todos os botões de Wi-Fi com o estado real
            await ToggleWifiAsync();

            // Atualiza a lista
            _ = RefreshWifiListAsync();
        }

        // Identifica a atualização mais recente; resultados de varreduras antigas são descartados
        private int _wifiRefreshId = 0;
        private bool _wifiConnecting = false;

        private async Task RefreshWifiListAsync()
        {
            try
            {
                if (wifiListContainer == null) return;
                // Não sobrescreve o spinner/resultado de conexão nem troca a rede enquanto o prompt de senha está aberto
                if (_wifiConnecting || wifiPasswordPanel.Visibility == Visibility.Visible) return;

                int refreshId = ++_wifiRefreshId;
                wifiListContainer.Children.Clear();

                if (!_wifiOn)
                {
                    var txtOff = new TextBlock
                    {
                        Style = this.Resources["FluentTextSec"] as Style,
                        Text = "O Wi-Fi está desativado.",
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    };
                    wifiListContainer.Children.Add(txtOff);
                    AdjustWifiPanelHeight();
                    return;
                }

                var txtScanning = new TextBlock
                {
                    Style = this.Resources["FluentTextSec"] as Style,
                    Text = "Procurando redes...",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                wifiListContainer.Children.Add(txtScanning);
                AdjustWifiPanelHeight();

                var networks = await _hardwareService.GetAvailableNetworksAsync();

                if (refreshId != _wifiRefreshId || _wifiConnecting) return;

                string? connectedSsid = await _hardwareService.GetConnectedNetworkSsidAsync();

                if (refreshId != _wifiRefreshId || _wifiConnecting) return;

                wifiListContainer.Children.Clear();

                if (networks == null || networks.Count == 0)
                {
                    var txtNone = new TextBlock
                    {
                        Style = this.Resources["FluentTextSec"] as Style,
                        Text = "Nenhuma rede encontrada.",
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    };
                    wifiListContainer.Children.Add(txtNone);
                    AdjustWifiPanelHeight();
                    return;
                }

                RenderWifiNetworks(networks, connectedSsid);
                AdjustWifiPanelHeight();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RefreshWifiListError] {ex.Message}");
            }
        }

        private void RenderWifiNetworks(List<HardwareService.WiFiNetworkInfo> networks, string? connectedSsid)
        {
            if (wifiListContainer == null) return;

            // Ordenar: conectada primeiro, depois por força de sinal
            var sorted = networks
                .OrderByDescending(n => n.Ssid == connectedSsid)
                .ThenByDescending(n => n.SignalBars)
                .ToList();

            int index = 0;
            foreach (var net in sorted)
            {
                bool isConnected = net.Ssid == connectedSsid;
                var normalBrushColor = isConnected
                    ? Color.FromArgb(0x33, _themeColor.R, _themeColor.G, _themeColor.B)
                    : ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20));

                var itemBorder = new Border
                {
                    Background = new SolidColorBrush(normalBrushColor),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Padding = new Thickness(10, 8, 10, 8),
                    BorderBrush = isConnected
                        ? new SolidColorBrush(Color.FromArgb(0x66, _themeColor.R, _themeColor.G, _themeColor.B))
                        : Brushes.Transparent,
                    BorderThickness = isConnected ? new Thickness(1) : new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = net
                };

                itemBorder.MouseEnter += (s, ev) => 
                {
                    var hoverColor = Color.FromRgb(0x2D, 0x2D, 0x30);
                    if (!(itemBorder.Background is SolidColorBrush bg) || bg.IsFrozen)
                    {
                        bg = new SolidColorBrush((itemBorder.Background as SolidColorBrush)?.Color ?? Colors.Transparent);
                        itemBorder.Background = bg;
                    }
                    bg.BeginAnimation(SolidColorBrush.ColorProperty, 
                        new ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(100))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                };

                itemBorder.MouseLeave += (s, ev) => 
                {
                    bool isConn = ((HardwareService.WiFiNetworkInfo)itemBorder.Tag).Ssid == connectedSsid;
                    var normColor = isConn 
                        ? Color.FromArgb(0x33, _themeColor.R, _themeColor.G, _themeColor.B)
                        : ((this.Resources["IslandInactiveBrush"] as SolidColorBrush)?.Color ?? Color.FromRgb(0x1E, 0x1E, 0x20));
                    
                    if (itemBorder.Background is SolidColorBrush bg && !bg.IsFrozen)
                    {
                        bg.BeginAnimation(SolidColorBrush.ColorProperty, 
                            new ColorAnimation(normColor, TimeSpan.FromMilliseconds(150))
                            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    }
                };
                
                itemBorder.MouseLeftButtonDown += WifiNetworkItem_Click;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                
                var txtSsid = new TextBlock
                {
                    Style = this.Resources["FluentText"] as Style,
                    Text = net.Ssid,
                    FontSize = 12,
                    FontWeight = isConnected ? FontWeights.Bold : FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                textStack.Children.Add(txtSsid);

                if (isConnected)
                {
                    var connLabel = new TextBlock
                    {
                        Style = this.Resources["FluentTextSec"] as Style,
                        Text = "Conectado",
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(_themeColor),
                        Margin = new Thickness(0, 2, 0, 0)
                    };
                    textStack.Children.Add(connLabel);
                }

                Grid.SetColumn(textStack, 0);
                grid.Children.Add(textStack);

                bool isSecure = !net.SecurityKind.Equals("None", StringComparison.OrdinalIgnoreCase) && 
                                !net.SecurityKind.Equals("Open", StringComparison.OrdinalIgnoreCase);
                if (isSecure)
                {
                    var txtLock = new TextBlock
                    {
                        Style = this.Resources["FluentIcon"] as Style,
                        Text = "\uE72E",
                        FontSize = 12,
                        Foreground = Brushes.LightGray,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 8, 0)
                    };
                    Grid.SetColumn(txtLock, 1);
                    grid.Children.Add(txtLock);
                }

                var signalIndicator = CreateSignalBarsIndicator(net.SignalBars);
                Grid.SetColumn(signalIndicator, 2);
                grid.Children.Add(signalIndicator);

                itemBorder.Child = grid;
                
                // Adiciona com animação escalonada
                AddNetworkItemWithAnimation(itemBorder, index++);
            }
        }

        private UIElement CreateSignalBarsIndicator(int bars)
        {
            var panel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0) 
            };
            
            Color[] barColors = bars switch
            {
                >= 3 => new[] { 
                    Color.FromRgb(0x10, 0x7C, 0x41), // Verde
                    Color.FromRgb(0x10, 0x7C, 0x41), 
                    Color.FromRgb(0x10, 0x7C, 0x41), 
                    Color.FromRgb(0x10, 0x7C, 0x41) 
                },
                2 => new[] { 
                    Color.FromRgb(0xF6, 0xA7, 0x33), // Amarelo/Accent
                    Color.FromRgb(0xF6, 0xA7, 0x33), 
                    Color.FromRgb(0x3A, 0x3A, 0x3C), // Inativo
                    Color.FromRgb(0x3A, 0x3A, 0x3C) 
                },
                _ => new[] { 
                    Color.FromRgb(0xE8, 0x11, 0x23), // Vermelho
                    Color.FromRgb(0x3A, 0x3A, 0x3C), 
                    Color.FromRgb(0x3A, 0x3A, 0x3C), 
                    Color.FromRgb(0x3A, 0x3A, 0x3C) 
                }
            };
            
            int[] heights = { 6, 10, 14, 18 };
            for (int i = 0; i < 4; i++)
            {
                panel.Children.Add(new Border
                {
                    Width = 3,
                    Height = heights[i],
                    CornerRadius = new CornerRadius(1.5),
                    Background = new SolidColorBrush(i < bars ? barColors[i] : Color.FromRgb(0x3A, 0x3A, 0x3C)),
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                });
            }
            return panel;
        }

        private void AddNetworkItemWithAnimation(UIElement item, int index)
        {
            item.Opacity = 0;
            item.RenderTransform = new TranslateTransform(0, 15);
            wifiListContainer.Children.Add(item);
            
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(index * 50), // Escalonamento
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            var slideUp = new DoubleAnimation(15, 0, TimeSpan.FromMilliseconds(250))
            {
                BeginTime = TimeSpan.FromMilliseconds(index * 50),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            item.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ((TranslateTransform)item.RenderTransform).BeginAnimation(
                TranslateTransform.YProperty, slideUp);
        }

        private void WifiNetworkItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border item && item.Tag is HardwareService.WiFiNetworkInfo net)
            {
                e.Handled = true;
                _selectedWifiNetwork = net;

                bool isSecure = !net.SecurityKind.Equals("None", StringComparison.OrdinalIgnoreCase) && 
                                !net.SecurityKind.Equals("Open", StringComparison.OrdinalIgnoreCase);
                
                if (!isSecure)
                {
                    _ = ConnectToWifiNetwork(net, "");
                }
                else
                {
                    txtWifiPasswordPrompt.Text = $"Introduza a palavra-passe para \"{net.Ssid}\"";
                    txtWifiPassword.Clear();
                    wifiPasswordPanel.Visibility = Visibility.Visible;
                    txtWifiPassword.Focus();
                }
            }
        }

        private void CancelWifiConnection_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            txtWifiPassword.Clear();
            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            _selectedWifiNetwork = null;
            AdjustWifiPanelHeight();
        }

        private void ConnectWifi_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_selectedWifiNetwork == null) return;

            string password = txtWifiPassword.Password;
            txtWifiPassword.Clear(); // Não mantém a senha na UI depois de usada
            wifiPasswordPanel.Visibility = Visibility.Collapsed;

            _ = ConnectToWifiNetwork(_selectedWifiNetwork, password);
        }

        private async Task ConnectToWifiNetwork(HardwareService.WiFiNetworkInfo net, string password)
        {
            if (wifiListContainer == null || _wifiConnecting) return;

            _wifiConnecting = true;
            _wifiRefreshId++; // invalida qualquer varredura em andamento
            bool success = false;
            try
            {
                wifiListContainer.Children.Clear();
                wifiListContainer.Children.Add(CreateConnectionSpinner(net.Ssid));
                AdjustWifiPanelHeight();

                if (net.RawNetwork != null)
                {
                    success = await _hardwareService.ConnectToNetworkAsync(net.RawNetwork, password);
                }

                wifiListContainer.Children.Clear();
                wifiListContainer.Children.Add(CreateConnectionResult(net.Ssid, success));
                AdjustWifiPanelHeight();

                await Task.Delay(3000);
            }
            finally
            {
                _wifiConnecting = false;
            }

            if (!_disposed && wifiPanel.Visibility == Visibility.Visible)
            {
                _ = RefreshWifiListAsync();
            }
        }

        /// <summary>
        /// Cria um spinner circular animado para indicar progresso de conexão.
        /// </summary>
        private UIElement CreateConnectionSpinner(string ssid)
        {
            var stack = new StackPanel 
            { 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 0) 
            };
            
            // Anel giratório usando um Arc/Ellipse com animação de rotação
            var spinner = new Border
            {
                Width = 24, Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(_themeColor),
                BorderThickness = new Thickness(2.5),
                Opacity = 0.8,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                // Clip para criar efeito de arco parcial
                Clip = new RectangleGeometry(new Rect(0, 0, 24, 12))
            };
            
            var rotation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            ((RotateTransform)spinner.RenderTransform).BeginAnimation(
                RotateTransform.AngleProperty, rotation);
            
            stack.Children.Add(spinner);
            stack.Children.Add(new TextBlock
            {
                Style = this.Resources["FluentTextSec"] as Style,
                Text = $"Conectando a \"{ssid}\"...",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
            
            return stack;
        }

        /// <summary>
        /// Cria um indicador de sucesso (✓ verde) ou erro (✗ vermelho) com animação.
        /// </summary>
        private UIElement CreateConnectionResult(string ssid, bool success)
        {
            var stack = new StackPanel 
            { 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 25, 0, 0) 
            };
            
            var iconBorder = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(success 
                    ? Color.FromArgb(0x33, 0x10, 0x7C, 0x41) 
                    : Color.FromArgb(0x33, 0xE8, 0x11, 0x23)),
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.5, 0.5)
            };
            
            iconBorder.Child = new TextBlock
            {
                Style = this.Resources["FluentIcon"] as Style,
                Text = success ? "\uE73E" : "\uE711",  // Check ou X
                FontSize = 16,
                Foreground = new SolidColorBrush(success 
                    ? Color.FromRgb(0x10, 0x7C, 0x41) 
                    : Color.FromRgb(0xE8, 0x11, 0x23)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            
            // Animação de escala: pop-in
            var scaleX = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
            var scaleY = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5 } };
            
            ((ScaleTransform)iconBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            ((ScaleTransform)iconBorder.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            
            stack.Children.Add(iconBorder);
            stack.Children.Add(new TextBlock
            {
                Style = this.Resources["FluentText"] as Style,
                Text = success ? $"Conectado a \"{ssid}\"" : $"Falha ao conectar a \"{ssid}\"",
                FontSize = 11,
                Foreground = new SolidColorBrush(success 
                    ? Color.FromRgb(0x10, 0x7C, 0x41) 
                    : Color.FromRgb(0xE8, 0x11, 0x23)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
            
            return stack;
        }

        private void WifiMenuOpen_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            wifiPasswordPanel.Visibility = Visibility.Collapsed;
            _selectedWifiNetwork = null;

            // Torna o painel visível temporariamente com opacidade zero para o Measure funcionar
            wifiPanel.Opacity = 0;
            wifiPanel.Visibility = Visibility.Visible;

            // Cross-fade fluido: inicia fade-out do expandido e entrada do painel Wi-Fi
            AnimatePanelOut(expandedIslandPanel);
            AnimatePanelIn(wifiPanel, slideDistance: 12, delayMs: 40);

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            // ATIVA PROTEÇÃO: Impede o fechamento por MouseLeave durante 3 segundos
            ActivateWifiTransitionProtection();

            // Inicia a auto-atualização periódica das redes Wi-Fi
            StartWifiAutoRefresh();

            UpdateWifiToggleHeaderUI();
            _ = RefreshWifiListAsync();
        }

        /// <summary>
        /// Ativa uma proteção temporária de 3 segundos que impede o fechamento da ilha
        /// por MouseLeave. Necessário porque a transição para o painel Wi-Fi redimensiona
        /// o islandBorder, fazendo o mouse ficar fora da área e disparando MouseLeave.
        /// </summary>
        private void ActivateWifiTransitionProtection()
        {
            // Para o timer de fechamento que possa estar ativo
            _closeDelayTimer?.Stop();
            
            _isWifiPanelTransitioning = true;

            // Para timer anterior se estiver rodando (caso de cliques rápidos)
            _wifiTransitionTimer?.Stop();

            if (_wifiTransitionTimer == null)
            {
                _wifiTransitionTimer = new DispatcherTimer 
                { 
                    Interval = TimeSpan.FromMilliseconds(3000) 
                };
                _wifiTransitionTimer.Tick += (s, ev) =>
                {
                    _wifiTransitionTimer?.Stop();
                    _isWifiPanelTransitioning = false;
                    
                    // Se o mouse já saiu da ilha durante a proteção, agora sim inicia o fechamento
                    if (!islandBorder.IsMouseOver && _isIslandOpen)
                    {
                        _closeDelayTimer?.Stop();
                        if (_closeDelayTimer != null)
                        {
                            _closeDelayTimer.Interval = TimeSpan.FromMilliseconds(HOVER_CLOSE_DELAY_MS);
                            _closeDelayTimer.Start();
                        }
                    }
                };
            }
            _wifiTransitionTimer.Start();
        }

        // Iniciar quando o painel Wi-Fi abre:
        private void StartWifiAutoRefresh()
        {
            _wifiAutoRefreshTimer?.Stop();
            _wifiAutoRefreshTimer = new DispatcherTimer 
            { 
                Interval = TimeSpan.FromSeconds(15) // Refresh a cada 15 segundos
            };
            _wifiAutoRefreshTimer.Tick += async (s, ev) =>
            {
                if (wifiPanel.Visibility == Visibility.Visible && _wifiOn)
                {
                    await RefreshWifiListAsync();
                }
                else
                {
                    _wifiAutoRefreshTimer?.Stop();
                }
            };
            _wifiAutoRefreshTimer.Start();
        }

        // Parar quando o painel Wi-Fi fecha:
        private void StopWifiAutoRefresh()
        {
            _wifiAutoRefreshTimer?.Stop();
            _wifiAutoRefreshTimer = null;
        }

        private async void WifiRefresh_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            
            // Animação de rotação no ícone de refresh
            if (icoWifiRefresh != null)
            {
                var rotation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                ((RotateTransform)icoWifiRefresh.RenderTransform)
                    .BeginAnimation(RotateTransform.AngleProperty, rotation);
            }
            
            await RefreshWifiListAsync();
        }

        private void AdjustWifiPanelHeight()
        {
            if (wifiPanel == null) return;

            double totalHeight = CalculateContentHeight(wifiPanel, extraOffset: 4.0);

            AnimateBorder(ISLAND_OPEN_W + 4.0, totalHeight);
            ApplyDepthHierarchy(IslandDepth.Detail);
        }

        private void DisposeWifi()
        {
            StopWifiAutoRefresh();
            _wifiTransitionTimer?.Stop();
            _wifiTransitionTimer = null;
        }
    }
}
