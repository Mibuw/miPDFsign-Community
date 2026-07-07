using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using miPDFsign.Helpers;
using miPDFsign.Models;
using Path = System.IO.Path;

namespace miPDFsign
{
    public partial class MainWindow : Window
    {
        // ----------------------------------------------------------------
        //  Fields
        // ----------------------------------------------------------------
        private readonly string      _pdfPath;
        private readonly PdfRenderer _renderer = new();

        private int _currentPage = 0;
        private int _renderDpi   = 150;

        // Per-page ink strokes  (key = 0-based page index) – used for PDF export
        private readonly Dictionary<int, StrokeCollection> _pageStrokes = new();

        // Per-field ink strokes (key = fieldName) – used for biometric data per signature
        private readonly Dictionary<string, StrokeCollection> _fieldStrokes = new();

        // Maps each finished stroke back to its field name (for undo)
        private readonly Dictionary<Stroke, string> _strokeFieldMap = new();

        // Per-page rendered image size in bitmap pixels (for PDF export)
        private readonly Dictionary<int, Size> _pageImageSizes = new();

        // PDF form checkboxes (AcroForm)
        private List<CheckboxInfo>              _checkboxes     = new();
        private readonly Dictionary<string, bool> _checkboxStates = new();

        // Signature-field descriptors (scanned from PDF markers)
        private List<SignatureFieldDescriptor>  _signatureFields = new();

        // Parser-based field descriptors (signPOS v3.0 identifiers)
        private List<ParsedCheckboxDescriptor>    _parsedCheckboxes  = new();
        private readonly Dictionary<string, bool> _parsedCheckboxStates = new();
        private List<DateFieldDescriptor>         _dateFields        = new();
        private List<LocationFieldDescriptor>     _locationFields    = new();
        private List<LocationDateFieldDescriptor> _locationDateFields = new();

        // Signer name cached at startup (avoids re-opening PDF on save)
        private string? _cachedSignerName;

        // Order in which signature fields were confirmed, for undo
        private readonly List<string> _signingOrder = new();

        // RSA key pair pre-generated in the background at startup (FES only)
        private Task<PdfCertSigner.CertPair>? _certTask;

        // Active (zoomed-in) signature field
        private SignatureFieldDescriptor? _activeField;
        private double _savedScrollH, _savedScrollV;

        // Per-page scale factors for screen ↔ PDF coordinate conversion
        private readonly Dictionary<int, (double sx, double sy, double pageH)> _pageScales = new();

        // ── Zoom state ────────────────────────────────────────────────────
        private double _zoomFactor   = 1.0;  // current display zoom (1.0 = 100 %)
        private double _preFocusZoom = 1.0;  // saved zoom before field zoom-in

        // ── Touch gesture state ───────────────────────────────────────────
        private readonly Dictionary<int, Point> _activeTouches  = new();
        private double _pinchStartDist;
        private double _touchStartZoom;
        private bool   _isPinching;
        private bool   _lastStylusWasTouch;           // set in PreviewStylusDown for touch devices
        private List<InkCanvasEditingMode> _modeBeforePinchAll = new();
        private Point    _swipeStartPt;
        private DateTime _swipeStartTime;

        // ── Continuous scroll state ───────────────────────────────────────
        private const double PageGap = 16.0;  // pixels of gap between pages in the document stack
        private readonly List<double>            _pageTopOffsets = new(); // unzoomed Y of each page top
        private readonly Dictionary<Stroke, int>  _strokePageMap  = new(); // stroke → 0-based page index
        private readonly Dictionary<Stroke, long> _strokeTimes    = new(); // stroke → UTC milliseconds (for group detection)

        // ── WM_POINTER P/Invoke (touch interception without EnablePointerSupport) ──
        // We intercept WM_POINTER touch at Win32 level so InkCanvas pen input continues
        // to work via the default WISPTIS/WM_TABLET_PACKET path.
        private const int WM_POINTERDOWN   = 0x0246;
        private const int WM_POINTERUPDATE = 0x0245;
        private const int WM_POINTERUP     = 0x0247;
        private const int PT_TOUCH         = 2;          // POINTER_INPUT_TYPE: touch

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_INFO
        {
            public int    pointerType;
            public uint   pointerId;
            public uint   frameId;
            public int    pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public WINPT  ptPixelLocation;
            public WINPT  ptHimetricLocation;
            public WINPT  ptPixelLocationRaw;
            public WINPT  ptHimetricLocationRaw;
            public uint   dwTime;
            public uint   historyCount;
            public int    InputData;
            public uint   dwKeyStates;
            public ulong  PerformanceCount;
            public int    ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINPT { public int x; public int y; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO info);

        private static uint GetWmPointerId(IntPtr wParam) => (uint)(wParam.ToInt64() & 0xFFFF);

        // ── Pen pressure (WM_POINTER PT_PEN → GetPointerPenInfo) ──────────────
        // Windows-Ink pen pressure is NOT delivered to WPF StylusPoints in this app
        // (the WISPTIS pen path is bypassed, so PressureFactor stays at the neutral 0.5).
        // We therefore sample the real pressure from WM_POINTER and re-apply it to the
        // collected stroke in InkOverlay_StrokeCollected → pressure-sensitive ink width.
        private const int PT_PEN = 3;            // POINTER_INPUT_TYPE: pen

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_PEN_INFO
        {
            public POINTER_INFO pointerInfo;
            public int   penFlags;     // PEN_FLAGS
            public int   penMask;      // PEN_MASK
            public uint  pressure;     // 0..1024 (0 = device reports no pressure)
            public uint  rotation;
            public int   tiltX;
            public int   tiltY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

        // Pen-pressure samples for the stroke currently being drawn: screen-pixel position
        // (matches POINTER_INFO.ptPixelLocation and Visual.PointToScreen) + normalised pressure.
        private readonly List<(double sx, double sy, double pressure)> _penSamples = new();
        private bool _penCapturing;

        // True from the moment Save is pressed: blocks any further ink/pen input so the
        // document can no longer be signed while/after saving.
        private bool _isSaving;

        private static readonly TimeSpan CheckboxLockDuration  = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan SigButtonLockDuration = TimeSpan.FromMilliseconds(500);
        private DateTime _checkboxLockedUntil  = DateTime.MinValue;
        private DateTime _sigButtonLockedUntil = DateTime.MinValue;

        // ── Freehand mode (no predefined signature fields) ────────────────
        // When true, the user may draw anywhere on the page.
        private bool           _freehandMode;
        private const string   FreehandFieldName = "__freehand__";

        // ----------------------------------------------------------------
        //  Constructor
        // ----------------------------------------------------------------
        public MainWindow(string pdfPath)
        {
            InitializeComponent();
            _pdfPath = pdfPath;
            Loaded       += OnLoaded;
            StateChanged += (_, _) => UpdateMaximizeRestoreIcon();
        }

        // ----------------------------------------------------------------
        //  Initialisation
        // ----------------------------------------------------------------
        private void ApplyUiLabels()
        {
            Title = UiLabels.AppTitle;

            BtnPrev.Content = UiLabels.BtnPrev;
            BtnNext.Content = UiLabels.BtnNext;

            BtnNextSigIconText.Text  = UiLabels.BtnNextSigIcon;
            BtnNextSigLabelText.Text = UiLabels.BtnNextSig;

            BtnClearIconText.Text  = UiLabels.BtnClearIcon;
            BtnClearLabelText.Text = UiLabels.BtnClear;

            BtnSaveIconText.Text  = UiLabels.BtnSaveIcon;
            BtnSaveLabelText.Text = UiLabels.BtnSave;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Always open on the primary monitor, maximised.
            MoveToAndMaximizePrimary();

            UiLabels.Load();
            ApplyUiLabels();
            TbStatus.Text = UiLabels.StatusLoading;

            try
            {
                var path = _pdfPath;
                var scan = await Task.Run(() => PdfLoadHelper.ScanAll(path));

                _checkboxes = scan.AcroCheckboxes;
                foreach (var cb in _checkboxes)
                    _checkboxStates.TryAdd(cb.FieldName, cb.IsChecked);

                _signatureFields    = scan.SignatureFields;
                _locationFields     = scan.LocationFields;
                _dateFields         = scan.DateFields;
                _locationDateFields = scan.LocationDateFields;
                _parsedCheckboxes   = scan.ParsedCheckboxes;
                foreach (var pcb in _parsedCheckboxes)
                    _parsedCheckboxStates.TryAdd(pcb.Identifier, pcb.IsChecked);

                _cachedSignerName = scan.SignerName;

                _renderer.Load(_pdfPath);

                string displayTitle = !string.IsNullOrWhiteSpace(scan.DocumentTitle)
                    ? scan.DocumentTitle!
                    : Path.GetFileNameWithoutExtension(_pdfPath);
                TitleText.Text = string.Format(UiLabels.TitleBarFormat, displayTitle);

                // Determine signing mode: if no signature fields found, allow freehand.
                _freehandMode = _signatureFields.Count == 0;
                if (_freehandMode)
                    AppLogger.Info("MainWindow: no signature fields found – freehand mode activated");

                await InitAllPages();
                UpdateSaveButton();

                // ── Native InkCanvas setup ────────────────────────────────
                InkOverlay.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color          = Colors.DarkBlue,
                    Width          = 2.5,
                    Height         = 2.5,
                    StylusTip      = StylusTip.Ellipse,
                    FitToCurve     = true,
                    IgnorePressure = false,
                };
                InkOverlay.StrokeCollected += InkOverlay_StrokeCollected;

                // Block touch-to-mouse-promoted events from creating ink strokes.
                // On WM_POINTER devices (HP Spectre etc.), BOTH touch and pen arrive via
                // WM_POINTER. With EnablePointerSupport=false WPF ignores WM_POINTER entirely,
                // so Windows promotes both to WM_LBUTTONDOWN for InkCanvas to consume.
                // Distinction: WndProcHook adds touch contacts to _activeTouches BEFORE
                // WM_LBUTTONDOWN fires → _activeTouches.Count > 0 means it's a touch event.
                // Pen fires with no active touch contacts → _activeTouches.Count == 0 → allowed.
                InkOverlay.PreviewMouseDown += (_, me) =>
                {
                    if (_activeTouches.Count > 0)
                        me.Handled = true;
                };

                if (_freehandMode)
                {
                    InkOverlay.EditingMode      = InkCanvasEditingMode.Ink;
                    InkOverlay.IsHitTestVisible = true;
                }

                // ── Touch gestures: dual registration (Touch + Stylus path) ─────────
                // Legacy WISPTIS mode: finger touches arrive as StylusDown (TabletDeviceType.Touch),
                // NOT as TouchDown. We register both so both driver stacks are covered.
                AddHandler(TouchDownEvent,  new EventHandler<TouchEventArgs>(Win_TouchDown),  true);
                AddHandler(TouchMoveEvent,  new EventHandler<TouchEventArgs>(Win_TouchMove),  true);
                AddHandler(TouchUpEvent,    new EventHandler<TouchEventArgs>(Win_TouchUp),    true);
                AddHandler(Stylus.PreviewStylusDownEvent, new StylusDownEventHandler(Win_PreviewStylusDown), true);
                AddHandler(Stylus.PreviewStylusMoveEvent, new StylusEventHandler(Win_PreviewStylusMove),     true);
                AddHandler(Stylus.PreviewStylusUpEvent,   new StylusEventHandler(Win_PreviewStylusUp),       true);

                // ── WM_POINTER hook for modern touch screens (HP Spectre, Surface, etc.) ──
                // Intercepts WM_POINTER touch at Win32 level, bypassing WPF's stylus stack.
                // This lets InkCanvas pen drawing stay on the default WISPTIS path (unaffected),
                // while still giving us pinch/scroll/swipe gesture events from touch.
                var hwndSrc = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                hwndSrc?.AddHook(WndProcHook);

                // Track which page the user has scrolled to in the continuous scroll view.
                Scroll.ScrollChanged += (_, _) => UpdateCurrentPageFromScroll();

                if (!string.IsNullOrWhiteSpace(_cachedSignerName))
                {
                    var name = _cachedSignerName!;
                    _certTask = Task.Run(() => PdfCertSigner.GenerateCertificatePublic(name));
                }

                TbStatus.Text = _freehandMode ? UiLabels.StatusFreehandHint : "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(UiLabels.MsgLoadError, ex.Message),
                    UiLabels.MsgLoadErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        /// <summary>
        /// Moves the window to the primary monitor and maximises it.
        /// Works even if WPF remembered a different screen from the last session.
        /// </summary>
        private void MoveToAndMaximizePrimary()
        {
            WindowState = WindowState.Normal;
            Left   = 0;
            Top    = 0;
            Width  = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            WindowState = WindowState.Maximized;
        }

        // ----------------------------------------------------------------
        //  Input handlers  (tap detection for checkboxes only;
        //  ink drawing is handled natively by InkCanvas)
        // ----------------------------------------------------------------

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.LeftButton == MouseButtonState.Pressed)
                HandleTapInteraction(GetPagePosition(e));
        }

        protected override void OnStylusDown(StylusDownEventArgs e)
        {
            base.OnStylusDown(e);
            HandleTapInteraction(GetPagePosition(e));
        }

        /// <summary>Checks whether the tap landed on a checkbox and toggles it.</summary>
        private void HandleTapInteraction(Point pagePt)
        {
            if (DateTime.Now < _checkboxLockedUntil) return;

            var cb = _checkboxes.FirstOrDefault(c => c.RenderRect.Contains(pagePt));
            if (cb != null)
            {
                ToggleCheckbox(cb);
                _checkboxLockedUntil = DateTime.Now + CheckboxLockDuration;
                return;
            }

            var cbp = _parsedCheckboxes.FirstOrDefault(c => c.RenderRect.Contains(pagePt));
            if (cbp != null)
            {
                ToggleParsedCheckbox(cbp);
                _checkboxLockedUntil = DateTime.Now + CheckboxLockDuration;
            }
        }

        // ----------------------------------------------------------------
        //  InkCanvas stroke handler
        // ----------------------------------------------------------------
        private void InkOverlay_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            // Discard strokes from finger (touch) input — only pen strokes are valid signatures.
            // _lastStylusWasTouch: set by Win_PreviewStylusDown when device type is Touch.
            // _isPinching: set when a 2-finger pinch is in progress.
            // Discard finger/touch strokes, pinch artefacts, and any stroke that arrives
            // after Save was pressed (no more signing once the document is being saved).
            if (_lastStylusWasTouch || _isPinching || _isSaving)
            {
                InkOverlay.Strokes.Remove(e.Stroke);
                return;
            }

            // Inject the real Windows-Ink pen pressure captured from WM_POINTER into the
            // stroke's StylusPoints (WPF delivered a flat 0.5). Drives the pressure-sensitive
            // signature width. No-op if no pen samples were captured.
            ApplyCapturedPressure(e.Stroke);

            string fieldName = _freehandMode
                ? FreehandFieldName
                : (_activeField?.FieldName ?? FreehandFieldName);

            // Record the wall-clock time of this stroke for group detection.
            _strokeTimes[e.Stroke] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Determine page from the stroke's absolute Y position in the document canvas.
            int strokePage = GetPageFromY(e.Stroke.GetBounds().Top);
            _strokePageMap[e.Stroke] = strokePage;

            // Per-page collection (for PDF export)
            if (!_pageStrokes.TryGetValue(strokePage, out var pageCol))
            { pageCol = new StrokeCollection(); _pageStrokes[strokePage] = pageCol; }
            pageCol.Add(e.Stroke);

            // Per-field collection (for biometric data / undo)
            if (!_fieldStrokes.TryGetValue(fieldName, out var fieldCol))
            { fieldCol = new StrokeCollection(); _fieldStrokes[fieldName] = fieldCol; }
            fieldCol.Add(e.Stroke);
            _strokeFieldMap[e.Stroke] = fieldName;

            if (fieldName != FreehandFieldName)
            {
                var field = _signatureFields.FirstOrDefault(f => f.FieldName == fieldName);
                if (field != null)
                {
                    field.IsSigned = true;
                    AppLogger.Info($"Signature stroke collected: {field.FieldName}");
                    if (!_signingOrder.Contains(fieldName))
                        _signingOrder.Add(fieldName);
                }
                RedrawOverlays();
            }
            UpdateSaveButton();
        }

