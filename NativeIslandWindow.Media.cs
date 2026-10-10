using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicIslandWindows.Services;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow
    {
        // ─── Atualização Sincronizada de Mídia ─────────────────────────────────────

        private void MediaService_StateChanged(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await UpdateMediaInfoAsync();
            });
        }

        private int _mediaUpdatePending = 0;
        private byte[]? _lastThumbBytes;

        private async Task UpdateMediaInfoAsync()
        {
            // Se já há uma atualização em andamento, marca como pendente: ela será repetida ao terminar
            if (Interlocked.CompareExchange(ref _mediaUpdateInProgress, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _mediaUpdatePending, 1);
                return;
            }

            try
            {
                do
                {
                    Interlocked.Exchange(ref _mediaUpdatePending, 0);
                    await UpdateMediaInfoCoreAsync();
                }
                while (Interlocked.CompareExchange(ref _mediaUpdatePending, 0, 1) == 1 && !_disposed);
            }
            finally { Interlocked.Exchange(ref _mediaUpdateInProgress, 0); }
        }

        private async Task UpdateMediaInfoCoreAsync()
        {
            try
            {
                var state = await _mediaService.GetCurrentStateAsync();
                _currentMode = state.Mode;

                BitmapImage? decodedThumbnail = null;
                bool hasThumbBytes = state.Mode == MediaMode.Music && state.ThumbnailBytes != null && state.ThumbnailBytes.Length > 0;
                // Mesma capa (o serviço devolve a mesma referência em cache) e já exibida: não decodifica nem anima de novo
                bool thumbUnchanged = hasThumbBytes
                    && ReferenceEquals(state.ThumbnailBytes, _lastThumbBytes)
                    && miniThumbBorder.Background is ImageBrush;
                if (hasThumbBytes && !thumbUnchanged)
                {
                    try
                    {
                        byte[] thumbBytes = state.ThumbnailBytes!;
                        decodedThumbnail = await Task.Run(() =>
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.StreamSource = new MemoryStream(thumbBytes);
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                            bitmap.EndInit();
                            bitmap.Freeze(); // Permite uso cross-thread no WPF
                            return bitmap;
                        });
                    }
                    catch (Exception imgEx)
                    {
                        Debug.WriteLine($"[ImageDecodeBackgroundError] {imgEx.Message}");
                    }
                }

                this.Dispatcher.Invoke(() =>
                {
                    // Verifica se o conteúdo compacto mudou para invalidar o cache da largura
                    bool contentChanged = false;
                    if (txtMiniTitle.Text != state.Title) contentChanged = true;
                    if (txtMiniSubtitle.Text != state.Subtitle) contentChanged = true;

                    string expectedPlayGlyph = state.IsPlaying ? "\uE769" : "\uE768";
                    if (state.Mode == MediaMode.Music)
                    {
                        if (miniMediaControls.Visibility != Visibility.Visible) contentChanged = true;
                        if (txtMiniPlayIcon.Text != expectedPlayGlyph) contentChanged = true;
                        
                        bool hasThumb = decodedThumbnail != null || thumbUnchanged;
                        if (hasThumb &&miniThumbBorder.Visibility != Visibility.Visible) contentChanged = true;
                        if (!hasThumb && txtMiniIcon.Visibility != Visibility.Visible) contentChanged = true;
                    }
                    else
                    {
                        if (miniMediaControls.Visibility != Visibility.Collapsed) contentChanged = true;
                        if (txtMiniIcon.Text != "\uE990" || txtMiniIcon.Visibility != Visibility.Visible) contentChanged = true;
                    }

                    if (contentChanged || _cachedCompactWidth <= 0)
                    {
                        _cachedCompactWidth = CalculateCompactWidth(state.Title, state.Subtitle, state.Mode == MediaMode.Music);
                    }

                    if (state.Mode == MediaMode.Music)
                    {
                        miniMediaControls.Visibility = Visibility.Visible;
                        
                        AnimateTextChange(txtMiniTitle, state.Title);
                        AnimateTextChange(txtMiniSubtitle, state.Subtitle);
                        txtMiniPlayIcon.Text = expectedPlayGlyph;

                        if (thumbUnchanged)
                        {
                            // Mesma capa, nada a fazer
                        }
                        else if (decodedThumbnail != null)
                        {
                            try
                            {
                                AnimateThumbnailChange(decodedThumbnail);
                                _lastThumbBytes = state.ThumbnailBytes;
                            }
                            catch (Exception imgEx)
                            {
                                Debug.WriteLine($"[ImageLoadError] {imgEx.Message}");
                                _lastThumbBytes = null;
                                UseDefaultMediaIcon();
                            }
                        }
                        else
                        {
                            _lastThumbBytes = null;
                            UseDefaultMediaIcon();
                        }
                    }
                    else
                    {
                        _lastThumbBytes = null;
                        miniMediaControls.Visibility = Visibility.Collapsed;
                        AnimateTextChange(txtMiniTitle, state.Title);
                        AnimateTextChange(txtMiniSubtitle, state.Subtitle);

                        miniThumbBorder.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
                        miniThumbBorder.Visibility = Visibility.Collapsed;
                        txtMiniIcon.Visibility = Visibility.Visible;
                        txtMiniIcon.Text = "\uE990"; 
                    }

                    if (!_isIslandOpen && !_isDropZoneActive)
                    {
                        double targetW = GetDynamicCompactWidth();
                        AnimateBorder(targetW, _miniBaseHeightLogica);
                    }

                    UpdateDinoState();
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[UpdateMedia] {ex.Message}"); }
        }

        private void UseDefaultMediaIcon()
        {
            miniThumbBorder.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            miniThumbBorder.Visibility = Visibility.Collapsed;
            txtMiniIcon.Visibility = Visibility.Visible;
            txtMiniIcon.Text = "\uE958";
        }

        private double CalculateCompactWidth(string title, string subtitle, bool hasMediaControls)
        {
            if (txtMiniTitle == null || txtMiniSubtitle == null) return 340;

            try
            {
                var typefaceTitle = new Typeface(txtMiniTitle.FontFamily, txtMiniTitle.FontStyle, txtMiniTitle.FontWeight, txtMiniTitle.FontStretch);
                var typefaceSubtitle = new Typeface(txtMiniSubtitle.FontFamily, txtMiniSubtitle.FontStyle, txtMiniSubtitle.FontWeight, txtMiniSubtitle.FontStretch);

                double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                var formattedTitle = new FormattedText(
                    string.IsNullOrEmpty(title) ? " " : title,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typefaceTitle,
                    txtMiniTitle.FontSize,
                    Brushes.Black,
                    pixelsPerDip);

                var formattedSubtitle = new FormattedText(
                    string.IsNullOrEmpty(subtitle) ? " " : subtitle,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typefaceSubtitle,
                    txtMiniSubtitle.FontSize,
                    Brushes.Black,
                    pixelsPerDip);

                double textWidth = Math.Max(formattedTitle.Width, formattedSubtitle.Width);
                textWidth = Math.Min(textWidth, 180); // Respeita o MaxWidth de 180 dos TextBlocks

                double iconWidth = 38; // 30px ícone + 8px margem
                double controlsWidth = hasMediaControls ? 64 : 0; // 2 botões de 30px + margem
                double padding = 34; // Espaçamento e respiro das bordas arredondadas da pílula

                double totalWidth = iconWidth + textWidth + 2 + controlsWidth + padding;
                return Math.Clamp(Math.Ceiling(totalWidth), 180, 380);
            }
            catch
            {
                return 340;
            }
        }

        private double GetDynamicCompactWidth()
        {
            if (_cachedCompactWidth > 0) return _cachedCompactWidth;
            return CalculateCompactWidth(txtMiniTitle?.Text ?? "", txtMiniSubtitle?.Text ?? "", miniMediaControls?.Visibility == Visibility.Visible);
        }

        /// <summary>
        /// Faz um crossfade e scale suave na imagem de thumbnail do tocador de mídia
        /// </summary>
        private void AnimateThumbnailChange(BitmapImage newThumbnail)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fadeOut.Completed += (s, e) =>
            {
                miniThumbBorder.Background = new ImageBrush(newThumbnail) 
                { 
                    Stretch = Stretch.UniformToFill 
                };

                bool isDinoActive = _dinoEasterEggActive || !NetworkInterface.GetIsNetworkAvailable();
                if (isDinoActive)
                {
                    miniThumbBorder.Visibility = Visibility.Collapsed;
                    txtMiniIcon.Visibility = Visibility.Collapsed;
                    dinoEasterEgg.Visibility = Visibility.Visible;
                }
                else
                {
                    txtMiniIcon.Visibility = Visibility.Collapsed;
                    dinoEasterEgg.Visibility = Visibility.Collapsed;
                    miniThumbBorder.Visibility = Visibility.Visible;
                }

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                miniThumbBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            
            miniThumbBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        /// <summary>
        /// Faz uma transição de texto com slide vertical elegante ao mudar títulos ou legendas
        /// </summary>
        private void AnimateTextChange(TextBlock? textBlock, string newText)
        {
            if (textBlock == null || textBlock.Text == newText) return;
            
            if (!(textBlock.RenderTransform is TranslateTransform))
                textBlock.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)textBlock.RenderTransform;
            
            // Cancela animações prévias para evitar conflitos de concorrência em trocas rápidas
            textBlock.BeginAnimation(UIElement.OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);

            // Fase 1: Slide-out + fade-out do texto atual
            var slideOut = new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            double currentOpacity = textBlock.Opacity > 0 ? textBlock.Opacity : 1.0;
            var fadeOut = new DoubleAnimation(currentOpacity, 0, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            
            fadeOut.Completed += (s, e) =>
            {
                textBlock.Text = newText;
                translate.Y = 6; // Posiciona abaixo
                
                // Fase 2: Slide-in + fade-in do novo texto
                var slideIn = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                
                translate.BeginAnimation(TranslateTransform.YProperty, slideIn);
                textBlock.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            
            translate.BeginAnimation(TranslateTransform.YProperty, slideOut);
            textBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // ─── Controles de Mídia ──────────────────────────────────────────────────
        private async void PlayPause_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync(MediaCommand.PlayPause); await UpdateMediaInfoAsync(); }

        private async void Next_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync(MediaCommand.Next); await UpdateMediaInfoAsync(); }

        private async void Previous_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync(MediaCommand.Previous); await UpdateMediaInfoAsync(); }

        private async void Shuffle_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync(MediaCommand.Shuffle); await UpdateMediaInfoAsync(); }

        private async void Repeat_Click(object sender, MouseButtonEventArgs e)
        { e.Handled = true; await _mediaService.HandleMediaCommandAsync(MediaCommand.Repeat); await UpdateMediaInfoAsync(); }

        // ─── Dino Easter Egg ──────────────────────────────────────────────────────
        private void MiniIconContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _dinoEasterEggActive = !_dinoEasterEggActive;
                UpdateDinoState();
            }
        }

        private void UpdateDinoState()
        {
            bool isOffline = !NetworkInterface.GetIsNetworkAvailable();
            bool shouldShow = _dinoEasterEggActive || isOffline;
            
            // Adjust color dynamically based on perceived background brightness
            if (_config != null && !string.IsNullOrEmpty(_config.BackgroundColor))
            {
                try
                {
                    var colorObj = ColorConverter.ConvertFromString(_config.BackgroundColor);
                    if (colorObj != null)
                    {
                        var c = (Color)colorObj;
                        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                        dinoEasterEgg.BodyColor = luminance > 0.5 ? Colors.Black : Colors.White;
                    }
                }
                catch { dinoEasterEgg.BodyColor = Colors.White; }
            }

            if (shouldShow)
            {
                txtMiniIcon.Visibility = Visibility.Collapsed;
                miniThumbBorder.Visibility = Visibility.Collapsed;
                dinoEasterEgg.Visibility = Visibility.Visible;
                dinoEasterEgg.Piscar();
            }
            else
            {
                dinoEasterEgg.Visibility = Visibility.Collapsed;
                if (_currentMode == MediaMode.Music)
                {
                    if (miniThumbBorder.Background is ImageBrush)
                    {
                        miniThumbBorder.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        txtMiniIcon.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    txtMiniIcon.Visibility = Visibility.Visible;
                }
            }
        }

        private void DisposeMedia()
        {
            if (_mediaService != null)
            {
                _mediaService.StateChanged -= MediaService_StateChanged;
                _mediaService.Dispose();
            }
        }
    }
}
