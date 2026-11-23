using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIslandWindows.Services
{
    /// <summary>
    /// Extrai paletas de cores a partir da capa da mídia e garante conformidade de contraste WCAG 2.1.
    /// </summary>
    public static class AdaptiveColorService
    {
        public readonly record struct AdaptiveIslandPalette(
            Color Background,
            Color InactiveSurface,
            Color Accent,
            Color Waveform,
            Color Foreground,
            Color SecondaryForeground,
            Color AccentForeground);

        private const double MinAccentContrastOnBackground = 4.5; // WCAG AA texto/UI
        private const double MinAccentContrastOnInactive = 3.5;   // WCAG AA componentes gráficos sobre cards/botões inativos

        /// <summary>
        /// Calcula a luminância relativa sRGB segundo a especificação WCAG 2.1.
        /// </summary>
        public static double GetRelativeLuminance(Color color)
        {
            static double Linearize(byte channel)
            {
                double s = channel / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.R)
                 + 0.7152 * Linearize(color.G)
                 + 0.0722 * Linearize(color.B);
        }

        /// <summary>
        /// Calcula a razão de contraste WCAG 2.1 entre duas cores (1.0 a 21.0).
        /// </summary>
        public static double GetContrastRatio(Color a, Color b)
        {
            double l1 = GetRelativeLuminance(a);
            double l2 = GetRelativeLuminance(b);
            if (l1 < l2) (l1, l2) = (l2, l1);
            return (l1 + 0.05) / (l2 + 0.05);
        }

        /// <summary>
        /// Retorna branco ou preto-carvão (#0D0D0F) garantindo maior contraste WCAG (>= 4.5:1 para qualquer cor).
        /// </summary>
        public static Color GetAccessibleForeground(Color background)
        {
            Color dark = Color.FromRgb(0x0D, 0x0D, 0x0F);
            Color light = Colors.White;
            return GetContrastRatio(light, background) >= GetContrastRatio(dark, background)
                ? light
                : dark;
        }

        /// <summary>
        /// Retorna cor de texto secundário acessível (contraste >= 4.5:1 quando possível).
        /// </summary>
        public static Color GetAccessibleSecondaryForeground(Color background)
        {
            Color primary = GetAccessibleForeground(background);
            bool isDarkBg = primary == Colors.White;
            Color candidate = isDarkBg
                ? Color.FromRgb(0xC2, 0xC2, 0xC8)
                : Color.FromRgb(0x3A, 0x3A, 0x3F);

            if (GetContrastRatio(candidate, background) >= 4.5)
                return candidate;

            return primary;
        }

        /// <summary>
        /// Extrai uma paleta completa (fundo, superfície inativa, destaque, waveform e textos)
        /// a partir da miniatura da mídia, preservando acessibilidade e contraste.
        /// </summary>
        public static AdaptiveIslandPalette ExtractPalette(BitmapSource thumbnail, Color fallbackAccent)
        {
            const int sampleSize = 24;
            var renderBmp = new FormatConvertedBitmap(thumbnail, PixelFormats.Bgra32, null, 0);
            var scaled = new TransformedBitmap(renderBmp, new ScaleTransform(
                (double)sampleSize / Math.Max(1, renderBmp.PixelWidth),
                (double)sampleSize / Math.Max(1, renderBmp.PixelHeight)));

            int stride = sampleSize * 4;
            byte[] pixels = new byte[stride * sampleSize];
            scaled.CopyPixels(pixels, stride, 0);

            var buckets = new Dictionary<int, (long rSum, long gSum, long bSum, int count)>();
            int totalOpaque = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 128) continue;

                totalOpaque++;
                // Quantização 4-bit por canal (4096 baldes) para agrupar tons dominantes
                int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                if (buckets.TryGetValue(key, out var acc))
                {
                    buckets[key] = (acc.rSum + r, acc.gSum + g, acc.bSum + b, acc.count + 1);
                }
                else
                {
                    buckets[key] = (r, g, b, 1);
                }
            }

            if (totalOpaque == 0 || buckets.Count == 0)
            {
                return BuildFromBaseColors(Color.FromRgb(0x00, 0x00, 0x00), fallbackAccent);
            }

            Color dominantColor = Color.FromRgb(0x12, 0x12, 0x14);
            int maxCount = -1;

            Color vibrantColor = fallbackAccent;
            double bestVibrantScore = -1;

            foreach (var entry in buckets.Values)
            {
                byte avgR = (byte)(entry.rSum / entry.count);
                byte avgG = (byte)(entry.gSum / entry.count);
                byte avgB = (byte)(entry.bSum / entry.count);
                var c = Color.FromRgb(avgR, avgG, avgB);
                RgbToHsl(c, out double h, out double s, out double l);

                // Dominante: favorece frequência ponderada por leve preferência a tons com alguma cor
                int weightedCount = s > 0.15 ? (int)(entry.count * 1.25) : entry.count;
                if (weightedCount > maxCount)
                {
                    maxCount = weightedCount;
                    dominantColor = c;
                }

                // Vibrante: descarta quase-pretos, quase-brancos e quase-cinzas
                if (s >= 0.26 && l >= 0.18 && l <= 0.85)
                {
                    double freqBoost = Math.Min(1.5, 0.7 + (double)entry.count / totalOpaque * 4.0);
                    double lightnessScore = 1.0 - Math.Abs(l - 0.55);
                    double score = (s * 2.2 + lightnessScore) * freqBoost;
                    if (score > bestVibrantScore)
                    {
                        bestVibrantScore = score;
                        vibrantColor = c;
                    }
                }
            }

            if (bestVibrantScore < 0)
            {
                // Capa monocromática / preto e branco: usa destaque neutro claro de alto contraste
                vibrantColor = Color.FromRgb(0xE5, 0xE5, 0xEA);
            }

            return BuildFromBaseColors(dominantColor, vibrantColor);
        }

        private static AdaptiveIslandPalette BuildFromBaseColors(Color dominantColor, Color vibrantColor)
        {
            RgbToHsl(dominantColor, out double domH, out double domS, out _);

            // Fundo escuro rico e atmosférico (preserva o matiz da capa mantendo L <= 0.045 para contraste AAA com branco)
            double bgSat = domS < 0.10 ? 0.04 : Math.Min(domS * 0.80, 0.52);
            double bgLight = domS < 0.10 ? 0.045 : 0.075;
            Color bg = HslToRgb(domH, bgSat, bgLight);

            // Garante teto de luminância relativa para nunca comprometer legibilidade do conteúdo claro
            while (GetRelativeLuminance(bg) > 0.045 && bgLight > 0.02)
            {
                bgLight -= 0.01;
                bg = HslToRgb(domH, bgSat, bgLight);
            }

            // Superfície inativa (botões/cards): sutilmente mais clara que o fundo, mas sempre escura e discreta
            // para contrastar com o destaque (sliders/ativos) e manter >= 8:1 com texto branco
            double inactSat = bgSat * 0.65;
            double inactLight = Math.Clamp(bgLight + 0.065, 0.11, 0.16);
            Color inactive = HslToRgb(domH, inactSat, inactLight);
            while (GetContrastRatio(Colors.White, inactive) < 8.0 && inactLight > bgLight + 0.03)
            {
                inactLight -= 0.01;
                inactive = HslToRgb(domH, inactSat, inactLight);
            }

            // Cor de destaque: garante contraste WCAG AA contra fundo e superfície inativa
            Color accent = EnsureMinimumContrast(vibrantColor, bg, MinAccentContrastOnBackground);
            accent = EnsureMinimumContrast(accent, inactive, MinAccentContrastOnInactive);

            Color waveform = EnsureMinimumContrast(vibrantColor, bg, MinAccentContrastOnBackground);
            Color fg = GetAccessibleForeground(bg);
            Color fgSec = GetAccessibleSecondaryForeground(bg);
            Color accentFg = GetAccessibleForeground(accent);

            return new AdaptiveIslandPalette(bg, inactive, accent, waveform, fg, fgSec, accentFg);
        }

        /// <summary>
        /// Ajusta a luminosidade da cor em HSL até atingir a razão de contraste mínima sobre o fundo informado.
        /// </summary>
        public static Color EnsureMinimumContrast(Color foreground, Color background, double minContrast)
        {
            if (GetContrastRatio(foreground, background) >= minContrast)
                return foreground;

            RgbToHsl(foreground, out double h, out double s, out double l);
            double bgLum = GetRelativeLuminance(background);

            // Se o fundo é escuro, clareia o primeiro plano; se for claro, escurece
            bool lighten = bgLum < 0.18;
            double step = lighten ? 0.025 : -0.025;

            for (int i = 0; i < 40; i++)
            {
                l = Math.Clamp(l + step, 0.0, 1.0);
                Color candidate = HslToRgb(h, s, l);
                if (GetContrastRatio(candidate, background) >= minContrast)
                    return candidate;

                if ((lighten && l >= 0.98) || (!lighten && l <= 0.02))
                    break;
            }

            return GetAccessibleForeground(background);
        }

        private static void RgbToHsl(Color color, out double h, out double s, out double l)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            l = (max + min) / 2.0;

            if (delta < 1e-6)
            {
                h = 0;
                s = 0;
                return;
            }

            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

            if (Math.Abs(max - r) < 1e-6)
                h = ((g - b) / delta) + (g < b ? 6.0 : 0.0);
            else if (Math.Abs(max - g) < 1e-6)
                h = ((b - r) / delta) + 2.0;
            else
                h = ((r - g) / delta) + 4.0;

            h /= 6.0;
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            if (s <= 1e-6)
            {
                byte v = (byte)Math.Clamp((int)Math.Round(l * 255.0), 0, 255);
                return Color.FromRgb(v, v, v);
            }

            static double HueToRgb(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
                if (t < 1.0 / 2.0) return q;
                if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
                return p;
            }

            double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            double p = 2.0 * l - q;

            double r = HueToRgb(p, q, h + 1.0 / 3.0);
            double g = HueToRgb(p, q, h);
            double b = HueToRgb(p, q, h - 1.0 / 3.0);

            return Color.FromRgb(
                (byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255),
                (byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255),
                (byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255));
        }
    }
}
