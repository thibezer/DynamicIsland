using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicIslandWindows
{
    /// <summary>
    /// Conversor que calcula o CornerRadius concêntrico para um elemento filho
    /// com base no CornerRadius do container pai e na margem/padding interno,
    /// seguindo a fórmula da Apple (WWDC 2025): radius_filho = parent_radius - margin_interna.
    /// </summary>
    public class ConcentricCornerRadiusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is CornerRadius parentRadius)
            {
                double margin = 10.0;
                if (parameter != null)
                {
                    double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out margin);
                }

                double topLeft     = Math.Max(0, parentRadius.TopLeft - margin);
                double topRight    = Math.Max(0, parentRadius.TopRight - margin);
                double bottomLeft  = Math.Max(0, parentRadius.BottomLeft - margin);
                double bottomRight = Math.Max(0, parentRadius.BottomRight - margin);

                return new CornerRadius(topLeft, topRight, bottomLeft, bottomRight);
            }
            
            // Fallback caso não seja passado um CornerRadius de entrada
            if (value is double singleRadius)
            {
                double margin = 10.0;
                if (parameter != null)
                {
                    double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out margin);
                }
                return new CornerRadius(Math.Max(0, singleRadius - margin));
            }

            return new CornerRadius(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
