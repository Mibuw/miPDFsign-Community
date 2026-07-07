using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
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
            using var reader = new PdfReader(pdfPath);
            using var pdfDoc = new PdfDocument(reader);
            return Scan(pdfDoc);
        }

        /// <summary>Returns signature fields only (backwards-compatible overload).</summary>
        public static List<SignatureFieldDescriptor> Scan(PdfDocument pdfDoc)
            => ScanAllFields(pdfDoc).SignatureFields;

        /// <summary>
        /// Scans all supported field types in a single pass over the document.
        /// </summary>
        public static ParsedFieldSet ScanAllFields(PdfDocument pdfDoc)
        {
            int totalPages = pdfDoc.GetNumberOfPages();
            AppLogger.Info($"PdfSignatureScanner: Starting scan ({totalPages} page(s))");

            var sigs       = new List<SignatureFieldDescriptor>();
            var locs       = new List<LocationFieldDescriptor>();
            var dates      = new List<DateFieldDescriptor>();
            var locDates   = new List<LocationDateFieldDescriptor>();
            var checkboxes = new List<ParsedCheckboxDescriptor>();

            for (int p = 1; p <= totalPages; p++)
            {
                var page      = pdfDoc.GetPage(p);
                var listener  = new ChunkListener();
                var processor = new PdfCanvasProcessor(listener);
                processor.ProcessPageContent(page);

                AppLogger.Debug($"  Page {p}: {listener.Chunks.Count} text chunk(s) extracted");

                ScanPage(listener.Chunks, p - 1, sigs, locs, dates, locDates, checkboxes);
            }

            AppLogger.Info($"PdfSignatureScanner: Scan complete – " +
                $"S={sigs.Count}  C={checkboxes.Count}  D={dates.Count}  " +
                $"L={locs.Count}  LD={locDates.Count}");

            return new ParsedFieldSet(sigs, locs, dates, locDates, checkboxes);
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

        // ----------------------------------------------------------------
        //  iText text extraction – produces TextChunks in PDF space
        //  (bottom-left origin, matching the original iText ChunkListener)
        // ----------------------------------------------------------------

        private sealed class ChunkListener : IEventListener
        {
            private readonly List<TextChunk> _chunks = new();
            public IReadOnlyList<TextChunk> Chunks => _chunks;

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT) return;
                var info = (TextRenderInfo)data;
                string text = info.GetText();
                if (string.IsNullOrEmpty(text)) return;

                var pt = info.GetBaseline().GetStartPoint();
                _chunks.Add(new TextChunk(text, pt.Get(0), pt.Get(1)));
            }

            public ICollection<EventType> GetSupportedEvents() =>
                new HashSet<EventType> { EventType.RENDER_TEXT };
        }

        // ── Text chunk ───────────────────────────────────────────────────

        private sealed record TextChunk(string Text, double X, double Y);
    }
}
