using System;
using System.Configuration;
using System.IO;
using Microsoft.Win32;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Resolves the destination path for the signed PDF, driven by <c>App.config</c>:
    ///   <list type="bullet">
    ///     <item><c>SaveAsDialog = true</c> (default): a "Save As" dialog is shown so the
    ///           user can pick folder and file name.</item>
    ///     <item><c>SaveAsDialog = false</c>: the document is written into
    ///           <c>TargetDirectory</c> without prompting (falling back to the source
    ///           folder when the setting is empty).</item>
    ///   </list>
    /// The file name is always <c>&lt;OriginalName&gt;_signed.pdf</c>.
    /// </summary>
    public static class SaveTargetHelper
    {
        /// <summary>
        /// Determines the output file path for the signed document.
        /// Returns <c>null</c> if the user cancelled the "Save As" dialog.
        /// </summary>
        /// <param name="sourcePdfPath">Path of the original (input) PDF.</param>
        public static string? ResolveOutputPath(string sourcePdfPath)
        {
            string sourceDir   = Path.GetDirectoryName(sourcePdfPath) ?? "";
            string defaultName = Path.GetFileNameWithoutExtension(sourcePdfPath) + "_signed.pdf";

            bool   useDialog = ReadBool("SaveAsDialog", true);
            string targetDir = (ConfigurationManager.AppSettings["TargetDirectory"] ?? "").Trim();
            if (targetDir.Length > 0)
                targetDir = Environment.ExpandEnvironmentVariables(targetDir);

            if (useDialog)
                return FromDialog(sourceDir, targetDir, defaultName);

            return FromTargetDirectory(sourceDir, targetDir, defaultName);
        }

        // ----------------------------------------------------------------
        //  Save-As dialog mode
        // ----------------------------------------------------------------
        private static string? FromDialog(string sourceDir, string targetDir, string defaultName)
        {
            string initialDir = targetDir.Length > 0 && Directory.Exists(targetDir)
                ? targetDir
                : sourceDir;

            var dlg = new SaveFileDialog
            {
                Title            = UiLabels.SaveDialogTitle,
                Filter           = UiLabels.SaveDialogFilter,
                DefaultExt       = ".pdf",
                AddExtension     = true,
                FileName         = defaultName,
                InitialDirectory = initialDir,
                OverwritePrompt  = true,
            };

            if (dlg.ShowDialog() != true)
            {
                AppLogger.Info("SaveTargetHelper: Save-As dialog cancelled by user");
                return null;
            }

            AppLogger.Info($"SaveTargetHelper: Save-As target → '{dlg.FileName}'");
            return dlg.FileName;
        }

        // ----------------------------------------------------------------
        //  Target-directory mode
        // ----------------------------------------------------------------
        private static string FromTargetDirectory(string sourceDir, string targetDir, string defaultName)
        {
            string dir = targetDir.Length > 0 ? targetDir : sourceDir;

            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    AppLogger.Info($"SaveTargetHelper: created target directory '{dir}'");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"SaveTargetHelper: target directory '{dir}' unusable ({ex.Message}); " +
                               $"falling back to source folder '{sourceDir}'");
                dir = sourceDir;
            }

            string outPath = Path.Combine(dir, defaultName);
            AppLogger.Info($"SaveTargetHelper: target-directory mode → '{outPath}'");
            return outPath;
        }

        // ----------------------------------------------------------------
        //  Helper
        // ----------------------------------------------------------------
        private static bool ReadBool(string key, bool fallback)
        {
            string? raw = ConfigurationManager.AppSettings[key];
            return bool.TryParse(raw, out bool v) ? v : fallback;
        }
    }
}
