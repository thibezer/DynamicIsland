using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DynamicIslandWindows
{
    public partial class NativeIslandWindow
    {
        // ─── Drag & Drop NATIVO PERFEITO (Focado apenas no Border) ──────────────────

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Link; ActivateDropMode(); }
            else { e.Effects = DragDropEffects.None; }
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Link;
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (_dragLeaveTimer == null)
            {
                _dragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DRAGLEAVE_DELAY_MS) };
                _dragLeaveTimer.Tick += DragLeaveTimer_Tick;
            }
            _dragLeaveTimer.Start();
            e.Handled = true;
        }

        private void DragLeaveTimer_Tick(object? sender, EventArgs e)
        {
            _dragLeaveTimer!.Stop();
            // Mouse.GetPosition não é atualizado durante um arraste OLE; usa o cursor do sistema
            Point mousePos = GetCursorPos(out POINT cursor)
                ? PointFromScreen(new Point(cursor.X, cursor.Y))
                : Mouse.GetPosition(this);

            // Se saiu da área física do Border visível, fecha a zona de drop
            bool inside = mousePos.X >= 10 &&
                          mousePos.X <= 10 + islandBorder.ActualWidth &&
                          mousePos.Y >= (this.Height - 10 - islandBorder.ActualHeight) &&
                          mousePos.Y <= this.Height - 10;

            if (!inside) DeactivateDropMode();
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            _dragLeaveTimer?.Stop();
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files?.Length > 0)
                {
                    _pendingDropFilePath = files[0];
                    txtDropFileName.Text = Path.GetFileName(files[0]);
                    ShowDropPanel();
                }
            }
            _isDraggingFile = false; e.Handled = true;
        }

        private void ActivateDropMode()
        {
            EnsureTopmost();
            if (_isDropZoneActive) return;
            _isDraggingFile = _isDropZoneActive = true;
        }

        private void DeactivateDropMode()
        {
            _isDraggingFile = _isDropZoneActive = false;
            _dragLeaveTimer?.Stop();
            _pendingDropFilePath = null;

            if (islandBorder != null)
            {
                islandBorder.Effect = null;
                islandBorder.BorderThickness = new Thickness(0);
            }

            CloseIsland();
        }

        private void ShowDropPanel()
        {
            EnsureTopmost();
            miniIslandPanel.Visibility = Visibility.Collapsed;
            expandedIslandPanel.Visibility = Visibility.Collapsed;
            dropZonePanel.Visibility = Visibility.Visible;

            if (mainContentGrid != null)
            {
                mainContentGrid.VerticalAlignment = VerticalAlignment.Bottom;
            }

            if (islandBorder != null)
            {
                var dropShadow = new DropShadowEffect
                {
                    Color = Color.FromRgb(0x4C, 0xC2, 0xFF),
                    ShadowDepth = 0,
                    Opacity = 0.8,
                    BlurRadius = 25
                };
                islandBorder.Effect = dropShadow;
                islandBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

                var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

                var opacityAnim = new DoubleAnimation(0.45, 0.95, TimeSpan.FromMilliseconds(900))
                { 
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = easing
                };

                dropShadow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);
            }

            // Altura ajustada ao conteúdo (em vez de fixa), para as margens superior e inferior ficarem iguais
            dropZonePanel.Measure(new Size(DROPZONE_W - 20, double.PositiveInfinity));
            double contentHeight = dropZonePanel.DesiredSize.Height + 13; // + padding vertical da ilha (6,5 x 2)
            AnimateBorder(DROPZONE_W, contentHeight > 13 ? Math.Min(contentHeight, DROPZONE_H) : DROPZONE_H);
        }

        private void OpenManualFilePicker()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Selecionar ficheiro para Dynamic Island", Filter = "Todos os ficheiros (*.*)|*.*" };
                if (dlg.ShowDialog(this) != true) return;

                _pendingDropFilePath = dlg.FileName;
                txtDropFileName.Text = Path.GetFileName(dlg.FileName);
                ShowDropPanel();
            }
            catch (Exception ex) { Debug.WriteLine($"[FilePicker] {ex.Message}"); }
        }

        private void ExcelAction_Click(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(_pendingDropFilePath))
            {
                _excelService.SendFileToExcel(_pendingDropFilePath); _pendingDropFilePath = null;
            }
            DeactivateDropMode();
        }

        private void CancelDrop_Click(object sender, MouseButtonEventArgs e)
        {
            _pendingDropFilePath = null; DeactivateDropMode();
        }

        private void DisposeDropZone()
        {
            _dragLeaveTimer?.Stop();
            _dragLeaveTimer = null;
        }
    }
}
