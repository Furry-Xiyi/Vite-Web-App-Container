using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class AboutPage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();
        private static readonly HttpClient ViteHealthClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        private bool _isInitializing = true;
        private readonly DispatcherTimer _viteHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private Process? _viteProcess;
        private bool _isCheckingViteHealth;
        private bool _hasSeenViteOnline;

        public ObservableCollection<TransparentCssRuleViewModel> Rules { get; } = new ObservableCollection<TransparentCssRuleViewModel>();
        public ObservableCollection<DevToolsSiteViewModel> DevToolsSites { get; } = new ObservableCollection<DevToolsSiteViewModel>();

        public AboutPage()
        {
            this.InitializeComponent();
            _viteHealthTimer.Tick += ViteHealthTimer_Tick;
            this.Loaded += AboutPage_Loaded;
            this.Unloaded += AboutPage_Unloaded;
        }

        private void AboutPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isInitializing = true;
            try
            {
                LoadRules();
                LoadAppInfo();
                PerSiteCssToggle.IsOn = SettingsManager.Instance.UsePerSiteTransparentCss;
                UpdateRuleHostVisibility();
                F12DevToolsToggle.IsOn = SettingsManager.Instance.IsF12DevToolsEnabled;
                LoadDevToolsSites();
                UpdateDownloadFolderPathText();
                SoundToggle.IsOn = SettingsManager.Instance.EnableSound;
                LoadViteDevServerSettings();
            }
            finally
            {
                _isInitializing = false;
            }

            _viteHealthTimer.Start();
            _ = CheckViteDevServerAsync();
        }

        private void AboutPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _viteHealthTimer.Stop();
        }

        private void LoadRules()
        {
            Rules.Clear();
            foreach (var rule in SettingsManager.Instance.TransparentCssRules)
            {
                Rules.Add(new TransparentCssRuleViewModel(rule, SettingsManager.Instance.UsePerSiteTransparentCss));
            }
        }

        private void LoadDevToolsSites()
        {
            DevToolsSites.Clear();
            var isMasterEnabled = SettingsManager.Instance.IsF12DevToolsEnabled;
            foreach (var container in SettingsManager.Instance.Containers
                .Where(item => !SettingsManager.Instance.IsDefaultContainer(item.Id)))
            {
                DevToolsSites.Add(new DevToolsSiteViewModel(container, isMasterEnabled));
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
                TxtCopyright.Text = $"©{DateTime.Now.Year} {Package.Current.PublisherDisplayName}。保留所有权利。";
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
                MainPage.Current?.GetContainerPageForSettings()?.RefreshTransparentCss();
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
            MainPage.Current?.GetContainerPageForSettings()?.RefreshTransparentCss();
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
            MainPage.Current?.ApplySettings();
        }

        private void PerSiteCssToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            SettingsManager.Instance.UsePerSiteTransparentCss = PerSiteCssToggle.IsOn;
            UpdateRuleHostVisibility();
            RefreshRulesView();
            MainPage.Current?.GetContainerPageForSettings()?.RefreshTransparentCss();
        }

        private void F12DevToolsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            SettingsManager.Instance.IsF12DevToolsEnabled = F12DevToolsToggle.IsOn;
            ReloadDevToolsSitesView();
            App.RefreshDevToolsAvailabilityForOpenViews();
        }

        private void DevToolsSiteToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            if (sender is not ToggleSwitch toggle)
            {
                return;
            }

            var containerId = (toggle.DataContext as DevToolsSiteViewModel)?.Id
                ?? (toggle.Tag as string);

            if (string.IsNullOrWhiteSpace(containerId)
                || SettingsManager.Instance.IsDefaultContainer(containerId))
            {
                return;
            }

            SettingsManager.Instance.SetContainerDevToolsEnabled(containerId, toggle.IsOn);

            if (toggle.DataContext is DevToolsSiteViewModel site)
            {
                site.IsDevToolsEnabled = toggle.IsOn;
            }

            App.RefreshDevToolsAvailabilityForOpenViews();
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

        private void RefreshDevToolsSitesView()
        {
            DevToolsSitesItemsControl.ItemsSource = null;
            DevToolsSitesItemsControl.ItemsSource = DevToolsSites;
        }

        private void ReloadDevToolsSitesView()
        {
            var wasInitializing = _isInitializing;
            _isInitializing = true;
            try
            {
                LoadDevToolsSites();
                RefreshDevToolsSitesView();
            }
            finally
            {
                _isInitializing = wasInitializing;
            }
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
            MainPage.Current?.OpenExternalLink(sender, e);
        }

        private void UpdateDownloadFolderPathText()
        {
            DownloadFolderPathText.Text = SettingsManager.Instance.DownloadFolderPath;
        }

        private async void BrowseDownloadFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            StorageApplicationPermissions.FutureAccessList.AddOrReplace(
                SettingsManager.DownloadFolderAccessToken,
                folder);
            SettingsManager.Instance.DownloadFolderToken = SettingsManager.DownloadFolderAccessToken;
            SettingsManager.Instance.DownloadFolderPath = folder.Path;
            UpdateDownloadFolderPathText();
        }

        private void ContainersManagementCard_Click(object sender, RoutedEventArgs e)
        {
            MainPage.Current?.OpenContainerManagementPage();
        }

        private void LoadViteDevServerSettings()
        {
            var settings = SettingsManager.Instance;
            ViteHostBox.Text = settings.ViteDevServerHost;
            VitePortNumberBox.Value = settings.ViteDevServerPort;
            ViteUsePathCheckBox.IsChecked = settings.ViteDevServerUsePath;
            VitePathBox.Text = settings.ViteDevServerPath;
            ViteCommandBox.Text = settings.ViteDevServerCommand;
            ViteWorkingDirectoryBox.Text = settings.ViteDevServerWorkingDirectory;
            UpdateVitePathInputState();
            UpdateViteDevServerUrlText();
            UpdateViteStatus(GetResourceString("ViteStatusNotChecked"));
            ViteHmrHintText.Visibility = Visibility.Collapsed;
        }

        private void ViteDevServerTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            SaveViteDevServerSettingsFromUi();
        }

        private void SaveViteDevServerSettingsFromUi()
        {
            var settings = SettingsManager.Instance;
            settings.ViteDevServerHost = ViteHostBox.Text;
            if (!double.IsNaN(VitePortNumberBox.Value)
                && !double.IsInfinity(VitePortNumberBox.Value)
                && VitePortNumberBox.Value >= 1
                && VitePortNumberBox.Value <= 65535)
            {
                settings.ViteDevServerPort = (int)Math.Round(VitePortNumberBox.Value);
            }

            settings.ViteDevServerUsePath = ViteUsePathCheckBox.IsChecked == true;
            settings.ViteDevServerPath = VitePathBox.Text;
            settings.ViteDevServerCommand = ViteCommandBox.Text;
            settings.ViteDevServerWorkingDirectory = ViteWorkingDirectoryBox.Text;
            UpdateVitePathInputState();
            UpdateViteDevServerUrlText();
        }

        private void VitePortNumberBox_ValueChanged(Microsoft.UI.Xaml.Controls.NumberBox sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializing)
            {
                return;
            }

            SaveViteDevServerSettingsFromUi();
        }

        private void ViteUsePathCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            SaveViteDevServerSettingsFromUi();
        }

        private void UpdateVitePathInputState()
        {
            VitePathBox.IsEnabled = ViteUsePathCheckBox.IsChecked == true;
        }

        private void UpdateViteDevServerUrlText()
        {
            ViteUrlText.Text = SettingsManager.Instance.ViteDevServerUrl;
        }

        private async void ViteHealthTimer_Tick(object sender, object e)
        {
            await CheckViteDevServerAsync();
        }

        private async Task CheckViteDevServerAsync()
        {
            if (_isCheckingViteHealth)
            {
                return;
            }

            _isCheckingViteHealth = true;
            var url = SettingsManager.Instance.ViteDevServerUrl;
            try
            {
                using var response = await ViteHealthClient.GetAsync(url);
                if ((int)response.StatusCode < 500)
                {
                    _hasSeenViteOnline = true;
                    UpdateViteStatus(string.Format(GetResourceString("ViteStatusOnlineFormat"), url, (int)response.StatusCode));
                    HideViteHmrHint();
                    return;
                }

                UpdateViteStatus(string.Format(GetResourceString("ViteStatusHttpErrorFormat"), url, (int)response.StatusCode));
                ShowViteHmrHintIfNeeded();
            }
            catch (Exception ex)
            {
                UpdateViteStatus(string.Format(GetResourceString("ViteStatusOfflineFormat"), url));
                AppendViteLog(string.Format(GetResourceString("ViteHealthCheckFailedFormat"), ex.Message));
                ShowViteHmrHintIfNeeded();
            }
            finally
            {
                _isCheckingViteHealth = false;
            }
        }

        private void ShowViteHmrHintIfNeeded()
        {
            if (!_hasSeenViteOnline)
            {
                return;
            }

            RunOnUiThread(() =>
            {
                ViteHmrHintText.Text = GetResourceString("ViteHmrDisconnectedHint");
                ViteHmrHintText.Visibility = Visibility.Visible;
            });
        }

        private void HideViteHmrHint()
        {
            RunOnUiThread(() => ViteHmrHintText.Visibility = Visibility.Collapsed);
        }

        private void UpdateViteStatus(string message)
        {
            RunOnUiThread(() => ViteStatusText.Text = message);
        }

        private async void ViteStartButton_Click(object sender, RoutedEventArgs e)
        {
            SaveViteDevServerSettingsFromUi();

            if (_viteProcess != null && !_viteProcess.HasExited)
            {
                AppendViteLog(GetResourceString("ViteProcessAlreadyRunning"));
                return;
            }

            var settings = SettingsManager.Instance;
            var workingDirectory = settings.ViteDevServerWorkingDirectory;
            if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
            {
                AppendViteLog(string.Format(GetResourceString("ViteWorkingDirectoryMissingFormat"), workingDirectory));
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + settings.ViteDevServerCommand,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        : workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _viteProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _viteProcess.OutputDataReceived += ViteProcess_OutputDataReceived;
                _viteProcess.ErrorDataReceived += ViteProcess_OutputDataReceived;
                _viteProcess.Exited += ViteProcess_Exited;

                if (_viteProcess.Start())
                {
                    _viteProcess.BeginOutputReadLine();
                    _viteProcess.BeginErrorReadLine();
                    AppendViteLog(string.Format(GetResourceString("ViteProcessStartedFormat"), settings.ViteDevServerCommand));
                    await Task.Delay(500);
                    await CheckViteDevServerAsync();
                }
            }
            catch (Exception ex)
            {
                AppendViteLog(string.Format(GetResourceString("ViteProcessStartFailedFormat"), ex.Message));
            }
        }

        private void ViteStopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_viteProcess == null || _viteProcess.HasExited)
                {
                    AppendViteLog(GetResourceString("ViteProcessNotRunning"));
                    return;
                }

                _viteProcess.Kill();
                AppendViteLog(GetResourceString("ViteProcessStopRequested"));
            }
            catch (Exception ex)
            {
                AppendViteLog(string.Format(GetResourceString("ViteProcessStopFailedFormat"), ex.Message));
            }
        }

        private async void ViteRetryButton_Click(object sender, RoutedEventArgs e)
        {
            SaveViteDevServerSettingsFromUi();
            await CheckViteDevServerAsync();
        }

        private void ViteUseDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            ViteHostBox.Text = "localhost";
            VitePortNumberBox.Value = 5173;
            ViteUsePathCheckBox.IsChecked = false;
            VitePathBox.Text = "";
            SaveViteDevServerSettingsFromUi();
        }

        private async void ViteBrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            RunOnUiThread(() =>
            {
                ViteWorkingDirectoryBox.Text = folder.Path;
                SaveViteDevServerSettingsFromUi();
            });
        }

        private async void ViteOpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var path = SettingsManager.Instance.ViteDevServerWorkingDirectory;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                AppendViteLog(GetResourceString("ViteWorkingDirectoryNotSet"));
                return;
            }

            try
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                await Launcher.LaunchFolderAsync(folder);
            }
            catch (Exception ex)
            {
                AppendViteLog(string.Format(GetResourceString("ViteOpenFolderFailedFormat"), ex.Message));
            }
        }

        private async void ViteOpenUrlButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainPage.Current == null)
            {
                return;
            }

            await MainPage.Current.OpenUrlInNewContainerAsync(
                SettingsManager.Instance.ViteDevServerUrl,
                GetResourceString("ViteDevContainerDisplayName"));
        }

        private void ViteReloadCurrentButton_Click(object sender, RoutedEventArgs e)
        {
            var containerPage = MainPage.Current?.GetContainerPageForSettings();
            if (containerPage == null)
            {
                AppendViteLog(GetResourceString("ViteReloadUnavailable"));
                return;
            }

            containerPage.Navigate(containerPage.CurrentUrl);
            AppendViteLog(GetResourceString("ViteReloadRequested"));
        }

        private async void ViteProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => AppendViteLog(e.Data));
        }

        private async void ViteProcess_Exited(object? sender, EventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                AppendViteLog(GetResourceString("ViteProcessExited"));
                _ = CheckViteDevServerAsync();
            });
        }

        private void AppendViteLog(string message)
        {
            RunOnUiThread(() =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                ViteLogBox.Text = string.IsNullOrEmpty(ViteLogBox.Text)
                    ? line
                    : ViteLogBox.Text + Environment.NewLine + line;
            });
        }

        private string GetResourceString(string key)
        {
            var value = _loader.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }

        private void RunOnUiThread(Action action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => action());
        }
    }

    public sealed class TransparentCssRuleViewModel
    {
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
            ? GetResourceString("CssRuleFallbackHeader")
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

                return string.Format(GetResourceString("CssRuleDescriptionWithHostFormat"), Host.Trim(), css);
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

        private static string GetResourceString(string key)
        {
            return new ResourceLoader().GetString(key);
        }
    }

    public sealed class DevToolsSiteViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isDevToolsEnabled;

        public DevToolsSiteViewModel(WebContainer container, bool isMasterEnabled)
        {
            Id = container.Id;
            DisplayName = SettingsManager.Instance.GetContainerSiteName(Id);
            HomeUrl = container.HomeUrl;
            IconUri = SettingsManager.Instance.GetContainerIconUri(Id);
            _isDevToolsEnabled = SettingsManager.Instance.IsContainerDevToolsEnabled(Id);
            IsMasterEnabled = isMasterEnabled;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; }
        public string DisplayName { get; }
        public string HomeUrl { get; }
        public Uri IconUri { get; }
        public bool IsDevToolsEnabled
        {
            get => _isDevToolsEnabled;
            set
            {
                if (_isDevToolsEnabled == value)
                {
                    return;
                }

                _isDevToolsEnabled = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDevToolsEnabled)));
            }
        }
        public bool IsMasterEnabled { get; }
    }
}
