using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class SitePermissionsPage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();
        private string _origin = "";
        private bool _isLoading = true;
        private int _loadVersion;
        private bool _isUnloaded;

        public ObservableCollection<SitePermissionViewModel> Permissions { get; } = new ObservableCollection<SitePermissionViewModel>();

        public SitePermissionsPage()
        {
            this.InitializeComponent();
            this.Loaded += SitePermissionsPage_Loaded;
            this.Unloaded += SitePermissionsPage_Unloaded;
        }

        private async void SitePermissionsPage_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _isUnloaded = false;
            var loadVersion = ++_loadVersion;
            _isLoading = true;
            _origin = GetCurrentOrigin();
            OriginText.Text = string.Format(GetString("SitePermissionsOriginFormat"), _origin);
            await YieldForPageLoadAsync();
            if (!IsCurrentLoad(loadVersion))
            {
                return;
            }

            await LoadPermissionsAsync(loadVersion);
            if (IsCurrentLoad(loadVersion))
            {
                _isLoading = false;
            }
        }

        private void SitePermissionsPage_Unloaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _isUnloaded = true;
            _loadVersion++;
        }

        private async Task LoadPermissionsAsync(int loadVersion)
        {
            Permissions.Clear();
            var permissions = new[]
            {
                ("Microphone", "\uE720"),
                ("Camera", "\uE722"),
                ("Geolocation", "\uE707"),
                ("Notifications", "\uEC42"),
                ("OtherSensors", "\uE950"),
                ("ClipboardRead", "\uE77F"),
                ("MultipleAutomaticDownloads", "\uE896"),
                ("FileReadWrite", "\uE932"),
                ("Autoplay", "\uE768"),
                ("LocalFonts", "\uE8D2"),
                ("MidiSystemExclusiveMessages", "\uE8D6"),
                ("WindowManagement", "\uE737")
            };

            for (var index = 0; index < permissions.Length; index++)
            {
                if (!IsCurrentLoad(loadVersion))
                {
                    return;
                }

                AddPermission(permissions[index].Item1, permissions[index].Item2);
                if ((index + 1) % 4 == 0)
                {
                    await YieldForPageLoadAsync();
                }
            }
        }

        private bool IsCurrentLoad(int loadVersion) =>
            !_isUnloaded && loadVersion == _loadVersion;

        private async Task YieldForPageLoadAsync()
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => { });
        }

        private void AddPermission(string permissionKind, string glyph)
        {
            var state = SettingsManager.Instance.GetSitePermissionState(_origin, permissionKind);
            Permissions.Add(new SitePermissionViewModel(permissionKind, GetPermissionDisplayName(permissionKind), glyph, StateToIndex(state)));
        }

        private void PermissionStateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || sender is not ComboBox comboBox || comboBox.Tag is not string permissionKind)
            {
                return;
            }

            var state = comboBox.SelectedIndex switch
            {
                1 => "Allow",
                2 => "Deny",
                _ => "Default"
            };
            SettingsManager.Instance.SetSitePermission(_origin, permissionKind, state);
        }

        private string GetCurrentOrigin()
        {
            var currentUrl = MainPage.Current?.GetContainerPageForSettings()?.CurrentUrl
                ?? MainPage.Current?.ContainerHomeUrl
                ?? SettingsManager.Instance.HomeUrl;
            if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }

            return currentUrl;
        }

        private string GetPermissionDisplayName(string permissionKind)
        {
            var key = "PermissionKind_" + permissionKind;
            var value = GetString(key);
            return value == key ? permissionKind : value;
        }

        private string GetString(string key)
        {
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }

        private static int StateToIndex(string state)
        {
            return state switch
            {
                "Allow" => 1,
                "Deny" => 2,
                _ => 0
            };
        }
    }

    public sealed class SitePermissionViewModel
    {
        public SitePermissionViewModel(string permissionKind, string displayName, string glyph, int selectedIndex)
        {
            PermissionKind = permissionKind;
            DisplayName = displayName;
            Glyph = glyph;
            SelectedIndex = selectedIndex;
        }

        public string PermissionKind { get; }
        public string DisplayName { get; }
        public string Glyph { get; }
        public int SelectedIndex { get; }
        public string Description => PermissionKind;
    }
}
