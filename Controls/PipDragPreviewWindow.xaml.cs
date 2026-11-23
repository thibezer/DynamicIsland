using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIslandWindows.Controls
{
    public partial class PipDragPreviewWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

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
