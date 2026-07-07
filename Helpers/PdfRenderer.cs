using System;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfiumViewer;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Renders PDF pages to WPF BitmapSource images using PDFium.
    /// </summary>
    public sealed class PdfRenderer : IDisposable
    {
        private PdfDocument? _doc;
        private bool _disposed;

        public int PageCount => _doc?.PageCount ?? 0;

        /// <summary>
        /// Load or reload a PDF from disk.
        /// </summary>
        public void Load(string path)
        {
            _doc?.Dispose();
            _doc = PdfDocument.Load(path);
        }

        /// <summary>
        /// Renders a single page at the given DPI (72 dpi = native PDF point size).
        /// </summary>
        /// <param name="pageIndex">0-based page index</param>
        /// <param name="renderDpi">Render resolution (default 150)</param>
        public BitmapSource RenderPage(int pageIndex, int renderDpi = 150)
        {
            if (_doc == null) throw new InvalidOperationException("No PDF loaded.");

            var sizePts = _doc.PageSizes[pageIndex];         // in 72-dpi points
            int w = (int)Math.Ceiling(sizePts.Width  * renderDpi / 72.0);
            int h = (int)Math.Ceiling(sizePts.Height * renderDpi / 72.0);

            using var bmp = _doc.Render(pageIndex, w, h, renderDpi, renderDpi,
                                        rotate: PdfRotation.Rotate0,
                                        flags:  PdfRenderFlags.Annotations);

            return ToBitmapSource(bmp);
        }

        /// <summary>
        /// Returns the page size in PDF points (1 pt = 1/72 inch).
        /// </summary>
        public System.Drawing.SizeF GetPageSizePts(int pageIndex) =>
            _doc?.PageSizes[pageIndex] ?? new System.Drawing.SizeF(595, 842);

        // ----------------------------------------------------------------
        //  Private helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Converts a GDI+ bitmap directly to a WPF BitmapSource via a raw pixel copy —
        /// no PNG encode/decode roundtrip.  ~10× faster than the MemoryStream/PNG path.
        /// </summary>
        private static BitmapSource ToBitmapSource(System.Drawing.Image img)
        {
            if (img is not System.Drawing.Bitmap bmp)
            {
                // Fallback for non-Bitmap images (shouldn't happen with PDFium)
                bmp = new System.Drawing.Bitmap(img);
            }

            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var bs = BitmapSource.Create(
                    data.Width, data.Height,
                    bmp.HorizontalResolution, bmp.VerticalResolution,
                    PixelFormats.Bgra32, null,
                    data.Scan0,
                    data.Stride * data.Height,
                    data.Stride);
                bs.Freeze();
                return bs;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _doc?.Dispose();
        }
    }
}
