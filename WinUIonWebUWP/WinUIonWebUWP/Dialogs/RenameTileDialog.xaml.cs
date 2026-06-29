using System.Collections.Generic;
using System.Linq;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Dialogs
{
    public sealed partial class RenameTileDialog : ContentDialog
    {
        private readonly string _originalName;
        private readonly IReadOnlyList<string> _reservedNames;

        public RenameTileDialog(string proposedName, IReadOnlyList<string> reservedNames)
        {
            this.InitializeComponent();
            _originalName = proposedName?.Trim() ?? "";
            _reservedNames = reservedNames;
            NameTextBox.Text = _originalName;
        }

        public string TileName => NameTextBox.Text.Trim();

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(TileName)
                || string.Equals(TileName, _originalName, StringComparison.OrdinalIgnoreCase)
                || _reservedNames.Any(item => string.Equals(item, TileName, StringComparison.OrdinalIgnoreCase)))
            {
                NameErrorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        }

        private void NameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            NameErrorText.Visibility = Visibility.Collapsed;
        }
    }
}
