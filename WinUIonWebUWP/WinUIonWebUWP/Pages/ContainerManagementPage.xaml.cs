using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.UI.StartScreen;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class ContainerManagementPage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();

        public ObservableCollection<ContainerItemViewModel> Containers { get; } = new ObservableCollection<ContainerItemViewModel>();

        public ContainerManagementPage()
        {
            this.InitializeComponent();
            this.Loaded += ContainerManagementPage_Loaded;
        }

        private void ContainerManagementPage_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshContainers();
        }

        private void RefreshContainers()
        {
            Containers.Clear();
            foreach (var container in SettingsManager.Instance.Containers)
            {
                Containers.Add(new ContainerItemViewModel(container, MainPage.Current?.ContainerId));
            }
        }

        private async void OpenContainerButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId)
            {
                await App.LaunchContainerInNewViewAsync(containerId);
            }
        }

        private async void SaveContainerButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is not string containerId
                || FindItem(containerId) is not ContainerItemViewModel item
                || !Uri.TryCreate(item.EditHomeUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "edge"))
            {
                return;
            }

            SettingsManager.Instance.UpdateContainer(containerId, item.EditDisplayName, item.EditHomeUrl);
            if (MainPage.Current?.ContainerId == containerId)
            {
                MainPage.Current.RefreshContainerIdentity();
            }

            RefreshContainers();
            await Task.CompletedTask;
        }

        private async void RefreshContainerIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId
                && MainPage.Current?.ContainerId == containerId)
            {
                await MainPage.Current.RefreshCurrentContainerIconAsync();
                RefreshContainers();
            }
        }

        private async void ClearContainerDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is not string containerId)
            {
                return;
            }

            try
            {
                var folderPath = SettingsManager.Instance.GetContainerWebViewDataFolder(containerId);
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Containers] Clear data failed: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private async void UnpinContainerButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId && SecondaryTile.Exists(containerId))
            {
                var tile = new SecondaryTile(containerId);
                try
                {
                    await tile.RequestDeleteAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Containers] Unpin failed: {ex.Message}");
                }
            }
        }

        private void DeleteContainerButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId
                && SettingsManager.Instance.DeleteContainer(containerId))
            {
                RefreshContainers();
            }
        }

        private void ContainerTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox box
                && box.DataContext is ContainerItemViewModel item)
            {
                if (box.Name == "ContainerNameBox")
                {
                    item.EditDisplayName = box.Text;
                }
                else if (box.Name == "ContainerUrlBox")
                {
                    item.EditHomeUrl = box.Text;
                }
            }
        }

        private ContainerItemViewModel? FindItem(string containerId)
        {
            foreach (var item in Containers)
            {
                if (string.Equals(item.Id, containerId, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static string? GetContainerId(object sender)
        {
            return sender is FrameworkElement element && element.Tag is string containerId
                ? containerId
                : null;
        }
    }

    public sealed class ContainerItemViewModel : INotifyPropertyChanged
    {
        public ContainerItemViewModel(WebContainer container, string? currentContainerId)
        {
            Id = container.Id;
            DisplayName = string.IsNullOrWhiteSpace(container.DisplayName)
                ? container.HomeUrl
                : container.DisplayName;
            HomeUrl = container.HomeUrl;
            EditDisplayName = DisplayName;
            EditHomeUrl = HomeUrl;
            CanDelete = !SettingsManager.Instance.IsDefaultContainer(Id);
            OpenVisibility = string.Equals(Id, currentContainerId, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
            IconUri = SettingsManager.Instance.GetContainerIconUri(Id);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string DisplayName { get; }
        public string HomeUrl { get; }
        public string EditDisplayName { get; set; }
        public string EditHomeUrl { get; set; }
        public bool CanDelete { get; }
        public Visibility OpenVisibility { get; }
        public Uri IconUri { get; }
    }
}
