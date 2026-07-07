using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using miPDFsign.Helpers;
using L = miPDFsign.Helpers.UiLabels;

namespace miPDFsign
{
    /// <summary>
    /// A-Trust Security Layer 1.2 signing flow.
    ///
    /// Flow:
    ///   1. NavigateToString loads the auto-POST form.
    ///   2. LoadCompleted (e.Uri==null) → DOM click submits to A-Trust.
    ///   3. A-Trust pages are shown (login, TAN, …).
    ///   4. Browser_Navigating detects response.aspx → updates status only.
    ///      IMPORTANT: do NOT hide browser in Navigating — the DOM must be
    ///      fully loaded before we can read it.
    ///   5. LoadCompleted for response.aspx:
    ///        a. Read DOM (must happen first, while browser is still visible/active)
    ///        b. Hide browser so raw XML is never shown
    ///        c. Show success message in info bar
    ///        d. DispatcherTimer → DialogResult=true after 1.5 s
    /// </summary>
    public partial class IdAustriaWindow : Window
    {
        private const string ATrustPostUrl =
            "https://www.a-trust.at/mobile/https-security-layer-request/default.aspx";

        private readonly byte[] _placeholderPdf;
        private bool _startProcess;
        private bool _silentSet;

        public byte[]? SignedCms { get; private set; }

        public IdAustriaWindow(byte[] placeholderPdf)
        {
            InitializeComponent();
            _placeholderPdf = placeholderPdf;
        }

        // ----------------------------------------------------------------
        //  Window loaded
        // ----------------------------------------------------------------
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Title              = L.IdAustriaWindowTitle;
            TbInfoBar.Text     = L.IdAustriaInstruction;
            BtnCancel.Content  = L.IdAustriaBtnCancel;

            try
            {
                string html = BuildRequestHtml(_placeholderPdf);
                TbStatus.Text = L.IdAustriaConnecting;
                _startProcess = true;
                Browser.NavigateToString(html);
            }
            catch (Exception ex)
            {
                AppLogger.Error("IdAustriaWindow: startup failed", ex);
                MessageBox.Show(string.Format(L.IdAustriaStartError, ex.Message),
                    L.IdAustriaStartErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
            }
        }

        // ----------------------------------------------------------------
        //  Navigating: detect response.aspx — update status ONLY.
        //  Do NOT hide the browser here; the DOM is not yet loaded.
        // ----------------------------------------------------------------
        private void Browser_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            string url = e.Uri?.ToString() ?? "";
            if (url.Contains("response.aspx", StringComparison.OrdinalIgnoreCase))
                TbStatus.Text = L.IdAustriaProcessing;
        }

