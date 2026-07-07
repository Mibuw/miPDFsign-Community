using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using miPDFsign.Models;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// iText-based helper for:
    ///   1. Detecting checkbox form fields in a PDF
    ///   2. Reading text/combo/list form field values
    ///   3. Exporting the signed/annotated PDF back to disk
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
            using var reader = new PdfReader(pdfPath);
            using var pdfDoc = new PdfDocument(reader);
            return GetCheckboxes(pdfDoc);
        }

        /// <summary>
        /// Overload for when a <see cref="PdfDocument"/> is already open (avoids a second open).
        /// </summary>
        public static List<CheckboxInfo> GetCheckboxes(PdfDocument pdfDoc)
        {
            var result = new List<CheckboxInfo>();

            var form = PdfAcroForm.GetAcroForm(pdfDoc, false);
            if (form == null) return result;

            foreach (var kvp in form.GetAllFormFields())
            {
                var field = kvp.Value;
                if (field is not PdfButtonFormField btn) continue;
                if (!IsCheckBox(btn)) continue;

                // A checkbox field may carry multiple widgets or just one.
                var widgets = field.GetWidgets();
                if (widgets == null) continue;

                string val  = field.GetValueAsString() ?? string.Empty;
                bool isOn   = val.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                           || val.Equals("On",  StringComparison.OrdinalIgnoreCase)
                           || val.Equals("1",   StringComparison.OrdinalIgnoreCase);

                foreach (var widget in widgets)
                {
                    var rectArr = widget.GetRectangle();
                    if (rectArr == null) continue;

                    float x0 = rectArr.GetAsNumber(0).FloatValue();
                    float y0 = rectArr.GetAsNumber(1).FloatValue();
                    float x1 = rectArr.GetAsNumber(2).FloatValue();
                    float y1 = rectArr.GetAsNumber(3).FloatValue();

                    var page    = widget.GetPage();
                    int pageNum = page != null ? pdfDoc.GetPageNumber(page) : 0; // 1-based

                    // iText widget rectangles are already in PDF space (bottom-left origin).
                    result.Add(new CheckboxInfo
                    {
                        FieldName = kvp.Key,
                        PageIndex = pageNum - 1,
                        PdfLeft   = Math.Min(x0, x1),
                        PdfBottom = Math.Min(y0, y1),
                        PdfWidth  = Math.Abs(x1 - x0),
                        PdfHeight = Math.Abs(y1 - y0),
                        IsChecked = isOn,
                    });
                }
            }

            return result;
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
            using var reader = new PdfReader(pdfPath);
            using var pdfDoc = new PdfDocument(reader);
            return GetTextFieldValue(pdfDoc, fieldName);
        }

        /// <summary>
        /// Overload for when a <see cref="PdfDocument"/> is already open.
        /// </summary>
        public static string? GetTextFieldValue(PdfDocument pdfDoc, string fieldName)
        {
            var form = PdfAcroForm.GetAcroForm(pdfDoc, false);
            if (form == null) return null;

            var field = form.GetField(fieldName);
            if (field == null) return null;

            var val = field.GetValueAsString();
            return string.IsNullOrWhiteSpace(val) ? null : val;
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
            AppLogger.Info($"PdfExporter.Export: '{System.IO.Path.GetFileName(inputPath)}' → '{System.IO.Path.GetFileName(outputPath)}'");
            AppLogger.Debug($"  AcroForm checkboxes: {checkboxStates.Count}  Parsed checkboxes: {parsedCheckboxes.Count}" +
                            $"  Date fields: {dateFields.Count}  LD fields: {locationDateFields.Count}" +
                            $"  Pages with strokes: {pageStrokes.Count}");

            using var reader = new PdfReader(inputPath);
            using var writer = new PdfWriter(outputPath);
            using var pdfDoc = new PdfDocument(reader, writer);

            // --- 1. Update AcroForm checkbox fields ---
            var form = PdfAcroForm.GetAcroForm(pdfDoc, false);
            if (form != null)
            {
                foreach (var kv in checkboxStates)
                {
                    var field = form.GetField(kv.Key);
                    if (field == null)
                    {
                        AppLogger.Warn($"  Checkbox field '{kv.Key}' not found in AcroForm – skipped");
                        continue;
                    }

                    try
                    {
                        // Determine the on-value by scanning the widget's normal appearance
                        // (/AP /N) dictionary for the key that is not "Off" (e.g. "Yes",
                        // "On", "Checked", …).  GetAppearanceState() is unreliable when the
                        // box is currently unchecked because it returns "Off" and we cannot
                        // distinguish "Off" from a custom on-name.
                        string onValue = "Yes";
                        var widgets = field.GetWidgets();
                        if (widgets?.Count > 0)
                        {
                            var apDict   = widgets[0].GetAppearanceDictionary();
                            var normalAp = apDict?.GetAsDictionary(PdfName.N);
                            if (normalAp != null)
                            {
                                foreach (var key in normalAp.KeySet())
                                {
                                    string keyName = key.GetValue();
                                    if (!keyName.Equals("Off", StringComparison.OrdinalIgnoreCase))
                                    {
                                        onValue = keyName;
                                        break;
                                    }
                                }
                            }
                        }

                        field.SetValue(kv.Value ? onValue : "Off");
                        AppLogger.Debug($"  Checkbox '{kv.Key}' set to {(kv.Value ? "checked" : "unchecked")} (on-value='{onValue}')");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"  Checkbox '{kv.Key}' value set failed: {ex.Message}");
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
                if (pageIdx < 0 || pageIdx >= pdfDoc.GetNumberOfPages())
                {
                    AppLogger.Warn($"  Page index {pageIdx} out of range – strokes skipped");
                    continue;
                }

                AppLogger.Debug($"  Page {pageIdx}: stamping {kv.Value.Count} stroke(s)");

                var pdfPage  = pdfDoc.GetPage(pageIdx + 1);
                var mediaBox = pdfPage.GetMediaBox();
                float pageW  = mediaBox.GetWidth();
                float pageH  = mediaBox.GetHeight();

                if (!pageImageSizes.TryGetValue(pageIdx, out Size imgSize) || imgSize.IsEmpty)
                {
                    AppLogger.Warn($"  Page {pageIdx}: no image size available – strokes skipped");
                    continue;
                }

                // Scale factors: logical PageContainer pixels → PDF points
                double scaleXtoPdf = pageW / imgSize.Width;
                double scaleYtoPdf = pageH / imgSize.Height;

                var canvas = new PdfCanvas(pdfPage);

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

                    // Corresponding rectangle in PDF space (bottom-left origin, Y inverted)
                    float pdfLeft   = mediaBox.GetLeft()   + (float)(bx * scaleXtoPdf);
                    float pdfBottom = mediaBox.GetBottom() + (float)((imgSize.Height - by - bh) * scaleYtoPdf);
                    float pdfWidth  = (float)(bw * scaleXtoPdf);
                    float pdfHeight = (float)(bh * scaleYtoPdf);

                    // Pixel size of the stroke image at exportDpi
                    int targetW = Math.Max(1, (int)(pdfWidth  / 72f * exportDpi));
                    int targetH = Math.Max(1, (int)(pdfHeight / 72f * exportDpi));

                    byte[] pngBytes = RenderStrokeToPng(stroke, bx, by, bw, bh, targetW, targetH);
                    if (pngBytes.Length == 0) continue;

                    canvas.AddImageFittedIntoRectangle(
                        ImageDataFactory.Create(pngBytes),
                        new Rectangle(pdfLeft, pdfBottom, pdfWidth, pdfHeight),
                        false);

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
                if (pcb.PageIndex < 0 || pcb.PageIndex >= pdfDoc.GetNumberOfPages()) continue;

                var pdfPage = pdfDoc.GetPage(pcb.PageIndex + 1);
                var cb      = new PdfCanvas(pdfPage);

                // Coordinates are already PDF space (bottom-left origin, Y-up).
                float x   = (float)pcb.FieldPdfX;
                float y   = (float)pcb.FieldPdfY;
                float w   = (float)Math.Max(pcb.Width,  1);
                float h   = (float)Math.Max(pcb.Height, 1);
                float pad = Math.Max(1f, Math.Min(w, h) * 0.12f);
                float lw  = Math.Max(0.8f, Math.Min(w, h) * 0.09f);

                cb.SaveState().SetStrokeColor(ColorConstants.BLACK).SetLineWidth(lw);

                switch (pcb.Style.ToUpperInvariant())
                {
                    case "H": // check mark (√)
                    {
                        float midX = x + w * 0.35f;
                        float midY = y + pad;
                        cb.MoveTo(x + pad,     y + h * 0.5f)
                          .LineTo(midX,        midY)
                          .LineTo(x + w - pad, y + h - pad)
                          .Stroke();
                        break;
                    }
                    case "C": // cross (×)
                    {
                        cb.MoveTo(x + pad,     y + pad)
                          .LineTo(x + w - pad, y + h - pad)
                          .Stroke()
                          .MoveTo(x + pad,     y + h - pad)
                          .LineTo(x + w - pad, y + pad)
                          .Stroke();
                        break;
                    }
                    case "D": // dot (●) – stroked circle outline
                    {
                        float r  = Math.Min(w, h) * 0.3f;
                        float cx = x + w * 0.5f;
                        float cy = y + h * 0.5f;
                        cb.Circle(cx, cy, r).Stroke();
                        break;
                    }
                    default: // filled rectangle
                    {
                        cb.SetFillColor(ColorConstants.BLACK)
                          .Rectangle(x + pad, y + pad, w - 2f * pad, h - 2f * pad)
                          .Fill();
                        break;
                    }
                }

                cb.RestoreState();
                cbStamped++;
                AppLogger.Debug($"  Parsed checkbox '{pcb.Identifier}' stamped (style={pcb.Style}) at ({x:F1},{y:F1}) {w:F1}×{h:F1}pt");
            }

            AppLogger.Info($"PdfExporter.Export: {cbStamped} parsed checkbox(es) stamped");
        }

        // ----------------------------------------------------------------
        //  Private helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns true when the button field is a check box (i.e. neither a radio
        /// button nor a push button, per the /Ff flags).
        /// </summary>
        private static bool IsCheckBox(PdfButtonFormField btn)
        {
            var ffObj = btn.GetPdfObject().GetAsInt(PdfName.Ff);
            int ff    = ffObj ?? 0;
            const int FF_RADIO       = 1 << 15;
            const int FF_PUSH_BUTTON = 1 << 16;
            return (ff & FF_RADIO) == 0 && (ff & FF_PUSH_BUTTON) == 0;
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
