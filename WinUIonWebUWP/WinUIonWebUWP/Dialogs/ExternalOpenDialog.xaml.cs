using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Dialogs
{
    public sealed partial class ExternalOpenDialog : ContentDialog
    {
        public bool UserConfirmed { get; private set; }

        public ExternalOpenDialog()
        {
            this.InitializeComponent();
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Primary = 否
            UserConfirmed = false;
        }

        private void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Secondary = 是
            UserConfirmed = true;
        }
    }
}