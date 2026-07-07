using System.Collections.Generic;
using Syncfusion.Pdf.Parsing;
using miPDFsign.Models;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Opens the PDF once and runs all startup scans in a single pass.
    /// </summary>
    public static class PdfLoadHelper
    {
        public record ScanResult(
            List<CheckboxInfo>                AcroCheckboxes,
            List<SignatureFieldDescriptor>    SignatureFields,
            List<LocationFieldDescriptor>     LocationFields,
            List<DateFieldDescriptor>         DateFields,
            List<LocationDateFieldDescriptor> LocationDateFields,
            List<ParsedCheckboxDescriptor>    ParsedCheckboxes,
            string?                           SignerName,
            string?                           DocumentTitle
        );

        // AcroForm field names tried in order when looking for a document ID / title.
        private static readonly string[] DocTitleFieldNames =
        {
            "DokumentID", "DokumentNr", "DokumentId", "DokumentNummer",
            "Dokumentennummer", "Titel", "Title", "Betreff", "Subject",
            "RapportNr", "RapportID", "FormularID",
        };

        /// <summary>
        /// Opens the PDF once and returns all field types plus signer name and document title.
        /// </summary>
        public static ScanResult ScanAll(string pdfPath)
        {
            AppLogger.Info($"PdfLoadHelper: Loading '{System.IO.Path.GetFileName(pdfPath)}'");

            using var pdfDoc = new PdfLoadedDocument(pdfPath);

            var acroCheckboxes = PdfExporter.GetCheckboxes(pdfDoc);
            AppLogger.Debug($"  AcroForm checkboxes: {acroCheckboxes.Count}");

            var parsed     = PdfSignatureScanner.ScanAllFields(pdfDoc);
            var signerName = PdfExporter.GetTextFieldValue(pdfDoc, "Name");
            AppLogger.Debug($"  Signer name field: '{signerName ?? "<null>"}'");

            string? docTitle = null;
            foreach (var fieldName in DocTitleFieldNames)
            {
                var val = PdfExporter.GetTextFieldValue(pdfDoc, fieldName);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    docTitle = val;
                    AppLogger.Debug($"  Document title from field '{fieldName}': '{docTitle}'");
                    break;
                }
            }

            return new ScanResult(
                AcroCheckboxes:    acroCheckboxes,
                SignatureFields:   parsed.SignatureFields,
                LocationFields:    parsed.LocationFields,
                DateFields:        parsed.DateFields,
                LocationDateFields: parsed.LocationDateFields,
                ParsedCheckboxes:  parsed.CheckboxFields,
                SignerName:        signerName,
                DocumentTitle:     docTitle
            );
        }
    }
}
