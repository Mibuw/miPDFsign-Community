using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Bilingual UI label provider (English / German).
    ///
    /// The active language is chosen from the operating-system UI culture
    /// (<see cref="CultureInfo.CurrentUICulture"/>): German when the two-letter ISO
    /// language is <c>"de"</c>, English otherwise. <b>English is always the fallback</b>
    /// when a German string is missing.
    ///
    /// Built-in English defaults live inline in each property; German strings live in the
    /// <see cref="_de"/> dictionary. Optionally, an external override file next to the
    /// executable can replace individual strings without recompiling:
    /// <c>miPDFsign.ui-labels.en.json</c> or <c>miPDFsign.ui-labels.de.json</c>
    /// (a legacy <c>miPDFsign.ui-labels.json</c> is still honoured as a last resort).
    /// </summary>
    public static class UiLabels
    {
        // ----------------------------------------------------------------
        //  State
        // ----------------------------------------------------------------
        private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
        private static bool _useGerman;
        private static bool _loaded;

        // ----------------------------------------------------------------
        //  Loading
        // ----------------------------------------------------------------

        /// <summary>
        /// Detects the active language and loads the matching external override file
        /// (if present). When <paramref name="filePath"/> is <c>null</c>, the file is looked
        /// up in <see cref="AppContext.BaseDirectory"/> as
        /// <c>miPDFsign.ui-labels.&lt;lang&gt;.json</c>. Safe to call multiple times.
        /// </summary>
        public static void Load(string? filePath = null)
        {
            _useGerman = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("de", StringComparison.OrdinalIgnoreCase);
            string lang = _useGerman ? "de" : "en";

            filePath ??= Path.Combine(AppContext.BaseDirectory, $"miPDFsign.ui-labels.{lang}.json");

            _overrides =
                TryLoadJson(filePath)
                // legacy single-file fallback (pre-bilingual)
                ?? TryLoadJson(Path.Combine(AppContext.BaseDirectory, "miPDFsign.ui-labels.json"))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            AppLogger.Info($"UiLabels: language '{lang}' active, {_overrides.Count} external override(s)");
            _loaded = true;
        }

        private static Dictionary<string, string>? TryLoadJson(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json,
                    new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip });
                return parsed != null
                    ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                    : null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"UiLabels: could not parse '{filePath}': {ex.Message} – ignoring");
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  Lookup
        //
        //  Priority:  external override  →  German (if active)  →  English default
        // ----------------------------------------------------------------
        private static string Get(string key, string en)
        {
            if (!_loaded) Load();
            if (_overrides.TryGetValue(key, out var ov) && !string.IsNullOrEmpty(ov))
                return ov;
            if (_useGerman && _de.TryGetValue(key, out var de) && !string.IsNullOrEmpty(de))
                return de;
            return en;
        }

        // ----------------------------------------------------------------
        //  German strings (English lives inline as the Get default = fallback)
        // ----------------------------------------------------------------
        private static readonly Dictionary<string, string> _de = new(StringComparer.OrdinalIgnoreCase)
        {
            // Toolbar
            ["BtnNextSig"] = "Nächste Signatur",
            ["BtnClear"]   = "Löschen",
            ["BtnSave"]    = "Speichern",

            // Status bar
            ["StatusLoading"]          = "⏳  Lade Dokument …",
            ["StatusSaving"]           = "⏳  Wird gespeichert …",
            ["SaveDialogTitle"]        = "Signiertes Dokument speichern unter",
            ["SaveDialogFilter"]       = "PDF-Dokument (*.pdf)|*.pdf",
            ["StatusMissingRequired1"] = "⚠  1 Pflichtfeld noch nicht signiert",
            ["StatusMissingRequiredN"] = "⚠  {0} Pflichtfelder noch nicht signiert",
            ["StatusFreehandHint"]     = "Bitte auf dem Dokument unterschreiben",
            ["StatusQesAborted"]            = "ID-Austria Signatur abgebrochen.",
            ["StatusQesAddingTimestamp"]    = "Signatur-Zeitstempel wird hinzugefügt…",
            ["StatusQesFetchingRevocation"] = "Revokierungsdaten werden abgefragt…",
            ["StatusQesAddingArchive"]      = "Archiv-Zeitstempel wird erstellt…",

            // ID-Austria window
            ["IdAustriaWindowTitle"]     = "ID-Austria Signatur",
            ["IdAustriaInstruction"]     = "Bitte mit ID-Austria in der folgenden Ansicht unterschreiben.",
            ["IdAustriaConnecting"]      = "Verbinde mit A-Trust…",
            ["IdAustriaSubmitting"]      = "Formulardaten werden übermittelt…",
            ["IdAustriaProcessing"]      = "Signatur wird verarbeitet…",
            ["IdAustriaSuccess"]         = "✓ Signatur erfolgreich – Dokument wird gespeichert…",
            ["IdAustriaUrlOpened"]       = "Geöffnet: {0}",
            ["IdAustriaStartError"]      = "Fehler beim Starten der ID-Austria Signatur:\n{0}",
            ["IdAustriaStartErrorTitle"] = "ID-Austria Fehler",
            ["IdAustriaParseError"]      = "Die Signaturantwort konnte nicht gelesen werden.",
            ["IdAustriaDecodeError"]     = "CMS-Signatur konnte nicht dekodiert werden.",
            ["IdAustriaErrorCode"]       = "A-Trust Fehlercode: {0}",
            ["IdAustriaNotRecognized"]   = "Die ID-Austria Signatur wurde nicht erkannt.\n{0}",
            ["IdAustriaNoSignature"]     = "Kein CMSSignature-Element in der Antwort gefunden.",
            ["IdAustriaBtnCancel"]       = "Abbrechen",

            // Signature-type dialog
            ["DlgSigTypeTitle"]        = "Signaturtyp wählen",
            ["DlgSigTypeHeader"]       = "Signaturtyp wählen",
            ["DlgSigTypeQesSub"]       = "Qualifizierte Elektronische Signatur via A-Trust Security Layer",
            ["DlgSigTypeFesTitle"]     = "Selbstsigniert (FES)",
            ["DlgSigTypeFesSub"]       = "Fortgeschrittene Signatur mit selbstsigniertem Zertifikat",
            ["DlgSigTypeFesNameLabel"] = "Name des Unterzeichners:",
            ["DlgSigTypeBtnCancel"]    = "Abbrechen",
            ["DlgSigTypeBtnOk"]        = "Weiter  →",

            // Signature-field overlays
            ["SigLabelActive"] = "✎  Jetzt unterschreiben",
            ["SigBtnSign"]     = "✎  Unterschreiben",
            ["SigBtnConfirm"]  = "✓  Fertig",
            ["SigBtnRetry"]    = "↺  Nochmal",
            ["SigBtnCancel"]   = "✕  Abbrechen",

            // Message boxes
            ["MsgLoadError"]          = "PDF konnte nicht geladen werden:\n{0}",
            ["MsgLoadErrorTitle"]     = "Fehler",
            ["MsgMissingFields"]      = "Folgende Pflichtfelder wurden noch nicht signiert:\n{0}",
            ["MsgMissingFieldsTitle"] = "Pflichtfelder fehlen",
            ["MsgSaveError"]          = "Fehler beim Speichern:\n{0}",
            ["MsgSaveErrorTitle"]     = "Fehler",
        };

        // ----------------------------------------------------------------
        //  Title bar
        // ----------------------------------------------------------------
        public static string AppTitle          => Get("AppTitle",          "miPDFsign");
        public static string TitleBarFormat    => Get("TitleBarFormat",    "miPDFsign  –  {0}");

        // ----------------------------------------------------------------
        //  Toolbar buttons
        // ----------------------------------------------------------------
        public static string BtnPrev           => Get("BtnPrev",           "◀");
        public static string BtnNext           => Get("BtnNext",           "▶");
        public static string BtnClose          => Get("BtnClose",          "✕");

        public static string BtnNextSigIcon    => Get("BtnNextSigIcon",    "✍");
        public static string BtnNextSig        => Get("BtnNextSig",        "Next signature");

        public static string BtnClearIcon      => Get("BtnClearIcon",      "✕");
        public static string BtnClear          => Get("BtnClear",          "Clear");

        public static string BtnSaveIcon       => Get("BtnSaveIcon",       "💾");
        public static string BtnSave           => Get("BtnSave",           "Save");

        // ----------------------------------------------------------------
        //  Status bar messages
        // ----------------------------------------------------------------
        public static string StatusLoading           => Get("StatusLoading",           "⏳  Loading document …");
        public static string StatusSaving            => Get("StatusSaving",            "⏳  Saving …");

        /// <summary>Shown after a successful save. {0} = filename.</summary>
        public static string StatusSavedFormat       => Get("StatusSavedFormat",       "✓  {0}");

        /// <summary>Title of the "Save As" dialog (when SaveAsDialog=true in App.config).</summary>
        public static string SaveDialogTitle         => Get("SaveDialogTitle",         "Save signed document as");

        /// <summary>File-type filter of the "Save As" dialog.</summary>
        public static string SaveDialogFilter        => Get("SaveDialogFilter",        "PDF document (*.pdf)|*.pdf");

        /// <summary>Shown when exactly one required field is unsigned.</summary>
        public static string StatusMissingRequired1  => Get("StatusMissingRequired1",  "⚠  1 required field not yet signed");

        /// <summary>Shown when multiple required fields are unsigned. {0} = count.</summary>
        public static string StatusMissingRequiredN  => Get("StatusMissingRequiredN",  "⚠  {0} required fields not yet signed");

        // ----------------------------------------------------------------
        //  Freehand / QES signing flow — status bar
        // ----------------------------------------------------------------
        public static string StatusFreehandHint          => Get("StatusFreehandHint",          "Please sign on the document");
        public static string StatusQesAborted            => Get("StatusQesAborted",            "ID-Austria signing cancelled.");
        public static string StatusQesAddingTimestamp    => Get("StatusQesAddingTimestamp",    "Adding signature timestamp…");
        public static string StatusQesFetchingRevocation => Get("StatusQesFetchingRevocation", "Fetching revocation data…");
        public static string StatusQesAddingArchive      => Get("StatusQesAddingArchive",      "Creating archive timestamp…");

        // ----------------------------------------------------------------
        //  ID-Austria window
        // ----------------------------------------------------------------
        public static string IdAustriaWindowTitle     => Get("IdAustriaWindowTitle",     "ID-Austria signature");
        public static string IdAustriaInstruction     => Get("IdAustriaInstruction",     "Please sign with ID-Austria in the view below.");
        public static string IdAustriaConnecting      => Get("IdAustriaConnecting",      "Connecting to A-Trust…");
        public static string IdAustriaSubmitting      => Get("IdAustriaSubmitting",      "Submitting form data…");
        public static string IdAustriaProcessing      => Get("IdAustriaProcessing",      "Processing signature…");
        public static string IdAustriaSuccess         => Get("IdAustriaSuccess",         "✓ Signature successful – saving document…");
        /// <summary>{0} = truncated URL.</summary>
        public static string IdAustriaUrlOpened       => Get("IdAustriaUrlOpened",       "Opened: {0}");
        /// <summary>{0} = exception message.</summary>
        public static string IdAustriaStartError      => Get("IdAustriaStartError",      "Error starting ID-Austria signing:\n{0}");
        public static string IdAustriaStartErrorTitle => Get("IdAustriaStartErrorTitle", "ID-Austria error");
        public static string IdAustriaParseError      => Get("IdAustriaParseError",      "The signature response could not be read.");
        public static string IdAustriaDecodeError     => Get("IdAustriaDecodeError",     "The CMS signature could not be decoded.");
        /// <summary>{0} = error code string.</summary>
        public static string IdAustriaErrorCode       => Get("IdAustriaErrorCode",       "A-Trust error code: {0}");
        /// <summary>{0} = error detail.</summary>
        public static string IdAustriaNotRecognized   => Get("IdAustriaNotRecognized",   "The ID-Austria signature was not recognized.\n{0}");
        public static string IdAustriaNoSignature     => Get("IdAustriaNoSignature",     "No CMSSignature element found in the response.");
        /// <summary>Prefix for error messages shown in the info bar. {0} = message.</summary>
        public static string IdAustriaErrorPrefix     => Get("IdAustriaErrorPrefix",     "✗ {0}");
        public static string IdAustriaBtnCancel       => Get("IdAustriaBtnCancel",       "Cancel");

        // ----------------------------------------------------------------
        //  Signature type dialog
        // ----------------------------------------------------------------
        public static string DlgSigTypeTitle        => Get("DlgSigTypeTitle",        "Choose signature type");
        public static string DlgSigTypeHeader       => Get("DlgSigTypeHeader",       "Choose signature type");
        public static string DlgSigTypeQesTitle     => Get("DlgSigTypeQesTitle",     "ID-Austria (QES)");
        public static string DlgSigTypeQesSub       => Get("DlgSigTypeQesSub",       "Qualified Electronic Signature via A-Trust Security Layer");
        public static string DlgSigTypeFesTitle     => Get("DlgSigTypeFesTitle",     "Self-signed (FES)");
        public static string DlgSigTypeFesSub       => Get("DlgSigTypeFesSub",       "Advanced signature with a self-signed certificate");
        public static string DlgSigTypeFesNameLabel => Get("DlgSigTypeFesNameLabel", "Signer name:");
        public static string DlgSigTypeBtnCancel    => Get("DlgSigTypeBtnCancel",    "Cancel");
        public static string DlgSigTypeBtnOk        => Get("DlgSigTypeBtnOk",        "Continue  →");

        // ----------------------------------------------------------------
        //  Signature field overlay buttons / labels
        // ----------------------------------------------------------------
        public static string SigLabelActive    => Get("SigLabelActive",    "✎  Sign now");
        public static string SigBtnSign        => Get("SigBtnSign",        "✎  Sign");
        public static string SigBtnConfirm     => Get("SigBtnConfirm",     "✓  Done");
        public static string SigBtnRetry       => Get("SigBtnRetry",       "↺  Retry");
        public static string SigBtnCancel      => Get("SigBtnCancel",      "✕  Cancel");

        // ----------------------------------------------------------------
        //  Message boxes
        // ----------------------------------------------------------------
        /// <summary>{0} = exception message.</summary>
        public static string MsgLoadError          => Get("MsgLoadError",          "The PDF could not be loaded:\n{0}");
        public static string MsgLoadErrorTitle     => Get("MsgLoadErrorTitle",     "Error");

        public static string MsgMissingFields      => Get("MsgMissingFields",      "The following required fields have not been signed yet:\n{0}");
        public static string MsgMissingFieldsTitle => Get("MsgMissingFieldsTitle", "Required fields missing");

        /// <summary>{0} = exception message.</summary>
        public static string MsgSaveError          => Get("MsgSaveError",          "Error while saving:\n{0}");
        public static string MsgSaveErrorTitle     => Get("MsgSaveErrorTitle",     "Error");
    }
}
