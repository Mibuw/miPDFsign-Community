using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiPdf;
using MiPdf.Forms;
using MiPdf.Graphics;
using MiPdf.Images;
using miPDFsign.Models;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// miPDFlib-based helper for:
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
            using var pdfDoc = PdfDocument.Open(File.ReadAllBytes(pdfPath));
            return GetCheckboxes(pdfDoc);
        }

        /// <summary>
        /// Overload for when a <see cref="PdfDocument"/> is already open (avoids a second open).
        /// </summary>
        public static List<CheckboxInfo> GetCheckboxes(PdfDocument pdfDoc)
        {
            var result = new List<CheckboxInfo>();

            if (pdfDoc.GetForm() is not PdfAcroForm form) return result;

            foreach (var field in form.Fields)
            {
                if (field is not PdfCheckBox cbField) continue;

                // A checkbox field may carry multiple widgets — each one is reported independently.
                // A widget is checked when its appearance state is one of its "on" states.
                foreach (var widget in cbField.Widgets)
                {
                    bool isChecked = widget.AppearanceState is { } state && state != "Off";
                    AddCheckboxInfo(result, pdfDoc, field.FullName, widget.Page, widget.Rect, isChecked);
                }
            }

            return result;
        }

        private static void AddCheckboxInfo(
            List<CheckboxInfo> result,
            PdfDocument        pdfDoc,
            string             fieldName,
            PdfPage?           page,
            PdfRectangle       rect,   // PDF user space (bottom-left origin)
            bool               isChecked)
        {
            int pageIdx = -1;
            double originX = 0, originY = 0;

            if (page != null)
            {
                for (int i = 0; i < pdfDoc.PageCount; i++)
                {
                    if (ReferenceEquals(pdfDoc.Pages[i], page))
                    {
                        pageIdx = i;
                        // The app's PDF space has its origin at the visible page's bottom-left corner.
                        originX = page.CropBox.Left;
                        originY = page.CropBox.Bottom;
                        break;
                    }
                }
            }

            result.Add(new CheckboxInfo
            {
                FieldName = fieldName,
                PageIndex = pageIdx,
                PdfLeft   = (float)(rect.Left   - originX),
                PdfBottom = (float)(rect.Bottom - originY),
                PdfWidth  = (float)rect.Width,
                PdfHeight = (float)rect.Height,
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
            using var pdfDoc = PdfDocument.Open(File.ReadAllBytes(pdfPath));
            return GetTextFieldValue(pdfDoc, fieldName);
        }

        /// <summary>
        /// Overload for when a <see cref="PdfDocument"/> is already open.
        /// </summary>
        public static string? GetTextFieldValue(PdfDocument pdfDoc, string fieldName)
        {
            if (pdfDoc.GetForm() is not PdfAcroForm form) return null;

            foreach (var field in form.Fields)
            {
                if (!string.Equals(field.FullName, fieldName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? val = field switch
                {
                    PdfTextField   tf => tf.Value,
                    PdfChoiceField cf => cf.SelectedValues.Count > 0
                                         ? string.Join(", ", cf.SelectedValues) : null,
                    _                 => null,
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

            using var pdfDoc = PdfDocument.Open(File.ReadAllBytes(inputPath));

            // --- 1. Update AcroForm checkbox fields ---
            if (pdfDoc.GetForm() is PdfAcroForm form)
            {
                foreach (var field in form.Fields)
                {
                    if (field is not PdfCheckBox cbField) continue;
                    if (!checkboxStates.TryGetValue(field.FullName, out bool isChecked)) continue;

                    try
                    {
                        cbField.IsChecked = isChecked;
                        AppLogger.Debug($"  Checkbox '{field.FullName}' set to {(isChecked ? "checked" : "unchecked")}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"  Checkbox '{field.FullName}' value set failed: {ex.Message}");
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
                if (pageIdx < 0 || pageIdx >= pdfDoc.PageCount)
                {
                    AppLogger.Warn($"  Page index {pageIdx} out of range – strokes skipped");
                    continue;
                }

                AppLogger.Debug($"  Page {pageIdx}: stamping {kv.Value.Count} stroke(s)");

                var pdfPage    = pdfDoc.Pages[pageIdx];
                var crop       = pdfPage.CropBox;
                double pageW   = crop.Width;
                double pageH   = crop.Height;

                if (!pageImageSizes.TryGetValue(pageIdx, out Size imgSize) || imgSize.IsEmpty)
                {
                    AppLogger.Warn($"  Page {pageIdx}: no image size available – strokes skipped");
                    continue;
                }

                // Scale factors: logical PageContainer pixels → PDF points
                double scaleXtoPdf = pageW / imgSize.Width;
                double scaleYtoPdf = pageH / imgSize.Height;

                using var canvas = pdfPage.GetCanvas();

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

                    // Corresponding rectangle in PDF points (relative to the visible page)
                    double pdfLeft   = bx * scaleXtoPdf;
                    double pdfBottom = (imgSize.Height - by - bh) * scaleYtoPdf;
                    double pdfWidth  = bw * scaleXtoPdf;
                    double pdfHeight = bh * scaleYtoPdf;

                    // Pixel size of the stroke image at exportDpi
                    int targetW = Math.Max(1, (int)(pdfWidth  / 72f * exportDpi));
                    int targetH = Math.Max(1, (int)(pdfHeight / 72f * exportDpi));

                    byte[] pngBytes = RenderStrokeToPng(stroke, bx, by, bw, bh, targetW, targetH);
                    if (pngBytes.Length == 0) continue;

                    // PNG alpha becomes a soft mask → the stroke stays transparent around the ink.
                    canvas.DrawImage(PdfImage.FromBytes(pngBytes),
                        crop.Left + pdfLeft, crop.Bottom + pdfBottom, pdfWidth, pdfHeight);

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
                if (pcb.PageIndex < 0 || pcb.PageIndex >= pdfDoc.PageCount) continue;

                var pdfPage  = pdfDoc.Pages[pcb.PageIndex];
                var crop     = pdfPage.CropBox;
                double x     = crop.Left   + pcb.FieldPdfX;
                double pdfY  = crop.Bottom + pcb.FieldPdfY;
                double w     = Math.Max(pcb.Width,  1);
                cbStamped++;
                double h     = Math.Max(pcb.Height, 1);
                double pad   = Math.Max(1.0, Math.Min(w, h) * 0.12);
                double lw    = Math.Max(0.8, Math.Min(w, h) * 0.09);

                using var canvas = pdfPage.GetCanvas();
                canvas.SaveState();
                canvas.SetStrokeColor(PdfColor.Black).SetFillColor(PdfColor.Black).SetLineWidth(lw);

                switch (pcb.Style.ToUpperInvariant())
                {
                    case "H": // check mark (√ checkmark)
                    {
                        double midX  = x + w * 0.35;
                        double midPY = pdfY + pad;
                        canvas.MoveTo(x + pad, pdfY + h * 0.5)
                              .LineTo(midX, midPY)
                              .LineTo(x + w - pad, pdfY + h - pad)
                              .Stroke();
                        break;
                    }
                    case "C": // cross (×)
                    {
                        canvas.MoveTo(x + pad, pdfY + pad).LineTo(x + w - pad, pdfY + h - pad).Stroke();
                        canvas.MoveTo(x + pad, pdfY + h - pad).LineTo(x + w - pad, pdfY + pad).Stroke();
                        break;
                    }
                    case "D": // dot (●)
                    {
                        double r = Math.Min(w, h) * 0.3;
                        canvas.Circle(x + w * 0.5, pdfY + h * 0.5, r).Stroke();
                        break;
                    }
                    default: // filled rectangle
                    {
                        canvas.Rectangle(x + pad, pdfY + pad, w - 2 * pad, h - 2 * pad).Fill();
                        break;
                    }
                }
                canvas.RestoreState();
            }

            AppLogger.Info($"PdfExporter.Export: {cbStamped} parsed checkbox(es) stamped");
            pdfDoc.Save(outputPath);
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
