using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

using Syncfusion.Pdf;
using Syncfusion.Pdf.Graphics;
using Syncfusion.Pdf.Interactive;
using Syncfusion.Pdf.Parsing;
using Syncfusion.Pdf.Security;

using SysPath = System.IO.Path;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;

using CmsAttribute      = Org.BouncyCastle.Asn1.Cms.Attribute;
using CmsAttributeTable = Org.BouncyCastle.Asn1.Cms.AttributeTable;
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace miPDFsign.Helpers
{
    public static class PdfCertSigner
    {
        // (refactor June 2026: single native-Syncfusion incremental signing path)
        // ----------------------------------------------------------------
        //  Public types
        // ----------------------------------------------------------------

        /// <summary>Pre-generated RSA key pair + self-signed certificate for a signing session.</summary>
        public readonly struct CertPair
        {
            public readonly AsymmetricCipherKeyPair KeyPair;
            public readonly BcX509Certificate         Cert;
            public CertPair(AsymmetricCipherKeyPair keyPair, BcX509Certificate cert)
            { KeyPair = keyPair; Cert = cert; }
        }

        public readonly struct BiometricPoint
        {
            public readonly float X;
            public readonly float Y;
            public readonly float Pressure;
            /// <summary>Milliseconds since the first point in this signature group (for forensic velocity/rhythm analysis).</summary>
            public readonly float TimestampMs;
            public BiometricPoint(float x, float y, float pressure, float timestampMs = 0f)
            { X = x; Y = y; Pressure = pressure; TimestampMs = timestampMs; }
        }

        // ----------------------------------------------------------------
        //  Field sign request  (one per signature field)
        // ----------------------------------------------------------------

        /// <summary>
        /// A single point of a signature ink stroke, expressed in <b>appearance-box</b>
        /// coordinates: top-left origin, Y growing downwards, units = PDF points,
        /// range [0..PdfW] × [0..PdfH]. These map 1:1 onto a Syncfusion signature
        /// appearance <see cref="Syncfusion.Pdf.Graphics.PdfGraphics"/> surface, so the
        /// strokes can be drawn as transparent vector paths (no opaque PNG needed).
        /// </summary>
        public readonly struct AppearancePoint
        {
            public readonly float X;
            public readonly float Y;
            /// <summary>Pen pressure 0..1 (WPF <c>StylusPoint.PressureFactor</c>); drives the
            /// stroke width so the rendered ink is pressure-sensitive. 0.5 = neutral default.</summary>
            public readonly float Pressure;
            public AppearancePoint(float x, float y, float pressure = 0.5f)
            { X = x; Y = y; Pressure = pressure; }
        }

        public sealed class FieldSignRequest
        {
            public string                        FieldName     { get; }
            public IReadOnlyList<BiometricPoint> BioPoints     { get; }
            /// <summary>PNG bytes rendered from the field's ink strokes; null = invisible field.
            /// Legacy fallback only — prefer <see cref="AppearanceStrokes"/> for a transparent appearance.</summary>
            public byte[]?                       AppearancePng { get; }
            /// <summary>
            /// Per-stroke ink geometry in appearance-box coordinates (see <see cref="AppearancePoint"/>).
            /// When present, the signature appearance is drawn as transparent vector strokes
            /// instead of an opaque composited image. null/empty = fall back to PNG / invisible.
            /// </summary>
            public IReadOnlyList<IReadOnlyList<AppearancePoint>>? AppearanceStrokes { get; }
            /// <summary>1-based PDF page number.</summary>
            public int                           PageNum       { get; }
            /// <summary>Bottom-left X of the field in PDF user-space (points).</summary>
            public float                         PdfX          { get; }
            /// <summary>Bottom-left Y of the field in PDF user-space (points).</summary>
            public float                         PdfY          { get; }
            public float                         PdfW          { get; }
            public float                         PdfH          { get; }

            public FieldSignRequest(
                string fieldName, IReadOnlyList<BiometricPoint> bioPoints,
                byte[]? appearancePng,
                int pageNum, float pdfX, float pdfY, float pdfW, float pdfH,
                IReadOnlyList<IReadOnlyList<AppearancePoint>>? appearanceStrokes = null)
            {
                FieldName         = fieldName;
                BioPoints         = bioPoints;
                AppearancePng     = appearancePng;
                AppearanceStrokes = appearanceStrokes;
                PageNum = pageNum; PdfX = pdfX; PdfY = pdfY; PdfW = pdfW; PdfH = pdfH;
            }
        }

        // ----------------------------------------------------------------
        //  OIDs
        // ----------------------------------------------------------------

        private static readonly DerObjectIdentifier EssOid =
            new DerObjectIdentifier("1.2.840.113549.1.9.16.2.47");

        private static readonly DerObjectIdentifier TsaOid =
            new DerObjectIdentifier("1.2.840.113549.1.9.16.2.14");

        private static readonly DerObjectIdentifier BioOid =
            new DerObjectIdentifier("1.3.6.1.4.1.53892.1.1");

        // Built-in fallback TSA list – used when App.config "TimestampServers" is empty.
        private static readonly string[] DefaultTsaUrls =
        {
            "http://timestamp.globalsign.com/tsa/r6advanced1",          // GlobalSign R6, SHA-256
            "http://timestamp.sectigo.com/",                            // Sectigo (Comodo), SHA-256
            "http://timestamp.entrust.net/TSS/RFC3161sha2TS",           // Entrust, SHA-256
        };

        /// <summary>
        /// Whether RFC 3161 timestamping is attempted at all (App.config "UseTimestamp",
        /// default <c>true</c>). When <c>false</c>, signatures stay PAdES Baseline-B and no
        /// LTA document timestamp is added (LTV/DSS is unaffected).
        /// </summary>
        private static bool TimestampEnabled =>
            !bool.TryParse(ConfigurationManager.AppSettings["UseTimestamp"], out bool on) || on;

        // TSA endpoints from App.config "TimestampServers" (";"- or newline-separated, tried in
        // order), falling back to DefaultTsaUrls. Resolved once and cached for the process.
        private static string[]? _tsaUrls;
        private static string[] TsaUrls => _tsaUrls ??= LoadTsaUrls();

        private static string[] LoadTsaUrls()
        {
            string raw = ConfigurationManager.AppSettings["TimestampServers"] ?? "";
            var urls = raw
                .Split(new[] { ';', '\n', '\r' },
                       StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => u.Length > 0)
                .ToArray();

            if (urls.Length == 0)
            {
                AppLogger.Debug("PdfCertSigner: no TimestampServers configured – using built-in defaults");
                return DefaultTsaUrls;
            }
            AppLogger.Info($"PdfCertSigner: {urls.Length} timestamp server(s) loaded from config");
            return urls;
        }

        // Static HttpClient – reuse socket connections across calls
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        // /Contents placeholder size in bytes.
        // The placeholder occupies 2 × CmsSlotSize hex characters in the PDF.
        private const int CmsSlotSize = 65_536;

        // ----------------------------------------------------------------
        //  Primary entry point – sign one field per call (incremental)
        // ----------------------------------------------------------------

        /// <summary>
        /// Signs each listed field in the PDF sequentially with incremental PAdES
        /// Baseline-B/T signatures.  One RSA-3072 key pair is generated per session;
        /// each field gets its own CMS blob containing field-specific biometric data.
        /// </summary>
        /// <summary>
        /// Returns the CMS bytes of the last signed field so the caller can pass them
        /// directly to <see cref="AddLtv"/> without re-parsing the PDF.
        /// </summary>
        public static byte[] SignFields(
            string pdfPath,
            string signerName,
            IReadOnlyList<FieldSignRequest> fields)
        {
            if (fields.Count == 0) return Array.Empty<byte>();
            AppLogger.Info($"PdfCertSigner.SignFields: {fields.Count} field(s), signer='{signerName}' – generating certificate");
            var certPair = GenerateCertificatePublic(signerName);
            return SignFields(pdfPath, signerName, fields, certPair);
        }

        /// <summary>
        /// Overload that accepts a pre-generated key pair (e.g. computed at startup).
        /// Returns the CMS bytes of the last signed field.
        /// </summary>
        public static byte[] SignFields(
            string pdfPath,
            string signerName,
            IReadOnlyList<FieldSignRequest> fields,
            CertPair certPair)
        {
            if (fields.Count == 0) return Array.Empty<byte>();
            AppLogger.Info($"PdfCertSigner.SignFields: signing {fields.Count} field(s) in '{SysPath.GetFileName(pdfPath)}'");

            // SaveCertAndKey(certPair.Cert, certPair.KeyPair.Private, pdfPath, signerName);

            byte[] lastCms = Array.Empty<byte>();
            foreach (var req in fields)
            {
                AppLogger.Info($"  Signing field '{req.FieldName}' (page {req.PageNum}, bio points: {req.BioPoints.Count})");
                lastCms = SignFieldIncremental(pdfPath, req, certPair.KeyPair, certPair.Cert);
                AppLogger.Info($"  Field '{req.FieldName}' signed successfully");
            }

            AppLogger.Info($"PdfCertSigner.SignFields: all {fields.Count} field(s) signed");
            return lastCms;
        }

        /// <summary>
        /// Generates an RSA-3072 self-signed certificate for <paramref name="signerName"/>.
        /// Public so it can be pre-computed at startup on a background thread.
        /// </summary>
        public static CertPair GenerateCertificatePublic(string signerName)
        {
            AppLogger.Info($"PdfCertSigner: Generating RSA-3072 certificate for '{signerName}'");
            var (kp, cert) = GenerateCertificate(signerName);
            AppLogger.Info($"PdfCertSigner: Certificate generated (serial={cert.SerialNumber}, valid until {cert.NotAfter:yyyy-MM-dd HH:mm:ss} UTC)");
            return new CertPair(kp, cert);
        }

        // ----------------------------------------------------------------
        //  CMS extraction from signed PDF
        // ----------------------------------------------------------------

        /// <summary>
        /// Extracts the raw CMS bytes of the most-recent /Contents entry from a signed PDF.
        /// Used to feed <see cref="AddLtv"/> after <see cref="SignFields"/> has saved the file.
        /// </summary>
        public static byte[] ExtractLastCmsBytes(byte[] pdfBytes)
        {
            string pdfStr = Encoding.Latin1.GetString(pdfBytes);
            var matches   = Regex.Matches(pdfStr, @"/Contents\s*<([0-9a-fA-F]+)>", RegexOptions.IgnoreCase);
            if (matches.Count == 0)
                throw new InvalidOperationException("ExtractLastCmsBytes: no /Contents entry found in PDF");

            string hexSlot = matches[^1].Groups[1].Value;
            if (hexSlot.Length == 0)
                throw new InvalidOperationException("ExtractLastCmsBytes: /Contents slot is empty");
            if (hexSlot.Length % 2 != 0) hexSlot = "0" + hexSlot;

            byte[] slot = Convert.FromHexString(hexSlot);

            // The /Contents slot is zero-padded to CmsSlotSize bytes.
            // We must NOT use TrimEnd('0') to find the end of the CMS — ASN.1 structures
            // regularly end with 0x00 or 0x30 bytes (SEQUENCE tags, NULL values, etc.),
            // so character-trimming corrupts the CMS.
            //
            // Instead, parse the outer DER SEQUENCE tag+length to get the exact byte count.
            if (slot.Length < 4 || slot[0] != 0x30)
                throw new InvalidOperationException(
                    "ExtractLastCmsBytes: /Contents does not start with a DER SEQUENCE (0x30) — slot may be empty or corrupt");

            int headerLen;
            int contentLen;
            if ((slot[1] & 0x80) == 0)
            {
                // Short form: length is directly in byte 1
                contentLen = slot[1];
                headerLen  = 2;
            }
            else
            {
                int numLenBytes = slot[1] & 0x7F;
                if (numLenBytes == 0 || numLenBytes > 4)
                    throw new InvalidOperationException(
                        $"ExtractLastCmsBytes: unsupported DER length encoding ({numLenBytes} length bytes)");
                contentLen = 0;
                for (int i = 0; i < numLenBytes; i++)
                    contentLen = (contentLen << 8) | slot[2 + i];
                headerLen = 2 + numLenBytes;
            }

            int totalLen = headerLen + contentLen;
            if (totalLen > slot.Length)
                throw new InvalidOperationException(
                    $"ExtractLastCmsBytes: DER-declared length {totalLen} exceeds slot size {slot.Length}");

            AppLogger.Debug($"ExtractLastCmsBytes: DER SEQUENCE {totalLen} bytes (header {headerLen} + content {contentLen})");
            return slot[..totalLen];
        }

        // ----------------------------------------------------------------
        //  Biometric data extraction  (forensic / verification use)
        // ----------------------------------------------------------------

        public sealed class BiometricData
        {
            public DateTimeOffset SigningTime { get; }
            public IReadOnlyList<BiometricPoint> Points { get; }
            internal BiometricData(DateTimeOffset ts, IReadOnlyList<BiometricPoint> pts)
            { SigningTime = ts; Points = pts; }
        }

        public static BiometricData ExtractBiometricData(
            string pdfPath, AsymmetricKeyParameter privateKey)
        {
            // Read PDF bytes and find the last /Contents hex blob (last = most recent signature)
            byte[] pdfBytes = File.ReadAllBytes(pdfPath);
            string pdfStr   = Encoding.Latin1.GetString(pdfBytes);

            var matches = Regex.Matches(pdfStr,
                @"/Contents\s*<([0-9a-fA-F]+)>", RegexOptions.IgnoreCase);
            if (matches.Count == 0)
                throw new InvalidOperationException("No signature found in the PDF.");

            string hexStr  = matches[^1].Groups[1].Value;
            byte[] rawCms  = Convert.FromHexString(hexStr.TrimEnd('0').PadLeft(
                hexStr.TrimEnd('0').Length % 2 == 0 ? 0 : 1, '0'));

            // Let BouncyCastle parse the DER; trailing padding zeros are harmless.
            var cmsSignedData = new CmsSignedData(rawCms);
            foreach (SignerInformation si in cmsSignedData.GetSignerInfos().GetSigners())
            {
                var sa = si.SignedAttributes;
                if (sa == null) continue;
                Org.BouncyCastle.Asn1.Cms.Attribute? bioAttr = sa[BioOid];
                if (bioAttr == null) continue;
                byte[] encBio = DerOctetString.GetInstance(bioAttr.AttrValues[0]).GetOctets();
                byte[] rawBio = HybridDecrypt(encBio, (RsaKeyParameters)privateKey);
                return DeserializeBioData(rawBio);
            }

            throw new InvalidOperationException(
                "No biometric data (OID " + BioOid.Id + ") found.");
        }

        // ----------------------------------------------------------------
        //  Private: certificate generation
        // ----------------------------------------------------------------

        private static (AsymmetricCipherKeyPair keyPair, BcX509Certificate cert)
            GenerateCertificate(string signerName)
        {
            var random = new SecureRandom();
            var keyGen = new RsaKeyPairGenerator();
            keyGen.Init(new KeyGenerationParameters(random, 3072));
            AsymmetricCipherKeyPair keyPair = keyGen.GenerateKeyPair();

            var certGen = new X509V3CertificateGenerator();
            var dn = new Org.BouncyCastle.Asn1.X509.X509Name("CN=" + signerName);
            certGen.SetIssuerDN(dn); certGen.SetSubjectDN(dn);
            certGen.SetSerialNumber(new BigInteger(128, random));
            // Short-lived certificate: valid for only 10 minutes. That is sufficient, because
            // the RFC-3161 timestamp (PAdES-T) proves the signing time and LTV/LTA archive the
            // signature long-term — so the signature stays valid even after the certificate
            // expires. The short lifetime minimises the window during which the (self-signed)
            // private key could be misused.
            // (-5 s NotBefore as tolerance against slight clock skew.)
            certGen.SetNotBefore(DateTime.UtcNow.AddSeconds(-5));
            certGen.SetNotAfter(DateTime.UtcNow.AddMinutes(10));
            certGen.SetPublicKey(keyPair.Public);
            BcX509Certificate bcCert = certGen.Generate(
                new Asn1SignatureFactory("SHA256WITHRSA", keyPair.Private, random));

            var certParser = new X509CertificateParser();
            return (keyPair, certParser.ReadCertificate(bcCert.GetEncoded()));
        }

        // ----------------------------------------------------------------
        //  Private: sign one PDF field
        //
        //  Strategy:
        //    Syncfusion's ComputeHash event delivers the byte-range content during Save().
        //    Our handler builds a full PAdES CMS (BouncyCastle: biometric OID, ESS) and
        //    returns it as SignedData.  Syncfusion writes the CMS into /Contents.
        //    sig.EstimatedSignatureSize pre-allocates enough space for our large CMS.
        // ----------------------------------------------------------------

        private static byte[] SignFieldIncremental(
            string pdfPath,
            FieldSignRequest req,
            AsymmetricCipherKeyPair keyPair,
            BcX509Certificate cert)
        {
            AppLogger.Debug($"    SignFieldIncremental: field='{req.FieldName}' " +
                $"page={req.PageNum} rect=({req.PdfX:F0},{req.PdfY:F0},{req.PdfW:F0},{req.PdfH:F0})");

            var random = new SecureRandom();
            byte[] rawBio = SerializeBioData(req.BioPoints);
            byte[] encBio = HybridEncrypt(rawBio, (RsaKeyParameters)keyPair.Public, random);
            AppLogger.Info($"    Biometric data: {req.BioPoints.Count} point(s), {rawBio.Length}B raw, {encBio.Length}B encrypted");

            string signerCn = req.FieldName;
            try
            {
                string dnStr = cert.SubjectDN.ToString();
                if (dnStr.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    signerCn = dnStr[3..].Trim();
            }
            catch { }

            string tmpOrig = pdfPath + ".orig_tmp";
            File.Move(pdfPath, tmpOrig, overwrite: true);

            try
            {
                byte[] origBytes = File.ReadAllBytes(tmpOrig);

                // Every field is signed through Syncfusion's NATIVE incremental-update
                // pipeline (PdfFileStructure.IncrementalUpdate = true). Save() then appends
                // a new revision instead of rewriting the file, so the /ByteRange of every
                // previously-applied signature still points at unchanged bytes and stays
                // valid in Adobe. This replaces the former hand-rolled ManualIncrementalSign
                // (manual xref/object surgery) with a single, standard code path.
                //
                // The earlier "Syncfusion always full-rewrites" conclusion was caused by
                // touching PdfLoadedPage.Graphics (which re-initialises page content and
                // breaks existing signatures). We deliberately never access page.Graphics
                // here — only page.Size and the signature's own appearance graphics.
                var (finalPdf, realCms) = SyncfusionSign(
                    origBytes, req, signerCn, keyPair, cert, encBio, random);

                // SubFilter sanity-check
                {
                    string pdfText = Encoding.Latin1.GetString(finalPdf);
                    int etsiCount  = CountOccurrences(pdfText, "ETSI.CAdES.detached");
                    int pkcs7Count = CountOccurrences(pdfText, "adbe.pkcs7.detached");
                    AppLogger.Info($"    SubFilter: ETSI.CAdES.detached×{etsiCount}  adbe.pkcs7.detached×{pkcs7Count}");
                    if (etsiCount == 0)
                        AppLogger.Warn("    *** ETSI.CAdES.detached NOT FOUND — signature is NOT PAdES!");
                }

                File.WriteAllBytes(pdfPath, finalPdf);
                AppLogger.Info($"    Signed PDF written: {finalPdf.Length} bytes");
                return realCms;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"    SignFieldIncremental failed for '{req.FieldName}'", ex);
                if (File.Exists(tmpOrig) && !File.Exists(pdfPath))
                    File.Move(tmpOrig, pdfPath);
                throw;
            }
            finally
            {
                if (File.Exists(tmpOrig)) File.Delete(tmpOrig);
            }
        }

        // ----------------------------------------------------------------
        //  SyncfusionSign — first signature only (full rewrite is acceptable)
        // ----------------------------------------------------------------

        private static (byte[] finalPdf, byte[] cms) SyncfusionSign(
            byte[] origBytes,
            FieldSignRequest req,
            string signerCn,
            AsymmetricCipherKeyPair keyPair,
            BcX509Certificate cert,
            byte[] encBio,
            SecureRandom random)
        {
            byte[]? builtCms = null;

            using var pdfDoc = new PdfLoadedDocument(new MemoryStream(origBytes));

            // Native incremental update: Save() appends a new revision instead of doing a
            // full rewrite, so every existing signature's /ByteRange remains valid.
            pdfDoc.FileStructure.IncrementalUpdate = true;

            var page = pdfDoc.Pages[req.PageNum - 1] as PdfLoadedPage
                ?? throw new InvalidOperationException($"Page {req.PageNum} not found");

            // Read the page size via PdfLoadedPage.Size ONLY. Never touch page.Graphics on
            // a loaded page — it re-initialises the page content stream and invalidates
            // any signature already present (confirmed Syncfusion behaviour).
            float pageH = page.Size.Height;

            // Single, consistent coordinate conversion. req.PdfX/PdfY are PDF user-space
            // (bottom-left origin); Syncfusion's RectangleF uses a top-left origin.
            var bounds = new Syncfusion.Drawing.RectangleF(
                req.PdfX, pageH - req.PdfY - req.PdfH, req.PdfW, req.PdfH);

            // CRITICAL: (pdfDoc, page, null, name) — do NOT assign to existingField.Signature
            // (registers a duplicate DocumentSaved handler → OverflowException).
            var sig = new PdfSignature(pdfDoc, page, null!, req.FieldName);
            sig.Bounds                         = bounds;
            sig.Reason                         = "Tablet signature";
            sig.LocationInfo                   = "POS";
            sig.SignedName                     = signerCn;
            sig.EstimatedSignatureSize         = CmsSlotSize;
            // CAdES → Syncfusion writes /SubFilter /ETSI.CAdES.detached itself (PAdES-BES),
            // replacing the former in-place byte patching of "adbe.pkcs7.detached".
            sig.Settings.CryptographicStandard = CryptographicStandard.CADES;
            sig.Settings.DigestAlgorithm       = DigestAlgorithm.SHA256;

            AppLogger.Debug($"    SyncfusionSign: field='{req.FieldName}' bounds={bounds} (CAdES, incremental)");

            // Transparent vector-ink appearance (no opaque PNG composited on white).
            DrawSignatureAppearance(sig.Appearance.Normal.Graphics, req);

            // Build the real PAdES CMS directly from the byte-range content Syncfusion hands
            // us, and return it. Syncfusion writes it into /Contents and computes the final
            // /ByteRange itself — no manual placeholder, SubFilter patch, trim or inject.
            sig.ComputeHash += (_, ars) =>
            {
                byte[] toSign = ars.Data ?? Array.Empty<byte>();
                builtCms = BuildPadesCms(toSign, keyPair.Private, cert, encBio, random);
                AppLogger.Info($"    ComputeHash: signing {toSign.Length}B → CMS {builtCms.Length}B");
                ars.SignedData = builtCms;
            };

            using var ms = new MemoryStream();
            pdfDoc.Save(ms);
            byte[] finalPdf = ms.ToArray();

            if (builtCms == null)
                throw new InvalidOperationException(
                    "SyncfusionSign: ComputeHash was never invoked — signature not created");

            AppLogger.Info($"    SyncfusionSign done: {finalPdf.Length}B");
            return (finalPdf, builtCms);
        }

        // ----------------------------------------------------------------
        //  Signature appearance — transparent vector ink
        // ----------------------------------------------------------------

        /// <summary>
        /// Draws the signature ink into a Syncfusion appearance <see cref="PdfGraphics"/>
        /// surface. When <see cref="FieldSignRequest.AppearanceStrokes"/> is present the
        /// strokes are rendered as black vector paths on a fully transparent background
        /// (the underlying document text remains visible). Otherwise it falls back to the
        /// legacy PNG image (which may be opaque) or draws nothing for an invisible field.
        /// </summary>
        private static void DrawSignatureAppearance(PdfGraphics g, FieldSignRequest req)
        {
            var strokes = req.AppearanceStrokes;
            if (strokes != null && strokes.Count > 0)
            {
                // Pressure-sensitive ink: each segment is drawn with its own pen width,
                // interpolated between MinPenWidth (lightest touch) and MaxPenWidth (full
                // pressure) from the two endpoints' average PressureFactor. Round caps/joins
                // make the varying-width segments blend into one continuous stroke.
                const float minPenW = 0.35f;   // PDF points at pressure 0
                const float maxPenW = 2.40f;   // PDF points at pressure 1
                static float WidthFor(float pressure) =>
                    minPenW + (maxPenW - minPenW) * Math.Clamp(pressure, 0f, 1f);

                // Match the on-screen ink colour (WPF Colors.DarkBlue = RGB 0,0,139).
                var inkColor = new PdfColor(0, 0, 139);
                var dotBrush = new PdfSolidBrush(inkColor);

                foreach (var stroke in strokes)
                {
                    if (stroke == null || stroke.Count == 0) continue;

                    if (stroke.Count == 1)
                    {
                        // A single tap/dot — draw a filled circle sized by its pressure.
                        float r = WidthFor(stroke[0].Pressure) * 0.5f;
                        g.DrawEllipse(dotBrush, stroke[0].X - r, stroke[0].Y - r, r * 2f, r * 2f);
                        continue;
                    }

                    for (int i = 1; i < stroke.Count; i++)
                    {
                        float segPressure = (stroke[i - 1].Pressure + stroke[i].Pressure) * 0.5f;
                        var pen = new PdfPen(inkColor, WidthFor(segPressure))
                        {
                            LineCap  = PdfLineCap.Round,
                            LineJoin = PdfLineJoin.Round,
                        };
                        g.DrawLine(pen,
                            stroke[i - 1].X, stroke[i - 1].Y,
                            stroke[i].X,     stroke[i].Y);
                    }
                }
                return;
            }

            // Legacy fallback: opaque PNG appearance.
            if (req.AppearancePng != null)
            {
                using var pngStream = new MemoryStream(req.AppearancePng);
                var bitmap = new PdfBitmap(pngStream);
                g.DrawImage(bitmap, 0, 0, req.PdfW, req.PdfH);
            }
        }

        // ----------------------------------------------------------------
        //  ByteRange parsing and CMS injection helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Locates the last /ByteRange array in the PDF bytes (by text position).
        /// Used by the QES placeholder/inject path and the LTA document-timestamp path,
        /// where exactly one new /ByteRange is appended per incremental update.
        /// </summary>
        private static (long b0, long l0, long b1, long l1) ParseByteRange(byte[] pdfBytes)
        {
            // Read as Latin-1 so byte values map 1-to-1 to chars (no multi-byte expansion)
            string text  = Encoding.Latin1.GetString(pdfBytes);
            var matches  = Regex.Matches(text,
                @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");

            if (matches.Count == 0)
                throw new InvalidOperationException("Could not find /ByteRange in PDF output");

            // Take the last occurrence (most recent incremental revision)
            var m = matches[^1];
            return (
                long.Parse(m.Groups[1].Value),
                long.Parse(m.Groups[2].Value),
                long.Parse(m.Groups[3].Value),
                long.Parse(m.Groups[4].Value)
            );
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            { count++; idx += needle.Length; }
            return count;
        }

        /// <summary>
        /// Replaces the zero-byte /Contents placeholder with the real CMS bytes.
        /// The hex content occupies bytes [b0+l0 .. b1) in the PDF — one byte for '<',
        /// 2×CmsSlotSize hex chars, one byte for '>'.
        /// </summary>
        private static byte[] InjectCms(
            byte[] pdf, byte[] cms, long b0, long l0, long b1)
        {
            // Hex-encode the CMS and pad to fill the slot exactly
            int    hexLen  = (int)(b1 - b0 - l0 - 2); // subtract '<' and '>'
            string cmsHex  = Convert.ToHexString(cms).ToLowerInvariant();
            if (cmsHex.Length > hexLen)
                throw new InvalidOperationException(
                    $"CMS hex ({cmsHex.Length} chars) exceeds /Contents slot ({hexLen} chars)");
            string padded  = cmsHex.PadRight(hexLen, '0');

            byte[] result  = (byte[])pdf.Clone();
            byte[] hexBytes = Encoding.ASCII.GetBytes(padded);

            long hexOffset = b0 + l0 + 1; // +1 to skip the '<'
            Array.Copy(hexBytes, 0L, result, hexOffset, hexBytes.Length);
            return result;
        }

        // ----------------------------------------------------------------
        //  PAdES CMS builder (detached CAdES, PAdES Baseline-B/T)
        // ----------------------------------------------------------------

        private static byte[] BuildPadesCms(
            byte[]                 signedContent,
            AsymmetricKeyParameter privateKey,
            BcX509Certificate        cert,
            byte[]                 encBio,
            SecureRandom           rng)
        {
            // ESS SigningCertificateV2 (PAdES compliance)
            byte[] certHash = DigestUtilities.CalculateDigest("SHA-256", cert.GetEncoded());
            var essCertIDv2 = new EssCertIDv2(
                new AlgorithmIdentifier(NistObjectIdentifiers.IdSha256), certHash);

            var extraAttrs = new Dictionary<DerObjectIdentifier, object>
            {
                [EssOid] = new CmsAttribute(EssOid,
                    new DerSet(new SigningCertificateV2(new[] { essCertIDv2 }))),
                [BioOid] = new CmsAttribute(BioOid,
                    new DerSet(new DerOctetString(encBio)))
            };
            AppLogger.Debug($"      ESS + biometric attributes added (bio={encBio.Length} bytes, OID {BioOid.Id})");

            // Build CMS SignedData
            var gen = new CmsSignedDataGenerator();
            gen.AddSignerInfoGenerator(
                new SignerInfoGeneratorBuilder()
                    .WithSignedAttributeGenerator(
                        new DefaultSignedAttributeTableGenerator(
                            new CmsAttributeTable(extraAttrs)))
                    .Build(new Asn1SignatureFactory("SHA256WITHRSA", privateKey, rng), cert));
            gen.AddCertificates(CollectionUtilities.CreateStore(new[] { cert }));

            CmsSignedData signed = gen.Generate(
                CmsSignedDataGenerator.Data,
                new CmsProcessableByteArray(signedContent),
                false);
            AppLogger.Info("      CMS SignedData created (SHA256withRSA, PAdES Baseline-B)");

            // RFC 3161 timestamp (PAdES Baseline-T, best effort)
            foreach (SignerInformation si in signed.GetSignerInfos().GetSigners())
            {
                byte[]? tsBytes = TryGetTimestampToken(si.GetSignature());
                if (tsBytes != null)
                {
                    var tsAttrTable = new Dictionary<DerObjectIdentifier, object>
                    {
                        [TsaOid] = new CmsAttribute(TsaOid,
                            new DerSet(Asn1Object.FromByteArray(tsBytes)))
                    };
                    SignerInformation newSi = SignerInformation.ReplaceUnsignedAttributes(
                        si, new CmsAttributeTable(tsAttrTable));
                    signed = CmsSignedData.ReplaceSigners(
                        signed, new SignerInformationStore(new[] { newSi }));
                    AppLogger.Info("      RFC 3161 timestamp embedded (PAdES Baseline-T)");
                }
                break; // only one signer
            }

            return signed.GetEncoded();
        }

        // ----------------------------------------------------------------
        //  QES path: prepare placeholder PDF + inject external CMS
        //
        //  Used when signing via ID-Austria (A-Trust Security Layer 1.2):
        //    1. PrepareQesSigning:  creates a placeholder PDF with an empty /Contents
        //                           slot and captures the bytes-to-sign.
        //    2. Caller sends bytes-to-sign to A-Trust → receives CMS blob.
        //    3. InjectQesCms:       patches the /Contents slot with the real CMS.
        //
        //  Same OverflowException fix applies: new PdfSignature(pdfDoc, page, null, name)
        //  without assigning to existingField.Signature.
        // ----------------------------------------------------------------

        /// <summary>
        /// Opens the PDF at <paramref name="pdfPath"/>, creates a placeholder signature
        /// increment (with an empty /Contents slot), and returns the bytes that must be
        /// signed by the external signer (A-Trust).
        /// </summary>
        /// <param name="pdfPath">Path to the PDF to sign (not modified).</param>
        /// <param name="req">Signature field parameters (field name, page, bounds, appearance).</param>
        /// <param name="encBio">Already-encrypted biometric blob (embedded as signed attribute).</param>
        /// <param name="placeholderPdf">Output: PDF bytes with the empty /Contents placeholder.</param>
        /// <returns>Byte range content (the bytes that A-Trust must sign).</returns>
        public static byte[] PrepareQesSigning(
            string pdfPath,
            FieldSignRequest req,
            byte[] encBio,
            out byte[] placeholderPdf)
        {
            AppLogger.Info($"PdfCertSigner.PrepareQesSigning: field='{req.FieldName}' page={req.PageNum}");

            byte[] captured = Array.Empty<byte>();

            using (var pdfDoc = new PdfLoadedDocument(pdfPath))
            {
                // Native incremental update so any signature already present stays valid.
                pdfDoc.FileStructure.IncrementalUpdate = true;

                var page  = pdfDoc.Pages[req.PageNum - 1] as PdfLoadedPage
                    ?? throw new InvalidOperationException($"Page {req.PageNum} not found");

                // page.Size only — never page.Graphics (would invalidate existing signatures).
                float pageH  = page.Size.Height;

                // Single, consistent top-left conversion (PDF bottom-left → Syncfusion top-left).
                var sigBounds = new Syncfusion.Drawing.RectangleF(
                    req.PdfX, pageH - req.PdfY - req.PdfH, req.PdfW, req.PdfH);

                var sig = new PdfSignature(pdfDoc, page, null!, req.FieldName);
                sig.Bounds                         = sigBounds;
                sig.Reason                         = "QES via ID-Austria";
                sig.LocationInfo                   = "POS";
                sig.EstimatedSignatureSize         = CmsSlotSize;
                // CAdES → Syncfusion writes /SubFilter /ETSI.CAdES.detached natively
                // (PAdES-BES); no post-hoc byte patching required.
                sig.Settings.CryptographicStandard = CryptographicStandard.CADES;
                sig.Settings.DigestAlgorithm       = DigestAlgorithm.SHA256;

                // Transparent vector-ink appearance.
                DrawSignatureAppearance(sig.Appearance.Normal.Graphics, req);

                // Capture the bytes-to-sign from ComputeHash; return a dummy CMS so the
                // PDF is well-formed (InjectQesCms overwrites it later).
                //
                // CRITICAL: ars.SignedData must be exactly CmsSlotSize bytes.
                // Syncfusion sizes the /Contents slot (and computes /ByteRange) based on
                // ars.SignedData.Length, NOT on EstimatedSignatureSize alone.
                // If ars.SignedData is smaller (e.g. CmsSlotSize/4), Syncfusion shrinks the
                // /Contents slot to 2×ars.SignedData.Length hex chars and recalculates
                // /ByteRange accordingly.  A-Trust then hashes a different byte range than
                // Adobe later verifies → "document was modified" error.
                // Solution: dummy must be exactly CmsSlotSize bytes so the slot and
                // /ByteRange agree between placeholder PDF, A-Trust, and Adobe.
                sig.ComputeHash += (_, ars) =>
                {
                    captured = ars.Data ?? Array.Empty<byte>();
                    // Zero-filled dummy of full slot size; InjectQesCms overwrites with real CMS.
                    ars.SignedData = new byte[CmsSlotSize];
                };

                using var ms = new MemoryStream();
                pdfDoc.Save(ms);
                placeholderPdf = ms.ToArray();
            }

            // /SubFilter is already /ETSI.CAdES.detached because the signature was created
            // with CryptographicStandard.CADES — no post-hoc byte patching is required.

            AppLogger.Info($"  PrepareQesSigning: captured {captured.Length} bytes-to-sign, " +
                           $"placeholder PDF {placeholderPdf.Length} bytes");

            // ── ByteRange sanity check + trim ─────────────────────────────
            // Adobe reports "document was modified" if bytes exist outside /ByteRange.
            // Some PDF writers (including Syncfusion) append trailing whitespace after
            // %%EOF that is not covered by /ByteRange. Trim those bytes so that:
            //   (a) A-Trust hashes exactly the byte-range content, and
            //   (b) the injected PDF has no uncovered trailing bytes.
            {
                var (br0, brl0, br1, brl1) = ParseByteRange(placeholderPdf);
                long covered = br1 + brl1;
                AppLogger.Info($"  ByteRange: [{br0},{brl0},{br1},{brl1}]  " +
                               $"covered={covered}  pdfLen={placeholderPdf.Length}  " +
                               $"slot={br1 - br0 - brl0 - 2} hex chars");

                if (covered < placeholderPdf.Length)
                {
                    AppLogger.Warn($"  *** /ByteRange does NOT cover the last " +
                                   $"{placeholderPdf.Length - covered} bytes — trimming placeholder!");
                    placeholderPdf = placeholderPdf[..(int)covered];
                }
                else if (covered == placeholderPdf.Length)
                {
                    AppLogger.Info("  /ByteRange covers entire placeholder PDF ✓");
                }
                else
                {
                    // covered > pdfLen: /ByteRange claims more bytes than exist — corrupt PDF
                    AppLogger.Warn($"  *** /ByteRange extends {covered - placeholderPdf.Length} bytes " +
                                   "PAST end of PDF — placeholder may be corrupt!");
                }

            }

            return captured;
        }

        /// <summary>
        /// Patches the /Contents slot of <paramref name="placeholderPdf"/> with the real
        /// <paramref name="cms"/> blob and returns the completed PDF bytes.
        /// </summary>
        public static byte[] InjectQesCms(byte[] placeholderPdf, byte[] cms)
        {
            var (b0, l0, b1, l1) = ParseByteRange(placeholderPdf);
            long covered = b1 + l1;
            int  hexLen  = (int)(b1 - b0 - l0 - 2);
            int  cmsHex  = cms.Length * 2;

            if (covered != placeholderPdf.Length)
                AppLogger.Warn($"InjectQesCms: ByteRange covers only {covered}/{placeholderPdf.Length} bytes – Adobe may report 'document modified'");
            if (cmsHex > hexLen)
                AppLogger.Warn($"InjectQesCms: CMS {cms.Length}B exceeds slot {hexLen / 2}B – signature will be truncated!");

            byte[] result = InjectCms(placeholderPdf, cms, b0, l0, b1);
            AppLogger.Info($"InjectQesCms: done ({result.Length}B, CMS {cms.Length}B)");
            return result;
        }

        // ----------------------------------------------------------------
        //  PAdES-LT: embed CRL / OCSP in a DSS incremental update
        // ----------------------------------------------------------------

        /// <summary>
        /// Upgrades a PAdES Baseline-B signed PDF to PAdES Baseline-LT by appending
        /// a Document Security Store (DSS) as an unsigned incremental update.
        /// The DSS embeds CRL and/or OCSP revocation data so the signature can be
        /// validated even after the signing certificate expires.
        /// </summary>
        public static async Task<byte[]> AddLtv(byte[] signedPdf, byte[] cms)
        {
            AppLogger.Info("PdfCertSigner.AddLtv: fetching revocation data for PAdES-LT");

            // ── 1. Parse CMS certificate chain ───────────────────────────────
            CmsSignedData cmsData;
            try { cmsData = new CmsSignedData(cms); }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddLtv: CMS parse failed: {ex.Message}");
                return signedPdf;
            }

            var allCerts = cmsData.GetCertificates()
                .EnumerateMatches(null)
                .ToList();

            AppLogger.Info($"AddLtv: {allCerts.Count} cert(s) in QES chain");

            // ── 1b. Also collect TSA certificates from signature-time-stamp ─────
            //  The signature-time-stamp unsigned attribute embeds a full RFC 3161
            //  TimeStampToken (CMS SignedData) which contains its own certificate
            //  chain.  We must include those in the DSS so Adobe can validate the
            //  timestamp chain even when its CRL distribution point is unreachable.
            var sigTstOid = new DerObjectIdentifier("1.2.840.113549.1.9.16.2.14");
            foreach (SignerInformation si in cmsData.GetSignerInfos().GetSigners())
            {
                var tstAttr = si.UnsignedAttributes?[sigTstOid];
                if (tstAttr == null) continue;
                foreach (Asn1Encodable attrVal in tstAttr.AttrValues)
                {
                    try
                    {
                        var tstToken  = new TimeStampToken(new CmsSignedData(attrVal.GetEncoded()));
                        var tstCerts  = tstToken.GetCertificates().EnumerateMatches(null).ToList();
                        allCerts.AddRange(tstCerts);
                        AppLogger.Info($"AddLtv: +{tstCerts.Count} cert(s) from signature-time-stamp TSA");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"AddLtv: signature-time-stamp cert extraction: {ex.Message}");
                    }
                }
            }

            if (allCerts.Count == 0)
            {
                AppLogger.Warn("AddLtv: no certificates found — skipping DSS");
                return signedPdf;
            }

            AppLogger.Info($"AddLtv: {allCerts.Count} cert(s) total (QES + TSA chain)");

            // ── 2. Fetch CRL / OCSP for each cert in the chain ───────────────
            var crlBlobs  = new List<byte[]>();
            var ocspBlobs = new List<byte[]>();

            // Iterate over a snapshot — we may append issuer certs mid-loop
            foreach (var cert in allCerts.ToList())
            {
                // CRL — download with size guard (some CDPs serve multi-MB files)
                string? crlUrl = LtvGetCdpUrl(cert);
                if (crlUrl != null)
                {
                    try
                    {
                        // Read headers first to catch oversized CRLs before they OOM us
                        using var crlResp = await _http.GetAsync(crlUrl,
                            HttpCompletionOption.ResponseHeadersRead);
                        if (!crlResp.IsSuccessStatusCode)
                        {
                            AppLogger.Warn($"AddLtv: CRL HTTP {(int)crlResp.StatusCode} from {crlUrl}");
                        }
                        else
                        {
                            long? crlSize = crlResp.Content.Headers.ContentLength;
                            if (crlSize > 5_000_000)
                            {
                                AppLogger.Warn($"AddLtv: CRL too large ({crlSize}B) at {crlUrl} — skipping");
                            }
                            else
                            {
                                byte[] crl = await crlResp.Content.ReadAsByteArrayAsync();
                                crlBlobs.Add(crl);
                                AppLogger.Info($"AddLtv: CRL {crl.Length}B ← {crlUrl}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"AddLtv: CRL fetch failed ({crlUrl}): {ex.Message}");
                    }
                }

                // OCSP
                string? ocspUrl = LtvGetOcspUrl(cert);
                if (ocspUrl != null)
                {
                    BcX509Certificate? issuer = LtvFindIssuer(cert, allCerts);

                    if (issuer == null)
                    {
                        // Download issuer from AIA id-ad-caIssuers
                        string? caUrl = LtvGetCaIssuersUrl(cert);
                        if (caUrl != null)
                        {
                            try
                            {
                                byte[] der = await _http.GetByteArrayAsync(caUrl);
                                issuer = new X509CertificateParser().ReadCertificate(der);
                                allCerts.Add(issuer);
                                AppLogger.Info($"AddLtv: issuer cert downloaded from {caUrl}");
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn($"AddLtv: issuer download failed: {ex.Message}");
                            }
                        }
                    }

                    if (issuer != null)
                    {
                        byte[]? ocsp = await LtvRequestOcsp(cert, issuer, ocspUrl);
                        if (ocsp != null) ocspBlobs.Add(ocsp);
                    }
                }
            }

            if (crlBlobs.Count == 0 && ocspBlobs.Count == 0)
            {
                AppLogger.Warn("AddLtv: no revocation data obtained — PDF returned without DSS");
                return signedPdf;
            }

            AppLogger.Info($"AddLtv: building DSS — {allCerts.Count} cert(s), " +
                           $"{crlBlobs.Count} CRL(s), {ocspBlobs.Count} OCSP(s)");

            // ── 3. Append DSS as incremental update ───────────────────────────
            try
            {
                return LtvBuildDssUpdate(signedPdf, allCerts, crlBlobs, ocspBlobs);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddLtv: DSS build failed: {ex.Message} — returning PDF without DSS");
                return signedPdf;
            }
        }

        // ── AIA / CDP URL extractors ──────────────────────────────────────────

        private static string? LtvGetCdpUrl(BcX509Certificate cert)
        {
            try
            {
                var raw = cert.GetExtensionValue(X509Extensions.CrlDistributionPoints);
                if (raw == null) return null;
                var cdp = CrlDistPoint.GetInstance(Asn1Object.FromByteArray(raw.GetOctets()));
                foreach (var dp in cdp.GetDistributionPoints())
                {
                    if (dp.DistributionPointName?.Type != DistributionPointName.FullName)
                        continue;
                    foreach (GeneralName gn in GeneralNames
                             .GetInstance(dp.DistributionPointName.Name).GetNames())
                    {
                        if (gn.TagNo == GeneralName.UniformResourceIdentifier)
                            return gn.Name.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        // id-ad-ocsp  : 1.3.6.1.5.5.7.48.1
        private static readonly DerObjectIdentifier OidAdOcsp       = new("1.3.6.1.5.5.7.48.1");
        // id-ad-caIssuers: 1.3.6.1.5.5.7.48.2
        private static readonly DerObjectIdentifier OidAdCaIssuers  = new("1.3.6.1.5.5.7.48.2");

        private static string? LtvGetOcspUrl(BcX509Certificate cert)     => LtvGetAiaUrl(cert, OidAdOcsp);
        private static string? LtvGetCaIssuersUrl(BcX509Certificate cert) => LtvGetAiaUrl(cert, OidAdCaIssuers);

        private static string? LtvGetAiaUrl(BcX509Certificate cert, DerObjectIdentifier accessMethod)
        {
            try
            {
                var raw = cert.GetExtensionValue(X509Extensions.AuthorityInfoAccess);
                if (raw == null) return null;
                var aia = AuthorityInformationAccess.GetInstance(
                    Asn1Object.FromByteArray(raw.GetOctets()));
                foreach (var desc in aia.GetAccessDescriptions())
                {
                    if (desc.AccessMethod.Equals(accessMethod) &&
                        desc.AccessLocation.TagNo == GeneralName.UniformResourceIdentifier)
                        return desc.AccessLocation.Name.ToString();
                }
            }
            catch { }
            return null;
        }

        private static BcX509Certificate? LtvFindIssuer(
            BcX509Certificate cert, IList<BcX509Certificate> pool)
        {
            foreach (var c in pool)
                if (!c.Equals(cert) && cert.IssuerDN.Equivalent(c.SubjectDN))
                    return c;
            return null;
        }

        // ── OCSP request ──────────────────────────────────────────────────────

        private static async Task<byte[]?> LtvRequestOcsp(
            BcX509Certificate cert, BcX509Certificate issuer, string url)
        {
            try
            {
#pragma warning disable CS0618 // CertificateID(string,X509Certificate,BigInteger) deprecated in BC 2.x; no non-deprecated equivalent without DigestCalculatorProvider wiring
                var id  = new CertificateID(CertificateID.HashSha1, issuer, cert.SerialNumber);
#pragma warning restore CS0618
                var gen = new OcspReqGenerator();
                gen.AddRequest(id);
                byte[] reqBytes = gen.Generate().GetEncoded();

                using var body = new ByteArrayContent(reqBytes);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");

                var resp = await _http.PostAsync(url, body);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLogger.Warn($"AddLtv: OCSP HTTP {(int)resp.StatusCode} from {url}");
                    return null;
                }
                byte[] ocsp = await resp.Content.ReadAsByteArrayAsync();
                AppLogger.Info($"AddLtv: OCSP {ocsp.Length}B ← {url}");
                return ocsp;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddLtv: OCSP failed ({url}): {ex.Message}");
                return null;
            }
        }

        // ── DSS incremental update builder ────────────────────────────────────

        private static byte[] LtvBuildDssUpdate(
            byte[] pdf,
            List<BcX509Certificate> certs,
            List<byte[]> crls,
            List<byte[]> ocsps)
        {
            string pdfStr = Encoding.Latin1.GetString(pdf);

            // Locate last "startxref N %%EOF"
            var sxm = Regex.Match(pdfStr,
                @"startxref\s+(\d+)\s+%%EOF", RegexOptions.RightToLeft);
            if (!sxm.Success)
                throw new InvalidOperationException("DSS: startxref not found in PDF");
            long prevXref = long.Parse(sxm.Groups[1].Value);

            // Locate last trailer dict → /Root and /Size
            var trm = Regex.Match(pdfStr,
                @"trailer\s*<<([\s\S]+?)>>", RegexOptions.RightToLeft);
            if (!trm.Success)
                throw new InvalidOperationException("DSS: trailer not found in PDF");
            string trDict = trm.Groups[1].Value;

            var rootM = Regex.Match(trDict, @"/Root\s+(\d+)\s+\d+\s+R");
            if (!rootM.Success)
                throw new InvalidOperationException("DSS: /Root not found in trailer");
            int catNum = int.Parse(rootM.Groups[1].Value);

            var szM = Regex.Match(trDict, @"/Size\s+(\d+)");
            if (!szM.Success)
                throw new InvalidOperationException("DSS: /Size not found in trailer");
            int curSize = int.Parse(szM.Groups[1].Value);

            // Extract existing catalog dict content (nested-<< aware)
            string existingCat = LtvExtractDictContent(pdfStr, catNum);
            // Remove any prior /DSS entry before re-adding
            existingCat = Regex.Replace(existingCat,
                @"/DSS\s+\d+\s+\d+\s+R\s*", "", RegexOptions.Singleline).Trim();

            // Assign new object numbers
            int nextNum  = curSize;
            int dssNum   = nextNum++;
            var certNums = certs.Select(_ => nextNum++).ToList();
            var crlNums  = crls.Select(_ => nextNum++).ToList();
            var ocspNums = ocsps.Select(_ => nextNum++).ToList();
            int newSize  = nextNum;

            // Build incremental update
            using var ms = new MemoryStream();
            ms.Write(pdf);
            if (pdf[^1] != '\n') LtvWriteL1(ms, "\n");

            var xref = new Dictionary<int, long>();

            // Stream objects: certs
            for (int i = 0; i < certs.Count; i++)
            { xref[certNums[i]] = ms.Position; LtvWriteStreamObj(ms, certNums[i], certs[i].GetEncoded()); }

            // Stream objects: CRLs
            for (int i = 0; i < crls.Count; i++)
            { xref[crlNums[i]] = ms.Position; LtvWriteStreamObj(ms, crlNums[i], crls[i]); }

            // Stream objects: OCSP responses
            for (int i = 0; i < ocsps.Count; i++)
            { xref[ocspNums[i]] = ms.Position; LtvWriteStreamObj(ms, ocspNums[i], ocsps[i]); }

            // DSS dictionary
            xref[dssNum] = ms.Position;
            var dss = new StringBuilder($"{dssNum} 0 obj\n<<\n/Type /DSS\n");
            if (certNums.Count > 0)
                dss.Append($"/Certs [{string.Join(" ", certNums.Select(n => $"{n} 0 R"))}]\n");
            if (crlNums.Count > 0)
                dss.Append($"/CRLs [{string.Join(" ", crlNums.Select(n => $"{n} 0 R"))}]\n");
            if (ocspNums.Count > 0)
                dss.Append($"/OCSPs [{string.Join(" ", ocspNums.Select(n => $"{n} 0 R"))}]\n");
            dss.Append(">>\nendobj\n");
            LtvWriteL1(ms, dss.ToString());

            // Updated catalog (same object number → overrides previous version via xref)
            xref[catNum] = ms.Position;
            LtvWriteL1(ms, $"{catNum} 0 obj\n<<\n{existingCat}\n/DSS {dssNum} 0 R\n>>\nendobj\n");

            // Cross-reference table
            long xrefOff = ms.Position;
            LtvWriteL1(ms, "xref\n");
            foreach (var (start, offs) in LtvBuildXrefSubsections(xref))
            {
                LtvWriteL1(ms, $"{start} {offs.Count}\n");
                foreach (long off in offs)
                    LtvWriteL1(ms, $"{off:D10} 00000 n \n");  // 20-byte entry (SP LF as EOL)
            }

            // Trailer + startxref + %%EOF
            LtvWriteL1(ms, $"trailer\n<< /Size {newSize} /Root {catNum} 0 R /Prev {prevXref} >>\n");
            LtvWriteL1(ms, $"startxref\n{xrefOff}\n%%EOF\n");

            byte[] result = ms.ToArray();
            AppLogger.Info($"AddLtv: DSS appended ({pdf.Length}B → {result.Length}B)");
            return result;
        }

        /// <summary>
        /// Extracts the inner content of the last "N 0 obj &lt;&lt; ... &gt;&gt; endobj" for the
        /// given object number, using a depth counter to handle nested dictionaries correctly.
        /// </summary>
        private static string LtvExtractDictContent(string pdfStr, int objNum)
        {
            // Find the last occurrence of "N \d obj"
            var m = Regex.Match(pdfStr,
                $@"\b{objNum}\s+\d+\s+obj\b",
                RegexOptions.RightToLeft);
            if (!m.Success) return "/Type /Catalog";

            int pos = m.Index + m.Length;
            // Skip whitespace
            while (pos < pdfStr.Length && pdfStr[pos] <= ' ') pos++;

            if (pos + 1 >= pdfStr.Length || pdfStr[pos] != '<' || pdfStr[pos + 1] != '<')
                return "/Type /Catalog";

            // Walk nested << >> with depth counter
            int depth = 0, end = pos;
            while (end < pdfStr.Length - 1)
            {
                if (pdfStr[end] == '<' && pdfStr[end + 1] == '<')      { depth++; end += 2; }
                else if (pdfStr[end] == '>' && pdfStr[end + 1] == '>') { depth--; end += 2; if (depth == 0) break; }
                else end++;
            }
            // Return content between outer << and >>
            return pdfStr[(pos + 2)..(end - 2)].Trim();
        }

        private static List<(int Start, List<long> Offsets)> LtvBuildXrefSubsections(
            Dictionary<int, long> entries)
        {
            var sorted = entries.OrderBy(kv => kv.Key).ToList();
            var result = new List<(int, List<long>)>();
            List<long>? cur = null;
            int start = 0, prev = -2;
            foreach (var (n, off) in sorted)
            {
                if (cur == null || n != prev + 1) { if (cur != null) result.Add((start, cur)); cur = new(); start = n; }
                cur.Add(off);
                prev = n;
            }
            if (cur != null) result.Add((start, cur));
            return result;
        }

        private static void LtvWriteStreamObj(Stream s, int num, byte[] data)
        {
            LtvWriteL1(s, $"{num} 0 obj\n<< /Length {data.Length} >>\nstream\n");
            s.Write(data);
            LtvWriteL1(s, "\nendstream\nendobj\n");
        }

        private static void LtvWriteL1(Stream s, string text) =>
            s.Write(Encoding.Latin1.GetBytes(text));

        // ----------------------------------------------------------------
        //  PAdES-LTA: document timestamp (RFC 3161 over the whole document)
        // ----------------------------------------------------------------

        /// <summary>
        /// Adds a Document Timestamp (RFC 3161) as a manually-constructed PDF incremental
        /// update, upgrading the PDF to PAdES Baseline-LTA.
        /// Call after <see cref="AddLtv"/> so the DSS is already present in the hash input.
        /// </summary>
        public static async Task<byte[]> AddArchiveTimestamp(byte[] ltPdf)
        {
            AppLogger.Info("PdfCertSigner.AddArchiveTimestamp: adding document timestamp (PAdES-LTA)");
            try
            {
                return await LtaBuildDocTimestamp(ltPdf);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddArchiveTimestamp: failed ({ex.Message}) — returning without LTA");
                return ltPdf;
            }
        }

        /// <summary>
        /// Builds a minimal PDF incremental update containing an RFC 3161 document timestamp.
        /// Bypasses Syncfusion entirely so we control /SubFilter /ETSI.RFC3161 directly.
        /// </summary>
        private static async Task<byte[]> LtaBuildDocTimestamp(byte[] ltPdf)
        {
            // 16 KB slot ample for any RFC 3161 token; hex = 2× = 32 768 chars
            const int SlotBytes = 16_384;
            const int HexLen    = SlotBytes * 2;

            // ── 1. Parse existing PDF ───────────────────────────────────────
            string pdfStr = Encoding.Latin1.GetString(ltPdf);

            var sxm = Regex.Match(pdfStr, @"startxref\s+(\d+)\s+%%EOF", RegexOptions.RightToLeft);
            if (!sxm.Success) throw new InvalidOperationException("LTA: startxref not found");
            long prevXref = long.Parse(sxm.Groups[1].Value);

            var trm = Regex.Match(pdfStr, @"trailer\s*<<([\s\S]+?)>>", RegexOptions.RightToLeft);
            if (!trm.Success) throw new InvalidOperationException("LTA: trailer not found");
            string trDict = trm.Groups[1].Value;

            var rootM = Regex.Match(trDict, @"/Root\s+(\d+)\s+\d+\s+R");
            if (!rootM.Success) throw new InvalidOperationException("LTA: /Root not found");
            int catNum = int.Parse(rootM.Groups[1].Value);

            var szM = Regex.Match(trDict, @"/Size\s+(\d+)");
            if (!szM.Success) throw new InvalidOperationException("LTA: /Size not found");
            int curSize = int.Parse(szM.Groups[1].Value);

            string catContent = LtvExtractDictContent(pdfStr, catNum);

            // ── 2. Assign new object numbers ────────────────────────────────
            int sigNum   = curSize;       // signature dictionary
            int annotNum = curSize + 1;   // widget annotation / field
            int newSize  = curSize + 2;

            // ── 3. Locate existing AcroForm ─────────────────────────────────
            var acroRefM = Regex.Match(catContent, @"/AcroForm\s+(\d+)\s+\d+\s+R");
            int acroNum  = acroRefM.Success ? int.Parse(acroRefM.Groups[1].Value) : 0;

            string acroCore = "";          // AcroForm content minus /Fields
            string existingFields = "";

            if (acroNum > 0)
            {
                string acroContent = LtvExtractDictContent(pdfStr, acroNum);
                var fieldsM = Regex.Match(acroContent, @"/Fields\s*\[([\s\S]*?)\]");
                if (fieldsM.Success) existingFields = fieldsM.Groups[1].Value.Trim();
                acroCore = Regex.Replace(acroContent, @"/Fields\s*\[[\s\S]*?\]", "").Trim();
            }

            string newFieldsList = string.IsNullOrWhiteSpace(existingFields)
                ? $"{annotNum} 0 R"
                : $"{existingFields} {annotNum} 0 R";

            // ── 4. Date string ───────────────────────────────────────────────
            string dateStr = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            // ── 5. Build incremental update ──────────────────────────────────
            // We use a fixed-width ByteRange placeholder (36 chars) so we can
            // patch it in-place after computing the real byte offsets.
            const string BrPlaceholder = "[0 0000000000 0000000000 0000000000]"; // 36 chars

            using var ms = new MemoryStream();
            ms.Write(ltPdf);
            if (ltPdf[^1] != '\n') LtvWriteL1(ms, "\n");

            var xref = new Dictionary<int, long>();

            // ── Signature dictionary ─────────────────────────────────────────
            xref[sigNum] = ms.Position;

            // Write everything up to AND INCLUDING the opening '<' of /Contents
            string sigPrefix =
                $"{sigNum} 0 obj\n" +
                $"<<\n" +
                $"/Type /Sig\n" +
                $"/Filter /Adobe.PPKLite\n" +
                $"/SubFilter /ETSI.RFC3161\n" +
                $"/ByteRange {BrPlaceholder}\n" +
                $"/Contents <";

            LtvWriteL1(ms, sigPrefix);

            // l0 = position of the '<' just written (= current pos − 1)
            long l0 = ms.Position - 1;

            // Write hex zeros — the /Contents placeholder
            byte[] hexZeros = new byte[HexLen];
            Array.Fill(hexZeros, (byte)'0');
            ms.Write(hexZeros);

            // Write closing '>'  →  b1 = position immediately after '>'
            LtvWriteL1(ms, ">");
            long b1 = ms.Position;

            // Rest of signature dictionary
            LtvWriteL1(ms, $"\n/M (D:{dateStr}+00'00')\n>>\nendobj\n");

            // ── Widget annotation / signature field ──────────────────────────
            xref[annotNum] = ms.Position;
            LtvWriteL1(ms,
                $"{annotNum} 0 obj\n" +
                $"<< /FT /Sig /Type /Annot /Subtype /Widget" +
                $" /Rect [0 0 0 0] /F 132" +
                $" /T (ArchiveTimestamp) /V {sigNum} 0 R >>\n" +
                $"endobj\n");

            // ── Updated AcroForm ─────────────────────────────────────────────
            if (acroNum > 0)
            {
                xref[acroNum] = ms.Position;
                LtvWriteL1(ms,
                    $"{acroNum} 0 obj\n" +
                    $"<<\n{acroCore}\n/Fields [{newFieldsList}]\n>>\n" +
                    $"endobj\n");
            }

            // ── Updated catalog ──────────────────────────────────────────────
            // Strip the old /AcroForm reference; we re-add it pointing to the
            // same object number (now updated above) or embed inline.
            string catStripped = Regex.Replace(catContent,
                @"/AcroForm\s+\d+\s+\d+\s+R", "", RegexOptions.Singleline).Trim();

            xref[catNum] = ms.Position;
            if (acroNum > 0)
            {
                LtvWriteL1(ms,
                    $"{catNum} 0 obj\n<<\n{catStripped}\n/AcroForm {acroNum} 0 R\n>>\nendobj\n");
            }
            else
            {
                // AcroForm was inline or absent — embed inline in the updated catalog
                string inlineAcro = $"<< /Fields [{newFieldsList}] >>";
                string catNoAcro  = Regex.Replace(catStripped,
                    @"/AcroForm\s*<<[\s\S]*?>>", "", RegexOptions.Singleline).Trim();
                LtvWriteL1(ms,
                    $"{catNum} 0 obj\n<<\n{catNoAcro}\n/AcroForm {inlineAcro}\n>>\nendobj\n");
            }

            // ── Cross-reference table ────────────────────────────────────────
            long xrefOff = ms.Position;
            LtvWriteL1(ms, "xref\n");
            foreach (var (start, offs) in LtvBuildXrefSubsections(xref))
            {
                LtvWriteL1(ms, $"{start} {offs.Count}\n");
                foreach (long off in offs)
                    LtvWriteL1(ms, $"{off:D10} 00000 n \n");
            }

            // ── Trailer ──────────────────────────────────────────────────────
            LtvWriteL1(ms, $"trailer\n<< /Size {newSize} /Root {catNum} 0 R /Prev {prevXref} >>\n");
            LtvWriteL1(ms, $"startxref\n{xrefOff}\n%%EOF\n");

            byte[] placeholder = ms.ToArray();
            long   l1          = placeholder.Length - b1;

            AppLogger.Info($"LtaBuildDocTimestamp: built {placeholder.Length}B " +
                           $"[0,{l0},{b1},{l1}]");

            // ── 6. Patch /ByteRange in-place ─────────────────────────────────
            // BrPlaceholder and the replacement are both exactly 36 chars.
            string brActual = $"[0 {l0:D10} {b1:D10} {l1:D10}]"; // also 36 chars
            byte[] brOldBytes = Encoding.Latin1.GetBytes(BrPlaceholder);
            byte[] brNewBytes = Encoding.Latin1.GetBytes(brActual);

            bool brFound = false;
            for (int i = ltPdf.Length; i <= placeholder.Length - brOldBytes.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < brOldBytes.Length; j++)
                    if (placeholder[i + j] != brOldBytes[j]) { ok = false; break; }
                if (!ok) continue;
                Buffer.BlockCopy(brNewBytes, 0, placeholder, i, brNewBytes.Length);
                brFound = true;
                break;
            }
            if (!brFound)
                throw new InvalidOperationException("LTA: ByteRange placeholder not found after build");

            // ── 7. Hash the signed regions ───────────────────────────────────
            byte[] toHash = new byte[(int)(l0 + l1)];
            Buffer.BlockCopy(placeholder, 0,       toHash, 0,       (int)l0);
            Buffer.BlockCopy(placeholder, (int)b1, toHash, (int)l0, (int)l1);
            byte[] docHash = DigestUtilities.CalculateDigest("SHA-256", toHash);

            // ── 8. Request TSA token ─────────────────────────────────────────
            byte[]? tsToken = await GetTsaTokenAsync(docHash);
            if (tsToken == null)
            {
                AppLogger.Warn("LtaBuildDocTimestamp: no TSA token — returning without LTA");
                return ltPdf;
            }

            // ── 9. Inject token into /Contents ───────────────────────────────
            byte[] result = InjectCms(placeholder, tsToken, 0L, l0, b1);
            AppLogger.Info($"AddArchiveTimestamp: PAdES-LTA complete " +
                           $"({ltPdf.Length}B → {result.Length}B, token {tsToken.Length}B)");
            return result;
        }

        /// <summary>Sends a SHA-256 hash to a TSA and returns the RFC 3161 token bytes.</summary>
        private static async Task<byte[]?> GetTsaTokenAsync(byte[] sha256Hash)
        {
            if (!TimestampEnabled)
            {
                AppLogger.Info("Timestamping disabled (UseTimestamp=false) – no TSA token requested");
                return null;
            }

            var reqGen = new TimeStampRequestGenerator();
            reqGen.SetCertReq(true);
            TimeStampRequest tsReq = reqGen.Generate(TspAlgorithms.Sha256, sha256Hash);
            byte[] reqBytes = tsReq.GetEncoded();

            foreach (string url in TsaUrls)
            {
                try
                {
                    AppLogger.Info($"AddArchiveTimestamp: TSA → {url}");
                    using var body = new ByteArrayContent(reqBytes);
                    body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
                    var resp = await _http.PostAsync(url, body);
                    if (!resp.IsSuccessStatusCode)
                    {
                        AppLogger.Warn($"AddArchiveTimestamp: TSA HTTP {(int)resp.StatusCode} from {url}");
                        continue;
                    }
                    byte[] respBytes = await resp.Content.ReadAsByteArrayAsync();
                    var tsResp = new TimeStampResponse(respBytes);
                    tsResp.Validate(tsReq);
                    var token = tsResp.TimeStampToken;
                    if (token == null) continue;
                    byte[] tokenBytes = token.GetEncoded();
                    AppLogger.Info($"AddArchiveTimestamp: token {tokenBytes.Length}B from {url}");
                    return tokenBytes;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"AddArchiveTimestamp: TSA {url} failed: {ex.Message}");
                }
            }
            AppLogger.Warn("AddArchiveTimestamp: all TSA URLs failed");
            return null;
        }

        // ----------------------------------------------------------------
        //  PAdES-B-T: add signature-time-stamp to an external CMS
        // ----------------------------------------------------------------

        /// <summary>
        /// Adds an RFC 3161 <c>signature-time-stamp</c> (id-aa-signatureTimeStampToken,
        /// OID 1.2.840.113549.1.9.16.2.14) as an unsigned attribute to the first signer
        /// of <paramref name="cms"/>, upgrading it from PAdES-B-B to PAdES-B-T.
        /// </summary>
        /// <remarks>
        /// The TSA token covers the raw signature value bytes (not the whole document).
        /// This makes the signing time cryptographically verifiable even after the signing
        /// certificate expires.
        /// </remarks>
        public static async Task<byte[]> AddSignatureTimestamp(byte[] cms)
        {
            try
            {
                var cmsData = new CmsSignedData(cms);
                var signers = cmsData.GetSignerInfos().GetSigners().ToList();
                if (signers.Count == 0)
                {
                    AppLogger.Warn("AddSignatureTimestamp: no signer found in CMS");
                    return cms;
                }

                var si = signers[0];

                // Hash the raw signature value (DER-encoded ECDSA/RSA bytes from A-Trust)
                byte[] sigValue = si.GetSignature();
                byte[] sigHash  = DigestUtilities.CalculateDigest("SHA-256", sigValue);

                byte[]? tsToken = await GetTsaTokenAsync(sigHash);
                if (tsToken == null)
                {
                    AppLogger.Warn("AddSignatureTimestamp: no TSA token — CMS stays B-B");
                    return cms;
                }

                // id-aa-signatureTimeStampToken = 1.2.840.113549.1.9.16.2.14
                var tsOid  = new DerObjectIdentifier("1.2.840.113549.1.9.16.2.14");
                var tsAttr = new CmsAttribute(tsOid,
                    new DerSet(Asn1Object.FromByteArray(tsToken)));

                // Merge into existing unsigned attributes (preserve any existing ones)
                // BC 2.x: ToDictionary() replaces the old BC 1.x ToHashtable()
                var attrDict = si.UnsignedAttributes?.ToDictionary()
                               ?? new Dictionary<DerObjectIdentifier, object>();
                attrDict[tsOid] = tsAttr;
                var newTable = new CmsAttributeTable(attrDict);

                // Rebuild the CMS with the updated signer
                var updatedSi  = SignerInformation.ReplaceUnsignedAttributes(si, newTable);
                var newSiStore = new SignerInformationStore(new[] { updatedSi });
                var newCms     = CmsSignedData.ReplaceSigners(cmsData, newSiStore);

                byte[] result = newCms.GetEncoded();
                AppLogger.Info($"AddSignatureTimestamp: B-B → B-T " +
                               $"(token {tsToken.Length}B, CMS {cms.Length}B → {result.Length}B)");
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddSignatureTimestamp: failed ({ex.Message}) — keeping original CMS");
                return cms;
            }
        }

        // ----------------------------------------------------------------
        //  Private: cert + key export to PEM (kept but not called)
        // ----------------------------------------------------------------

        private static void SaveCertAndKey(
            BcX509Certificate cert, AsymmetricKeyParameter privateKey,
            string pdfPath, string signerName)
        {
            string safeName = signerName;
            foreach (char c in SysPath.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');
            safeName = safeName.Trim().Replace(' ', '_');

            string dir     = SysPath.GetDirectoryName(pdfPath) ?? ".";
            string crtPath = SysPath.Combine(dir, safeName + ".crt");
            string keyPath = SysPath.Combine(dir, safeName + ".key");

            using (var sw = new StreamWriter(crtPath))
                new PemWriter(sw).WriteObject(cert);

            using (var sw = new StreamWriter(keyPath))
                new PemWriter(sw).WriteObject(privateKey);
        }

        // ----------------------------------------------------------------
        //  RFC 3161 timestamping
        // ----------------------------------------------------------------

        private static byte[]? TryGetTimestampToken(byte[] signatureBytes)
        {
            if (!TimestampEnabled)
            {
                AppLogger.Info("    Timestamping disabled (UseTimestamp=false) – signature stays PAdES Baseline-B");
                return null;
            }
            AppLogger.Debug("    Requesting RFC 3161 timestamp token");
            byte[] sigHash = DigestUtilities.CalculateDigest("SHA-256", signatureBytes);
            var reqGen = new TimeStampRequestGenerator();
            reqGen.SetCertReq(true);
            TimeStampRequest tsReq = reqGen.Generate(TspAlgorithms.Sha256, sigHash);
            byte[] reqEncoded = tsReq.GetEncoded();

            foreach (string url in TsaUrls)
            {
                try
                {
                    AppLogger.Debug($"    TSA request → {url}");
                    var content = new ByteArrayContent(reqEncoded);
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/timestamp-query");
                    var resp = _http.PostAsync(url, content).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                    {
                        AppLogger.Warn($"    TSA {url} returned HTTP {(int)resp.StatusCode} – trying next");
                        continue;
                    }
                    byte[] respBytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    var tsResp = new TimeStampResponse(respBytes);
                    tsResp.Validate(tsReq);
                    TimeStampToken token = tsResp.TimeStampToken;
                    if (token == null)
                    {
                        AppLogger.Warn($"    TSA {url} returned null token – trying next");
                        continue;
                    }
                    AppLogger.Info($"    Timestamp token obtained from {url}");
                    return token.GetEncoded();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"    TSA {url} failed: {ex.Message}");
                }
            }
            AppLogger.Warn("    No timestamp token obtained – signature will be PAdES Baseline-B only");
            return null;
        }

        // ----------------------------------------------------------------
        //  Biometric data serialisation
        // ----------------------------------------------------------------

        private static byte[] SerializeBioData(IReadOnlyList<BiometricPoint> points)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            bw.Write(points.Count);
            foreach (var p in points) { bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Pressure); bw.Write(p.TimestampMs); }
            return ms.ToArray();
        }

        private static BiometricData DeserializeBioData(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            long tsMs  = br.ReadInt64();
            int  count = br.ReadInt32();
            var pts    = new List<BiometricPoint>(count);
            for (int i = 0; i < count; i++)
                pts.Add(new BiometricPoint(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                    ms.Position < ms.Length ? br.ReadSingle() : 0f)); // backward-compatible: old data has no TimestampMs
            return new BiometricData(DateTimeOffset.FromUnixTimeMilliseconds(tsMs), pts);
        }

        // ----------------------------------------------------------------
        //  Hybrid encryption / decryption (RSA-OAEP + AES-CBC)
        // ----------------------------------------------------------------

        private static byte[] HybridEncrypt(byte[] data, RsaKeyParameters pubKey, SecureRandom rng)
        {
            var aesKey = new byte[32]; var iv = new byte[16];
            rng.NextBytes(aesKey); rng.NextBytes(iv);
            var aesCipher = CipherUtilities.GetCipher("AES/CBC/PKCS7Padding");
            aesCipher.Init(true, new ParametersWithIV(new KeyParameter(aesKey), iv));
            byte[] encData = aesCipher.DoFinal(data);
            var oaep = new OaepEncoding(new RsaEngine());
            oaep.Init(true, new ParametersWithRandom(pubKey, rng));
            byte[] encKey = oaep.ProcessBlock(aesKey, 0, aesKey.Length);
            using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
            bw.Write(encKey.Length); bw.Write(encKey); bw.Write(iv); bw.Write(encData);
            return ms.ToArray();
        }

        private static byte[] HybridDecrypt(byte[] encrypted, RsaKeyParameters privateKey)
        {
            using var ms = new MemoryStream(encrypted); using var br = new BinaryReader(ms);
            int encKeyLen = br.ReadInt32(); byte[] encKey = br.ReadBytes(encKeyLen);
            byte[] iv     = br.ReadBytes(16);
            byte[] encData = br.ReadBytes((int)(ms.Length - ms.Position));
            var oaep = new OaepEncoding(new RsaEngine());
            oaep.Init(false, privateKey);
            byte[] aesKey = oaep.ProcessBlock(encKey, 0, encKey.Length);
            var aesCipher = CipherUtilities.GetCipher("AES/CBC/PKCS7Padding");
            aesCipher.Init(false, new ParametersWithIV(new KeyParameter(aesKey), iv));
            return aesCipher.DoFinal(encData);
        }
    }
}