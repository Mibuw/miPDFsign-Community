using System.Collections.Generic;

namespace miPDFsign.Models
{
    /// <summary>
    /// Represents a parsed Location field marker: {L,corrX,corrY,fontType,fontSize,"dialogText","sigRef"}
    /// The marker is embedded invisibly in PDF text; position is the marker's text origin + offsets.
    /// </summary>
    public class LocationFieldDescriptor
    {
        // ── Position (PDF user space, bottom-left origin, in points) ────
        public double PdfMarkerX { get; set; }
        public double PdfMarkerY { get; set; }
        public double CorrX      { get; set; }
        public double CorrY      { get; set; }
        public int    PageIndex  { get; set; }

        // ── Field-type attributes ────────────────────────────────────────
        public string FontType   { get; set; } = "A";   // A = Arial, etc.
        public double FontSize   { get; set; } = 10;

        /// <summary>Text shown in the location-input dialog (for future UI use).</summary>
        public string DialogText { get; set; } = "";

        /// <summary>Signature-reference identifier this field is linked to.</summary>
        public string SigRef     { get; set; } = "";

        // ── Computed PDF coordinates ─────────────────────────────────────
        /// <summary>Baseline X in PDF points.</summary>
        public double FieldPdfX => PdfMarkerX + CorrX;
        /// <summary>Baseline Y in PDF points (bottom-up).</summary>
        public double FieldPdfY => PdfMarkerY + CorrY;

        // ── Runtime value ────────────────────────────────────────────────
        /// <summary>Location text entered by the user (empty until filled).</summary>
        public string Value { get; set; } = "";

        // ── WPF render rect (screen pixels, set by ShowPage) ─────────────
        public System.Windows.Rect RenderRect { get; set; }

        // ----------------------------------------------------------------
        //  Parser
        // ----------------------------------------------------------------

        /// <summary>
        /// Tries to parse a marker of the form {L,corrX,corrY,fontType,fontSize,"dialogText","sigRef"}.
        /// Returns null on malformed input.
        /// </summary>
        public static LocationFieldDescriptor? TryParse(
            string markerText, double markerX, double markerY, int pageIndex)
        {
            // Strip braces and split tokens (handles quoted strings)
            string inner = markerText.TrimStart('{').TrimEnd('}');
            var parts = SplitTokens(inner);

            // Minimum: L, corrX, corrY, fontType, fontSize, sigRef  (6)
            if (parts.Count < 6) return null;
            if (parts[0] != "L") return null;

            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double corrX)) return null;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double corrY)) return null;

            string fontType = parts[3];

            if (!double.TryParse(parts[4], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double fontSize)) return null;

            // Optional dialogText (7 parts) or just sigRef (6 parts)
            string dialogText = parts.Count >= 8 ? parts[5] : "";
            string sigRef     = parts.Count >= 8 ? parts[6] : parts[5];

            return new LocationFieldDescriptor
            {
                PdfMarkerX = markerX,
                PdfMarkerY = markerY,
                CorrX      = corrX,
                CorrY      = corrY,
                PageIndex  = pageIndex,
                FontType   = fontType,
                FontSize   = fontSize,
                DialogText = dialogText,
                SigRef     = sigRef,
            };
        }

        // ----------------------------------------------------------------
        //  Token splitter (handles "quoted, strings" and unquoted values)
        // ----------------------------------------------------------------
        internal static List<string> SplitTokens(string s)
        {
            var tokens = new System.Collections.Generic.List<string>();
            var cur    = new System.Text.StringBuilder();
            bool inQ   = false;

            foreach (char c in s)
            {
                if (c == '"')
                {
                    inQ = !inQ;
                }
                else if (c == ',' && !inQ)
                {
                    tokens.Add(cur.ToString().Trim());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            tokens.Add(cur.ToString().Trim());
            return tokens;
        }
    }
}