        // ----------------------------------------------------------------
        //  Checkbox toggle
        // ----------------------------------------------------------------
        private void ToggleCheckbox(CheckboxInfo cb)
        {
            bool newState = !_checkboxStates.GetValueOrDefault(cb.FieldName, cb.IsChecked);
            _checkboxStates[cb.FieldName] = newState;
            cb.IsChecked = newState;
            RedrawOverlays();
        }

        private void ToggleParsedCheckbox(ParsedCheckboxDescriptor pcb)
        {
            bool newState = !_parsedCheckboxStates.GetValueOrDefault(pcb.Identifier, pcb.IsChecked);

            if (!string.IsNullOrEmpty(pcb.GroupIdentifier) &&
                pcb.GroupBehavior?.Equals("E", System.StringComparison.OrdinalIgnoreCase) == true &&
                newState)
            {
                foreach (var sibling in _parsedCheckboxes.Where(
                    c => c.GroupIdentifier == pcb.GroupIdentifier && c != pcb))
                {
                    _parsedCheckboxStates[sibling.Identifier] = false;
                    sibling.IsChecked = false;
                }
            }

            _parsedCheckboxStates[pcb.Identifier] = newState;
            pcb.IsChecked = newState;
            RedrawOverlays();
        }

        // ----------------------------------------------------------------
        //  Overlay rendering  (checkboxes + signature fields)
        // ----------------------------------------------------------------
        private void RedrawOverlays()
        {
            CheckboxCanvas.Children.Clear();
            SigButtonCanvas.Children.Clear();

            // ── Signature fields (all pages) ──────────────────────────
            foreach (var sf in _signatureFields)
            {
                var r        = sf.RenderRect;
                bool isActive = sf == _activeField;

                Color borderColor = isActive
                    ? Color.FromRgb(0x1E, 0x90, 0xFF)
                    : sf.IsSigned
                        ? Color.FromRgb(0x88, 0x88, 0x88)
                        : sf.Required
                            ? Color.FromRgb(0xE5, 0x47, 0x47)
                            : Color.FromRgb(0x40, 0xBF, 0x5E);

                Color fillColor = isActive
                    ? Color.FromArgb(35, 0x1E, 0x90, 0xFF)
                    : sf.IsSigned
                        ? Color.FromArgb(30, 0x88, 0x88, 0x88)
                        : sf.Required
                            ? Color.FromArgb(25, 0xE5, 0x47, 0x47)
                            : Color.FromArgb(25, 0x40, 0xBF, 0x5E);

                var border = new Rectangle
                {
                    Width           = r.Width,
                    Height          = r.Height,
                    Stroke          = new SolidColorBrush(borderColor),
                    StrokeThickness = isActive ? 3.0 : (sf.Required && !sf.IsSigned ? 2.5 : 1.5),
                    StrokeDashArray = (isActive || sf.IsSigned) ? null : new DoubleCollection { 4, 2 },
                    Fill            = new SolidColorBrush(fillColor),
                };
                Canvas.SetLeft(border, r.Left);
                Canvas.SetTop (border, r.Top);
                CheckboxCanvas.Children.Add(border);

                string labelText = isActive ? UiLabels.SigLabelActive :
                                   !string.IsNullOrEmpty(sf.Label) ? sf.Label : "";
                if (!string.IsNullOrEmpty(labelText))
                {
                    var lbl = new TextBlock
                    {
                        Text       = labelText,
                        Foreground = new SolidColorBrush(borderColor),
                        FontSize   = Math.Clamp(r.Height * 0.14, 8, 14),
                        FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                        Opacity    = isActive ? 1.0 : 0.85,
                    };
                    Canvas.SetLeft(lbl, r.Left + 4);
                    Canvas.SetTop (lbl, r.Top  + 2);
                    CheckboxCanvas.Children.Add(lbl);
                }

                if (sf.IsSigned && !isActive)
                {
                    var check = new TextBlock
                    {
                        Text       = "✓",
                        Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0xBF, 0x5E)),
                        FontSize   = Math.Clamp(r.Height * 0.4, 12, 32),
                        FontWeight = FontWeights.Bold,
                        Opacity    = 0.7,
                    };
                    Canvas.SetLeft(check, r.Left + r.Width - check.FontSize - 4);
                    Canvas.SetTop (check, r.Top  + (r.Height - check.FontSize) / 2);
                    CheckboxCanvas.Children.Add(check);
                }
            }

