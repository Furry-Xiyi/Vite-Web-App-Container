using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class AboutPage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();
        private readonly ObservableCollection<TransparentCssRule> _rules = new ObservableCollection<TransparentCssRule>();
        private bool _isInitializing = true;

        public AboutPage()
        {
            this.InitializeComponent();
            Breadcrumb.ItemsSource = new[] { _loader.GetString("Settings_Breadcrumb") };
            TransparentCssRulesList.ItemsSource = _rules;
            this.Loaded += AboutPage_Loaded;
        }

        private void AboutPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRules();
            LoadAppInfo();
            SoundToggle.IsOn = SettingsManager.Instance.EnableSound;
            _isInitializing = false;
        }

        private void LoadRules()
        {
            _rules.Clear();
            foreach (var rule in SettingsManager.Instance.TransparentCssRules)
            {
                _rules.Add(rule);
            }
        }

        private void LoadAppInfo()
        {
            try
            {
                TxtAppName.Text = Package.Current.DisplayName;
                var v = Package.Current.Id.Version;
                TxtVersion.Text = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
                TxtCopyright.Text = $"©{DateTime.Now.Year} {Package.Current.PublisherDisplayName}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadAppInfo Error: {ex.Message}");
            }
        }

        private async void EditCssRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TransparentCssRule rule)
            {
                return;
            }

            var selectorBox = new TextBox
            {
                Text = rule.Selector,
                AcceptsReturn = false
            };

            var cssBox = new TextBox
            {
                Text = rule.Css,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = _loader.GetString("CssSelectorLabel") });
            panel.Children.Add(selectorBox);
            panel.Children.Add(new TextBlock { Text = _loader.GetString("CssRuleLabel") });
            panel.Children.Add(cssBox);

            var dialog = new ContentDialog
            {
                Title = _loader.GetString("EditCssRuleDialogTitle"),
                Content = panel,
                PrimaryButtonText = _loader.GetString("SaveUrlButton.Content"),
                CloseButtonText = _loader.GetString("CancelUrlButton.Content")
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            SettingsManager.Instance.UpdateTransparentCssRule(rule.Id, selectorBox.Text.Trim(), cssBox.Text.Trim());
            LoadRules();
            MainPage.Instance?.GetHomePageForSettings()?.RefreshTransparentCss();
        }

        private void DeleteCssRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TransparentCssRule rule)
            {
                return;
            }

            SettingsManager.Instance.RemoveTransparentCssRule(rule.Id);
            LoadRules();
            MainPage.Instance?.GetHomePageForSettings()?.RefreshTransparentCss();
        }

        private void SoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            SettingsManager.Instance.EnableSound = SoundToggle.IsOn;
            MainPage.Instance?.ApplySettings();
        }

        private void OpenExternalLink(object sender, RoutedEventArgs e)
        {
            MainPage.Instance?.OpenExternalLink(sender, e);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Instance?.CloseSettingsHost();
        }
    }
}
