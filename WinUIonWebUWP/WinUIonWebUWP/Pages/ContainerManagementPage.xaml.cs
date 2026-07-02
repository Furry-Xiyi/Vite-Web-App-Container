using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.UI.StartScreen;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

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

        private async void ContainerManagementPage_Loaded(object sender, RoutedEventArgs e)
        {
            var mainPage = MainPage.Current;
            if (mainPage?.GetContainerPageForSettings() != null)
            {
                await mainPage.RefreshCurrentContainerIconAsync();
            }

            RefreshContainers();
        }

        private void RefreshContainers()
        {
            Containers.Clear();
            foreach (var container in SettingsManager.Instance.Containers.Where(item => !SettingsManager.Instance.IsDefaultContainer(item.Id)))
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
                || FindItem(containerId) is not ContainerItemViewModel item)
            {
                return;
            }

            var displayName = item.EditDisplayName;
            var homeUrl = item.EditHomeUrl;
            if (TryGetContainerEditValues(sender, out var editedDisplayName, out var editedHomeUrl))
            {
                displayName = editedDisplayName;
                homeUrl = editedHomeUrl;
            }

            if (!Uri.TryCreate(homeUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "edge"))
            {
                return;
            }

            SettingsManager.Instance.UpdateContainer(containerId, displayName, homeUrl);
            if (MainPage.Current?.ContainerId == containerId)
            {
                MainPage.Current.RefreshContainerIdentity();
            }

            RefreshContainers();
            await Task.CompletedTask;
        }

        private async void RefreshContainerIconButton_Click(object sender, RoutedEventArgs e)
        {
            var mainPage = MainPage.Current;
            if (GetContainerId(sender) is string containerId
                && mainPage?.ContainerId == containerId)
            {
                await mainPage.RefreshCurrentContainerIconAsync();
                RefreshContainers();
            }
        }

        private async void ClearContainerDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is not string containerId)
            {
                return;
            }

            await ClearContainerProfileFolderFromButtonAsync(sender, containerId);
        }

        private async void ClearContainerCacheButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is not string containerId)
            {
                return;
            }

            await ClearContainerProfileFolderFromButtonAsync(sender, containerId);
        }

        private void ReinjectContainerScriptsButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId
                && string.Equals(MainPage.Current?.ContainerId, containerId, StringComparison.OrdinalIgnoreCase))
            {
                MainPage.Current?.GetContainerPageForSettings()?.ReinjectHostScripts();
            }
        }

        private void OpenContainerDevToolsButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId
                && string.Equals(MainPage.Current?.ContainerId, containerId, StringComparison.OrdinalIgnoreCase))
            {
                MainPage.Current?.GetContainerPageForSettings()?.OpenDevToolsWindow();
            }
        }

        private void CopyContainerDiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is not string containerId
                || FindItem(containerId) is not ContainerItemViewModel item)
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(item.CreateDiagnosticsText());
            Clipboard.SetContent(dataPackage);
        }

        private static async Task ClearContainerProfileFolderFromButtonAsync(object sender, string containerId)
        {
            var control = sender as Control;
            if (control != null)
            {
                control.IsEnabled = false;
            }

            try
            {
                await ClearContainerProfileFolderAsync(containerId);
            }
            finally
            {
                if (control != null)
                {
                    control.IsEnabled = true;
                }
            }
        }

        private static async Task ClearContainerProfileFolderAsync(string containerId)
        {
            try
            {
                var folderPath = SettingsManager.Instance.GetContainerWebViewDataFolder(containerId);
                await Task.Run(() =>
                {
                    if (Directory.Exists(folderPath))
                    {
                        Directory.Delete(folderPath, true);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Containers] Clear data failed: {ex.Message}");
            }
        }

        private async void ToggleContainerPinButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is not string containerId)
            {
                return;
            }

            if (SecondaryTile.Exists(containerId))
            {
                await UnpinContainerFromStartAsync(containerId);
            }
            else if (MainPage.Current is MainPage mainPage)
            {
                await mainPage.PinExistingContainerToStartAsync(containerId);
            }

            RefreshContainers();
        }

        private async void DeleteContainerButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetContainerId(sender) is string containerId
                && !SettingsManager.Instance.IsDefaultContainer(containerId))
            {
                if (!await UnpinContainerFromStartAsync(containerId))
                {
                    return;
                }

                if (SettingsManager.Instance.DeleteContainer(containerId))
                {
                    RefreshContainers();
                }
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

        private static bool TryGetContainerEditValues(object sender, out string displayName, out string homeUrl)
        {
            displayName = "";
            homeUrl = "";

            if (sender is not DependencyObject source)
            {
                return false;
            }

            var root = FindAncestorWithNamedTextBoxes(source);
            var nameBox = root == null ? null : FindNamedDescendant<TextBox>(root, "ContainerNameBox");
            var urlBox = root == null ? null : FindNamedDescendant<TextBox>(root, "ContainerUrlBox");
            if (nameBox == null || urlBox == null)
            {
                return false;
            }

            displayName = nameBox.Text;
            homeUrl = urlBox.Text;
            return true;
        }

        private static DependencyObject? FindAncestorWithNamedTextBoxes(DependencyObject source)
        {
            for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (FindNamedDescendant<TextBox>(current, "ContainerNameBox") != null
                    && FindNamedDescendant<TextBox>(current, "ContainerUrlBox") != null)
                {
                    return current;
                }
            }

            return null;
        }

        private static T? FindNamedDescendant<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T element && element.Name == name)
                {
                    return element;
                }

                var match = FindNamedDescendant<T>(child, name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
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

        private static async Task<bool> UnpinContainerFromStartAsync(string containerId)
        {
            if (!SecondaryTile.Exists(containerId))
            {
                return true;
            }

            var tile = new SecondaryTile(containerId);
            try
            {
                await tile.RequestDeleteAsync();
                return !SecondaryTile.Exists(containerId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Containers] Unpin failed: {ex.Message}");
                return false;
            }
        }
    }

    public sealed class ContainerItemViewModel : INotifyPropertyChanged
    {
        public ContainerItemViewModel(WebContainer container, string? currentContainerId)
        {
            Id = container.Id;
            DisplayName = SettingsManager.Instance.GetContainerSiteName(Id);
            HomeUrl = container.HomeUrl;
            EditDisplayName = SettingsManager.Instance.GetContainerDisplayName(Id);
            EditHomeUrl = HomeUrl;
            CanDelete = !SettingsManager.Instance.IsDefaultContainer(Id);
            IsCurrentContainer = string.Equals(Id, currentContainerId, StringComparison.OrdinalIgnoreCase);
            IsPinned = SecondaryTile.Exists(Id);
            OpenVisibility = IsCurrentContainer ? Visibility.Collapsed : Visibility.Visible;
            IconUri = SettingsManager.Instance.GetContainerIconUri(Id);
            IconSource = new BitmapImage(IconUri);
            WebViewRuntimeVersion = GetWebViewRuntimeVersion();
            DiagnosticCurrentUrl = IsCurrentContainer
                ? MainPage.Current?.GetContainerPageForSettings()?.CurrentUrl ?? HomeUrl
                : HomeUrl;
            DiagnosticOrigin = GetOrigin(DiagnosticCurrentUrl);
            ProfilePath = SettingsManager.Instance.GetContainerWebViewDataFolder(Id);

            var manifestName = SettingsManager.Instance.GetContainerManifestName(Id);
            ManifestSummary = string.IsNullOrWhiteSpace(manifestName)
                ? GetResourceString("ContainerDiagnosticManifestMissing")
                : manifestName;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string DisplayName { get; }
        public string HomeUrl { get; }
        public string EditDisplayName { get; set; }
        public string EditHomeUrl { get; set; }
        public bool CanDelete { get; }
        public bool IsCurrentContainer { get; }
        public bool IsPinned { get; }
        public Visibility OpenVisibility { get; }
        public Visibility PinnedBadgeVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;
        public string PinButtonToolTip => IsPinned
            ? GetResourceString("ContainerUnpinButtonToolTip")
            : GetResourceString("ContainerPinButtonToolTip");
        public Uri IconUri { get; }
        public ImageSource IconSource { get; }

        public string WebViewRuntimeVersion { get; }
        public string DiagnosticCurrentUrl { get; }
        public string DiagnosticOrigin { get; }
        public string ProfilePath { get; }
        public string ManifestSummary { get; }

        public string CreateDiagnosticsText()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{GetResourceString("ContainerDiagnosticNameLabelText")}: {DisplayName}");
            builder.AppendLine($"{GetResourceString("ContainerDiagnosticRuntimeLabelText")}: {WebViewRuntimeVersion}");
            builder.AppendLine($"{GetResourceString("ContainerDiagnosticUrlLabelText")}: {DiagnosticCurrentUrl}");
            builder.AppendLine($"{GetResourceString("ContainerDiagnosticOriginLabelText")}: {DiagnosticOrigin}");
            builder.AppendLine($"{GetResourceString("ContainerDiagnosticProfileLabelText")}: {ProfilePath}");
            builder.AppendLine($"{GetResourceString("ContainerDiagnosticManifestLabelText")}: {ManifestSummary}");
            builder.AppendLine($"Vite: {SettingsManager.Instance.ViteDevServerUrl}");
            return builder.ToString();
        }

        private static string GetOrigin(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : "";
        }

        private static string GetWebViewRuntimeVersion()
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                return GetResourceString("ContainerDiagnosticRuntimeUnavailable");
            }
        }

        private static string GetResourceString(string key)
        {
            return ResourceLoader.GetForCurrentView().GetString(key);
        }
    }
}