            // ── Signature field action buttons (all pages) ───────────
            foreach (var sf in _signatureFields)
            {
                var r     = sf.RenderRect;
                double btnY = r.Bottom + 6;

                if (sf == _activeField)
                {
                    double gap  = 4;
                    double btnW = (r.Width - gap * 2) / 3;
                    double btnH = 42;
                    var capturedField = sf;

                    AddSigButton(UiLabels.SigBtnConfirm, r.Left, btnY, btnW, btnH,
                        Color.FromRgb(0x27, 0xAE, 0x60), Colors.White,
                        () => { if (DateTime.Now < _sigButtonLockedUntil) return;
                                DeactivateField(); });

                    AddSigButton(UiLabels.SigBtnRetry, r.Left + btnW + gap, btnY, btnW, btnH,
                        Color.FromRgb(0x1E, 0x90, 0xFF), Colors.White,
                        () => { if (DateTime.Now < _sigButtonLockedUntil) return;
                                ClearFieldStrokes(capturedField.FieldName); });

                    AddSigButton(UiLabels.SigBtnCancel, r.Left + (btnW + gap) * 2, btnY, btnW, btnH,
                        Color.FromRgb(0x60, 0x60, 0x80), Colors.White,
                        () => { if (DateTime.Now < _sigButtonLockedUntil) return;
                                ClearFieldStrokes(capturedField.FieldName); DeactivateField(); });
                }
                else if (!sf.IsSigned)
                {
                    double btnW = Math.Min(r.Width * 0.85, 260);
                    double btnH = 48;
                    double btnX = r.Left + (r.Width - btnW) / 2;
                    Color  col  = sf.Required
                        ? Color.FromRgb(0xBE, 0x38, 0x38)
                        : Color.FromRgb(0x27, 0x8A, 0x48);
                    var capturedField = sf;
                    AddSigButton(UiLabels.SigBtnSign, btnX, btnY, btnW, btnH, col, Colors.White,
                        () => DelayedActivate(capturedField));
                }
            }

            // ── AcroForm checkboxes (all pages) ──────────────────────
            foreach (var cb in _checkboxes)
            {
                bool state = _checkboxStates.GetValueOrDefault(cb.FieldName, cb.IsChecked);
                var  r     = cb.RenderRect;

                var cbBorder = new Rectangle
                {
                    Width           = r.Width,
                    Height          = r.Height,
                    Stroke          = state ? Brushes.Green : Brushes.OrangeRed,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(30,
                        state ? (byte)0   : (byte)255,
                        state ? (byte)180 : (byte)80, 0))
                };
                Canvas.SetLeft(cbBorder, r.Left);
                Canvas.SetTop (cbBorder, r.Top);
                CheckboxCanvas.Children.Add(cbBorder);

