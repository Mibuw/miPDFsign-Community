namespace miPDFsign.Models
{
    /// <summary>
    /// Represents a parsed Date field marker: {D,corrX,corrY,fontType,fontSize,"sigRef"}
    /// When the PDF loads the field is automatically filled with the current date + time.
    /// </summary>
    public class DateFieldDescriptor
    {
        // ── Position (PDF user space, bottom-left origin, in points) ────
        public double PdfMarkerX { get; set; }
        public double PdfMarkerY { get; set; }
        public double CorrX      { get; set; }
        public double CorrY      { get; set; }
        public int    PageIndex  { get; set; }

        // ── Field-type attributes ────────────────────────────────────────
        public string FontType   { get; set; } = "A";
        public double FontSize   { get; set; } = 10;

        /// <summary>Signature-reference identifier this date field is linked to.</summary>
        public string SigRef     { get; set; } = "";

        /// <summary>Returns the current date formatted as dd.MM.yyyy.</summary>
        public string FormatNow() => System.DateTime.Now.ToString("dd.MM.yyyy");

        // ── Computed PDF coordinates ─────────────────────────────────────
        /// <summary>Baseline X in PDF points.</summary>
        public double FieldPdfX => PdfMarkerX + CorrX;
        /// <summary>Baseline Y in PDF points (bottom-up).</summary>
        public double FieldPdfY => PdfMarkerY + CorrY;

        // ── WPF render rect (screen pixels, set by ShowPage) ─────────────
        public System.Windows.Rect RenderRect { get; set; }

        // ----------------------------------------------------------------
        //  Parser
        // ----------------------------------------------------------------

        /// <summary>
        /// Tries to parse a marker of the form {D,corrX,corrY,fontType,fontSize,"sigRef"}.
        /// Returns null on malformed input.
        /// </summary>
        public static DateFieldDescriptor? TryParse(
            string markerText, double markerX, double markerY, int pageIndex)
        {
            string inner = markerText.TrimStart('{').TrimEnd('}');
            var parts = LocationFieldDescriptor.SplitTokens(inner);

            // Minimum: D, corrX, corrY, fontType, fontSize, sigRef  (6 tokens)
            if (parts.Count < 6) return null;
            if (parts[0] != "D") return null;

            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double corrX)) return null;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double corrY)) return null;

            string fontType = parts[3];

            if (!double.TryParse(parts[4], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double fontSize)) return null;

            string sigRef = parts[5];

            return new DateFieldDescriptor
            {
                PdfMarkerX = markerX,
                PdfMarkerY = markerY,
                CorrX      = corrX,
                CorrY      = corrY,
                PageIndex  = pageIndex,
                FontType   = fontType,
                FontSize   = fontSize,
                SigRef     = sigRef,
            };
        }
    }
}
