using System;
using System.Globalization;
using System.Windows;

namespace miPDFsign.Models
{
    /// <summary>
    /// Parsed representation of a signature-field marker embedded in a PDF as plain text.
    /// Format: {S, corrX, corrY, width, height, R|O, N|R, extra, label, fieldName}
    /// All dimensions are in PDF user-space units (points, 1 pt = 1/72 inch).
    /// CorrX / CorrY can be negative.  CorrY is positive-upward (PDF convention).
    /// </summary>
    public class SignatureFieldDescriptor
    {
        // ── Parsed fields ─────────────────────────────────────────────
        public string FieldName  { get; set; } = "";
        public string Label      { get; set; } = "";
        public double CorrX      { get; set; }   // offset in PDF pts (+ = right)
        public double CorrY      { get; set; }   // offset in PDF pts (+ = up)
        public double Width      { get; set; }   // field width  in PDF pts
        public double Height     { get; set; }   // field height in PDF pts
        public bool   Required   { get; set; }   // R = required, O = optional
        public bool   Rotated    { get; set; }   // N = normal, R = rotated
        public int    PageIndex  { get; set; }   // 0-based

        // ── Position of the marker glyph in PDF user space ────────────
        // Origin = bottom-left; Y increases upward.
        public double PdfMarkerX { get; set; }
        public double PdfMarkerY { get; set; }

        // ── Computed field origin (bottom-left) in PDF space ──────────
        public double FieldPdfX => PdfMarkerX + CorrX;
        public double FieldPdfY => PdfMarkerY + CorrY;   // CorrY is already PDF-direction

        // ── Screen rect (rendered image pixels), filled by ShowPage ───
        public Rect   RenderRect { get; set; }

        // ── Runtime state ─────────────────────────────────────────────
        /// <summary>True after the user has drawn at least one stroke in this field.</summary>
        public bool   IsSigned   { get; set; }

        // ── Parser ────────────────────────────────────────────────────

        /// <summary>
        /// Parses a marker such as {S,0,40,100,30,R,N,1,Person signature,SIG1}.
        /// Returns null if the text is not a valid S-type signature marker.
        /// </summary>
        public static SignatureFieldDescriptor? TryParse(
            string markerText, double markerX, double markerY, int pageIndex)
        {
            string inner = markerText.Trim();
            if (inner.StartsWith("{", StringComparison.Ordinal)) inner = inner[1..];
            if (inner.EndsWith("}",   StringComparison.Ordinal)) inner = inner[..^1];

            var parts = inner.Split(',');
            if (parts.Length < 10) return null;
            if (!parts[0].Trim().Equals("S", StringComparison.OrdinalIgnoreCase)) return null;

            static bool TryParseDbl(string s, out double v) =>
                double.TryParse(s.Trim(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out v);

            if (!TryParseDbl(parts[1], out double corrX)) return null;
            if (!TryParseDbl(parts[2], out double corrY)) return null;
            if (!TryParseDbl(parts[3], out double width))  return null;
            if (!TryParseDbl(parts[4], out double height)) return null;

            bool required = parts[5].Trim().Equals("R", StringComparison.OrdinalIgnoreCase);
            bool rotated  = parts[6].Trim().Equals("R", StringComparison.OrdinalIgnoreCase);
            // parts[7] = extra/ignored parameter

            // Label may itself contain commas → everything from [8] to [len-2], joined
            string fieldName = parts[parts.Length - 1].Trim();
            string label     = string.Join(",", parts[8..(parts.Length - 1)]).Trim();

            return new SignatureFieldDescriptor
            {
                FieldName  = fieldName,
                Label      = label,
                CorrX      = corrX,
                CorrY      = corrY,
                Width      = width,
                Height     = height,
                Required   = required,
                Rotated    = rotated,
                PageIndex  = pageIndex,
                PdfMarkerX = markerX,
                PdfMarkerY = markerY,
            };
        }
    }
}
