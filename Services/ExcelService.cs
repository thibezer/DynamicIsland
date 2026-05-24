using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicIslandWindows.Services
{
    public class ExcelService
    {
        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        [DllImport("ole32.dll")]
        private static extern int CLSIDFromProgID(
            [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid pclsid);

        public void SendFileToExcel(string filePath)
        {
            object? excelApp = null;
            object? activeSheet = null;
            object? activeCell = null;
            object? hyperlinks = null;

            try
            {
                if (CLSIDFromProgID("Excel.Application", out Guid clsid) != 0)
                {
                    ShowExcelError("Excel não encontrado. Certifique-se de que está instalado.");
                    return;
                }

                GetActiveObject(ref clsid, IntPtr.Zero, out excelApp);
                if (excelApp == null)
                {
                    ShowExcelError("Nenhuma janela do Excel está aberta.");
                    return;
                }

                var flags = System.Reflection.BindingFlags.GetProperty;
                activeSheet = excelApp.GetType().InvokeMember("ActiveSheet", flags, null, excelApp, null);
                activeCell = excelApp.GetType().InvokeMember("ActiveCell", flags, null, excelApp, null);
                hyperlinks = activeSheet?.GetType().InvokeMember("Hyperlinks", flags, null, activeSheet, null);

                if (activeSheet == null || activeCell == null || hyperlinks == null)
                {
                    ShowExcelError("Não foi possível aceder à folha ou célula ativa do Excel.");
                    return;
                }

                object missing = System.Reflection.Missing.Value;
                hyperlinks.GetType().InvokeMember("Add",
                    System.Reflection.BindingFlags.InvokeMethod, null, hyperlinks,
                    new object[] { activeCell, filePath, missing, missing, Path.GetFileName(filePath) });

                MessageBox.Show("Hiperlink criado no Excel com sucesso!",
                    "Sucesso Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (COMException comEx)
            {
                Debug.WriteLine($"[Excel COM] {comEx.Message}");
                ShowExcelError("Erro de comunicação com o Excel.\n\nCertifique-se de que a célula não está em modo de edição.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Excel] {ex.Message}");
                ShowExcelError(ex.Message);
            }
            finally
            {
                // BLINDAGEM: Garante que os processos do Excel sejam encerrados da memória
                if (hyperlinks != null) Marshal.ReleaseComObject(hyperlinks);
                if (activeCell != null) Marshal.ReleaseComObject(activeCell);
                if (activeSheet != null) Marshal.ReleaseComObject(activeSheet);
                if (excelApp != null) Marshal.ReleaseComObject(excelApp);
            }
        }

        private static void ShowExcelError(string msg) =>
            MessageBox.Show(msg, "Aviso Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
