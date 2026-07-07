using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Parsing;
using miPDFsign.Models;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Scans a PDF for embedded field markers (signPOS Parsing Identifiers v3.0).
    ///
    /// Supported types:
    ///   S  – Signature field
    ///   L  – Location text field
    ///   D  – Date/time field (auto-filled)
    ///   LD – Combined location + date field
    ///   C  – Checkbox field
    /// </summary>
    public static class PdfSignatureScanner
    {
        // LD must come before L so the alternation matches the longer prefix first.
        private static readonly Regex AnyMarkerRegex =
            new Regex(@"\{(?:LD|L|D|S|C),[^}]+\}", RegexOptions.Compiled);

        // ── Result record ─────────────────────────────────────────────────

        /// <summary>All parsed field types found in a single document scan.</summary>
        public record ParsedFieldSet(
            List<SignatureFieldDescriptor>    SignatureFields,
            List<LocationFieldDescriptor>     LocationFields,
            List<DateFieldDescriptor>         DateFields,
            List<LocationDateFieldDescriptor> LocationDateFields,
            List<ParsedCheckboxDescriptor>    CheckboxFields
        );

        // ----------------------------------------------------------------
        //  Public API – backwards-compatible + new combined scan
        // ----------------------------------------------------------------

        /// <summary>Returns signature fields only (backwards-compatible overload).</summary>
        public static List<SignatureFieldDescriptor> Scan(string pdfPath)
        {
            using var pdfDoc = new PdfLoadedDocument(File.ReadAllBytes(pdfPath));
            return Scan(pdfDoc);
        }

        /// <summary>Returns signature fields only (backwards-compatible overload).</summary>
        public static List<SignatureFieldDescriptor> Scan(PdfLoadedDocument pdfDoc)
            => ScanAllFields(pdfDoc).SignatureFields;

        /// <summary>
        /// Scans all supported field types in a single pass over the document.
        /// </summary>
        public static ParsedFieldSet ScanAllFields(PdfLoadedDocument pdfDoc)
        {
            int totalPages = pdfDoc.Pages.Count;
            AppLogger.Info($"PdfSignatureScanner: Starting scan ({totalPages} page(s))");

            var sigs       = new List<SignatureFieldDescriptor>();
            var locs       = new List<LocationFieldDescriptor>();
            var dates      = new List<DateFieldDescriptor>();
            var locDates   = new List<LocationDateFieldDescriptor>();
            var checkboxes = new List<ParsedCheckboxDescriptor>();

            for (int p = 0; p < totalPages; p++)
            {
                var page = pdfDoc.Pages[p] as PdfLoadedPage;
                if (page == null) continue;

                var chunks = ExtractTextChunks(page);
                AppLogger.Debug($"  Page {p + 1}: {chunks.Count} text chunk(s) extracted");

                ScanPage(chunks, p, sigs, locs, dates, locDates, checkboxes);
            }

            AppLogger.Info($"PdfSignatureScanner: Scan complete – " +
                $"S={sigs.Count}  C={checkboxes.Count}  D={dates.Count}  " +
                $"L={locs.Count}  LD={locDates.Count}");

            return new ParsedFieldSet(sigs, locs, dates, locDates, checkboxes);
        }

        // ----------------------------------------------------------------
        //  Syncfusion text extraction – produces TextChunks in PDF space
        //  (bottom-left origin, matching the original iText ChunkListener)
        // ----------------------------------------------------------------

        private static List<TextChunk> ExtractTextChunks(PdfLoadedPage page)
        {
            var result = new List<TextChunk>();
            double pageHeight = page.Size.Height;

            try
            {
                // .NET Core API: ExtractText(out TextLineCollection) returns word-level bounds.
                // Bounds are in page-space (top-left origin); we convert to PDF space (bottom-left).
                var lineCollection = new TextLineCollection();
                page.ExtractText(out lineCollection);
                if (lineCollection?.TextLine == null) return result;

                foreach (var line in lineCollection.TextLine)
                {
                    if (line.WordCollection == null) continue;
                    foreach (var word in line.WordCollection)
                    {
                        if (string.IsNullOrEmpty(word.Text)) continue;

                        var b = word.Bounds;   // RectangleF (Syncfusion.Drawing or System.Drawing)
                        double pdfX = b.X;
                        double pdfY = pageHeight - b.Y - b.Height; // top-left → bottom-left

                        result.Add(new TextChunk(word.Text, pdfX, pdfY));
                    }
                }
            }
            catch (Exception ex)
            {
                // Fallback: plain-text extraction (no position data).
                AppLogger.Warn($"  ExtractTextChunks: structured extraction failed ({ex.Message}), " +
                               "falling back to plain text – field positions will be inaccurate");
                try
                {
                    string plain = page.ExtractText();
                    if (!string.IsNullOrEmpty(plain))
                        result.Add(new TextChunk(plain, 0, 0));
                }
                catch { /* ignore */ }
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  Per-page scan (unchanged from iText version)
        // ----------------------------------------------------------------

        private static void ScanPage(
            IReadOnlyList<TextChunk> chunks,
            int pageIndex,
            List<SignatureFieldDescriptor>    sigs,
            List<LocationFieldDescriptor>     locs,
            List<DateFieldDescriptor>         dates,
            List<LocationDateFieldDescriptor> locDates,
            List<ParsedCheckboxDescriptor>    checkboxes)
        {
            if (chunks.Count == 0)
            {
                AppLogger.Debug($"  ScanPage({pageIndex}): no text chunks – skipped");
                return;
            }

            var sorted = chunks
                .OrderByDescending(c => c.Y)
                .ThenBy(c => c.X)
                .ToList();

            var sb     = new StringBuilder();
            var posMap = new List<(int startIdx, double x, double y)>(sorted.Count);

            foreach (var chunk in sorted)
            {
                posMap.Add((sb.Length, chunk.X, chunk.Y));
                sb.Append(chunk.Text);
            }

            string assembled = sb.ToString();
            var matches = AnyMarkerRegex.Matches(assembled);

            AppLogger.Debug($"  ScanPage({pageIndex}): {matches.Count} marker(s) found");

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var (mx, my) = ResolvePosition(posMap, m.Index);
                string token = m.Value;

                int comma   = token.IndexOf(',');
                string type = comma > 0 ? token.Substring(1, comma - 1) : "";

                AppLogger.Debug($"    Marker [{type}] at ({mx:F1},{my:F1}): {token}");

                switch (type)
                {
                    case "S":
                    {
                        var d = SignatureFieldDescriptor.TryParse(token, mx, my, pageIndex);
                        if (d != null) { sigs.Add(d); AppLogger.Debug($"      → S parsed: {d.FieldName}"); }
                        else AppLogger.Warn($"      → S parse failed: {token}");
                        break;
                    }
                    case "LD":
                    {
                        var d = LocationDateFieldDescriptor.TryParse(token, mx, my, pageIndex);
                        if (d != null) { locDates.Add(d); AppLogger.Debug($"      → LD parsed: ref={d.SigRef}"); }
                        else AppLogger.Warn($"      → LD parse failed: {token}");
                        break;
                    }
                    case "L":
                    {
                        var d = LocationFieldDescriptor.TryParse(token, mx, my, pageIndex);
                        if (d != null) { locs.Add(d); AppLogger.Debug($"      → L parsed: ref={d.SigRef}"); }
                        else AppLogger.Warn($"      → L parse failed: {token}");
                        break;
                    }
                    case "D":
                    {
                        var d = DateFieldDescriptor.TryParse(token, mx, my, pageIndex);
                        if (d != null) { dates.Add(d); AppLogger.Debug($"      → D parsed: ref={d.SigRef}"); }
                        else AppLogger.Warn($"      → D parse failed: {token}");
                        break;
                    }
                    case "C":
                    {
                        var d = ParsedCheckboxDescriptor.TryParse(token, mx, my, pageIndex);
                        if (d != null) { checkboxes.Add(d); AppLogger.Debug($"      → C parsed: id={d.Identifier}  w={d.Width}x{d.Height}"); }
                        break;
                    }
                    // Skip unknown tokens
                    default:
                        AppLogger.Debug($"      → unknown token type: {token.Split(':')[0]}");
                        break;
                }
            }

        }

        // ── Position resolution ──────────────────────────────────────────

        private static (double mx, double my) ResolvePosition(
            IReadOnlyList<(int startIdx, double x, double y)> posMap, int charIndex)
        {
            double rx = 0, ry = 0;
            foreach (var entry in posMap)
            {
                if (entry.startIdx > charIndex) break;
                rx = entry.x;
                ry = entry.y;
            }
            return (rx, ry);
        }

        // ── Text chunk ───────────────────────────────────────────────────

        private sealed record TextChunk(string Text, double X, double Y);
    }
}
