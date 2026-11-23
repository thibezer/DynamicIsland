using System;
using System.Windows;
using System.Windows.Media;

namespace DynamicIslandWindows.Controls
{
    public partial class PipDragPreviewWindow : Window
    {
        public PipDragPreviewWindow(ImageSource? thumbnail)
        {
            InitializeComponent();

            if (thumbnail != null)
            {
                thumbBackdrop.Background = new ImageBrush(thumbnail)
                {
                    Stretch = Stretch.UniformToFill
                };
            }
        }

        public void UpdateDragState(double dipCenterX, double dipCenterY, bool isInsideIsland)
        {
            Left = dipCenterX - (Width / 2.0);
            Top = dipCenterY - (Height / 2.0);

            if (isInsideIsland)
            {
                previewCard.Opacity = 0.55;
                previewCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                txtHint.Text = "Arraste para fora da ilha";
            }
            else
            {
                previewCard.Opacity = 0.96;
                previewCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0xC2, 0xFF));
                txtHint.Text = "Solte para abrir PiP aqui";
            }
        }
    }
}
