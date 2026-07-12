using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Dialogs
{
    public sealed partial class CreateDesktopShortcutDialog : ContentDialog
    {
        private readonly ResourceLoader _loader = new ResourceLoader();

        public CreateDesktopShortcutDialog(IEnumerable<WebContainer> containers)
        {
            InitializeComponent();

            Title = _loader.GetString("CreateDesktopShortcutDialogTitle");
            PrimaryButtonText = _loader.GetString("CreateDesktopShortcutDialogPrimaryButtonText");
            SecondaryButtonText = _loader.GetString("CreateDesktopShortcutDialogSecondaryButtonText");
            IsPrimaryButtonEnabled = false;

            var items = containers
                .Where(item => !SettingsManager.Instance.IsDefaultContainer(item.Id))
                .Select(item => new ShortcutContainerChoiceViewModel(item))
                .ToList();

            ContainersListView.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public string SelectedContainerId { get; private set; } = "";

        private void ContainersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedContainerId = ContainersListView.SelectedItem is ShortcutContainerChoiceViewModel item
                ? item.Id
                : "";
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(SelectedContainerId);
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = string.IsNullOrWhiteSpace(SelectedContainerId);
        }
    }

    public sealed class ShortcutContainerChoiceViewModel
    {
        public ShortcutContainerChoiceViewModel(WebContainer container)
        {
            Id = container.Id;
            DisplayName = SettingsManager.Instance.GetContainerDisplayName(Id);
            HomeUrl = SettingsManager.Instance.GetContainerHomeUrl(Id);
            IconUri = SettingsManager.Instance.GetContainerIconUri(Id);
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string HomeUrl { get; }
        public Uri IconUri { get; }
    }
}