        // ----------------------------------------------------------------
        //  LoadCompleted
        // ----------------------------------------------------------------
        private void Browser_LoadCompleted(object sender, NavigationEventArgs e)
        {
            string url = e.Uri?.ToString() ?? "";

            // Suppress IE script error dialogs once ActiveX is available
            if (!_silentSet) { SetBrowserSilent(); _silentSet = true; }

            // ── Auto-submit (NavigateToString fires with e.Uri == null) ─────
            if (_startProcess &&
                (string.IsNullOrEmpty(url) ||
                 url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)))
            {
                _startProcess = false;
                TbStatus.Text = L.IdAustriaSubmitting;
                try
                {
                    // Managed DOM click — not InvokeScript/eval, which can be
                    // blocked by IE's Local Machine Zone Lockdown
                    dynamic doc = Browser.Document;
                    dynamic? btn = doc?.getElementById("submit");
                    btn?.click();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"IdAustriaWindow: DOM click failed: {ex.Message}");
                    try { Browser.InvokeScript("eval", new object[] { "document.getElementById('submit').click()" }); }
                    catch { /* ignore */ }
                }
                return;
            }

            // ── Response page loaded ─────────────────────────────────────────
            if (url.Contains("response.aspx", StringComparison.OrdinalIgnoreCase))
            {
                // Step 1: Read DOM NOW while browser is still active/loaded.
                // IE renders the XML response as a formatted HTML tree using <SPAN> elements.
                // innerText gives the raw text content (= the actual XML with real < > chars).
                // outerHTML gives the HTML rendering (tags replaced by &lt; &gt; inside spans).
                // We try innerText first; outerHTML is kept as fallback for span-based parsing.
                string innerText = "";
                string outerHtml = "";
                try
                {
                    dynamic doc = Browser.Document;
                    innerText = doc?.documentElement?.innerText ?? "";
                    outerHtml = doc?.documentElement?.outerHTML ?? "";
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"IdAustriaWindow: DOM read failed: {ex.Message}");
                }

                // Step 2: Hide browser so the raw XML page is never visible
                Browser.Visibility = Visibility.Hidden;

                // Step 3: Parse and handle
                HandleResponse(innerText, outerHtml);
                return;
            }

            if (!string.IsNullOrEmpty(url))
                TbStatus.Text = string.Format(L.IdAustriaUrlOpened, TruncateUrl(url));
        }

        // ----------------------------------------------------------------
        //  Parse A-Trust response and show result.
        //
        //  IE's XML viewer renders the XML as coloured HTML using spans:
        //    <SPAN class=t>sl:CMSSignature</SPAN>  ← tag name
        //    <SPAN class=m>&gt;</SPAN>              ← literal >
        //    <SPAN class=tx>BASE64…</SPAN>          ← text content  ← what we want
        //
        //  innerText strategy: IE's innerText returns the visible plain-text,
        //  which is the original XML with real < > characters restored.
        //  This lets the straightforward XML regex work.
        //
        //  outerHTML fallback: if innerText is empty or the XML regex fails,
        //  extract the base64 directly from the <SPAN class=tx> that immediately
        //  follows the "sl:CMSSignature" tag-name span.
        // ----------------------------------------------------------------
        private void HandleResponse(string innerText, string outerHtml)
        {
            if (string.IsNullOrWhiteSpace(innerText) && string.IsNullOrWhiteSpace(outerHtml))
            {
                AppLogger.Warn("IdAustriaWindow: empty response document");
                ShowError(L.IdAustriaParseError);
                return;
            }

            AppLogger.Debug($"IdAustriaWindow: innerText={innerText.Length}, outerHtml={outerHtml.Length}");

            string? base64Cms = null;

            // ── Strategy 1: regex on innerText (raw XML) ──────────────────
            if (!string.IsNullOrWhiteSpace(innerText))
            {
                var m = Regex.Match(innerText,
                    @"<sl:CMSSignature[^>]*>([\s\S]+?)</sl:CMSSignature>",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                    base64Cms = m.Groups[1].Value;
            }

            // ── Strategy 2: extract from IE XML-viewer SPAN in outerHTML ──
            // IE renders: <SPAN class=t>sl:CMSSignature</SPAN><SPAN class=m>&gt;</SPAN>
            //             <SPAN class=tx>BASE64</SPAN>
            if (base64Cms == null && !string.IsNullOrWhiteSpace(outerHtml))
            {
                var m = Regex.Match(outerHtml,
                    @"<SPAN\s[^>]*class=t[^>]*>\s*sl:CMSSignature\s*</SPAN>\s*" +
                    @"<SPAN\s[^>]*class=m[^>]*>&gt;</SPAN>\s*" +
                    @"<SPAN\s[^>]*class=tx[^>]*>([\s\S]+?)</SPAN>",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                    base64Cms = m.Groups[1].Value;
            }

            // ── Strategy 3: looser outerHTML scan (unquoted class attribute) ─
            // Some IE versions render class=tx without quotes
            if (base64Cms == null && !string.IsNullOrWhiteSpace(outerHtml))
            {
                // Find "sl:CMSSignature" then grab the very next class=tx span
                var m = Regex.Match(outerHtml,
                    @"sl:CMSSignature[\s\S]{1,200}?class=tx[^>]*>([\s\S]+?)</SPAN>",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                    base64Cms = m.Groups[1].Value;
            }

            // ── Success ────────────────────────────────────────────────────
            if (base64Cms != null)
            {
                string clean = base64Cms
                    .Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();
                try
                {
                    SignedCms = Convert.FromBase64String(clean);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"IdAustriaWindow: base64 decode failed: {ex.Message}");
                    ShowError(L.IdAustriaDecodeError);
                    return;
                }

                AppLogger.Info($"IdAustriaWindow: CMS received, {SignedCms.Length} bytes");
                InfoBar.Background   = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
                TbInfoBar.Text       = L.IdAustriaSuccess;
                TbInfoBar.Foreground = Brushes.White;
                TbInfoBar.FontWeight = FontWeights.SemiBold;
                TbStatus.Text        = "";

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) => { timer.Stop(); DialogResult = true; };
                timer.Start();
                return;
            }

            // ── Error response ──────────────────────────────────────────────
            // Try to find an error code in innerText or outerHTML
            string source = string.IsNullOrWhiteSpace(innerText) ? outerHtml : innerText;
            var errMatch = Regex.Match(source,
                @"<sl:ErrorCode[^>]*>([\s\S]+?)</sl:ErrorCode>",
                RegexOptions.IgnoreCase);
            if (!errMatch.Success)
                errMatch = Regex.Match(outerHtml,
                    @"sl:ErrorCode[\s\S]{1,100}?class=tx[^>]*>([\s\S]+?)</SPAN>",
                    RegexOptions.IgnoreCase);

            string errInfo = errMatch.Success
                ? string.Format(L.IdAustriaErrorCode, errMatch.Groups[1].Value.Trim())
                : L.IdAustriaNoSignature;

            AppLogger.Warn($"IdAustriaWindow: {errInfo}");
            ShowError(string.Format(L.IdAustriaNotRecognized, errInfo));
        }

        private void ShowError(string message)
        {
            InfoBar.Background  = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            TbInfoBar.Text       = string.Format(L.IdAustriaErrorPrefix, message);
            TbInfoBar.Foreground = Brushes.White;
            TbInfoBar.FontWeight = FontWeights.SemiBold;
            TbStatus.Text        = "";
            Browser.Visibility   = Visibility.Hidden;
        }

        // ----------------------------------------------------------------
        //  Suppress IE script errors (set Silent=true on ActiveX instance)
        // ----------------------------------------------------------------
        private void SetBrowserSilent()
        {
            try
            {
                var field = typeof(System.Windows.Controls.WebBrowser)
                    .GetField("_axIWebBrowser2",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                object? ax = field?.GetValue(Browser);
                if (ax != null)
                    ax.GetType().InvokeMember("Silent",
                        BindingFlags.SetProperty, null, ax, new object[] { true });
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"IdAustriaWindow: SetBrowserSilent failed: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        //  Cancel
        // ----------------------------------------------------------------
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ----------------------------------------------------------------
        //  Build HTML form
        //  XML: standard double quotes — single-quoted HTML attributes → no escaping needed
        // ----------------------------------------------------------------
        private static string BuildRequestHtml(byte[] placeholderPdf)
        {
            string base64Pdf = Convert.ToBase64String(placeholderPdf);

            // Determine ExcludedByteRange from the placeholder PDF's /ByteRange.
            // Without this, A-Trust may hash the entire PDF content (including the
            // /Contents placeholder zeros) instead of respecting /ByteRange.
            // Reference implementation (CmsSig2 template) uses the same approach.
            //
            // /ByteRange [b0 l0 b1 l1] → ExcludedByteRange From=(b0+l0) To=(b1-1)
            //   b0+l0 = first byte of '<' before /Contents hex data
            //   b1-1  = last byte of '>' after /Contents hex data
            string excludedByteRange = "";
            try
            {
                string pdfText = System.Text.Encoding.Latin1.GetString(placeholderPdf);
                var brMatches  = Regex.Matches(pdfText,
                    @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
                if (brMatches.Count > 0)
                {
                    var m   = brMatches[^1];   // last occurrence (most-recent revision)
                    long b0 = long.Parse(m.Groups[1].Value);
                    long l0 = long.Parse(m.Groups[2].Value);
                    long b1 = long.Parse(m.Groups[3].Value);
                    long from = b0 + l0;       // inclusive start of /Contents '<hex...>'
                    long to   = b1 - 1;        // inclusive end   of /Contents '>'
                    excludedByteRange =
                        "<sl:ExcludedByteRange>" +
                          $"<sl:From>{from}</sl:From>" +
                          $"<sl:To>{to}</sl:To>" +
                        "</sl:ExcludedByteRange>";
                    AppLogger.Info($"IdAustriaWindow: ExcludedByteRange From={from} To={to}");
                }
                else
                {
                    AppLogger.Warn("IdAustriaWindow: /ByteRange not found in placeholder — " +
                                   "sending without ExcludedByteRange");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"IdAustriaWindow: ExcludedByteRange parse failed: {ex.Message}");
            }

            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<sl:CreateCMSSignatureRequest " +
                    "xmlns:sl=\"http://www.buergerkarte.at/namespaces/securitylayer/1.2#\" " +
                    "Structure=\"detached\" " +
                    "PAdESCompatibility=\"true\">" +
                  "<sl:KeyboxIdentifier>SecureSignatureKeypair</sl:KeyboxIdentifier>" +
                  "<sl:DataObject>" +
                    "<sl:MetaInfo>" +
                      "<sl:MimeType>application/pdf</sl:MimeType>" +
                    "</sl:MetaInfo>" +
                    "<sl:Content>" +
                      "<sl:Base64Content>" + base64Pdf + "</sl:Base64Content>" +
                    "</sl:Content>" +
                    excludedByteRange +
                  "</sl:DataObject>" +
                "</sl:CreateCMSSignatureRequest>";

            return
                "<html><head><title>ID-Austria Signature</title></head><body>" +
                  "<form method='post' id='form1' action='" + ATrustPostUrl + "'>" +
                    "<input type='hidden' name='XMLRequest' id='XMLRequest' value='" + xml + "'/>" +
                    "<input type='submit' id='submit' value='start' style='display:none'/>" +
                  "</form>" +
                "</body></html>";
        }

        private static string TruncateUrl(string url)
        {
            if (url.Length <= 80) return url;
            return url[..40] + "…" + url[^36..];
        }
    }
}
