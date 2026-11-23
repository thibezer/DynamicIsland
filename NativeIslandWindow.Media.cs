using NAudio.CoreAudioApi;
using System.Windows.Threading;
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
                    string currentTitleTarget = _targetTexts.TryGetValue(txtMiniTitle, out string? tt) ? tt : txtMiniTitle.Text;
                    string currentSubTarget = _targetTexts.TryGetValue(txtMiniSubtitle, out string? st) ? st : txtMiniSubtitle.Text;
                    if (txtMiniTitle.Text != state.Title || currentTitleTarget != state.Title) contentChanged = true;
                    if (txtMiniSubtitle.Text != state.Subtitle || currentSubTarget != state.Subtitle) contentChanged = true;

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
                        if (txtMediaExpandedTitle != null)
                        {
                            txtMediaExpandedTitle.Text = state.Title;
                        }
                        txtMiniPlayIcon.Text = expectedPlayGlyph;
                        UpdateWaveformAnimation(state.IsPlaying);

                        _isMediaPlaying = state.IsPlaying;
                        _mediaHasTimeline = state.HasTimeline && state.Duration > TimeSpan.Zero;
                        _mediaDuration = state.Duration;
                        if (_mediaHasTimeline)
                        {
                            var pos = state.Position;
                            if (state.IsPlaying && state.LastUpdatedTime > DateTimeOffset.MinValue)
                            {
                                var delta = DateTimeOffset.UtcNow - state.LastUpdatedTime;
                                if (delta > TimeSpan.Zero && delta < TimeSpan.FromHours(12))
                                    pos += delta;
                            }
                            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                            if (pos > state.Duration) pos = state.Duration;
                            _mediaBasePosition = pos;
                            _mediaPositionCapturedUtc = DateTimeOffset.UtcNow;
                        }
                        else
                        {
                            _mediaBasePosition = TimeSpan.Zero;
                        }

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
                        _isMediaPlaying = false;
                        _mediaHasTimeline = false;
                        _mediaBasePosition = TimeSpan.Zero;
                        _mediaDuration = TimeSpan.Zero;
                        UpdateWaveformAnimation(false);
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
                    UpdateBottomExpandedState();
                    UpdateMediaProgressUI();
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
                double controlsWidth = hasMediaControls ? 84 : 0; // Waveform animado (18px) + margem (6px) + 2 botões (60px)
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

        private readonly System.Collections.Generic.Dictionary<TextBlock, int> _textAnimGen = new();
        private readonly System.Collections.Generic.Dictionary<TextBlock, string> _targetTexts = new();

        /// <summary>
        /// Faz uma transição de texto com slide vertical elegante ao mudar títulos ou legendas
        /// </summary>
        private void AnimateTextChange(TextBlock? textBlock, string newText)
        {
            if (textBlock == null) return;

            string currentTarget = _targetTexts.TryGetValue(textBlock, out string? t) ? t : textBlock.Text;
            if (currentTarget == newText && textBlock.Text == newText) return;

            _targetTexts[textBlock] = newText;
            int gen = (_textAnimGen.TryGetValue(textBlock, out int g) ? g : 0) + 1;
            _textAnimGen[textBlock] = gen;

            if (!(textBlock.RenderTransform is TranslateTransform))
                textBlock.RenderTransform = new TranslateTransform();
            
            var translate = (TranslateTransform)textBlock.RenderTransform;
            
            // Cancela animações prévias para evitar conflitos de concorrência em trocas rápidas
            textBlock.BeginAnimation(UIElement.OpacityProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);

            double targetOpacity = ReferenceEquals(textBlock, txtMiniSubtitle) ? 0.65 : 1.0;

            // Fase 1: Slide-out + fade-out do texto atual
            var slideOut = new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            double currentOpacity = textBlock.Opacity > 0 ? textBlock.Opacity : targetOpacity;
            var fadeOut = new DoubleAnimation(currentOpacity, 0, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            
            fadeOut.Completed += (s, e) =>
            {
                // Ignora callbacks de animações antigas que foram canceladas/substituídas
                if (_textAnimGen.TryGetValue(textBlock, out int activeGen) && activeGen != gen)
                    return;

                textBlock.Text = newText;
                translate.Y = 6; // Posiciona abaixo
                
                // Fase 2: Slide-in + fade-in do novo texto
                var slideIn = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeIn = new DoubleAnimation(0, targetOpacity, TimeSpan.FromMilliseconds(180))
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

        // ─── Equalizador / Waveform Animado Estilo iOS ──────────────────────────
        private bool _isWaveformAnimating = false;
        private Color _currentWaveformColor = Color.FromRgb(0x30, 0xD1, 0x58);

        private const double RestBarHeight = 2.8;

        private DispatcherTimer? _visualizerTimer;
        private MMDeviceEnumerator? _meterEnumerator;
        private MMDevice? _meterDevice;
        private float _peakTracker = 0.1f;
        private float _prevMaster = 0f;
        private double _curBar1 = RestBarHeight;
        private double _curBar2 = RestBarHeight;
        private double _curBar3 = RestBarHeight;
        private double _curBar4 = RestBarHeight;

        public void UpdateWaveformColor(BitmapSource? thumbnail)
        {
            try
            {
                Color targetColor = ExtractVibrantColor(thumbnail);
                if (targetColor == _currentWaveformColor) return;
                _currentWaveformColor = targetColor;

                if (this.Resources["WaveformAccentBrush"] is SolidColorBrush currentBrush && !currentBrush.IsFrozen)
                {
                    var anim = new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(350))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    currentBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
                else
                {
                    var newBrush = new SolidColorBrush(targetColor);
                    this.Resources["WaveformAccentBrush"] = newBrush;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateWaveformColor] {ex.Message}");
            }
        }

        private Color ExtractVibrantColor(BitmapSource? thumbnail)
        {
            if (thumbnail != null)
            {
                try
                {
                    // Amostra uma grade de 16x16 pixels para extração ultra-rápida (< 1ms)
                    int sampleW = 16;
                    int sampleH = 16;
                    var renderBmp = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
                    var scaled = new TransformedBitmap(renderBmp, new ScaleTransform(
                        (double)sampleW / Math.Max(1, renderBmp.PixelWidth),
                        (double)sampleH / Math.Max(1, renderBmp.PixelHeight)));

                    int stride = sampleW * 4;
                    byte[] pixels = new byte[stride * sampleH];
                    scaled.CopyPixels(pixels, stride, 0);

                    float bestScore = -1f;
                    Color bestColor = Color.FromRgb(0x30, 0xD1, 0x58);

                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        byte a = pixels[i + 3];

                        if (a < 128) continue;

                        float rf = r / 255f;
                        float gf = g / 255f;
                        float bf = b / 255f;

                        float max = Math.Max(rf, Math.Max(gf, bf));
                        float min = Math.Min(rf, Math.Min(gf, bf));
                        float delta = max - min;

                        float sat = (max == 0) ? 0 : delta / max;
                        float val = max;

                        // Descarta tons quase pretos, excessivamente escuros ou quase cinzas
                        if (val < 0.25f || sat < 0.28f) continue;

                        // Favorece cores vivas e expressivas
                        float score = (sat * 2.2f) + val;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            // Garante brilho suficiente para contraste no fundo preto da Island
                            float boost = val < 0.65f ? 0.65f / val : 1.0f;
                            byte finalR = (byte)Math.Clamp((int)(r * boost), 0, 255);
                            byte finalG = (byte)Math.Clamp((int)(g * boost), 0, 255);
                            byte finalB = (byte)Math.Clamp((int)(b * boost), 0, 255);
                            bestColor = Color.FromRgb(finalR, finalG, finalB);
                        }
                    }

                    if (bestScore > 0)
                    {
                        return bestColor;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExtractVibrantColor] {ex.Message}");
                }
            }

            // Fallback: se o tema do usuário for claro/vibrante, aproveita-o; se for escuro/cinza (como #444444), usa o verde clássico do iOS
            float lum = (0.299f * _themeColor.R + 0.587f * _themeColor.G + 0.114f * _themeColor.B) / 255f;
            if (lum >= 0.45f)
            {
                return _themeColor;
            }

            return Color.FromRgb(0x30, 0xD1, 0x58); // Apple Music Green
        }

        private void UpdateWaveformAnimation(bool isPlaying)
        {
            if (waveBar1 == null || waveBar2 == null || waveBar3 == null || waveBar4 == null) return;

            if (isPlaying)
            {
                if (_isWaveformAnimating) return;
                _isWaveformAnimating = true;

                // Cancela qualquer animação estática ativa antes de ligar o visualizador em tempo real
                waveBar1.BeginAnimation(FrameworkElement.HeightProperty, null);
                waveBar2.BeginAnimation(FrameworkElement.HeightProperty, null);
                waveBar3.BeginAnimation(FrameworkElement.HeightProperty, null);
                waveBar4.BeginAnimation(FrameworkElement.HeightProperty, null);

                _curBar1 = waveBar1.ActualHeight > 0 ? waveBar1.ActualHeight : RestBarHeight;
                _curBar2 = waveBar2.ActualHeight > 0 ? waveBar2.ActualHeight : RestBarHeight;
                _curBar3 = waveBar3.ActualHeight > 0 ? waveBar3.ActualHeight : RestBarHeight;
                _curBar4 = waveBar4.ActualHeight > 0 ? waveBar4.ActualHeight : RestBarHeight;

                if (_visualizerTimer == null)
                {
                    _visualizerTimer = new DispatcherTimer(DispatcherPriority.Render)
                    {
                        Interval = TimeSpan.FromMilliseconds(25) // ~40 FPS suave
                    };
                    _visualizerTimer.Tick += VisualizerTimer_Tick;
                }

                _visualizerTimer.Start();
            }
            else
            {
                _visualizerTimer?.Stop();
                if (!_isWaveformAnimating && Math.Abs(waveBar1.Height - RestBarHeight) < 0.1 && Math.Abs(waveBar2.Height - RestBarHeight) < 0.1) return;
                _isWaveformAnimating = false;

                // Todas as barras descem ao nível de repouso uniforme (pontos/cápsulas mínimas idênticas)
                StopWaveBar(waveBar1, RestBarHeight);
                StopWaveBar(waveBar2, RestBarHeight);
                StopWaveBar(waveBar3, RestBarHeight);
                StopWaveBar(waveBar4, RestBarHeight);
            }
        }

        private void VisualizerTimer_Tick(object? sender, EventArgs e)
        {
            if (waveBar1 == null || waveBar2 == null || waveBar3 == null || waveBar4 == null) return;

            try
            {
                if (_meterDevice == null)
                {
                    _meterEnumerator ??= new MMDeviceEnumerator();
                    _meterDevice = _meterEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                var meter = _meterDevice?.AudioMeterInformation;
                if (meter == null) return;

                float master = meter.MasterPeakValue;
                float left = meter.PeakValues.Count > 0 ? meter.PeakValues[0] : master;
                float right = meter.PeakValues.Count > 1 ? meter.PeakValues[1] : master;

                // Quando o áudio estiver em silêncio ou pausa momentânea, decai suavemente para o repouso mínimo uniforme
                if (master < 0.002f)
                {
                    _curBar1 += (RestBarHeight - _curBar1) * 0.15;
                    _curBar2 += (RestBarHeight - _curBar2) * 0.15;
                    _curBar3 += (RestBarHeight - _curBar3) * 0.15;
                    _curBar4 += (RestBarHeight - _curBar4) * 0.15;

                    waveBar1.Height = _curBar1;
                    waveBar2.Height = _curBar2;
                    waveBar3.Height = _curBar3;
                    waveBar4.Height = _curBar4;
                    return;
                }

                // AGC (Controle Automático de Ganho): adapta-se à faixa de volume do áudio do usuário
                if (master > _peakTracker)
                {
                    _peakTracker = master;
                }
                else
                {
                    _peakTracker = Math.Max(0.04f, _peakTracker * 0.985f);
                }

                // Normalização perceptual não-linear (potência 0.6)
                float norm = Math.Clamp((float)Math.Pow(master / _peakTracker, 0.6), 0f, 1f);
                float transient = Math.Max(0f, (master - _prevMaster) * 3.5f);
                _prevMaster = master;

                // Alvos para as 4 barras em tempo real guiados pelo som real
                double t1 = RestBarHeight + 10.5 * Math.Clamp((left * 1.2f / _peakTracker) + transient, 0f, 1f);
                double t2 = RestBarHeight + 14.5 * norm;
                double t3 = RestBarHeight + 12.5 * Math.Clamp((right * 1.1f / _peakTracker) + Math.Abs(left - right) * 2.0f, 0f, 1f);
                double t4 = RestBarHeight + 9.5 * Math.Clamp((norm * 0.7f) + (transient * 1.5f), 0f, 1f);

                // Física suave de ataque e decaimento: sobe rápido no beat, desce com gravidade suave
                _curBar1 = (t1 > _curBar1) ? _curBar1 + (t1 - _curBar1) * 0.65 : _curBar1 + (t1 - _curBar1) * 0.18;
                _curBar2 = (t2 > _curBar2) ? _curBar2 + (t2 - _curBar2) * 0.70 : _curBar2 + (t2 - _curBar2) * 0.15;
                _curBar3 = (t3 > _curBar3) ? _curBar3 + (t3 - _curBar3) * 0.65 : _curBar3 + (t3 - _curBar3) * 0.18;
                _curBar4 = (t4 > _curBar4) ? _curBar4 + (t4 - _curBar4) * 0.75 : _curBar4 + (t4 - _curBar4) * 0.22;

                waveBar1.Height = _curBar1;
                waveBar2.Height = _curBar2;
                waveBar3.Height = _curBar3;
                waveBar4.Height = _curBar4;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VisualizerTick] {ex.Message}");
                try { _meterDevice?.Dispose(); } catch { }
                _meterDevice = null;
            }
        }
private static void StopWaveBar(Border bar, double restH)
        {
            bar.BeginAnimation(FrameworkElement.HeightProperty, null);
            double currentH = bar.ActualHeight > 0 ? bar.ActualHeight : bar.Height;
            var settle = new DoubleAnimation(currentH, restH, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            settle.Completed += (s, e) =>
            {
                bar.BeginAnimation(FrameworkElement.HeightProperty, null);
                bar.Height = restH;
            };
            bar.BeginAnimation(FrameworkElement.HeightProperty, settle);
        }

        private void DisposeMedia()
        {
            UpdateWaveformAnimation(false);
            try
            {
                _mediaProgressTimer?.Stop();
                _visualizerTimer?.Stop();
                _meterDevice?.Dispose();
                _meterEnumerator?.Dispose();
            }
            catch { }
            if (_mediaService != null)
            {
                _mediaService.StateChanged -= MediaService_StateChanged;
                _mediaService.Dispose();
            }
        }

        // ─── Estado Expandido da Barra Inferior (Pílula de Progresso de Mídia) ───
        private TimeSpan _mediaBasePosition = TimeSpan.Zero;
        private TimeSpan _mediaDuration = TimeSpan.Zero;
        private DateTimeOffset _mediaPositionCapturedUtc = DateTimeOffset.MinValue;
        private bool _mediaHasTimeline = false;
        private bool _isMediaPlaying = false;
        private bool _isScrubbingMedia = false;
        private DispatcherTimer? _mediaProgressTimer;

        internal void UpdateBottomExpandedState()
        {
            if (miniCompactTextPanel == null || expandedMediaProgressPill == null) return;

            bool showExpandedPill = _isIslandOpen && _currentMode == MediaMode.Music;

            if (showExpandedPill)
            {
                miniCompactTextPanel.Visibility = Visibility.Collapsed;
                if (expandedMediaProgressPill.Visibility != Visibility.Visible)
                {
                    expandedMediaProgressPill.Opacity = 0;
                    expandedMediaProgressPill.Visibility = Visibility.Visible;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    expandedMediaProgressPill.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }

                UpdateMediaProgressUI();

                if (_mediaProgressTimer == null)
                {
                    _mediaProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    _mediaProgressTimer.Tick += (s, e) =>
                    {
                        if (_isIslandOpen && _currentMode == MediaMode.Music && !_isScrubbingMedia)
                        {
                            UpdateMediaProgressUI();
                        }
                    };
                }
                if (!_mediaProgressTimer.IsEnabled)
                {
                    _mediaProgressTimer.Start();
                }
            }
            else
            {
                _mediaProgressTimer?.Stop();
                expandedMediaProgressPill.Visibility = Visibility.Collapsed;
                if (miniCompactTextPanel.Visibility != Visibility.Visible)
                {
                    miniCompactTextPanel.Opacity = 0;
                    miniCompactTextPanel.Visibility = Visibility.Visible;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    miniCompactTextPanel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
            }
        }

        private TimeSpan GetCurrentMediaPosition()
        {
            if (!_mediaHasTimeline || _mediaDuration <= TimeSpan.Zero) return TimeSpan.Zero;

            var pos = _mediaBasePosition;
            if (_isMediaPlaying && _mediaPositionCapturedUtc > DateTimeOffset.MinValue)
            {
                var elapsed = DateTimeOffset.UtcNow - _mediaPositionCapturedUtc;
                if (elapsed > TimeSpan.Zero) pos += elapsed;
            }
            if (pos < TimeSpan.Zero) return TimeSpan.Zero;
            if (pos > _mediaDuration) return _mediaDuration;
            return pos;
        }

        private void UpdateMediaProgressUI(TimeSpan? overridePosition = null)
        {
            if (txtMediaElapsed == null || txtMediaRemaining == null || mediaProgressFill == null || mediaProgressGrid == null) return;

            if (!_mediaHasTimeline || _mediaDuration <= TimeSpan.Zero)
            {
                txtMediaElapsed.Text = "0:00";
                txtMediaRemaining.Text = "-0:00";
                mediaProgressFill.Width = 0;
                return;
            }

            var currentPos = overridePosition ?? GetCurrentMediaPosition();
            var remaining = _mediaDuration - currentPos;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            txtMediaElapsed.Text = FormatMediaTime(currentPos);
            txtMediaRemaining.Text = "-" + FormatMediaTime(remaining);

            double totalWidth = mediaProgressGrid.ActualWidth;
            if (totalWidth > 0)
            {
                double ratio = Math.Clamp(currentPos.TotalSeconds / _mediaDuration.TotalSeconds, 0.0, 1.0);
                mediaProgressFill.Width = totalWidth * ratio;
            }

            UpdateMediaProgressFillTextContrast();
        }

        private void UpdateMediaProgressFillTextContrast()
        {
            if (TryFindResource("ThemeAccentBrush") is SolidColorBrush accentBrush)
            {
                var c = accentBrush.Color;
                double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                var fg = luminance > 0.55
                    ? new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12))
                    : Brushes.White;
                if (txtMediaElapsedFill != null) txtMediaElapsedFill.Foreground = fg;
                if (txtMediaExpandedTitleFill != null) txtMediaExpandedTitleFill.Foreground = fg;
                if (txtMediaRemainingFill != null) txtMediaRemainingFill.Foreground = fg;
            }
        }

        private static string FormatMediaTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
            return $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
        }

        private void MediaProgress_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (mediaProgressGrid == null || mediaProgressFillOverlay == null) return;
            double w = mediaProgressGrid.ActualWidth;
            double h = mediaProgressGrid.ActualHeight;
            if (w > 0 && h > 0)
            {
                mediaProgressGrid.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14);
                mediaProgressFillOverlay.Width = w;
                mediaProgressFillOverlay.Height = h;
                UpdateMediaProgressUI();
            }
        }

        private TimeSpan CalculateScrubPosition(MouseEventArgs e)
        {
            if (mediaProgressGrid == null || !_mediaHasTimeline || _mediaDuration <= TimeSpan.Zero)
                return TimeSpan.Zero;

            double width = mediaProgressGrid.ActualWidth;
            if (width <= 0) return TimeSpan.Zero;

            double x = Math.Clamp(e.GetPosition(mediaProgressGrid).X, 0.0, width);
            double ratio = x / width;
            return TimeSpan.FromSeconds(_mediaDuration.TotalSeconds * ratio);
        }

        private void MediaProgress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_mediaHasTimeline || _mediaDuration <= TimeSpan.Zero) return;

            _isScrubbingMedia = true;
            expandedMediaProgressPill.CaptureMouse();
            var target = CalculateScrubPosition(e);
            UpdateMediaProgressUI(target);
        }

        private void MediaProgress_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isScrubbingMedia) return;
            e.Handled = true;
            var target = CalculateScrubPosition(e);
            UpdateMediaProgressUI(target);
        }

        private async void MediaProgress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isScrubbingMedia) return;
            e.Handled = true;
            _isScrubbingMedia = false;
            expandedMediaProgressPill.ReleaseMouseCapture();

            var target = CalculateScrubPosition(e);
            _mediaBasePosition = target;
            _mediaPositionCapturedUtc = DateTimeOffset.UtcNow;
            UpdateMediaProgressUI(target);

            await _mediaService.SeekAsync(target);
        }
    }
}