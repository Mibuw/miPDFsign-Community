using System;
using System.IO;
using System.Windows;
using miPDFsign.Helpers;

namespace miPDFsign
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // No third-party license registration needed: the PDF engine miPDFlib (Apache-2.0)
            // requires no runtime license key.
            AppLogger.Initialize();

            // ── Parse command-line argument ──────────────────────────────
            string? pdfPath = null;

            if (e.Args.Length > 0)
                pdfPath = e.Args[0];

            // If no argument, open a file dialog
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                AppLogger.Info("No valid PDF path argument – opening file dialog.");
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = "Select PDF document",
                    Filter = "PDF files (*.pdf)|*.pdf"
                };

                if (dlg.ShowDialog() != true)
                {
                    AppLogger.Info("File dialog cancelled – shutting down.");
                    Shutdown(0);
                    return;
                }
                pdfPath = dlg.FileName;
            }

            AppLogger.Info($"Opening PDF: {pdfPath}");

            // ── Show main window ─────────────────────────────────────────
            var window = new MainWindow(pdfPath!);
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("Application exiting.");
            base.OnExit(e);
        }
    }
}