                if (state)
                {
                    double pad = r.Width * 0.15;
                    var cross = new System.Windows.Shapes.Path
                    {
                        Stroke             = Brushes.Black,
                        StrokeThickness    = Math.Max(2, r.Width * 0.12),
                        StrokeLineJoin     = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap   = PenLineCap.Round,
                        Data = new PathGeometry(new[]
                        {
                            new PathFigure(new Point(r.Left + pad, r.Top + pad),
                                new PathSegment[] { new LineSegment(
                                    new Point(r.Right - pad, r.Bottom - pad), true) }, false),
                            new PathFigure(new Point(r.Right - pad, r.Top + pad),
                                new PathSegment[] { new LineSegment(
                                    new Point(r.Left + pad, r.Bottom - pad), true) }, false),
                        })
                    };
                    CheckboxCanvas.Children.Add(cross);
                }
            }

            // ── Parsed checkboxes (signPOS C markers – all pages) ────
            foreach (var pcb in _parsedCheckboxes)
            {
                bool state = _parsedCheckboxStates.GetValueOrDefault(pcb.Identifier, pcb.IsChecked);
                var  r     = pcb.RenderRect;

                var stroke = state
                    ? Brushes.Green
                    : pcb.IsRequired
                        ? new SolidColorBrush(Color.FromRgb(0xBE, 0x38, 0x38))
                        : Brushes.OrangeRed;

                var cbBorder = new Rectangle
                {
                    Width           = r.Width,
                    Height          = r.Height,
                    Stroke          = stroke,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(30,
                        state ? (byte)0   : (byte)255,
                        state ? (byte)180 : (byte)80, 0))
                };
                Canvas.SetLeft(cbBorder, r.Left);
                Canvas.SetTop (cbBorder, r.Top);
                CheckboxCanvas.Children.Add(cbBorder);

                if (state)
                {
                    double pad = r.Width * 0.15;
                    var cross = new System.Windows.Shapes.Path
                    {
                        Stroke             = Brushes.Black,
                        StrokeThickness    = Math.Max(2, r.Width * 0.12),
                        StrokeLineJoin     = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap   = PenLineCap.Round,
                        Data = new PathGeometry(new[]
                        {
                            new PathFigure(new Point(r.Left + pad, r.Top + pad),
                                new PathSegment[] { new LineSegment(
                                    new Point(r.Right - pad, r.Bottom - pad), true) }, false),
                            new PathFigure(new Point(r.Right - pad, r.Top + pad),
                                new PathSegment[] { new LineSegment(
                                    new Point(r.Left + pad, r.Bottom - pad), true) }, false),
                        })
                    };
                    CheckboxCanvas.Children.Add(cross);
                }
            }

            // ── Date fields (all pages) ───────────────────────────────
            foreach (var df in _dateFields)
            {
                var r          = df.RenderRect;
                double fontSize = Math.Clamp(r.Height * 0.85, 7, 20);
                string dateText = df.FormatNow();

                var bg = new Rectangle
                {
                    Width   = r.Width,
                    Height  = r.Height,
                    Fill    = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF)),
                    Stretch = Stretch.Fill,
                };
                Canvas.SetLeft(bg, r.Left);
                Canvas.SetTop (bg, r.Top);
                CheckboxCanvas.Children.Add(bg);

                var tb = new TextBlock
                {
                    Text       = dateText,
                    Foreground = Brushes.Black,
                    FontSize   = fontSize,
                    FontFamily = new FontFamily("Arial"),
                };
                Canvas.SetLeft(tb, r.Left + 1);
                Canvas.SetTop (tb, r.Top);
                CheckboxCanvas.Children.Add(tb);
            }

            // ── Location/Date combined fields (all pages) ─────────────
            foreach (var ldf in _locationDateFields)
            {
                var r          = ldf.RenderRect;
                double fontSize = Math.Clamp(r.Height * 0.85, 7, 20);
                string dateText = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                string display  = string.IsNullOrEmpty(ldf.LocationValue)
                    ? dateText
                    : ldf.LocationValue + ldf.Separator + dateText;

                var bg = new Rectangle
                {
                    Width  = r.Width,
                    Height = r.Height,
                    Fill   = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF)),
                };
                Canvas.SetLeft(bg, r.Left);
                Canvas.SetTop (bg, r.Top);
                CheckboxCanvas.Children.Add(bg);

                var tb = new TextBlock
                {
                    Text       = display,
                    Foreground = Brushes.Black,
                    FontSize   = fontSize,
                    FontFamily = new FontFamily("Arial"),
                };
                Canvas.SetLeft(tb, r.Left + 1);
                Canvas.SetTop (tb, r.Top);
                CheckboxCanvas.Children.Add(tb);
            }
        }

        // ----------------------------------------------------------------
        //  Signature button helpers
        // ----------------------------------------------------------------
        private void AddSigButton(string label, double x, double y, double w, double h,
                                   Color bg, Color fg, Action onClick)
        {
            var btn = new Button
            {
                Content          = label,
                Width            = w,
                Height           = h,
                Background       = new SolidColorBrush(bg),
                Foreground       = new SolidColorBrush(fg),
                BorderThickness  = new Thickness(0),
                FontSize         = Math.Clamp(h * 0.36, 9, 14),
                FontWeight       = FontWeights.SemiBold,
                Padding          = new Thickness(6, 2, 6, 2),
                FocusVisualStyle = null,
                Cursor           = Cursors.Hand,
                Template         = (ControlTemplate)FindResource("BtnTemplate"),
            };
            btn.Click += (_, _) => onClick();
            Canvas.SetLeft(btn, x);
            Canvas.SetTop (btn, y);
            SigButtonCanvas.Children.Add(btn);
        }

        private void ClearFieldStrokes(string fieldName)
        {
            if (!_fieldStrokes.TryGetValue(fieldName, out var fc)) return;

            foreach (var stroke in fc.ToList())
            {
                InkOverlay.Strokes.Remove(stroke);
                if (_strokePageMap.TryGetValue(stroke, out int sPage) &&
                    _pageStrokes.TryGetValue(sPage, out var pc))
                    pc.Remove(stroke);
                _strokePageMap.Remove(stroke);
                _strokeFieldMap.Remove(stroke);
                _strokeTimes.Remove(stroke);
            }
            fc.Clear();

            var field = _signatureFields.FirstOrDefault(f => f.FieldName == fieldName);
            if (field != null) field.IsSigned = false;
            _signingOrder.Remove(fieldName);

            RedrawOverlays();
            UpdateSaveButton();
        }

        // ----------------------------------------------------------------
        //  Field zoom  (activate = zoom in, deactivate = zoom out)
        // ----------------------------------------------------------------
        private void DelayedActivate(SignatureFieldDescriptor field, int delayMs = 120)
        {
            var t = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(delayMs) };
            t.Tick += (_, _) => { t.Stop(); ActivateField(field); };
            t.Start();
        }

        private void ActivateField(SignatureFieldDescriptor field)
        {
            AppLogger.Info($"Signature field activated: {field.FieldName} (page {field.PageIndex + 1})");
            _activeField  = field;
            _sigButtonLockedUntil = DateTime.Now + SigButtonLockDuration;
            _savedScrollH = Scroll.HorizontalOffset;
            _savedScrollV = Scroll.VerticalOffset;

            double viewW = Scroll.ViewportWidth  > 20 ? Scroll.ViewportWidth  : ActualWidth;
            double viewH = Scroll.ViewportHeight > 20 ? Scroll.ViewportHeight : ActualHeight;

            double zoom = Math.Clamp(
                Math.Min(viewW * 0.80 / field.RenderRect.Width,
                         viewH * 0.80 / field.RenderRect.Height),
                2.0, 8.0);

            _preFocusZoom = _zoomFactor;
            _zoomFactor   = zoom;
            ApplyZoom();

            RedrawOverlays();

            // Enable native ink drawing (no clip – strokes attributed to field via StrokeCollected)
            InkOverlay.EditingMode      = InkCanvasEditingMode.Ink;
            InkOverlay.IsHitTestVisible = true;

            Dispatcher.InvokeAsync(() =>
            {
                double cx = (field.RenderRect.Left + field.RenderRect.Width  / 2) * zoom;
                double cy = (field.RenderRect.Top  + field.RenderRect.Height / 2) * zoom;
                Scroll.ScrollToHorizontalOffset(cx - Scroll.ViewportWidth  / 2);
                Scroll.ScrollToVerticalOffset  (cy - Scroll.ViewportHeight / 2);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void DeactivateField(bool restoreScroll = true)
        {
            if (_activeField == null) return;

            // Disable ink drawing
            InkOverlay.EditingMode      = InkCanvasEditingMode.None;
            InkOverlay.IsHitTestVisible = false;

            _activeField = null;

            _zoomFactor = _preFocusZoom;
            ApplyZoom();

            RedrawOverlays();

            if (restoreScroll)
            {
                double h = _savedScrollH, v = _savedScrollV;
                Dispatcher.InvokeAsync(() =>
                {
                    Scroll.ScrollToHorizontalOffset(h);
                    Scroll.ScrollToVerticalOffset(v);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // ----------------------------------------------------------------
        //  Zoom
        // ----------------------------------------------------------------
        private void ApplyZoom()
        {
            PageContainer.LayoutTransform = new ScaleTransform(_zoomFactor, _zoomFactor);
            TbZoom.Text = $"{(int)Math.Round(_zoomFactor * 100)} %";
        }

        /// <summary>
        /// Fits the page width to the viewport (height scrolls freely in continuous scroll mode).
        /// Call after layout (e.g. via Dispatcher Loaded priority).
        /// </summary>
        private void RecalcFitWidth()
        {
            if (PageContainer.Width <= 0) return;
            double availW = Scroll.ActualWidth - SystemParameters.VerticalScrollBarWidth - 6;
            if (availW < 10) return;
            _zoomFactor = Math.Clamp(availW / PageContainer.Width, 0.05, 10.0);
            ApplyZoom();
        }

        /// <summary>
        /// Fits the FIRST page to the viewport HEIGHT on startup, so the document is zoomed
        /// in to a full page (the first page fills the height) rather than zoomed to full
        /// width or shrunk so the whole multi-page document fits. Further pages scroll below.
        /// </summary>
        private void RecalcFitPage()
        {
            if (PageContainer.Width <= 0 || PageContainer.Height <= 0) return;
            double firstPageH = _pageImageSizes.TryGetValue(0, out var sz0) && sz0.Height > 0
                ? sz0.Height
                : PageContainer.Height;
            double availH = Scroll.ActualHeight - 4;
            if (availH < 10) return;
            _zoomFactor = Math.Clamp(availH / firstPageH, 0.05, 10.0);
            ApplyZoom();
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoomFactor = Math.Max(_zoomFactor / 1.2, 0.05);
            ApplyZoom();
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoomFactor = Math.Min(_zoomFactor * 1.2, 10.0);
            ApplyZoom();
        }

        // ----------------------------------------------------------------
        //  Touch gestures – pinch-to-zoom & swipe-to-navigate
        //
        //  Two input paths are covered:
        //  • WM_POINTER / WM_TOUCH  → TouchDown/Move/Up events  (modern mode)
        //  • WISPTIS legacy mode    → StylusDown/Move/Up with TabletDeviceType.Touch
        //    (Wintab/WISPTIS driver intercepts WM_TOUCH so WPF never raises TouchDown)
        //
        //  Both paths call the same HandleContactDown/Move/Up helpers.
        //  Stylus contacts get negative keys (~StylusDevice.Id) to avoid collision
        //  with TouchDevice IDs (which are non-negative).
        // ----------------------------------------------------------------

        // ── Touch path (WM_POINTER / WM_TOUCH) ───────────────────────────
        private void Win_TouchDown(object? sender, TouchEventArgs e)
        {
            var pt = e.GetTouchPoint(Scroll).Position;
            HandleContactDown(e.TouchDevice.Id, pt);
        }

        private void Win_TouchMove(object? sender, TouchEventArgs e) =>
            HandleContactMove(e.TouchDevice.Id, e.GetTouchPoint(Scroll).Position);

        private void Win_TouchUp(object? sender, TouchEventArgs e) =>
            HandleContactUp(e.TouchDevice.Id);

        // ── Stylus/WISPTIS path (finger touches arrive as StylusDown) ────
        private static bool IsTouchStylus(StylusEventArgs e) =>
            e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;

        private void Win_PreviewStylusDown(object sender, StylusDownEventArgs e)
        {
            // Only handle touch-type stylus devices (WISPTIS path for older touch hardware).
            // Pen input goes straight to InkCanvas — do not intercept it here.
            if (!IsTouchStylus(e)) return;
            _lastStylusWasTouch = true;
            HandleContactDown(~e.StylusDevice!.Id, e.GetPosition(Scroll));
        }

        private void Win_PreviewStylusMove(object sender, StylusEventArgs e)
        {
            if (!IsTouchStylus(e)) return;
            HandleContactMove(~e.StylusDevice!.Id, e.GetPosition(Scroll));
        }

        private void Win_PreviewStylusUp(object sender, StylusEventArgs e)
        {
            if (!IsTouchStylus(e)) return;
            _lastStylusWasTouch = false;
            HandleContactUp(~e.StylusDevice!.Id);
        }

        // ── WM_POINTER hook (primary path for HP Spectre / modern touch screens) ──────
        // Modern Windows touchscreens send WM_POINTER, not WM_TOUCH/WM_TABLET_PACKET.
        // WPF only routes WM_POINTER through its touch-event system when EnablePointerSupport
        // is on — but that breaks InkCanvas DynamicRenderer (no wet-ink preview for pen).
        // Solution: intercept WM_POINTER touch at Win32 level, leaving WPF's stylus stack
        // unchanged so InkCanvas pen drawing works normally.
        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP)
            {
                uint id = GetWmPointerId(wParam);
                if (GetPointerInfo(id, out var info))
                {
                    if (info.pointerType == PT_TOUCH)
                    {
                        // Convert screen pixels → Scroll-relative logical coordinates
                        var scrollPt = Scroll.PointFromScreen(
                            new Point(info.ptPixelLocation.x, info.ptPixelLocation.y));
                        int key = (int)id;

                        if (msg == WM_POINTERDOWN)
                            HandleContactDown(key, scrollPt);
                        else if (msg == WM_POINTERUPDATE)
                            HandleContactMove(key, scrollPt);
                        else
                            HandleContactUp(key);
                    }
                    else if (info.pointerType == PT_PEN)
                    {
                        // Read pen pressure but DO NOT consume the message — InkCanvas
                        // still draws the wet ink via its own (WISPTIS) path.
                        CapturePenPressure(msg, id);
                    }
                }
            }
            return IntPtr.Zero; // never mark as handled — let default Win32 processing continue
        }

        // ── Pen pressure capture / application ────────────────────────────
        private void CapturePenPressure(int msg, uint id)
        {
            if (_isSaving) return;   // no more signing once Save was pressed
            if (msg == WM_POINTERDOWN)
            {
                _penSamples.Clear();
                _penCapturing = true;
            }
            if (!_penCapturing) return;

            if (GetPointerPenInfo(id, out var ppi))
            {
                double p = ppi.pressure / 1024.0;          // 0..1024 → 0..1
                _penSamples.Add((ppi.pointerInfo.ptPixelLocation.x,
                                 ppi.pointerInfo.ptPixelLocation.y, p));
            }

            if (msg == WM_POINTERUP)
                _penCapturing = false;  // keep samples until StrokeCollected consumes them
        }

        /// <summary>
        /// Overwrites each StylusPoint's PressureFactor of <paramref name="stroke"/> with the
        /// real pen pressure sampled from WM_POINTER during the stroke, matched by nearest
        /// screen position. No-op when no pen samples were captured (mouse/touch input), so
        /// the existing 0.5 neutral pressure is left untouched.
        /// </summary>
        private void ApplyCapturedPressure(Stroke stroke)
        {
            if (_penSamples.Count == 0) return;

            var spc = stroke.StylusPoints;
            double pMin = 1.0, pMax = 0.0;
            bool any = false;

            for (int i = 0; i < spc.Count; i++)
            {
                Point scr;
                try { scr = InkOverlay.PointToScreen(new Point(spc[i].X, spc[i].Y)); }
                catch { return; }   // visual not connected — leave pressure as-is

                double best = double.MaxValue, pr = 0.5;
                foreach (var s in _penSamples)
                {
                    double dx = s.sx - scr.X, dy = s.sy - scr.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; pr = s.pressure; }
                }

                // 0 = device reported no pressure → keep neutral so a flat pen never vanishes.
                float pf = pr <= 0.0 ? 0.5f : (float)Math.Clamp(pr, 0.02, 1.0);
                var sp = spc[i];
                sp.PressureFactor = pf;
                spc[i] = sp;

                if (pr < pMin) pMin = pr;
                if (pr > pMax) pMax = pr;
                any = true;
            }

            if (any)
                AppLogger.Info($"Pen pressure applied: {spc.Count} pts / {_penSamples.Count} samples, " +
                               $"pressure {pMin:F2}..{pMax:F2}");
            _penSamples.Clear();
        }

        // ── Shared gesture logic ──────────────────────────────────────────
        private void HandleContactDown(int key, Point pt)
        {
            // ── Dedup: same physical finger can arrive via BOTH TouchDown AND StylusDown ──
            const double SameFinger = 50.0;
            if (_activeTouches.Any(kv => PtDist(kv.Value, pt) < SameFinger))
                return;

            _activeTouches[key] = pt;

            if (_activeTouches.Count == 1)
            {
                _swipeStartPt   = pt;
                _swipeStartTime = DateTime.UtcNow;
                _isPinching     = false;
            }
            else if (_activeTouches.Count == 2 && !_isPinching)
            {
                _isPinching             = true;
                var pts                 = _activeTouches.Values.ToArray();
                _pinchStartDist         = PtDist(pts[0], pts[1]);
                _touchStartZoom         = _zoomFactor;
                _modeBeforePinchAll     = new List<InkCanvasEditingMode> { InkOverlay.EditingMode };
                InkOverlay.EditingMode  = InkCanvasEditingMode.None;
            }
        }

        private void HandleContactMove(int key, Point pt)
        {
            if (!_activeTouches.ContainsKey(key)) return;
            var prev = _activeTouches[key];
            _activeTouches[key] = pt;

            if (_isPinching && _activeTouches.Count >= 2 && _pinchStartDist > 0)
            {
                // ── Pinch-to-zoom (anchored on the gesture midpoint) ─────────
                var pts  = _activeTouches.Values.ToArray();
                double d = PtDist(pts[0], pts[1]);

                double oldZoom = _zoomFactor;
                double newZoom = Math.Clamp(_touchStartZoom * (d / _pinchStartDist), 0.05, 10.0);

                // Midpoint between the two fingers, in ScrollViewer (viewport) coordinates.
                double midX = (pts[0].X + pts[1].X) / 2.0;
                double midY = (pts[0].Y + pts[1].Y) / 2.0;

                // The unscaled content point currently under the midpoint.
                double contentX = (Scroll.HorizontalOffset + midX) / oldZoom;
                double contentY = (Scroll.VerticalOffset   + midY) / oldZoom;

                _zoomFactor = newZoom;
                ApplyZoom();

                // Force the ScrollViewer to recompute its extent at the new scale before we
                // re-position, otherwise the offsets clamp against the old (stale) extent.
                Scroll.UpdateLayout();

                // Re-anchor: keep that same content point under the fingers so the document
                // zooms toward the pinch centre instead of drifting upward.
                Scroll.ScrollToHorizontalOffset(contentX * newZoom - midX);
                Scroll.ScrollToVerticalOffset  (contentY * newZoom - midY);
            }
            else if (_activeTouches.Count == 1 && !_isPinching)
            {
                // ── Single-finger pan (no PanningMode → manual scroll) ───────
                Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset - (pt.X - prev.X));
                Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset - (pt.Y - prev.Y));
            }
        }

        private void HandleContactUp(int key)
        {
            if (!_activeTouches.ContainsKey(key)) return;
            var endPt = _activeTouches[key];
            _activeTouches.Remove(key);

            if (_isPinching)
            {
                // Don't restore until the LAST finger lifts.
                // Bug fix: the old code reset _isPinching on first-finger-up (count→1) without
                // restoring EditingMode (count≠0). Then on second-finger-up (count→0) _isPinching
                // was already false → EditingMode stayed stuck at None forever.
                if (_activeTouches.Count == 0)
                {
                    _isPinching = false;
                    if (_modeBeforePinchAll.Count > 0)
                        InkOverlay.EditingMode = _modeBeforePinchAll[0];
                    _modeBeforePinchAll.Clear();
                }
                return; // never treat any pinch-related lift as a swipe
            }

            if (_activeTouches.Count == 0 && !_isPinching && _activeField == null)
            {
                // ── Swipe-to-navigate ────────────────────────────────────────
                // Active only when page fits horizontally (no horizontal scroll →
                // ScrollableWidth ≈ 0). When zoomed in the user is panning, not navigating.
                if (Scroll.ScrollableWidth < 2)
                {
                    var elapsed = (DateTime.UtcNow - _swipeStartTime).TotalMilliseconds;
                    var dx      = endPt.X - _swipeStartPt.X;
                    var dy      = endPt.Y - _swipeStartPt.Y;

                    if (elapsed is > 0 and < 500
                        && Math.Abs(dx) >= 80
                        && Math.Abs(dx) > Math.Abs(dy) * 1.5
                        && Math.Abs(dx / elapsed) >= 0.4) // ≥ 0.4 px/ms = 400 px/s
                    {
                        DeactivateField(restoreScroll: false);
                        ScrollToPage(dx < 0 ? _currentPage + 1 : _currentPage - 1);
                    }
                }
            }
        }

        private static double PtDist(Point a, Point b) =>
            Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        // ----------------------------------------------------------------
        //  Save button guard
        // ----------------------------------------------------------------
        private void UpdateSaveButton()
        {
            if (_freehandMode)
            {
                bool hasStrokes = _pageStrokes.Values.Any(c => c.Count > 0);
                BtnSave.IsEnabled    = true;   // always enabled; no strokes → no signature appearance
                BtnNextSig.IsEnabled = false;
                TbStatus.Text = hasStrokes ? "" : UiLabels.StatusFreehandHint;
                return;
            }

            bool allRequiredSigned = _signatureFields
                .Where(f => f.Required)
                .All(f => f.IsSigned);

            BtnSave.IsEnabled    = true;       // always enabled; unsigned fields get invisible signature
            BtnNextSig.IsEnabled = _signatureFields.Any(f => !f.IsSigned);

            if (!allRequiredSigned)
            {
                int missing = _signatureFields.Count(f => f.Required && !f.IsSigned);
                TbStatus.Text = missing == 1
                    ? UiLabels.StatusMissingRequired1
                    : string.Format(UiLabels.StatusMissingRequiredN, missing);
            }
            else if (_signatureFields.Any())
            {
                TbStatus.Text = "";
            }
        }

        // ----------------------------------------------------------------
        //  Page rendering – continuous scroll
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the 0-based page index for a given absolute Y in the document canvas.
        /// Y is in bitmap-pixel space (before zoom).
        /// </summary>
        private int GetPageFromY(double y)
        {
            for (int i = _pageTopOffsets.Count - 1; i >= 0; i--)
                if (y >= _pageTopOffsets[i]) return i;
            return 0;
        }

        /// <summary>
        /// Computes and stores absolute RenderRects (Y measured from document top, not page top)
        /// for all field types on the given page.
        /// </summary>
        private void ComputeFieldRectsAbsolute(
            int pageIndex, double imgW, double imgH,
            double scaleX, double scaleY, double pageH)
        {
            double yOff = pageIndex < _pageTopOffsets.Count ? _pageTopOffsets[pageIndex] : 0;

            foreach (var cb in _checkboxes.Where(c => c.PageIndex == pageIndex))
                cb.RenderRect = new Rect(
                    cb.PdfLeft * scaleX,
                    yOff + (pageH - cb.PdfBottom - cb.PdfHeight) * scaleY,
                    cb.PdfWidth * scaleX, cb.PdfHeight * scaleY);

            foreach (var sf in _signatureFields.Where(f => f.PageIndex == pageIndex))
                sf.RenderRect = new Rect(
                    sf.FieldPdfX * scaleX,
                    yOff + (pageH - sf.FieldPdfY - sf.Height) * scaleY,
                    sf.Width * scaleX, sf.Height * scaleY);

            foreach (var pcb in _parsedCheckboxes.Where(f => f.PageIndex == pageIndex))
            {
                double w      = Math.Max(pcb.Width,  5.0);
                double h      = Math.Max(pcb.Height, 5.0);
                double left   = Math.Clamp(pcb.FieldPdfX * scaleX, 0, Math.Max(0, imgW - w * scaleX));
                double topRel = Math.Clamp((pageH - pcb.FieldPdfY - h) * scaleY, 0, Math.Max(0, imgH - h * scaleY));
                pcb.RenderRect = new Rect(left, yOff + topRel, w * scaleX, h * scaleY);
            }

            foreach (var df in _dateFields.Where(f => f.PageIndex == pageIndex))
                df.RenderRect = new Rect(
                    df.FieldPdfX * scaleX,
                    yOff + (pageH - df.FieldPdfY - df.FontSize) * scaleY,
                    df.FontSize * 0.60 * 16 * scaleX, df.FontSize * scaleY);

            foreach (var ldf in _locationDateFields.Where(f => f.PageIndex == pageIndex))
                ldf.RenderRect = new Rect(
                    ldf.FieldPdfX * scaleX,
                    yOff + (pageH - ldf.FieldPdfY - ldf.FontSize) * scaleY,
                    ldf.FontSize * 0.60 * 28 * scaleX, ldf.FontSize * scaleY);

            foreach (var lf in _locationFields.Where(f => f.PageIndex == pageIndex))
                lf.RenderRect = new Rect(
                    lf.FieldPdfX * scaleX,
                    yOff + (pageH - lf.FieldPdfY - lf.FontSize) * scaleY,
                    lf.FontSize * 0.60 * 20 * scaleX, lf.FontSize * scaleY);
        }

        /// <summary>
        /// Renders all PDF pages into PageImageStack and sizes the overlay canvases to
        /// the full document height.  Called once at startup.
        /// </summary>
        private async Task InitAllPages()
        {
            if (_renderer.PageCount == 0) return;

            double totalH = 0;
            double maxW   = 0;

            for (int pi = 0; pi < _renderer.PageCount; pi++)
            {
                int pageIndex = pi; // capture for closure
                var bmp = await Task.Run(() => _renderer.RenderPage(pageIndex, _renderDpi));

                double imgW = bmp.PixelWidth;
                double imgH = bmp.PixelHeight;
                _pageImageSizes[pageIndex] = new Size(imgW, imgH);

                var sizePts = _renderer.GetPageSizePts(pageIndex);
                double scaleX = imgW / sizePts.Width;
                double scaleY = imgH / sizePts.Height;
                _pageScales[pageIndex] = (scaleX, scaleY, sizePts.Height);

                // Record page top offset (gap added before every page except the first)
                if (pi > 0) totalH += PageGap;
                _pageTopOffsets.Add(totalH);
                totalH += imgH;
                maxW = Math.Max(maxW, imgW);

                // Compute absolute field positions for this page
                ComputeFieldRectsAbsolute(pageIndex, imgW, imgH, scaleX, scaleY, sizePts.Height);

                // Insert gap separator between pages
                if (pi > 0)
                    PageImageStack.Children.Add(new Border { Height = PageGap });

                // Add page image
                var img = new Image
                {
                    Source              = bmp,
                    Width               = imgW,
                    Height              = imgH,
                    Stretch             = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                    SnapsToDevicePixels = true,
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                PageImageStack.Children.Add(img);
            }

            // Resize PageContainer and all overlay canvases to full document dimensions
            PageContainer.Width  = maxW;
            PageContainer.Height = totalH;
            CheckboxCanvas.Width   = PreviewCanvas.Width   = InkOverlay.Width  = SigButtonCanvas.Width  = maxW;
            CheckboxCanvas.Height  = PreviewCanvas.Height  = InkOverlay.Height = SigButtonCanvas.Height = totalH;

            RedrawOverlays();
            UpdatePageUI();

            _ = Dispatcher.InvokeAsync(RecalcFitPage, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Scrolls the view so that the top of the given page is visible.</summary>
        private void ScrollToPage(int pageIndex)
        {
            pageIndex    = Math.Clamp(pageIndex, 0, _renderer.PageCount - 1);
            _currentPage = pageIndex;
            UpdatePageUI();

            if (pageIndex >= _pageTopOffsets.Count) return;
            double targetY = _pageTopOffsets[pageIndex] * _zoomFactor;
            Dispatcher.InvokeAsync(() => Scroll.ScrollToVerticalOffset(targetY),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Updates _currentPage based on which page is closest to the viewport centre.</summary>
        private void UpdateCurrentPageFromScroll()
        {
            if (_pageTopOffsets.Count == 0) return;

            double viewMidY = (Scroll.VerticalOffset + Scroll.ViewportHeight / 2) / _zoomFactor;
            int    page     = 0;
            double best     = double.MaxValue;

            for (int i = 0; i < _pageTopOffsets.Count; i++)
            {
                double pageMid = _pageTopOffsets[i] +
                    (_pageImageSizes.TryGetValue(i, out var sz) ? sz.Height / 2 : 0);
                double dist = Math.Abs(pageMid - viewMidY);
                if (dist < best) { best = dist; page = i; }
            }

            if (page != _currentPage) { _currentPage = page; UpdatePageUI(); }
        }

        private void UpdatePageUI()
        {
            TbPage.Text       = $"  {_currentPage + 1} / {_renderer.PageCount}  ";
            BtnPrev.IsEnabled = _currentPage > 0;
            BtnNext.IsEnabled = _currentPage < _renderer.PageCount - 1;
        }

        // ----------------------------------------------------------------
        //  Toolbar button handlers
        // ----------------------------------------------------------------
        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        { DeactivateField(restoreScroll: false); ScrollToPage(_currentPage - 1); }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        { DeactivateField(restoreScroll: false); ScrollToPage(_currentPage + 1); }

        private void BtnNextSig_Click(object sender, RoutedEventArgs e)
        {
            var pending = _signatureFields
                .Where(f => !f.IsSigned)
                .OrderBy(f => f.PageIndex)
                .ThenByDescending(f => f.FieldPdfY)
                .ToList();

            if (pending.Count == 0) return;

            SignatureFieldDescriptor next;
            if (_activeField != null)
            {
                int idx = pending.IndexOf(_activeField);
                next = (idx >= 0 && idx + 1 < pending.Count)
                    ? pending[idx + 1]
                    : pending[0];
            }
            else
            {
                next = pending[0];
            }

            DeactivateField(restoreScroll: false);
            ActivateField(next);
        }

        private void BtnClearPage_Click(object sender, RoutedEventArgs e)
        {
            DeactivateField(restoreScroll: false);
            _pageStrokes.Clear();
            _fieldStrokes.Clear();
            _strokeFieldMap.Clear();
            _strokePageMap.Clear();
            _strokeTimes.Clear();
            _signingOrder.Clear();
            InkOverlay.Strokes.Clear();

            foreach (var sf in _signatureFields) sf.IsSigned = false;

            _checkboxStates.Clear();
            foreach (var cb in _checkboxes) { cb.IsChecked = false; _checkboxStates[cb.FieldName] = false; }

            RedrawOverlays();
            UpdateSaveButton();
        }

        /// <summary>
        /// Re-enables ink input after a save was aborted or failed, so the user can sign
        /// and retry. In freehand mode drawing is active immediately; in field mode the
        /// canvas returns to idle (the user re-activates a field to draw).
        /// </summary>
        private void RestoreInkInputAfterSave()
        {
            _isSaving = false;
            if (_freehandMode)
            {
                InkOverlay.EditingMode      = InkCanvasEditingMode.Ink;
                InkOverlay.IsHitTestVisible = true;
            }
            else
            {
                InkOverlay.EditingMode      = InkCanvasEditingMode.None;
                InkOverlay.IsHitTestVisible = false;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // ── Signature type dialog ─────────────────────────────────
            string prefilledName = _cachedSignerName ?? "";
            var dlg = new SignatureTypeDialog(prefilledName) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            var    sigType    = dlg.SelectedType;
            string signerName = dlg.SignerName.Trim().Length > 0
                ? dlg.SignerName.Trim()
                : (prefilledName.Length > 0 ? prefilledName : "Signer");

            // Sign into a temporary working file first; ask for the final location only AFTER
            // signing succeeds (so the QES/ID-Austria flow runs before the "Save As" prompt).
            string outDir       = Path.GetDirectoryName(_pdfPath)!;
            string docBaseName  = Path.GetFileNameWithoutExtension(_pdfPath);
            string tempWorkPath = Path.Combine(Path.GetTempPath(),
                "miPDFsign_" + docBaseName + "_signing.pdf");
            string outPath      = tempWorkPath;   // pipeline writes here; set to the chosen path after Save-As

            // ── Lock UI ───────────────────────────────────────────────
            BtnSave.IsEnabled    = false;
            BtnClear.IsEnabled   = false;
            BtnNextSig.IsEnabled = false;
            TbStatus.Text        = UiLabels.StatusSaving;

            // Block any further signing once Save was pressed.
            _isSaving = true;
            InkOverlay.EditingMode      = InkCanvasEditingMode.None;
            InkOverlay.IsHitTestVisible = false;

            await Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            AppLogger.Info($"Save started: {Path.GetFileName(outPath)}, type={sigType}, signer='{signerName}'");

            try
            {
                // 1. Filter sig-field strokes out of page export (avoid double-rendering)
                var sigFieldStrokeSet = new HashSet<Stroke>(_strokeFieldMap.Keys);
                var filteredPageStrokes = _pageStrokes.ToDictionary(
                    kv => kv.Key,
                    kv => new StrokeCollection(kv.Value.Where(s => !sigFieldStrokeSet.Contains(s))));

                // 2. Export PDF (WPF rendering, must stay on UI thread)
                PdfExporter.Export(
                    _pdfPath, outPath,
                    filteredPageStrokes, _pageImageSizes, _checkboxStates,
                    _parsedCheckboxes, _parsedCheckboxStates,
                    _dateFields, _locationDateFields);

                // 3. Build FieldSignRequests (on UI thread – reads WPF Stroke objects)
                var fieldsToSign = BuildFieldSignRequests(signerName, outDir);

                // ── FES path (self-signed certificate) ────────────────
                if (sigType == SignatureType.FES)
                {
                    if (fieldsToSign.Count > 0)
                    {
                        PdfCertSigner.CertPair certPair = _certTask != null
                            ? await _certTask
                            : await Task.Run(() =>
                                PdfCertSigner.GenerateCertificatePublic(signerName));

                        // Regenerate the (background-)pre-generated certificate when it no
                        // longer fits: either the signer name changed, or the cert is close to
                        // expiry because the user lingered before saving. The cert is only valid
                        // for 10 minutes, so a stale one would place the signing timestamp
                        // outside its validity window and invalidate the signature.
                        // NotAfter is UTC-valued; compare ticks directly (no ToUniversalTime()).
                        bool nameMismatch = !certPair.Cert.SubjectDN.ToString()
                                .Contains(signerName, StringComparison.OrdinalIgnoreCase);
                        bool nearExpiry   = certPair.Cert.NotAfter <= DateTime.UtcNow.AddMinutes(2);
                        if (nameMismatch || nearExpiry)
                        {
                            if (nearExpiry)
                                AppLogger.Info("FES: pre-generated certificate near expiry – regenerating a fresh 10-minute cert");
                            certPair = await Task.Run(() =>
                                PdfCertSigner.GenerateCertificatePublic(signerName));
                        }

                        string name   = signerName;
                        var    fields = fieldsToSign;
                        // Encrypt biometric data with the persistent biometric key (same as QES)
                        // so it stays decryptable — NOT with the ephemeral 10-minute signing key.
                        var bioKey = BiometricCertManager.GetEncryptionPublicKey(outDir, docBaseName);
                        // SignFields returns the CMS bytes directly — avoids re-reading and
                        // re-parsing the PDF (ExtractLastCmsBytes was fragile: it matched
                        // wrong /Contents entries in PDFs with pre-existing page content streams).
                        byte[] fesCms = await Task.Run(() => PdfCertSigner.SignFields(outPath, name, fields, certPair, bioKey));

                        // PAdES-LT: embed DSS (self-signed cert has no CRL/OCSP, but
                        // TSA cert chain from signature-time-stamp is still embedded)
                        TbStatus.Text = UiLabels.StatusQesFetchingRevocation;
                        byte[] fesBytes = await Task.Run(() => File.ReadAllBytes(outPath));
                        fesBytes        = await PdfCertSigner.AddLtv(fesBytes, fesCms);

                        // PAdES-LTA: document-level archive timestamp
                        TbStatus.Text = UiLabels.StatusQesAddingArchive;
                        fesBytes      = await PdfCertSigner.AddArchiveTimestamp(fesBytes);

                        await Task.Run(() => File.WriteAllBytes(outPath, fesBytes));
                    }
                }
                // ── QES path (ID-Austria / A-Trust Security Layer) ────
                else
                {
                    if (fieldsToSign.Count > 0)
                    {
                        // Get biometric encryption key (auto-generates PFX if not configured)
                        var bioKey = BiometricCertManager.GetEncryptionPublicKey(outDir, docBaseName);
                        var rng    = new Org.BouncyCastle.Security.SecureRandom();

                        // Collect all biometric points across all fields
                        var allBioPoints = fieldsToSign
                            .SelectMany(f => f.BioPoints)
                            .ToList();

                        // Encrypt biometric data
                        byte[] rawBio = await Task.Run(() => SerializeBio(allBioPoints));
                        byte[] encBio = await Task.Run(() => HybridEncryptBio(rawBio, bioKey, rng));

                        // Prepare signature placeholder using first signed field
                        var req = fieldsToSign[0];
                        var (bytesToSign, placeholderPdf) = await Task.Run(() =>
                        {
                            byte[] placeholder;
                            byte[] bytes = PdfCertSigner.PrepareQesSigning(
                                outPath, req, encBio, out placeholder);
                            return (bytes, placeholder);
                        });

                        // Show A-Trust ID-Austria dialog
                        var atWin = new IdAustriaWindow(placeholderPdf) { Owner = this };
                        bool signed = atWin.ShowDialog() == true;
                        byte[]? cms = atWin.SignedCms;

                        if (!signed || cms == null)
                        {
                            TbStatus.Text = UiLabels.StatusQesAborted;
                            BtnSave.IsEnabled  = true;
                            BtnClear.IsEnabled = false;
                            try { if (File.Exists(tempWorkPath)) File.Delete(tempWorkPath); } catch { }
                            RestoreInkInputAfterSave();   // allow signing again after abort
                            return;
                        }

                        // Add signature-time-stamp to CMS (PAdES-B-T: proves time of signing)
                        TbStatus.Text = UiLabels.StatusQesAddingTimestamp;
                        cms = await PdfCertSigner.AddSignatureTimestamp(cms);

                        // Inject CMS into placeholder PDF
                        byte[] finalPdf = await Task.Run(() =>
                            PdfCertSigner.InjectQesCms(placeholderPdf, cms));

                        // Fetch CRL / OCSP and embed DSS (PAdES-LT)
                        TbStatus.Text = UiLabels.StatusQesFetchingRevocation;
                        finalPdf = await PdfCertSigner.AddLtv(finalPdf, cms);

                        // Add document timestamp over the complete document incl. DSS (PAdES-LTA)
                        TbStatus.Text = UiLabels.StatusQesAddingArchive;
                        finalPdf = await PdfCertSigner.AddArchiveTimestamp(finalPdf);

                        await Task.Run(() => File.WriteAllBytes(outPath, finalPdf));
                    }
                }

                // ── Ask for the final destination now that signing succeeded ──
                string? finalPath = SaveTargetHelper.ResolveOutputPath(_pdfPath);
                if (finalPath == null)
                {
                    // User cancelled the Save-As dialog after signing → discard the result.
                    AppLogger.Info("Save aborted after signing: user cancelled Save-As dialog");
                    try { if (File.Exists(tempWorkPath)) File.Delete(tempWorkPath); } catch { }
                    TbStatus.Text      = "";
                    BtnSave.IsEnabled  = true;
                    BtnClear.IsEnabled = false;
                    RestoreInkInputAfterSave();
                    return;
                }
                if (!string.Equals(finalPath, tempWorkPath, StringComparison.OrdinalIgnoreCase))
                    File.Move(tempWorkPath, finalPath, overwrite: true);
                outPath = finalPath;

                AppLogger.Info($"Save successful: {Path.GetFileName(outPath)}");
                TbStatus.Text = string.Format(UiLabels.StatusSavedFormat, Path.GetFileName(outPath));
                System.Media.SystemSounds.Exclamation.Play();
                Close();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Save failed: {Path.GetFileName(outPath)}", ex);
                try { if (File.Exists(tempWorkPath)) File.Delete(tempWorkPath); } catch { }
                BtnSave.IsEnabled  = true;
                BtnClear.IsEnabled = false;
                TbStatus.Text      = "";
                RestoreInkInputAfterSave();   // allow signing again after a failed save
                MessageBox.Show(string.Format(UiLabels.MsgSaveError, ex.Message),
                    UiLabels.MsgSaveErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ----------------------------------------------------------------
        //  Build FieldSignRequests from current stroke state
        // ----------------------------------------------------------------
        private List<PdfCertSigner.FieldSignRequest> BuildFieldSignRequests(
            string signerName, string outputDir)
        {
            var result = new List<PdfCertSigner.FieldSignRequest>();

            if (_freehandMode)
            {
                var allStrokes = _pageStrokes.Values.SelectMany(c => c).ToList();
                if (allStrokes.Count == 0)
                {
                    // No strokes → invisible signature at a default position on page 1
                    AppLogger.Info("Freehand mode: no strokes → invisible signature at default rect");
                    result.Add(new PdfCertSigner.FieldSignRequest(
                        FreehandFieldName,
                        new List<PdfCertSigner.BiometricPoint>(),
                        null,
                        1, 0f, 0f, 10f, 10f));
                    return result;
                }

                // Grouping thresholds:
                // - Time: strokes within 3 s of each other belong to the same signature
                //   (covers multi-part sigs like "Vorname + Nachname" with natural pen-lifts).
                // - Space: if the new stroke's bounding box is more than SpatialGapPx canvas units
                //   away from the accumulated group bounding box, it starts a new group regardless
                //   of the time gap — i.e. clearly separate areas → separate signature fields.
                const long   GroupGapMs     = 3000;
                const double SpatialGapPx   = 200.0; // canvas units; tune if needed

                // Collect all strokes across all pages, sorted by time.
                var allSortedStrokes = _pageStrokes
                    .SelectMany(kv => kv.Value.Select(s => (page: kv.Key, stroke: s)))
                    .OrderBy(t => _strokeTimes.GetValueOrDefault(t.stroke, 0))
                    .ToList();

                // Group consecutive strokes by BOTH time gap AND spatial proximity.
                // A new group starts when either threshold is exceeded.
                var groups      = new List<List<(int page, Stroke stroke)>>();
                var groupBounds = new List<Rect>(); // accumulated bounding box per group

                foreach (var item in allSortedStrokes)
                {
                    long t            = _strokeTimes.GetValueOrDefault(item.stroke, 0);
                    Rect strokeBounds = item.stroke.GetBounds();

                    bool startNew = true;
                    if (groups.Count > 0)
                    {
                        long lastT      = _strokeTimes.GetValueOrDefault(groups[^1][^1].stroke, 0);
                        bool timeOk     = (t - lastT) <= GroupGapMs;
                        bool spatialOk  = StrokeBoundsDistance(groupBounds[^1], strokeBounds) <= SpatialGapPx;
                        startNew = !timeOk || !spatialOk;
                        if (startNew)
                            AppLogger.Info($"    Group split: timeGap={t - lastT}ms, " +
                                $"spatialGap={StrokeBoundsDistance(groupBounds[^1], strokeBounds):F0}px");
                    }

                    if (startNew)
                    {
                        groups.Add(new List<(int, Stroke)> { item });
                        groupBounds.Add(strokeBounds);
                    }
                    else
                    {
                        groups[^1].Add(item);
                        groupBounds[^1] = Rect.Union(groupBounds[^1], strokeBounds);
                    }
                }

                AppLogger.Info($"Freehand: {allSortedStrokes.Count} stroke(s) → {groups.Count} group(s)");

                // Total number of groups across all pages — used to decide whether to include page/group suffixes.
                bool multiGroup = groups.Count > 1;
                // Check if there are strokes on more than one page across all groups.
                bool multiPage  = allSortedStrokes.Select(t => t.page).Distinct().Count() > 1;

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var group = groups[gi];

                    // Determine the page(s) this group touches.
                    // If a group spans pages (unusual but possible), split by page within the group.
                    var pageStrokesInGroup = group
                        .GroupBy(t => t.page)
                        .OrderBy(g => g.Key)
                        .ToList();

                    foreach (var pageSub in pageStrokesInGroup)
                    {
                        int pageIndex = pageSub.Key;
                        var strokes   = pageSub.Select(t => t.stroke).ToList();

                        if (!_pageScales.TryGetValue(pageIndex, out var ps)) continue;
                        double pageYOff = pageIndex < _pageTopOffsets.Count
                            ? _pageTopOffsets[pageIndex] : 0;

                        // Field naming convention:
                        //   single group, single page  → __freehand__
                        //   single group, multi-page   → __freehand__<pageIdx>
                        //   multi-group                → __freehand__<pageIdx>_<groupIdx>
                        string fieldName = (!multiGroup && !multiPage)
                            ? FreehandFieldName
                            : (!multiGroup)
                                ? $"{FreehandFieldName}{pageIndex}"
                                : $"{FreehandFieldName}{pageIndex}_{gi}";

                        var sc = new StrokeCollection(strokes);
                        Rect bounds = sc.GetBounds();
                        bounds.Inflate(5, 5);

                        // Compute relative timestamps from the first point of this group.
                        long groupStartMs = _strokeTimes.GetValueOrDefault(group[0].stroke, 0);
                        var bioPoints = strokes
                            .SelectMany(s =>
                            {
                                long strokeStartMs = _strokeTimes.GetValueOrDefault(s, groupStartMs);
                                // Distribute the stroke's points evenly across the stroke duration;
                                // we only have one timestamp per stroke (at collection time), so
                                // individual points get the same relative timestamp as the stroke.
                                float relMs = Math.Max(0f, strokeStartMs - groupStartMs);
                                return s.StylusPoints.Cast<StylusPoint>()
                                    .Select(sp => new PdfCertSigner.BiometricPoint(
                                        (float)sp.X, (float)sp.Y, (float)sp.PressureFactor, relMs));
                            })
                            .ToList();

                        float pdfX = (float)(bounds.Left                        / ps.sx);
                        float pdfY = (float)(ps.pageH - (bounds.Bottom - pageYOff) / ps.sy);
                        float pdfW = (float)(bounds.Width                       / ps.sx);
                        float pdfH = (float)(bounds.Height                      / ps.sy);

                        byte[] png = RenderFieldStrokesToPng(sc, bounds);
                        var apStrokes = BuildAppearanceStrokes(sc, bounds, ps.sx, ps.sy);
                        result.Add(new PdfCertSigner.FieldSignRequest(
                            fieldName, bioPoints, png,
                            pageIndex + 1, pdfX, pdfY, pdfW, pdfH, apStrokes));

                        AppLogger.Info($"Freehand: group {gi}, page {pageIndex + 1}, " +
                                       $"{strokes.Count} stroke(s) → field '{fieldName}' " +
                                       $"at ({pdfX:F0},{pdfY:F0},{pdfW:F0},{pdfH:F0})");
                    }
                }

                return result;
            }

            // Normal mode: one request per field; unsigned fields get invisible signature (null appearance)
            foreach (var sf in _signatureFields)
            {
                _fieldStrokes.TryGetValue(sf.FieldName, out var fc);
                AppLogger.Debug($"  Building sign request for '{sf.FieldName}': " +
                                $"{fc?.Count ?? 0} stroke(s), page {sf.PageIndex + 1}");

                var bioPoints = new List<PdfCertSigner.BiometricPoint>();
                if (fc != null)
                    foreach (var stroke in fc)
                        foreach (StylusPoint sp in stroke.StylusPoints)
                            bioPoints.Add(new PdfCertSigner.BiometricPoint(
                                (float)sp.X, (float)sp.Y, (float)sp.PressureFactor));

                byte[]? appearancePng = null;
                IReadOnlyList<IReadOnlyList<PdfCertSigner.AppearancePoint>>? appearanceStrokes = null;
                float pdfX = (float)sf.FieldPdfX;
                float pdfY = (float)sf.FieldPdfY;
                float pdfW = (float)sf.Width;
                float pdfH = (float)sf.Height;

                if (fc != null && fc.Count > 0
                    && _pageScales.TryGetValue(sf.PageIndex, out var ps))
                {
                    const double pad = 5;
                    Rect sb = fc.GetBounds();
                    sb.Inflate(pad, pad);

                    // sb is in absolute document Y; subtract the page's top offset to get
                    // page-relative Y before converting to PDF point coordinates.
                    double pageYOff = sf.PageIndex < _pageTopOffsets.Count
                        ? _pageTopOffsets[sf.PageIndex] : 0;

                    pdfX = (float)(sb.Left                       / ps.sx);
                    pdfY = (float)(ps.pageH - (sb.Bottom - pageYOff) / ps.sy);
                    pdfW = (float)(sb.Width                      / ps.sx);
                    pdfH = (float)(sb.Height                     / ps.sy);

                    appearancePng = RenderFieldStrokesToPng(fc, sb);
                    appearanceStrokes = BuildAppearanceStrokes(fc, sb, ps.sx, ps.sy);
                    AppLogger.Debug($"    PDF rect=({pdfX:F0},{pdfY:F0},{pdfW:F0},{pdfH:F0})  " +
                                    $"bio points={bioPoints.Count}  " +
                                    $"png={(appearancePng?.Length ?? 0)} bytes");
                }
                else
                {
                    AppLogger.Warn($"  '{sf.FieldName}': no strokes or page scale missing – " +
                                   "using marker rect, no appearance PNG");
                }

                result.Add(new PdfCertSigner.FieldSignRequest(
                    sf.FieldName, bioPoints, appearancePng,
                    sf.PageIndex + 1, pdfX, pdfY, pdfW, pdfH, appearanceStrokes));
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  Convert WPF ink strokes → signature appearance-box coordinates
        //
        //  Produces per-stroke point lists in the signature appearance box:
        //  top-left origin, Y growing downwards, units = PDF points, range
        //  [0..PdfW] × [0..PdfH]. These map 1:1 onto Syncfusion's appearance
        //  graphics so PdfCertSigner can draw transparent vector ink.
        //
        //  box      = the canvas-space bounding rectangle the strokes were
        //             scaled against (already padding-inflated by the caller).
        //  sx / sy  = canvas-units-per-PDF-point for this page (_pageScales).
        // ----------------------------------------------------------------
        private static List<IReadOnlyList<PdfCertSigner.AppearancePoint>> BuildAppearanceStrokes(
            StrokeCollection strokes, Rect box, double sx, double sy)
        {
            var result = new List<IReadOnlyList<PdfCertSigner.AppearancePoint>>();
            if (sx <= 0 || sy <= 0) return result;

            foreach (var s in strokes)
            {
                var pts = new List<PdfCertSigner.AppearancePoint>(s.StylusPoints.Count);
                foreach (StylusPoint sp in s.StylusPoints)
                    pts.Add(new PdfCertSigner.AppearancePoint(
                        (float)((sp.X - box.Left) / sx),
                        (float)((sp.Y - box.Top)  / sy),
                        (float)sp.PressureFactor));   // 0..1, drives pressure-sensitive width
                if (pts.Count > 0)
                    result.Add(pts);
            }
            return result;
        }

        // ----------------------------------------------------------------
        //  Biometric data helpers (QES path)
        // ----------------------------------------------------------------
        private static byte[] SerializeBio(IReadOnlyList<PdfCertSigner.BiometricPoint> pts)
        {
            using var ms = new System.IO.MemoryStream();
            using var bw = new System.IO.BinaryWriter(ms);
            bw.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            bw.Write(pts.Count);
            foreach (var p in pts) { bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Pressure); }
            return ms.ToArray();
        }

        private static byte[] HybridEncryptBio(
            byte[] data,
            Org.BouncyCastle.Crypto.Parameters.RsaKeyParameters pubKey,
            Org.BouncyCastle.Security.SecureRandom rng)
        {
            var aesKey = new byte[32]; var iv = new byte[16];
            rng.NextBytes(aesKey); rng.NextBytes(iv);
            var aesCipher = Org.BouncyCastle.Security.CipherUtilities.GetCipher("AES/CBC/PKCS7Padding");
            aesCipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(
                new Org.BouncyCastle.Crypto.Parameters.KeyParameter(aesKey), iv));
            byte[] encData = aesCipher.DoFinal(data);
            var oaep = new Org.BouncyCastle.Crypto.Encodings.OaepEncoding(
                new Org.BouncyCastle.Crypto.Engines.RsaEngine());
            oaep.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithRandom(pubKey, rng));
            byte[] encKey = oaep.ProcessBlock(aesKey, 0, aesKey.Length);
            using var ms = new System.IO.MemoryStream();
            using var bw = new System.IO.BinaryWriter(ms);
            bw.Write(encKey.Length); bw.Write(encKey); bw.Write(iv); bw.Write(encData);
            return ms.ToArray();
        }

        // ----------------------------------------------------------------
        //  Rendering helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Minimum Euclidean distance between two bounding boxes (0 if they overlap or touch).
        /// Used to decide whether a new stroke is spatially close enough to join an existing group.
        /// </summary>
        private static double StrokeBoundsDistance(Rect a, Rect b)
        {
            double dx = Math.Max(0, Math.Max(b.Left - a.Right,  a.Left - b.Right));
            double dy = Math.Max(0, Math.Max(b.Top  - a.Bottom, a.Top  - b.Bottom));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static byte[] RenderFieldStrokesToPng(StrokeCollection strokes, Rect bounds)
        {
            int w = Math.Max(1, (int)Math.Ceiling(bounds.Width));
            int h = Math.Max(1, (int)Math.Ceiling(bounds.Height));

            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new System.Windows.Media.TranslateTransform(-bounds.Left, -bounds.Top));
                foreach (var stroke in strokes)
                    stroke.Draw(dc);
                dc.Pop();
            }

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(dv);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // ----------------------------------------------------------------
        //  Coordinate helpers
        // ----------------------------------------------------------------
        private Point GetPagePosition(InputEventArgs e)
        {
            try
            {
                if (PresentationSource.FromVisual(PageContainer) == null) return new Point();

                // GetPosition(PageContainer) does NOT reliably invert LayoutTransform
                // when PageContainer is inside a ScrollViewer + centering Grid.
                // Round-tripping through physical screen coordinates is the safe path:
                // PointFromScreen accounts for DPI, scroll offset, centering, AND zoom.
                Point winPt = e switch
                {
                    StylusEventArgs se => se.GetPosition(this),
                    MouseEventArgs  me => me.GetPosition(this),
                    _                  => new Point(),
                };
                return PageContainer.PointFromScreen(PointToScreen(winPt));
            }
            catch (InvalidOperationException) { return new Point(); }
        }

        // ── Window control buttons ────────────────────────────────────────────

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void UpdateMaximizeRestoreIcon()
        {
            if (BtnMaximizeRestore == null) return;
            // □ = maximize  ❐ = restore-down
            BtnMaximizeRestore.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ── Custom title bar drag / double-click ──────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            // Double-click toggles maximise/restore
            if (e.ClickCount == 2)
            {
                MaximizeRestoreBtn_Click(sender, e);
                return;
            }

            if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;

            // Restore from maximised first so the window can be dragged freely
            if (WindowState == WindowState.Maximized)
            {
                // Keep the window under the cursor when restoring
                double mouseX = e.GetPosition(this).X;
                WindowState = WindowState.Normal;
                Left = mouseX - Width / 2;
                Top  = 0;
            }
            DragMove();
        }
    }

}
