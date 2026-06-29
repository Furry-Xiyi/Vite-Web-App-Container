using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WinUIonWebUWP.Pages;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Resources;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace WinUIonWebUWP
{
    public sealed partial class MainPage : Page
    {
        public static MainPage? Instance { get; private set; }

        private readonly ResourceLoader _loader = new ResourceLoader();
        private readonly ObservableCollection<string> _urlHistoryItems = new ObservableCollection<string>();
        private readonly string _containerDisplayName;
        private bool _isHostedTitleBarVisible;
        private string _hostedDocumentTitle = "";

        public bool IsWindowActive { get; private set; } = true;

        public MainPage()
        {
            this.InitializeComponent();
            Instance = this;

            _containerDisplayName = _loader.GetString("AppDisplayName");
            TitleBarAppName.Text = _containerDisplayName;
            ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
            UpdateWindowTitle();

            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;
            Window.Current.SetTitleBar(TitleBarDragArea);
            coreTitleBar.LayoutMetricsChanged += CoreTitleBar_LayoutMetricsChanged;
            UpdateTitleBarButtonInset(coreTitleBar);

            ContentFrame.Navigate(typeof(Pages.HomePage));

            this.Loaded += MainPage_Loaded;

            CoreWindow.GetForCurrentThread().Activated += MainPage_CoreWindowActivated;
            ToolTipService.SetToolTip(MoreButton, _loader.GetString("MoreButton_ToolTip"));
            UrlAutoSuggestBox.ItemsSource = _urlHistoryItems;
        }

        private void CoreTitleBar_LayoutMetricsChanged(CoreApplicationViewTitleBar sender, object args)
        {
            UpdateTitleBarButtonInset(sender);
        }

        private void UpdateTitleBarButtonInset(CoreApplicationViewTitleBar titleBar)
        {
            MoreButton.Margin = new Thickness(0, 0, titleBar.SystemOverlayRightInset, 0);
            TitleBarDragArea.Margin = new Thickness(0, 0, titleBar.SystemOverlayRightInset + MoreButton.Width, 0);
        }

        private void MainPage_CoreWindowActivated(CoreWindow sender, WindowActivatedEventArgs args)
        {
            bool isActive = args.WindowActivationState != CoreWindowActivationState.Deactivated;
            IsWindowActive = isActive;
            TitleBarAppName.Opacity = isActive ? 1.0 : 0.5;
            GetHomePage()?.SetHostedWindowActive(isActive);
        }

        public async void OpenExternalLink(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string btnUrl)
            {
                var dialog = new Dialogs.ExternalOpenDialog();
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                    await Launcher.LaunchUriAsync(new Uri(btnUrl));
            }
            else if (sender is HyperlinkButton link && link.Tag is string linkUrl)
            {
                var dialog = new Dialogs.ExternalOpenDialog();
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                    await Launcher.LaunchUriAsync(new Uri(linkUrl));
            }
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplySettings();
        }

        public void ApplySettings()
        {
            try
            {
                var s = SettingsManager.Instance;
                _ = s.PanePosition;

                ElementSoundPlayer.State = s.EnableSound
                    ? ElementSoundPlayerState.On
                    : ElementSoundPlayerState.Off;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ApplySettings Error: {ex.Message}");
            }
        }

        public static string GetWebView2BrowserArguments(bool forceDark)
        {
            var features = new List<string>
            {
                "msEdgeFluentOverlayScrollbar",
                "msVisualRejuvMica"
            };

            if (forceDark)
            {
                features.Add("WebContentsForceDark");
            }

            return $"--enable-features={string.Join(",", features)}";
        }

        private HomePage? GetHomePage() => ContentFrame.Content as HomePage;

        public HomePage? GetHomePageForSettings() => GetHomePage();

        public void SetHostedTitleBarVisible(bool isVisible)
        {
            if (_isHostedTitleBarVisible == isVisible)
            {
                return;
            }

            _isHostedTitleBarVisible = isVisible;
            TitleBarIdentityArea.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            ContentFrame.Margin = isVisible ? new Thickness(0) : new Thickness(0, 32, 0, 0);
        }

        public void UpdateHostedDocumentInfo(string? title, Uri? iconUri)
        {
            _hostedDocumentTitle = string.IsNullOrWhiteSpace(title) ? "" : title.Trim();

            TitleBarAppName.Text = string.IsNullOrWhiteSpace(_hostedDocumentTitle)
                ? _containerDisplayName
                : $"{_containerDisplayName} - {_hostedDocumentTitle}";

            if (iconUri != null)
            {
                try
                {
                    ImgAppIcon.Source = new BitmapImage(iconUri);
                }
                catch
                {
                    ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
                }
            }
            else
            {
                ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
            }

            UpdateWindowTitle();
        }

        private void UpdateWindowTitle()
        {
            ApplicationView.GetForCurrentView().Title = string.IsNullOrWhiteSpace(_hostedDocumentTitle)
                ? _containerDisplayName
                : $"{_containerDisplayName} - {_hostedDocumentTitle}";
        }

        private void MoreFlyout_Opening(object sender, object e)
        {
            GetHomePage()?.SetHostedWindowActive(true);
            UrlAutoSuggestBox.Text = SettingsManager.Instance.HomeUrl;
            RefreshUrlHistoryItems();
            UrlAutoSuggestBox.IsSuggestionListOpen = false;
            UrlAutoSuggestBox.IsEnabled = false;
            HideUrlError();
        }

        private void MoreFlyout_Opened(object sender, object e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UrlAutoSuggestBox.IsEnabled = true;
            });
        }

        private void UrlAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            HideUrlError();
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ShowUrlHistorySuggestions();
            }
        }

        private void UrlAutoSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ShowUrlHistorySuggestions();
        }

        private void UrlAutoSuggestBox_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ShowUrlHistorySuggestions();
        }

        private void UrlAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string url)
            {
                sender.Text = url;
            }

            TryApplyUrl();
        }

        private void UrlAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string url)
            {
                sender.Text = url;
            }
        }

        private void DeleteUrlHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is AppBarButton button && button.Tag is string url)
            {
                SettingsManager.Instance.RemoveHomeUrlHistory(url);
                ShowUrlHistorySuggestions();
                UrlAutoSuggestBox.Focus(FocusState.Programmatic);
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ShowUrlHistorySuggestions);
            }
        }

        private void RestoreUrlButton_Click(object sender, RoutedEventArgs e)
        {
            var defaultUrl = _loader.GetString("DefaultHomeUrl");
            SettingsManager.Instance.HomeUrl = defaultUrl;
            UrlAutoSuggestBox.Text = defaultUrl;
            HideUrlError();
            GetHomePage()?.Navigate(defaultUrl);
            MoreFlyout.Hide();
        }

        private void CancelUrlButton_Click(object sender, RoutedEventArgs e)
        {
            HideUrlError();
            MoreFlyout.Hide();
        }

        private void SaveUrlButton_Click(object sender, RoutedEventArgs e)
        {
            TryApplyUrl();
        }

        private void SettingsEntryButton_Click(object sender, RoutedEventArgs e)
        {
            MoreFlyout.Hide();
            SettingsFrame.Navigate(typeof(Pages.AboutPage));
            SettingsHost.Visibility = Visibility.Visible;
        }

        public void CloseSettingsHost()
        {
            SettingsHost.Visibility = Visibility.Collapsed;
            SettingsFrame.Content = null;
            GetHomePage()?.SetHostedWindowActive(IsWindowActive);
        }

        private void TryApplyUrl()
        {
            var url = UrlAutoSuggestBox.Text?.Trim();
            if (!IsSupportedUrl(url))
            {
                UrlErrorText.Visibility = Visibility.Visible;
                return;
            }

            SettingsManager.Instance.HomeUrl = url;
            SettingsManager.Instance.AddHomeUrlHistory(url);
            RefreshUrlHistoryItems();
            GetHomePage()?.Navigate(url);
            MoreFlyout.Hide();
        }

        private static bool IsSupportedUrl(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "edge")
                && !string.IsNullOrWhiteSpace(uri.Host);
        }

        private void HideUrlError() => UrlErrorText.Visibility = Visibility.Collapsed;

        private void RefreshUrlHistoryItems()
        {
            _urlHistoryItems.Clear();
            foreach (var url in SettingsManager.Instance.HomeUrlHistory.Where(IsSupportedUrl))
            {
                _urlHistoryItems.Add(url);
            }
        }

        private void ShowUrlHistorySuggestions()
        {
            RefreshUrlHistoryItems();
            UrlAutoSuggestBox.IsSuggestionListOpen = _urlHistoryItems.Count > 0;
        }
    }
}
