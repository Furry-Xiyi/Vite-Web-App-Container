using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class AboutPage : Page
    {
        private bool _isInitializing = true;

        public ObservableCollection<TransparentCssRuleViewModel> Rules { get; } = new ObservableCollection<TransparentCssRuleViewModel>();

        public AboutPage()
        {
            this.InitializeComponent();
            this.Loaded += AboutPage_Loaded;
        }

        private void AboutPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRules();
            LoadAppInfo();
            PerSiteCssToggle.IsOn = SettingsManager.Instance.UsePerSiteTransparentCss;
            UpdateRuleHostVisibility();
            SoundToggle.IsOn = SettingsManager.Instance.EnableSound;
            _isInitializing = false;
        }

        private void LoadRules()
        {
            Rules.Clear();
            foreach (var rule in SettingsManager.Instance.TransparentCssRules)
            {
                Rules.Add(new TransparentCssRuleViewModel(rule, SettingsManager.Instance.UsePerSiteTransparentCss));
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

        private void AddCssRuleButton_Click(object sender, RoutedEventArgs e)
        {
            var item = new TransparentCssRuleViewModel(new TransparentCssRule
            {
                Id = Guid.NewGuid().ToString("N")
            }, SettingsManager.Instance.UsePerSiteTransparentCss)
            {
                IsDirty = true
            };

            Rules.Add(item);
            TransparentCssExpander.IsExpanded = true;
        }

        private async void DeleteCssRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TransparentCssRuleViewModel rule)
            {
                return;
            }

            var dialog = new Dialogs.DeleteCssRuleDialog();
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(rule.Id))
            {
                SettingsManager.Instance.RemoveTransparentCssRule(rule.Id);
                MainPage.Instance?.GetHomePageForSettings()?.RefreshTransparentCss();
            }

            Rules.Remove(rule);
        }

        private void SaveCssRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TransparentCssRuleViewModel rule)
            {
                return;
            }

            SettingsManager.Instance.UpdateTransparentCssRule(
                rule.Id,
                rule.Host.Trim(),
                rule.Selector.Trim(),
                rule.Css.Trim());

            rule.IsDirty = false;
            rule.RefreshSummary();
            if (sender is Button button)
            {
                button.Visibility = Visibility.Collapsed;
            }
            RefreshRulesView();
            MainPage.Instance?.GetHomePageForSettings()?.RefreshTransparentCss();
        }

        private void CssRuleTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox || textBox.DataContext is not TransparentCssRuleViewModel rule)
            {
                return;
            }

            bool changed = false;
            switch (textBox.Name)
            {
                case "CssHostBox" when rule.Host != textBox.Text:
                    rule.Host = textBox.Text;
                    changed = true;
                    break;
                case "CssSelectorBox" when rule.Selector != textBox.Text:
                    rule.Selector = textBox.Text;
                    changed = true;
                    break;
                case "CssContentBox" when rule.Css != textBox.Text:
                    rule.Css = textBox.Text;
                    changed = true;
                    break;
            }

            if (changed)
            {
                var saveButton = FindNamedDescendant<Button>(FindCardRoot(textBox), "SaveCssRuleButton");
                if (saveButton != null)
                {
                    saveButton.Visibility = Visibility.Visible;
                }
            }
        }

        private void SoundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            SettingsManager.Instance.EnableSound = SoundToggle.IsOn;
            MainPage.Instance?.ApplySettings();
        }

        private void PerSiteCssToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            SettingsManager.Instance.UsePerSiteTransparentCss = PerSiteCssToggle.IsOn;
            UpdateRuleHostVisibility();
            RefreshRulesView();
            MainPage.Instance?.GetHomePageForSettings()?.RefreshTransparentCss();
        }

        private void UpdateRuleHostVisibility()
        {
            foreach (var rule in Rules)
            {
                rule.UsePerSite = PerSiteCssToggle.IsOn;
            }
            RefreshRulesView();
        }

        private void RefreshRulesView()
        {
            RulesItemsControl.ItemsSource = null;
            RulesItemsControl.ItemsSource = Rules;
        }

        private static DependencyObject FindCardRoot(DependencyObject element)
        {
            var current = element;
            while (VisualTreeHelper.GetParent(current) is DependencyObject parent)
            {
                current = parent;
                if (current is CommunityToolkit.WinUI.Controls.SettingsCard)
                {
                    return current;
                }
            }

            return element;
        }

        private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
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

        private void OpenExternalLink(object sender, RoutedEventArgs e)
        {
            MainPage.Instance?.OpenExternalLink(sender, e);
        }
    }

    public sealed class TransparentCssRuleViewModel
    {
        private static readonly ResourceLoader Loader = new ResourceLoader();
        private string _host;
        private string _selector;
        private string _css;
        private bool _isDirty;
        private bool _usePerSite;
        private bool _isReady;

        public TransparentCssRuleViewModel(TransparentCssRule rule, bool usePerSite)
        {
            Id = rule.Id;
            _host = rule.Host;
            _selector = rule.Selector;
            _css = rule.Css;
            _usePerSite = usePerSite;
            _isReady = true;
        }

        public string Id { get; }

        public string Host
        {
            get => _host;
            set
            {
                if (_host == value) return;
                _host = value;
                MarkDirty();
            }
        }

        public string Selector
        {
            get => _selector;
            set
            {
                if (_selector == value) return;
                _selector = value;
                MarkDirty();
            }
        }

        public string Css
        {
            get => _css;
            set
            {
                if (_css == value) return;
                _css = value;
                MarkDirty();
            }
        }

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                if (_isDirty == value) return;
                _isDirty = value;
            }
        }

        public bool UsePerSite
        {
            get => _usePerSite;
            set
            {
                if (_usePerSite == value) return;
                _usePerSite = value;
            }
        }

        public string Header => string.IsNullOrWhiteSpace(Selector)
            ? Loader.GetString("CssRuleFallbackHeader")
            : Selector;

        public string Description
        {
            get
            {
                var css = string.IsNullOrWhiteSpace(Css) ? "" : Css.Trim();
                if (!UsePerSite || string.IsNullOrWhiteSpace(Host))
                {
                    return css;
                }

                return string.Format(Loader.GetString("CssRuleDescriptionWithHostFormat"), Host.Trim(), css);
            }
        }

        public Visibility SaveVisibility => IsDirty ? Visibility.Visible : Visibility.Collapsed;

        public Visibility HostVisibility => UsePerSite ? Visibility.Visible : Visibility.Collapsed;

        public void RefreshSummary() { }

        private void MarkDirty()
        {
            if (_isReady)
            {
                IsDirty = true;
            }
        }
    }
}
