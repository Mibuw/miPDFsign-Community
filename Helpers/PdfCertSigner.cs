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

using MiPdf;
using MiPdf.Images;
using MiPdf.Signatures;

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
        // (November 2026: PDF side on miPDFlib — deferred signing with our own BouncyCastle CMS)
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
        /// range [0..PdfW] × [0..PdfH]. They are drawn into the signature appearance with
        /// miPDFlib's <see cref="InkRenderer"/> as transparent vector paths (no opaque PNG needed).
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
        // Both are freely usable, EU-qualified (eIDAS) timestamp authorities on the EU Trusted List,
        // so the resulting PAdES-T/LTA timestamps validate as qualified in the EU DSS validator.
        private static readonly string[] DefaultTsaUrls =
        {
            "http://tsa.belgium.be/connect",                            // BOSA (BE, QTSP: FPS Policy and Support), SHA-256
            "http://tss.accv.es:8318/tsa",                              // ACCV (ES, Generalitat Valenciana), SHA-256
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
            CertPair certPair,
            RsaKeyParameters? bioEncryptionKey = null)
        {
            if (fields.Count == 0) return Array.Empty<byte>();
            AppLogger.Info($"PdfCertSigner.SignFields: signing {fields.Count} field(s) in '{SysPath.GetFileName(pdfPath)}'");

            // SaveCertAndKey(certPair.Cert, certPair.KeyPair.Private, pdfPath, signerName);

            byte[] lastCms = Array.Empty<byte>();
            foreach (var req in fields)
            {
                AppLogger.Info($"  Signing field '{req.FieldName}' (page {req.PageNum}, bio points: {req.BioPoints.Count})");
                lastCms = SignFieldIncremental(pdfPath, req, certPair.KeyPair, certPair.Cert, bioEncryptionKey);
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
        //  Strategy (miPDFlib deferred signing):
        //    PrepareExternalSignature appends an incremental revision with the signature
        //    field, its appearance and an empty /Contents slot of CmsSlotSize bytes.
        //    We build the full PAdES CMS (BouncyCastle: biometric OID, ESS, TSA) over the
        //    byte-range content and CompleteExternalSignature writes it into /Contents.
        // ----------------------------------------------------------------

        private static byte[] SignFieldIncremental(
            string pdfPath,
            FieldSignRequest req,
            AsymmetricCipherKeyPair keyPair,
            BcX509Certificate cert,
            RsaKeyParameters? bioEncryptionKey = null)
        {
            AppLogger.Debug($"    SignFieldIncremental: field='{req.FieldName}' " +
                $"page={req.PageNum} rect=({req.PdfX:F0},{req.PdfY:F0},{req.PdfW:F0},{req.PdfH:F0})");

            var random = new SecureRandom();
            byte[] rawBio = SerializeBioData(req.BioPoints);
            // Encrypt the biometric data with the dedicated persistent biometric key when
            // supplied (so it can be decrypted later); fall back to the signing key otherwise.
            var bioPubKey = bioEncryptionKey ?? (RsaKeyParameters)keyPair.Public;
            byte[] encBio = HybridEncrypt(rawBio, bioPubKey, random);
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

                // miPDFlib always signs as an incremental update: the original bytes stay
                // untouched, so the /ByteRange of every previously-applied signature still
                // points at unchanged bytes and stays valid.
                var (finalPdf, realCms) = MiPdfSign(
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
        //  MiPdfSign — every field (incremental update, own CMS)
        // ----------------------------------------------------------------

        private static (byte[] finalPdf, byte[] cms) MiPdfSign(
            byte[] origBytes,
            FieldSignRequest req,
            string signerCn,
            AsymmetricCipherKeyPair keyPair,
            BcX509Certificate cert,
            byte[] encBio,
            SecureRandom random)
        {
            var options = SignatureOptions(origBytes, req, "Tablet signature", signerCn);
            AppLogger.Debug($"    MiPdfSign: field='{req.FieldName}' rect={options.Rectangle} (PAdES, incremental)");

            // Step 1: incremental revision with field, appearance and an empty /Contents slot.
            // Format = PAdES → /SubFilter /ETSI.CAdES.detached.
            var prepared = PdfSigner.PrepareExternalSignature(
                origBytes, options, certificate: null, containerSize: CmsSlotSize);

            // Step 2: our own PAdES CMS over the byte-range content (biometric attribute + TSA).
            byte[] toSign   = prepared.GetSignedData();
            byte[] builtCms = BuildPadesCms(toSign, keyPair.Private, cert, encBio, random);
            AppLogger.Info($"    MiPdfSign: signing {toSign.Length}B → CMS {builtCms.Length}B");

            // Step 3: embed the CMS into /Contents.
            byte[] finalPdf = PdfSigner.CompleteExternalSignature(prepared, builtCms);

            AppLogger.Info($"    MiPdfSign done: {finalPdf.Length}B");
            return (finalPdf, builtCms);
        }

        /// <summary>
        /// Signature options for <paramref name="req"/>: field name, page, visible rectangle
        /// and the transparent ink appearance.
        /// </summary>
        private static PdfSignatureOptions SignatureOptions(
            byte[] pdf, FieldSignRequest req, string reason, string? signerName)
        {
            // req.PdfX/PdfY are bottom-left PDF points relative to the visible page (CropBox);
            // miPDFlib expects absolute user-space coordinates.
            PdfRectangle crop;
            using (var doc = PdfDocument.Open(pdf))
                crop = doc.GetPage(req.PageNum).CropBox;

            return new PdfSignatureOptions
            {
                FieldName        = req.FieldName,
                PageNumber       = req.PageNum,
                Rectangle        = PdfRectangle.FromSize(
                    crop.Left + req.PdfX, crop.Bottom + req.PdfY, req.PdfW, req.PdfH),
                Reason           = reason,
                Location         = "POS",
                SignerName       = signerName,
                Format           = PdfSignatureFormat.PAdES,
                SignatureCreator = "miPDFsign Community",
                Appearance       = BuildAppearance(req),
            };
        }

        // ----------------------------------------------------------------
        //  Signature appearance — transparent vector ink
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds the signature appearance. When <see cref="FieldSignRequest.AppearanceStrokes"/>
        /// is present the strokes are rendered as dark-blue, pressure-sensitive vector paths on a
        /// fully transparent background (the underlying document text remains visible).
        /// Otherwise it falls back to the legacy PNG image or draws nothing (empty field).
        /// </summary>
        private static PdfSignatureAppearance BuildAppearance(FieldSignRequest req)
        {
            var strokes = req.AppearanceStrokes?
                .Where(s => s != null && s.Count > 0)
                .Select(s => (IReadOnlyList<InkPoint>)s
                    .Select(p => new InkPoint(p.X, p.Y, p.Pressure))
                    .ToList())
                .ToList();
            PdfImage? png = req.AppearancePng is { Length: > 0 } bytes ? PdfImage.FromBytes(bytes) : null;

            return new PdfSignatureAppearance
            {
                Text = "",   // ink only — no "Signed by" text strip
                Draw = (canvas, w, h) =>
                {
                    if (strokes is { Count: > 0 })
                        // Appearance-box coordinates have a top-left origin; 0.35..2.4 pt
                        // stroke width by pressure, ink colour = WPF Colors.DarkBlue.
                        InkRenderer.Draw(canvas, strokes, minWidth: 0.35, maxWidth: 2.4,
                            topLeftHeight: h);
                    else if (png != null)
                        canvas.DrawImage(png, 0, 0, w, h);   // legacy fallback
                },
            };
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            { count++; idx += needle.Length; }
            return count;
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
        //  Both steps are miPDFlib's deferred signing (PrepareExternalSignature /
        //  CompleteExternalSignature).
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

            // The /Contents slot is CmsSlotSize bytes: A-Trust's CMS (with certificate chain)
            // plus the signature-time-stamp added afterwards must fit into it.
            byte[] origBytes = File.ReadAllBytes(pdfPath);
            var options  = SignatureOptions(origBytes, req, "QES via ID-Austria", signerName: null);
            var prepared = PdfSigner.PrepareExternalSignature(
                origBytes, options, certificate: null, containerSize: CmsSlotSize);

            placeholderPdf  = prepared.PreparedPdf;
            byte[] captured = prepared.GetSignedData();

            var br = prepared.ByteRange;
            AppLogger.Info($"  PrepareQesSigning: captured {captured.Length} bytes-to-sign, " +
                           $"placeholder PDF {placeholderPdf.Length} bytes, " +
                           $"ByteRange [{br[0]},{br[1]},{br[2]},{br[3]}], slot {prepared.ContainerSize}B");
            return captured;
        }

        /// <summary>
        /// Writes the real <paramref name="cms"/> blob into the /Contents slot of the
        /// signature field <paramref name="fieldName"/> in <paramref name="placeholderPdf"/>
        /// and returns the completed PDF bytes.
        /// </summary>
        public static byte[] InjectQesCms(byte[] placeholderPdf, string fieldName, byte[] cms)
        {
            byte[] result = PdfSigner.CompleteExternalSignature(placeholderPdf, fieldName, cms);
            AppLogger.Info($"InjectQesCms: done ({result.Length}B, CMS {cms.Length}B)");
            return result;
        }

        // ----------------------------------------------------------------
        //  PAdES-LT / LTA via miPDFlib (PdfLtv)
        // ----------------------------------------------------------------

        // One revocation source for the process: caches CRLs, OCSP responses and AIA issuers.
        // CRLs above 5 MB (e.g. A-Trust's 143 MB CRL) are skipped; OCSP is used instead.
        private static readonly HttpRevocationSource _revocation = new();

        /// <summary>
        /// Upgrades the signatures of <paramref name="signedPdf"/> to PAdES Baseline-LT by
        /// appending a Document Security Store (DSS) as an unsigned incremental update.
        /// The DSS embeds the certificates (signer + TSA chains, missing issuers via AIA) and
        /// their OCSP responses / CRLs, so the signatures can be validated offline even after
        /// the signing certificate expires. Never fails: on any problem the input is returned.
        /// </summary>
        public static Task<byte[]> AddLtv(byte[] signedPdf) => Task.Run(() =>
        {
            AppLogger.Info("PdfCertSigner.AddLtv: fetching revocation data for PAdES-LT");
            try
            {
                var lt = PdfLtv.AddValidationData(signedPdf, new LtvOptions { RevocationSource = _revocation });
                LogLtvWarnings("AddLtv", lt.Warnings);
                AppLogger.Info($"AddLtv: DSS — {lt.CertificatesAdded} cert(s), {lt.OcspResponsesAdded} OCSP(s), " +
                               $"{lt.CrlsAdded} CRL(s), complete={lt.IsComplete} ({signedPdf.Length}B → {lt.Pdf.Length}B)");
                return lt.Pdf;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddLtv: DSS build failed: {ex.Message} — returning PDF without DSS");
                return signedPdf;
            }
        });

        /// <summary>
        /// Adds a Document Timestamp (RFC 3161, /SubFilter /ETSI.RFC3161) plus the validation
        /// data of its TSA chain, upgrading the PDF to PAdES Baseline-LTA.
        /// Call after <see cref="AddLtv"/> so the DSS is already covered by the timestamp.
        /// </summary>
        public static Task<byte[]> AddArchiveTimestamp(byte[] ltPdf) => Task.Run(() =>
        {
            if (!TimestampEnabled)
            {
                AppLogger.Info("AddArchiveTimestamp: timestamping disabled (UseTimestamp=false) – no LTA");
                return ltPdf;
            }

            AppLogger.Info("PdfCertSigner.AddArchiveTimestamp: adding document timestamp (PAdES-LTA)");
            try
            {
                var tsa = FailoverTimestampClient.FromUrls(TsaUrls);
                var lta = PdfLtv.MakeLta(ltPdf, tsa, new LtvOptions { RevocationSource = _revocation });
                LogLtvWarnings("AddArchiveTimestamp", lta.Warnings);
                if (lta.Level != PadesLevel.BLTA)
                {
                    AppLogger.Warn("AddArchiveTimestamp: no document timestamp obtained — returning without LTA");
                    return ltPdf;
                }
                AppLogger.Info($"AddArchiveTimestamp: PAdES-LTA complete ({ltPdf.Length}B → {lta.Pdf.Length}B)");
                return lta.Pdf;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"AddArchiveTimestamp: failed ({ex.Message}) — returning without LTA");
                return ltPdf;
            }
        });

        private static void LogLtvWarnings(string step, IReadOnlyList<string> warnings)
        {
            foreach (var w in warnings)
                AppLogger.Warn($"{step}: {w}");
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
