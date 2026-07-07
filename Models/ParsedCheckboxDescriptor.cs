namespace miPDFsign.Models
{
    /// <summary>
    /// Represents a parser-based checkbox marker.  Two wire formats are supported:
    ///
    ///   Format A (10+ tokens):  {C,corrX,corrY,width,height,req,style,order,"label","id"[,...]}
    ///   Format B (9+ tokens):   {C,corrX,corrY,size,req,style,order,"label","id"[,...]}
    ///                           (size used for both width and height — square checkbox)
    ///
    /// Unlike <see cref="CheckboxInfo"/> (which comes from AcroForm fields),
    /// this checkbox is embedded as invisible text in the PDF content stream.
    /// </summary>
    public class ParsedCheckboxDescriptor
    {
        // ── Position (PDF user space, bottom-left origin, in points) ────
        public double PdfMarkerX { get; set; }
        public double PdfMarkerY { get; set; }
        public double CorrX      { get; set; }
        public double CorrY      { get; set; }
        public int    PageIndex  { get; set; }

        // ── Box dimensions (PDF points) ───────────────────────────────────
        public double Width  { get; set; }
        public double Height { get; set; }

        // ── Field-type attributes ────────────────────────────────────────
        /// <summary>R = required, O = optional.</summary>
        public bool   IsRequired { get; set; }

        /// <summary>Check mark style: C = cross (×), H = hook (√), D = dot.</summary>
        public string Style      { get; set; } = "C";

        /// <summary>Tab/rendering order within the page.</summary>
        public int    Order      { get; set; }

        public string Label      { get; set; } = "";

        /// <summary>Unique field identifier (used as key in state dictionary).</summary>
        public string Identifier { get; set; } = "";

        // ── Optional group attributes ─────────────────────────────────────
        /// <summary>Group identifier for mutually exclusive checkbox groups (optional).</summary>
        public string? GroupIdentifier { get; set; }

        /// <summary>E = exclusive (radio-button within group), N = multiple allowed.</summary>
        public string? GroupBehavior   { get; set; }

        // ── Computed PDF coordinates ─────────────────────────────────────
        /// <summary>Left edge in PDF points.</summary>
        public double FieldPdfX => PdfMarkerX + CorrX;
        /// <summary>Bottom edge in PDF points (Y-up).</summary>
        public double FieldPdfY => PdfMarkerY + CorrY;

        // ── Runtime state ────────────────────────────────────────────────
        public bool IsChecked { get; set; }

        // ── WPF render rect (screen pixels, set by ShowPage) ─────────────
        public System.Windows.Rect RenderRect { get; set; }

        // ----------------------------------------------------------------
        //  Parser
        // ----------------------------------------------------------------

        /// <summary>
        /// Tries to parse a {C,...} marker.  Supports two formats:
        ///   Format A (10+): C,corrX,corrY,width,height,req,style,order,label,id[,groupId,behavior]
        ///   Format B (9+):  C,corrX,corrY,size,req,style,order,label,id[,groupId,behavior]
        /// Returns null on malformed input.
        /// </summary>
        public static ParsedCheckboxDescriptor? TryParse(
            string markerText, double markerX, double markerY, int pageIndex)
        {
            string inner = markerText.TrimStart('{').TrimEnd('}');
            var parts = LocationFieldDescriptor.SplitTokens(inner);

            if (parts.Count < 9) return null;
            if (parts[0] != "C") return null;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Any;

            if (!double.TryParse(parts[1], ns, ic, out double corrX)) return null;
            if (!double.TryParse(parts[2], ns, ic, out double corrY)) return null;

            // Auto-detect format:
            // Format A has separate width + height (both parseable as doubles at [3] and [4]).
            // Format B has a single size at [3] and a non-numeric req token at [4].
            double width, height;
            int reqOffset;

            if (parts.Count >= 10 &&
                double.TryParse(parts[3], ns, ic, out double wA) &&
                double.TryParse(parts[4], ns, ic, out double hA))
            {
                // Format A: {C,corrX,corrY,width,height,req,...}
                width = wA; height = hA; reqOffset = 5;
            }
            else if (double.TryParse(parts[3], ns, ic, out double sz))
            {
                // Format B: {C,corrX,corrY,size,req,...}  (square checkbox)
                width = height = sz; reqOffset = 4;
            }
            else return null;

            // Need at least 5 more tokens after reqOffset: req, style, order, label, id
            if (parts.Count < reqOffset + 5) return null;

            bool   isRequired = parts[reqOffset].Equals("R", System.StringComparison.OrdinalIgnoreCase);
            string style      = parts[reqOffset + 1];

            if (!int.TryParse(parts[reqOffset + 2], out int order)) order = 0;

            string label      = parts[reqOffset + 3];
            string identifier = parts[reqOffset + 4];

            string? groupId       = parts.Count >= reqOffset + 6 ? parts[reqOffset + 5] : null;
            string? groupBehavior = parts.Count >= reqOffset + 7 ? parts[reqOffset + 6] : null;

            return new ParsedCheckboxDescriptor
            {
                PdfMarkerX      = markerX,
                PdfMarkerY      = markerY,
                CorrX           = corrX,
                CorrY           = corrY,
                PageIndex       = pageIndex,
                Width           = width,
                Height          = height,
                IsRequired      = isRequired,
                Style           = style,
                Order           = order,
                Label           = label,
                Identifier      = identifier,
                GroupIdentifier = groupId,
                GroupBehavior   = groupBehavior,
            };
        }
    }
}
