using System.Windows;

namespace miPDFsign.Models
{
    /// <summary>
    /// Represents a PDF form checkbox field with its page position
    /// and current checked state.
    /// </summary>
    public class CheckboxInfo
    {
        /// <summary>AcroForm field name (key in the form dictionary)</summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>0-based page index</summary>
        public int PageIndex { get; set; }

        // PDF coordinate space (origin bottom-left, points)
        public float PdfLeft   { get; set; }
        public float PdfBottom { get; set; }
        public float PdfWidth  { get; set; }
        public float PdfHeight { get; set; }

        public bool IsChecked { get; set; }

        /// <summary>
        /// Bounding rectangle in WPF logical pixels on the current page image.
        /// Set by the renderer whenever a page is displayed.
        /// </summary>
        public Rect RenderRect { get; set; }
    }
}
