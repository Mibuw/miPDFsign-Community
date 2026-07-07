<p align="center">
  <img src="Assets/miPDFsign_logo.png" width="120" alt="miPDFsign logo">
</p>

# miPDFsign Community

**miPDFsign Community** is the free, open-source (**AGPL-3.0**) edition of miPDFsign — a .NET 8 WPF application for signature tablets. It captures handwritten signatures together with biometric pressure data and embeds them as a **PAdES signature** into PDF documents. It uses the **iText** PDF engine (AGPL).

---

## Features

- **Handwritten signature capture** on all pen-based, Windows-Ink-capable devices (tablets, notebooks, Surface, etc.) including pressure and timing data – no Wintab driver required
- **Biometric data** is hybrid-encrypted (RSA-OAEP 3072 bit + AES-256-CBC) and embedded in the PDF in a proprietary format
- **PAdES signatures** at levels Baseline-**B / T / LT / LTA**
- **Multi-signature** per PDF (freehand mode) via incremental iText updates – existing signatures remain valid
- **QES support** via ID Austria / A-Trust
- **PDF rendering** of pages as bitmaps (PDFium) for display and field placement
- **PDF form detection**: signature, date, location, and checkbox fields are read out automatically
- **Configurable UI texts** via `miPDFsign.ui-labels.json` – editable without recompilation

---

## Technical Details

| Property | Value |
|---|---|
| Framework | .NET 8 (`net8.0-windows`, WinExe) |
| Platform | x86 (`PlatformTarget = x86`) |
| UI | WPF (`UseWPF = true`) |
| Namespace | `miPDFsign` |

### Libraries Used

| Package | Purpose |
|---|---|
| `itext` (+ `itext.bouncy-castle-adapter`) | PDF forms, stamping, PAdES signing (AGPL) |
| `BouncyCastle.Cryptography` | Custom CMS attributes, biometrics encryption |
| `PdfiumViewer` (+ native x86/x64) | Render PDF pages as bitmaps |
| `System.Drawing.Common` | Bitmap processing |
| `System.Configuration.ConfigurationManager` | `App.config` / appSettings |

---

## Project Structure

```
miPDFsign/
├── App.xaml / App.xaml.cs          # Entry point, Syncfusion license registration
├── MainWindow.xaml / .cs           # Main window
├── IdAustriaWindow.xaml / .cs      # QES signature via ID Austria
├── SignatureTypeDialog.xaml / .cs  # Signature type selection
├── miPDFsign.ui-labels.json           # UI texts (editable without recompile)
├── Assets/                         # Icon, logos
├── Helpers/
│   ├── PdfCertSigner.cs            # PDF signing (PAdES B/T/LT/LTA, core component)
│   ├── PdfExporter.cs              # Export / merge PDF
│   ├── PdfLoadHelper.cs            # Load PDF, determine page size
│   ├── PdfRenderer.cs              # PDF pages as bitmap via PDFium
│   ├── PdfSignatureScanner.cs      # Read signature fields from PDF
│   ├── UiLabels.cs                 # UI label management
│   └── AppLogger.cs                # Central logging
├── Models/                         # Field descriptors (signature, date, location, checkbox)
└── Setup/                          # Inno Setup script & build scripts
```

---

## Signature Architecture (Brief Overview)

All signature fields are signed via **a single** standard-compliant Syncfusion path
(`PdfLoadedDocument` with `FileStructure.IncrementalUpdate = true`). The CMS structure
is built in the `ComputeHash` event with BouncyCastle (`/SubFilter /ETSI.CAdES.detached`,
SHA-256). Signature images are drawn into the appearance as transparent vector strokes.

**PAdES levels:**

| Level | Description |
|---|---|
| Baseline-B | Always – base CMS signature |
| Baseline-T | RFC-3161 timestamp (TSA, best-effort) |
| LT (LTV) | CRL + OCSP embedded for all certificates (DSS) |
| LTA | Document timestamp over the entire PDF |

> For implementation details, coordinate conversion, TSA configuration, and
> migration history, see [`CLAUDE.md`](./CLAUDE.md).

---

## Build & Deployment

```bash
# Debug build
dotnet build -c Debug

# Release / Publish (self-contained, win-x86, multi-file)
dotnet publish -c Release
```

The publish is **self-contained** (no .NET runtime required on the target machine)
and consists of `miPDFsign.exe` + `miPDFsign.dll` together with all dependencies in one
folder. The installer is generated via the Inno Setup script under `Setup/`
(see `Setup/build.bat`).

---

## Development Environment

- **Visual Studio** (recommended for the XAML designer)
- **VS Code** with the *VS Code Tools for WPF* possible

---

## License

miPDFsign Community is licensed under the **GNU Affero General Public License v3.0**
(AGPL-3.0) — see [`LICENSE`](./LICENSE). Because it links **iText** under the AGPL, the
entire application is AGPL: if you distribute it (or offer it over a network), you must
make the complete corresponding source available under the same license.

Building a **closed-source / commercial** product on this code base requires a
**commercial iText license** instead. The commercial, Syncfusion-based edition of
miPDFsign is a separate product.

Third-party components and their licenses are documented in
[`THIRD-PARTY-NOTICES.md`](./THIRD-PARTY-NOTICES.md).
