using System;
using System.Configuration;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Manages the RSA certificate used to encrypt biometric signature data.
    ///
    /// Priority:
    ///   1. If <c>BiometricCertPath</c> (App.config) points to an existing PFX → load it.
    ///   2. Otherwise: generate a fresh RSA-3072 self-signed cert, export as PFX next to
    ///      <paramref name="outputPdfDir"/> and update the runtime setting so the same key is
    ///      reused within the session (App.config is NOT written back).
    /// </summary>
    public static class BiometricCertManager
    {
        // Cached key pair for the current process lifetime
        private static AsymmetricCipherKeyPair? _keyPair;
        private static BcX509Certificate?       _cert;
        private static readonly object          _lock = new();

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the RSA public key used to encrypt biometric data.
        /// Generates and caches a key pair if needed.
        /// </summary>
        public static RsaKeyParameters GetEncryptionPublicKey(string? outputPdfDir = null)
        {
            EnsureKeyPair(outputPdfDir);
            return (RsaKeyParameters)_keyPair!.Public;
        }

        /// <summary>
        /// Returns the RSA private key for decrypting / verifying biometric data (forensic use).
        /// </summary>
        public static RsaKeyParameters? GetDecryptionPrivateKey()
        {
            if (_keyPair == null) return null;
            return (RsaKeyParameters)_keyPair.Private;
        }

        // ----------------------------------------------------------------
        //  Internal
        // ----------------------------------------------------------------

        private static void EnsureKeyPair(string? outputPdfDir)
        {
            lock (_lock)
            {
                if (_keyPair != null) return;

                string certPath = ConfigurationManager.AppSettings["BiometricCertPath"] ?? "";
                string certPass = ConfigurationManager.AppSettings["BiometricCertPassword"] ?? "";

                if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath))
                {
                    LoadFromPfx(certPath, certPass);
                    AppLogger.Info($"BiometricCertManager: loaded cert from '{certPath}'");
                }
                else
                {
                    GenerateAndSave(outputPdfDir);
                }
            }
        }

        private static void LoadFromPfx(string pfxPath, string password)
        {
            // Use .NET's X509Certificate2 to read the PFX, then convert to BouncyCastle
            var cert2 = new X509Certificate2(pfxPath, password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

            // Export public certificate bytes → BouncyCastle parser
            var parser = new X509CertificateParser();
            _cert = parser.ReadCertificate(cert2.RawData);

            // Export private key → BouncyCastle (requires RSA)
            using var rsa = cert2.GetRSAPrivateKey()
                ?? throw new InvalidOperationException(
                    "BiometricCertManager: PFX does not contain an RSA private key.");

            RSAParameters rsaParams = rsa.ExportParameters(includePrivateParameters: true);
            _keyPair = new AsymmetricCipherKeyPair(
                DotNetUtilities.GetRsaPublicKey(rsa),
                DotNetUtilities.GetRsaKeyPair(rsaParams).Private);
        }

        private static void GenerateAndSave(string? outputPdfDir)
        {
            AppLogger.Info("BiometricCertManager: no cert configured – generating RSA-3072 self-signed cert");

            var random = new SecureRandom();
            var keyGen = new RsaKeyPairGenerator();
            keyGen.Init(new KeyGenerationParameters(random, 3072));
            _keyPair = keyGen.GenerateKeyPair();

            var certGen = new X509V3CertificateGenerator();
            var dn = new X509Name("CN=miPDFsign Biometric Key");
            certGen.SetIssuerDN(dn);
            certGen.SetSubjectDN(dn);
            certGen.SetSerialNumber(new BigInteger(128, random));
            certGen.SetNotBefore(DateTime.UtcNow.AddSeconds(-5));
            certGen.SetNotAfter(DateTime.UtcNow.AddYears(20));
            certGen.SetPublicKey(_keyPair.Public);
            _cert = certGen.Generate(
                new Asn1SignatureFactory("SHA256WITHRSA", _keyPair.Private, random));

            // Export to PKCS#12 / PFX
            var store = new Pkcs12StoreBuilder().Build();
            var certEntry = new X509CertificateEntry(_cert);
            store.SetCertificateEntry("biometric", certEntry);
            store.SetKeyEntry("biometric", new AsymmetricKeyEntry(_keyPair.Private),
                new[] { certEntry });

            string saveDir = !string.IsNullOrWhiteSpace(outputPdfDir) && Directory.Exists(outputPdfDir)
                ? outputPdfDir
                : AppDomain.CurrentDomain.BaseDirectory;

            string pfxPath = Path.Combine(saveDir, "biometric_key.pfx");
            using (var fs = new FileStream(pfxPath, FileMode.Create, FileAccess.Write))
                store.Save(fs, Array.Empty<char>(), random);

            AppLogger.Info($"BiometricCertManager: biometric PFX saved to '{pfxPath}'");
        }
    }
}
