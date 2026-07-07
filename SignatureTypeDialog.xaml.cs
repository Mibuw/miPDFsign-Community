using System.Windows;
using System.Windows.Media;
using miPDFsign.Helpers;

namespace miPDFsign
{
    public enum SignatureType { FES, QES }

    public partial class SignatureTypeDialog : Window
    {
        // ----------------------------------------------------------------
        //  Result properties (read after ShowDialog() returns true)
        // ----------------------------------------------------------------
        public SignatureType SelectedType { get; private set; } = SignatureType.FES;
        public string        SignerName   { get; private set; } = "";

        // ----------------------------------------------------------------
        //  Constructor
        // ----------------------------------------------------------------
        public SignatureTypeDialog(string prefilledName = "")
        {
            InitializeComponent();

            Title              = UiLabels.DlgSigTypeTitle;
            TbHeader.Text      = UiLabels.DlgSigTypeHeader;
            TbQesTitle.Text    = UiLabels.DlgSigTypeQesTitle;
            TbQesSub.Text      = UiLabels.DlgSigTypeQesSub;
            TbFesTitle.Text    = UiLabels.DlgSigTypeFesTitle;
            TbFesSub.Text      = UiLabels.DlgSigTypeFesSub;
            TbFesNameLabel.Text = UiLabels.DlgSigTypeFesNameLabel;
            BtnCancel.Content  = UiLabels.DlgSigTypeBtnCancel;
            BtnOk.Content      = UiLabels.DlgSigTypeBtnOk;

            TbName.Text = prefilledName;
            UpdateCardHighlight();
            UpdateOkButton();
        }

        // ----------------------------------------------------------------
        //  Radio-button / card events
        // ----------------------------------------------------------------
        private void RbFes_Checked(object sender, RoutedEventArgs e)
        {
            UpdateCardHighlight();
            UpdateOkButton();
        }

        private void RbQes_Checked(object sender, RoutedEventArgs e)
        {
            UpdateCardHighlight();
            UpdateOkButton();
        }

        private void CardFes_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RbFes.IsChecked = true;
        }

        private void CardQes_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RbQes.IsChecked = true;
        }

        private void TbName_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e) => UpdateOkButton();

        // ----------------------------------------------------------------
        //  Dialog buttons
        // ----------------------------------------------------------------
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedType = (RbQes.IsChecked == true) ? SignatureType.QES : SignatureType.FES;
            SignerName   = TbName.Text.Trim();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ----------------------------------------------------------------
        //  UI helpers
        // ----------------------------------------------------------------
        private void UpdateCardHighlight()
        {
            // Guard: called during InitializeComponent() before all controls are ready
            if (CardQes == null || CardFes == null || TbName == null) return;

            bool qes = RbQes.IsChecked == true;
            var active   = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF));
            var inactive = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC));

            CardQes.BorderBrush = qes  ? active : inactive;
            CardFes.BorderBrush = !qes ? active : inactive;

            TbName.IsEnabled = !qes;
        }

        private void UpdateOkButton()
        {
            if (BtnOk == null || TbName == null) return;

            bool qes      = RbQes.IsChecked == true;
            bool fesReady = !qes && TbName.Text.Trim().Length > 0;
            BtnOk.IsEnabled = qes || fesReady;
        }
    }
}
