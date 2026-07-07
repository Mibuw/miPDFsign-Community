namespace miPDFsign.Models
{
    /// <summary>
    /// Represents a parsed combined Location+Date field marker:
    /// {LD,corrX,corrY,fontType,fontSize,"separator","dialogText","sigRef"}
    /// Renders both a location entry and the current date/time, joined by the separator.
    /// </summary>
    public class LocationDateFieldDescriptor
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

        /// <summary>String placed between location and date (e.g. ", " or " / ").</summary>
        public string Separator  { get; set; } = ", ";

        /// <summary>Text shown in the location-input dialog.</summary>
        public string DialogText { get; set; } = "";

        /// <summary>Signature-reference identifier.</summary>
        public string SigRef     { get; set; } = "";

        // ── Computed PDF coordinates ─────────────────────────────────────
        public double FieldPdfX => PdfMarkerX + CorrX;
        public double FieldPdfY => PdfMarkerY + CorrY;

        // ── Runtime value ────────────────────────────────────────────────
        /// <summary>Location text entered by the user (empty until filled).</summary>
        public string LocationValue { get; set; } = "";

        // ── WPF render rect (screen pixels, set by ShowPage) ─────────────
        public System.Windows.Rect RenderRect { get; set; }

        // ----------------------------------------------------------------
        //  Parser
        // ----------------------------------------------------------------

        /// <summary>
        /// Tries to parse {LD,corrX,corrY,fontType,fontSize,"separator","dialogText","sigRef"}.
        /// Returns null on malformed input.
        /// </summary>
        public static LocationDateFieldDescriptor? TryParse(
            string markerText, double markerX, double markerY, int pageIndex)
        {
            string inner = markerText.TrimStart('{').TrimEnd('}');
            var parts = LocationFieldDescriptor.SplitTokens(inner);

            // Minimum: LD, corrX, corrY, fontType, fontSize, sigRef  (6)
            // Full:    LD, corrX, corrY, fontType, fontSize, sep, dialogText, sigRef  (8)
            if (parts.Count < 6) return null;
            if (parts[0] != "LD") return null;

            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double corrX)) return null;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double corrY)) return null;

            string fontType = parts[3];

            if (!double.TryParse(parts[4], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double fontSize)) return null;

            string separator  = parts.Count >= 8 ? parts[5] : ", ";
            string dialogText = parts.Count >= 8 ? parts[6] : (parts.Count >= 7 ? parts[5] : "");
            string sigRef     = parts[^1];

            return new LocationDateFieldDescriptor
            {
                PdfMarkerX = markerX,
                PdfMarkerY = markerY,
                CorrX      = corrX,
                CorrY      = corrY,
                PageIndex  = pageIndex,
                FontType   = fontType,
                FontSize   = fontSize,
                Separator  = separator,
                DialogText = dialogText,
                SigRef     = sigRef,
            };
        }
    }
}
