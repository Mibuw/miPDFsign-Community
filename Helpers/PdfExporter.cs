using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Graphics;
using Syncfusion.Pdf.Interactive;
using Syncfusion.Pdf.Parsing;
using miPDFsign.Models;
using SysDrawing = System.Drawing;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Syncfusion-based helper for:
    ///   1. Detecting checkbox form fields in a PDF
    ///   2. Exporting the signed/annotated PDF back to disk
    /// </summary>
    public static class PdfExporter
    {
        // ----------------------------------------------------------------
        //  Checkbox detection
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns all checkbox widgets found in the PDF's AcroForm,
        /// with their position in PDF coordinate space (origin = bottom-left).
        /// </summary>
        public static List<CheckboxInfo> GetCheckboxes(string pdfPath)
        {
            using var pdfDoc = new PdfLoadedDocument(File.ReadAllBytes(pdfPath));
            return GetCheckboxes(pdfDoc);
        }

        /// <summary>
        /// Overload for when a <see cref="PdfLoadedDocument"/> is already open (avoids a second open).
        /// </summary>
        public static List<CheckboxInfo> GetCheckboxes(PdfLoadedDocument pdfDoc)
        {
            var result = new List<CheckboxInfo>();

            if (pdfDoc.Form is not PdfLoadedForm form) return result;

            foreach (PdfLoadedField field in form.Fields)
            {
                if (field is not PdfLoadedCheckBoxField cbField) continue;

                // A checkbox field may carry multiple widgets (Items) or just one.
                if (cbField.Items.Count > 0)
                {
                    // Multiple widgets — iterate each item independently.
                    foreach (PdfLoadedCheckBoxItem item in cbField.Items)
                    {
                        AddCheckboxInfo(result, pdfDoc, field.Name, item.Page,
                            item.Bounds, item.Checked);
                    }
                }
                else
                {
                    // Single widget — use the field directly.
                    AddCheckboxInfo(result, pdfDoc, field.Name, cbField.Page,
                        cbField.Bounds, cbField.Checked);
                }
            }

            return result;
        }

        private static void AddCheckboxInfo(
            List<CheckboxInfo> result,
            PdfLoadedDocument  pdfDoc,
            string             fieldName,
            PdfPageBase?       page,
            Syncfusion.Drawing.RectangleF sfBounds,  // Syncfusion: top-left origin
            bool               isChecked)
        {
            int pageIdx = -1;
            float pageHeight = 0f;

            if (page != null)
            {
                for (int i = 0; i < pdfDoc.Pages.Count; i++)
                {
                    if (ReferenceEquals(pdfDoc.Pages[i], page))
                    {
                        pageIdx    = i;
                        pageHeight = (pdfDoc.Pages[i] as PdfLoadedPage)!.Size.Height;
                        break;
                    }
                }
            }

            // Syncfusion widget bounds use the page's top-left coordinate system.
            // Convert to PDF space (bottom-left) for consistency with the rest of the app.
            float pdfBottom = pageHeight - sfBounds.Y - sfBounds.Height;

            result.Add(new CheckboxInfo
            {
                FieldName = fieldName,
                PageIndex = pageIdx,
                PdfLeft   = sfBounds.X,
                PdfBottom = pdfBottom,
                PdfWidth  = sfBounds.Width,
                PdfHeight = sfBounds.Height,
                IsChecked = isChecked,
            });
        }

        // ----------------------------------------------------------------
        //  Text field reader
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the value of a text (or any) form field, or <c>null</c>
        /// if the field does not exist or is empty.
        /// </summary>
        public static string? GetTextFieldValue(string pdfPath, string fieldName)
        {
            using var pdfDoc = new PdfLoadedDocument(File.ReadAllBytes(pdfPath));
            return GetTextFieldValue(pdfDoc, fieldName);
        }

        /// <summary>
        /// Overload for when a <see cref="PdfLoadedDocument"/> is already open.
        /// </summary>
        public static string? GetTextFieldValue(PdfLoadedDocument pdfDoc, string fieldName)
        {
            if (pdfDoc.Form is not PdfLoadedForm form) return null;

            foreach (PdfLoadedField field in form.Fields)
            {
                if (!string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? val = field switch
                {
                    PdfLoadedTextBoxField     tf => tf.Text,
                    PdfLoadedComboBoxField    cb => cb.SelectedValue,
                    PdfLoadedListBoxField     lb => lb.SelectedValue != null && lb.SelectedValue.Length > 0
                                                     ? string.Join(", ", lb.SelectedValue) : null,
                    _                            => null,
                };
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }

            return null;
        }

        // ----------------------------------------------------------------
        //  Export
        // ----------------------------------------------------------------

        /// <summary>
        /// Creates a new signed PDF:
        ///   - Updates checkbox values in the AcroForm
        ///   - Flattens all ink strokes (per page) as transparent PNG overlays
        ///   - Stamps parsed checkboxes and date/location fields
        ///   - Writes the result to <paramref name="outputPath"/>
        /// </summary>
        /// <param name="inputPath">Original PDF path</param>
        /// <param name="outputPath">Destination PDF path</param>
        /// <param name="pageStrokes">Key = 0-based page index; Value = StrokeCollection</param>
        /// <param name="pageImageSizes">Key = 0-based page index; rendered image size in logical pixels</param>
        /// <param name="checkboxStates">Key = field name; Value = checked state</param>
        public static void Export(
            string inputPath,
            string outputPath,
            Dictionary<int, StrokeCollection>          pageStrokes,
            Dictionary<int, Size>                      pageImageSizes,
            Dictionary<string, bool>                   checkboxStates,
            IReadOnlyList<ParsedCheckboxDescriptor>    parsedCheckboxes,
            IReadOnlyDictionary<string, bool>          parsedCheckboxStates,
            IReadOnlyList<DateFieldDescriptor>         dateFields,
            IReadOnlyList<LocationDateFieldDescriptor> locationDateFields)
        {
            AppLogger.Info($"PdfExporter.Export: '{Path.GetFileName(inputPath)}' → '{Path.GetFileName(outputPath)}'");
            AppLogger.Debug($"  AcroForm checkboxes: {checkboxStates.Count}  Parsed checkboxes: {parsedCheckboxes.Count}" +
                            $"  Date fields: {dateFields.Count}  LD fields: {locationDateFields.Count}" +
                            $"  Pages with strokes: {pageStrokes.Count}");

            using var pdfDoc = new PdfLoadedDocument(File.ReadAllBytes(inputPath));

            // --- 1. Update AcroForm checkbox fields ---
            if (pdfDoc.Form is PdfLoadedForm form)
            {
                foreach (PdfLoadedField field in form.Fields)
                {
                    if (field is not PdfLoadedCheckBoxField cbField) continue;
                    if (!checkboxStates.TryGetValue(field.Name, out bool isChecked)) continue;

                    try
                    {
                        cbField.Checked = isChecked;
                        AppLogger.Debug($"  Checkbox '{field.Name}' set to {(isChecked ? "checked" : "unchecked")}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"  Checkbox '{field.Name}' value set failed: {ex.Message}");
                    }
                }
            }
            else if (checkboxStates.Count > 0)
            {
                AppLogger.Warn("  No AcroForm found – checkbox states not written");
            }

            // --- 2. Stamp ink strokes onto pages ---
            // Each stroke becomes its own small PNG covering only the stroke's bounding box.
            const float exportDpi = 300f;
            int totalStrokesWritten = 0;

            foreach (var kv in pageStrokes)
            {
                if (kv.Value.Count == 0) continue;
                int pageIdx = kv.Key;
                if (pageIdx < 0 || pageIdx >= pdfDoc.Pages.Count)
                {
                    AppLogger.Warn($"  Page index {pageIdx} out of range – strokes skipped");
                    continue;
                }

                AppLogger.Debug($"  Page {pageIdx}: stamping {kv.Value.Count} stroke(s)");

                var pdfPage    = pdfDoc.Pages[pageIdx] as PdfLoadedPage;
                if (pdfPage == null) continue;

                var pageSize   = pdfPage.Size;
                float pageW    = pageSize.Width;
                float pageH    = pageSize.Height;

                if (!pageImageSizes.TryGetValue(pageIdx, out Size imgSize) || imgSize.IsEmpty)
                {
                    AppLogger.Warn($"  Page {pageIdx}: no image size available – strokes skipped");
                    continue;
                }

                // Scale factors: logical PageContainer pixels → PDF points
                double scaleXtoPdf = pageW / imgSize.Width;
                double scaleYtoPdf = pageH / imgSize.Height;

                var graphics = pdfPage.Graphics;

                foreach (var stroke in kv.Value)
                {
                    // Bounding box in logical pixels (includes tip radius already)
                    var bounds = stroke.GetBounds();
                    const double pad = 6; // extra pixels so antialiased edges aren't clipped
                    double bx = Math.Max(0, bounds.X - pad);
                    double by = Math.Max(0, bounds.Y - pad);
                    double bw = Math.Min(imgSize.Width  - bx, bounds.Width  + pad * 2);
                    double bh = Math.Min(imgSize.Height - by, bounds.Height + pad * 2);
                    if (bw <= 0 || bh <= 0) continue;

                    // Corresponding rectangle in PDF points
                    float pdfLeft   = (float)(bx  * scaleXtoPdf);
                    float pdfBottom = (float)((imgSize.Height - by - bh) * scaleYtoPdf);
                    float pdfWidth  = (float)(bw * scaleXtoPdf);
                    float pdfHeight = (float)(bh * scaleYtoPdf);

                    // Syncfusion uses top-left origin; convert from PDF space (bottom-left)
                    float sfY = pageH - pdfBottom - pdfHeight;

                    // Pixel size of the stroke image at exportDpi
                    int targetW = Math.Max(1, (int)(pdfWidth  / 72f * exportDpi));
                    int targetH = Math.Max(1, (int)(pdfHeight / 72f * exportDpi));

                    byte[] pngBytes = RenderStrokeToPng(stroke, bx, by, bw, bh, targetW, targetH);
                    if (pngBytes.Length == 0) continue;

                    using var imgStream = new MemoryStream(pngBytes);
                    var bitmap = new PdfBitmap(imgStream);
                    graphics.DrawImage(bitmap, pdfLeft, sfY, pdfWidth, pdfHeight);

                    totalStrokesWritten++;
                }
            }

            AppLogger.Info($"PdfExporter.Export: {totalStrokesWritten} stroke(s) stamped");

            // --- 3. Stamp parsed checkboxes ---
            int cbStamped = 0;

            foreach (var pcb in parsedCheckboxes)
            {
                bool isChecked = parsedCheckboxStates.TryGetValue(pcb.Identifier, out bool st)
                    ? st : pcb.IsChecked;
                if (!isChecked) continue;
                if (pcb.PageIndex < 0 || pcb.PageIndex >= pdfDoc.Pages.Count) continue;

                var pdfPage  = pdfDoc.Pages[pcb.PageIndex] as PdfLoadedPage;
                if (pdfPage == null) continue;

                float pageH  = pdfPage.Size.Height;
                float x      = (float)pcb.FieldPdfX;
                float pdfY   = (float)pcb.FieldPdfY;
                float w      = (float)Math.Max(pcb.Width,  1);
                cbStamped++;
                float h      = (float)Math.Max(pcb.Height, 1);
                float sfTop  = pageH - pdfY - h; // convert PDF-space bottom → Syncfusion top
                float pad    = Math.Max(1f, Math.Min(w, h) * 0.12f);
                float lw     = Math.Max(0.8f, Math.Min(w, h) * 0.09f);

                var graphics = pdfPage.Graphics;
                var pen      = new PdfPen(PdfBrushes.Black, lw);

                // Local helpers: convert a PDF-space Y coordinate to Syncfusion Y
                float Sf(float pdfPointY) => pageH - pdfPointY;

                switch (pcb.Style.ToUpperInvariant())
                {
                    case "H": // check mark (√ checkmark)
                    {
                        float midX  = x + w * 0.35f;
                        float midPY = pdfY + pad;         // mid-point Y in PDF space
                        graphics.DrawLine(pen,
                            x + pad,     Sf(pdfY + h * 0.5f),
                            midX,        Sf(midPY));
                        graphics.DrawLine(pen,
                            midX,        Sf(midPY),
                            x + w - pad, Sf(pdfY + h - pad));
                        break;
                    }
                    case "C": // cross (×)
                    {
                        graphics.DrawLine(pen, x + pad, Sf(pdfY + pad), x + w - pad, Sf(pdfY + h - pad));
                        graphics.DrawLine(pen, x + pad, Sf(pdfY + h - pad), x + w - pad, Sf(pdfY + pad));
                        break;
                    }
                    case "D": // dot (●)
                    {
                        float r = Math.Min(w, h) * 0.3f;
                        float cx = x + w * 0.5f;
                        float cy = pdfY + h * 0.5f;
                        graphics.DrawEllipse(pen,
                            cx - r, Sf(cy + r), r * 2f, r * 2f);
                        break;
                    }
                    default: // filled rectangle
                    {
                        graphics.DrawRectangle(new PdfSolidBrush(new PdfColor(0, 0, 0)),
                            x + pad, Sf(pdfY + h - pad), w - 2f * pad, h - 2f * pad);
                        break;
                    }
                }
            }

            AppLogger.Info($"PdfExporter.Export: {cbStamped} parsed checkbox(es) stamped");
            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            pdfDoc.Save(outFs);
        }

        // ----------------------------------------------------------------
        //  Stroke rasterisation helper
        // ----------------------------------------------------------------

        /// <summary>
        /// Renders a single WPF <see cref="Stroke"/> into a PNG byte array at the requested
        /// pixel size.  The source region is <c>(bx, by, bw, bh)</c> in logical page-image
        /// pixels; it is scaled to <paramref name="targetW"/> × <paramref name="targetH"/> pixels.
        /// </summary>
        private static byte[] RenderStrokeToPng(
            Stroke stroke,
            double bx, double by, double bw, double bh,
            int targetW, int targetH)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double scaleX = targetW / Math.Max(1, bw);
                double scaleY = targetH / Math.Max(1, bh);
                dc.PushTransform(new TranslateTransform(-bx, -by));
                dc.PushTransform(new ScaleTransform(scaleX, scaleY));
                stroke.Draw(dc);
                dc.Pop();
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
    }
}
