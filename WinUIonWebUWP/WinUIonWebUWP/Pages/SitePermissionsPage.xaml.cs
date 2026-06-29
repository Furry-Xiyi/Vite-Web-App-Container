using System;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class SitePermissionsPage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();
        private string _origin = "";
        private bool _isLoading = true;

        public ObservableCollection<SitePermissionViewModel> Permissions { get; } = new ObservableCollection<SitePermissionViewModel>();

        public SitePermissionsPage()
        {
            this.InitializeComponent();
            this.Loaded += SitePermissionsPage_Loaded;
        }

        private void SitePermissionsPage_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _origin = GetCurrentOrigin();
            OriginText.Text = string.Format(GetString("SitePermissionsOriginFormat"), _origin);
            LoadPermissions();
            _isLoading = false;
        }

        private void LoadPermissions()
        {
            Permissions.Clear();
            AddPermission("Microphone", "\uE720");
            AddPermission("Camera", "\uE722");
            AddPermission("Geolocation", "\uE707");
            AddPermission("Notifications", "\uEC42");
            AddPermission("OtherSensors", "\uE950");
            AddPermission("ClipboardRead", "\uE77F");
            AddPermission("MultipleAutomaticDownloads", "\uE896");
            AddPermission("FileReadWrite", "\uE932");
            AddPermission("Autoplay", "\uE768");
            AddPermission("LocalFonts", "\uE8D2");
            AddPermission("MidiSystemExclusiveMessages", "\uE8D6");
            AddPermission("WindowManagement", "\uE737");
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
