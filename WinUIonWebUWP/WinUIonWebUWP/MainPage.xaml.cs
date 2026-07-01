using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WinUIonWebUWP.Pages;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.StartScreen;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Media.Animation;

namespace WinUIonWebUWP
{
    public sealed partial class MainPage : Page
    {
        private const double DefaultTitleBarHeight = 32;
        private const double MaxHostedTitleBarHeight = 96;

        [ThreadStatic]
        private static MainPage? _currentInstance;
        [ThreadStatic]
        private static string? _pendingContainerId;

        public static MainPage? Instance => _currentInstance;
        public static MainPage? Current => (Window.Current.Content as Frame)?.Content as MainPage ?? _currentInstance;

        public static void SetPendingContainerId(string containerId)
        {
            _pendingContainerId = containerId;
        }

        public ObservableCollection<string> BreadcrumbItems { get; } = new ObservableCollection<string>();

        private readonly ResourceLoader _loader = new ResourceLoader();
        private readonly ObservableCollection<string> _urlHistoryItems = new ObservableCollection<string>();
        private readonly ObservableCollection<DownloadItemViewModel> _downloadItems = new ObservableCollection<DownloadItemViewModel>();
        private static readonly HttpClient HttpClient = new HttpClient();
        private string _containerId;
        private string _containerDisplayName;
        private bool _isHostedTitleBarVisible;
        private bool _isSettingsHostOpen;
        private bool _isDownloadsFlyoutOpen;
        private bool _isDownloadsContextFlyoutOpen;
        private bool _isDownloadsButtonPinned;
        private bool _isDownloadsButtonTransient;
        private bool _isDownloadCompletionAcknowledged = true;
        private bool _requestedHostedTitleBarVisible;
        private bool _isHostedPageLoaded;
        private Type? _currentSettingsPageType;
        private bool _isDownloadAccessDialogOpen;
        private string _pinCandidateUrl = "";
        private double _titleBarLeftInset;
        private double _titleBarRightInset = 48;
        private double _hostedTitleBarHeight = DefaultTitleBarHeight;
        private IReadOnlyList<TitleBarInteractiveRect> _hostedTitleBarInteractiveRects = Array.Empty<TitleBarInteractiveRect>();
        private Color? _hostedTitleBarForegroundColor;
        private bool _hasHostedTitleBarThemeOverride;
        private string _hostedDocumentTitle = "";
        private string _hostedManifestName = "";
        private Uri? _hostedDocumentIconUri;
        private IReadOnlyList<Uri> _hostedDocumentIconCandidates = Array.Empty<Uri>();
        private Uri? _preparedTileIconUri;
        private string _preparedTileIconKey = "";
        private TileIconSet _preparedTileIcons = TileIconSet.Empty;
        private DownloadItemViewModel? _hoveredDownloadItem;
        private readonly DispatcherTimer _downloadsFileRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private bool _isRefreshingDownloadFileAvailability;
        private ContainerPage? _containerPage;

        public bool IsWindowActive { get; private set; } = true;
        public string ContainerId => _containerId;
        private bool IsPrimaryContainer => SettingsManager.Instance.IsDefaultContainer(_containerId);

        public MainPage()
        {
            this.InitializeComponent();
            _currentInstance = this;

            var pendingContainerId = _pendingContainerId;
            _containerId = !string.IsNullOrWhiteSpace(pendingContainerId)
                && SettingsManager.Instance.HasContainer(pendingContainerId)
                    ? pendingContainerId
                    : SettingsManager.Instance.PrimaryContainerId;
            _pendingContainerId = null;
            _containerDisplayName = SettingsManager.Instance.GetContainerDisplayName(_containerId);
            _hostedManifestName = SettingsManager.Instance.GetContainerManifestName(_containerId);
            AppThemeManager.LoadSettings(_containerId);
            AppThemeManager.ApplyTheme();
            AppThemeManager.ApplyMaterial();
            TitleBarAppName.Text = _containerDisplayName;
            ImgAppIcon.Source = new BitmapImage(SettingsManager.Instance.GetContainerIconUri(_containerId));
            LoadCachedHostedTitleBarState();
            UpdateWindowTitle();

            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;
            Window.Current.SetTitleBar(TitleBarDragArea);
            coreTitleBar.LayoutMetricsChanged += CoreTitleBar_LayoutMetricsChanged;
            TitleBarArea.SizeChanged += TitleBarArea_SizeChanged;
            UpdateTitleBarButtonInset(coreTitleBar);

            ContentFrame.Navigated += ContentFrame_Navigated;

            var navigationManager = SystemNavigationManager.GetForCurrentView();
            navigationManager.BackRequested += SystemNavigationManager_BackRequested;
            navigationManager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;

            ContentFrame.Navigate(typeof(Pages.ContainerPage), this);

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;

            CoreWindow.GetForCurrentThread().Activated += MainPage_CoreWindowActivated;
            UrlAutoSuggestBox.ItemsSource = _urlHistoryItems;
            DownloadsListView.ItemsSource = _downloadItems;
            _downloadsFileRefreshTimer.Tick += DownloadsFileRefreshTimer_Tick;
            LoadDownloadHistory();
            _isDownloadsButtonPinned = SettingsManager.Instance.IsDownloadsButtonPinned;
            InitializeDownloadTitleBarStatus();
            UpdateDownloadsEmptyState();
            UpdateDownloadTitleBarButton(false);
            UpdateDownloadTitleBarStatus();
            UpdateDownloadsPinMenuItems();
            App.RegisterContainerView(_containerId, ApplicationView.GetForCurrentView().Id);
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.UnregisterContainerView(_containerId, ApplicationView.GetForCurrentView().Id);
        }

        private void CoreTitleBar_LayoutMetricsChanged(CoreApplicationViewTitleBar sender, object args)
        {
            UpdateTitleBarButtonInset(sender);
        }

        private void TitleBarArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTitleBarDragAreaBounds();
            UpdateHostedTitleBarGeometry();
        }

        private void UpdateTitleBarButtonInset(CoreApplicationViewTitleBar titleBar)
        {
            const double titleBarButtonSpacing = 2;
            var downloadButtonWidth = DownloadTitleBarButton.Visibility == Visibility.Visible
                ? DownloadTitleBarButton.Width
                : 0;
            var downloadButtonSpacing = downloadButtonWidth > 0 ? titleBarButtonSpacing : 0;
            var rightInset = titleBar.SystemOverlayRightInset + MoreButton.Width + downloadButtonWidth + downloadButtonSpacing;
            var activeHeight = GetActiveTitleBarHeight();

            MoreButton.Margin = new Thickness(0, 0, titleBar.SystemOverlayRightInset, 0);
            DownloadTitleBarButton.Margin = new Thickness(0, 0, titleBar.SystemOverlayRightInset + MoreButton.Width + downloadButtonSpacing, 0);
            TitleBarIdentityArea.Margin = new Thickness(titleBar.SystemOverlayLeftInset + 16, 0, 16, 0);
            TitleBarArea.Height = activeHeight;
            TitleBarDragArea.Height = activeHeight;

            _titleBarLeftInset = titleBar.SystemOverlayLeftInset;
            _titleBarRightInset = rightInset;
            UpdateTitleBarDragAreaBounds();
            UpdateHostedTitleBarGeometry();
        }

        private double GetActiveTitleBarHeight() =>
            _isHostedTitleBarVisible ? _hostedTitleBarHeight : DefaultTitleBarHeight;

        private void UpdateHostedTitleBarGeometry()
        {
            _ = GetContainerPage()?.SetHostTitleBarGeometryAsync(
                _titleBarLeftInset,
                _titleBarRightInset,
                GetActiveTitleBarHeight());
        }

        public void SetHostedTitleBarInteractiveRects(IReadOnlyList<TitleBarInteractiveRect>? rects)
        {
            _hostedTitleBarInteractiveRects = rects ?? Array.Empty<TitleBarInteractiveRect>();
            UpdateTitleBarDragAreaBounds();
        }

        public void SetHostedTitleBarAppearance(string? theme, string? foreground, string? background = null)
        {
            if (!_isHostedTitleBarVisible || _isSettingsHostOpen)
            {
                return;
            }

            var nextTheme = NormalizeHostedTitleBarTheme(theme);
            if (nextTheme.HasValue && AppThemeManager.CurrentTheme != nextTheme.Value)
            {
                _hasHostedTitleBarThemeOverride = true;
                AppThemeManager.CurrentTheme = nextTheme.Value;
                AppThemeManager.ApplyTheme();
                AppThemeManager.ApplyMaterial();
                GetContainerPage()?.RefreshHostTheme();
            }

            var hasForeground = TryParseCssColor(foreground, out var color);
            var hasBackground = TryParseCssColor(background, out var backgroundColor);
            if (!hasForeground)
            {
                color = GetThemeForegroundColor();
            }

            if (hasBackground && GetContrastRatio(color, backgroundColor) < 4.5)
            {
                color = GetContrastingForegroundColor(backgroundColor);
            }
            else if (!hasBackground && nextTheme.HasValue && !IsReadableForTheme(color, nextTheme.Value))
            {
                color = nextTheme.Value == ElementTheme.Dark ? Colors.White : Colors.Black;
            }

            _hostedTitleBarForegroundColor = color;
            ApplyHostedTitleBarForeground();
        }

        private static ElementTheme? NormalizeHostedTitleBarTheme(string? theme)
        {
            return theme?.Trim().ToLowerInvariant() switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => null
            };
        }

        private static bool TryParseCssColor(string? value, out Color color)
        {
            color = default;
            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                text = text.Substring(1);
            }

            if (text.Length != 6
                || !int.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                || !int.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                || !int.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return false;
            }

            color = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
            return true;
        }

        private static Color GetThemeForegroundColor() =>
            AppThemeManager.GetIsDarkTheme() ? Colors.White : Colors.Black;

        private static bool IsReadableForTheme(Color color, ElementTheme theme)
        {
            var luminance = GetRelativeLuminance(color);
            return theme == ElementTheme.Dark
                ? luminance >= 0.5
                : luminance < 0.5;
        }

        private static Color GetContrastingForegroundColor(Color background)
        {
            return GetContrastRatio(Colors.White, background) >= GetContrastRatio(Colors.Black, background)
                ? Colors.White
                : Colors.Black;
        }

        private static double GetContrastRatio(Color foreground, Color background)
        {
            var foregroundLuminance = GetRelativeLuminance(foreground);
            var backgroundLuminance = GetRelativeLuminance(background);
            var light = Math.Max(foregroundLuminance, backgroundLuminance);
            var dark = Math.Min(foregroundLuminance, backgroundLuminance);
            return (light + 0.05) / (dark + 0.05);
        }

        private static double GetRelativeLuminance(Color color)
        {
            static double Linearize(byte component)
            {
                var value = component / 255.0;
                return value <= 0.03928
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.R)
                + 0.7152 * Linearize(color.G)
                + 0.0722 * Linearize(color.B);
        }

        private void ApplyHostedTitleBarForeground()
        {
            var color = _isHostedTitleBarVisible && _hostedTitleBarForegroundColor.HasValue
                ? _hostedTitleBarForegroundColor
                : null;

            AppThemeManager.CustomizeTitleBar(color);

            if (color.HasValue)
            {
                var brush = new SolidColorBrush(color.Value);
                MoreButton.Foreground = brush;
                DownloadTitleBarButton.Foreground = brush;
                DownloadTitleBarProgressRing.Foreground = brush;
            }
            else
            {
                MoreButton.ClearValue(Control.ForegroundProperty);
                DownloadTitleBarButton.ClearValue(Control.ForegroundProperty);
                DownloadTitleBarProgressRing.ClearValue(Control.ForegroundProperty);
            }
        }

        public void RefreshHostedTitleBarForeground()
        {
            ApplyHostedTitleBarForeground();
        }

        private void ResetHostedTitleBarAppearance()
        {
            _hostedTitleBarForegroundColor = null;

            if (_hasHostedTitleBarThemeOverride)
            {
                _hasHostedTitleBarThemeOverride = false;
                AppThemeManager.LoadSettings(_containerId);
                AppThemeManager.ApplyTheme();
                AppThemeManager.ApplyMaterial();
                GetContainerPage()?.RefreshHostTheme();
            }

            ApplyHostedTitleBarForeground();
        }

        private void UpdateTitleBarDragAreaBounds()
        {
            const double exclusionPadding = 4;
            const double minDragSegmentWidth = 8;
            const double minDragSegmentHeight = 8;
            var windowWidth = TitleBarArea.ActualWidth > 0
                ? TitleBarArea.ActualWidth
                : Window.Current.Bounds.Width;
            var activeHeight = GetActiveTitleBarHeight();
            var titleBarButtonHeight = Math.Min(DefaultTitleBarHeight, activeHeight);
            var dragRects = new List<TitleBarRect>
            {
                new TitleBarRect(0, 0, Math.Max(0, windowWidth), activeHeight)
            };

            if (_titleBarLeftInset > 0 && titleBarButtonHeight > 0)
            {
                dragRects = SubtractTitleBarRect(
                    dragRects,
                    new TitleBarRect(0, 0, Math.Max(0, _titleBarLeftInset), titleBarButtonHeight),
                    minDragSegmentWidth,
                    minDragSegmentHeight);
            }

            if (_titleBarRightInset > 0 && titleBarButtonHeight > 0)
            {
                var rightInset = Math.Max(0, _titleBarRightInset);
                dragRects = SubtractTitleBarRect(
                    dragRects,
                    new TitleBarRect(Math.Max(0, windowWidth - rightInset), 0, rightInset, titleBarButtonHeight),
                    minDragSegmentWidth,
                    minDragSegmentHeight);
            }

            var interactiveRects = _isHostedTitleBarVisible
                ? _hostedTitleBarInteractiveRects
                : Array.Empty<TitleBarInteractiveRect>();

            foreach (var rect in interactiveRects)
            {
                if (rect.Width <= 0
                    || rect.Height <= 0
                    || rect.Bottom <= 0
                    || rect.Top >= activeHeight)
                {
                    continue;
                }

                dragRects = SubtractTitleBarRect(
                    dragRects,
                    TitleBarRect.FromEdges(
                        Math.Max(0, rect.Left - exclusionPadding),
                        Math.Max(0, rect.Top - exclusionPadding),
                        Math.Min(windowWidth, rect.Right + exclusionPadding),
                        Math.Min(activeHeight, rect.Bottom + exclusionPadding)),
                    minDragSegmentWidth,
                    minDragSegmentHeight);
            }

            TitleBarDragArea.Margin = new Thickness(0);
            TitleBarDragArea.Width = windowWidth;
            TitleBarDragArea.Height = activeHeight;
            TitleBarDragArea.Children.Clear();

            foreach (var rect in dragRects)
            {
                AddTitleBarDragSegment(rect.Left, rect.Top, rect.Width, rect.Height);
            }
        }

        private void AddTitleBarDragSegment(double left, double top, double width, double height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var segment = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent)
            };
            Canvas.SetLeft(segment, left);
            Canvas.SetTop(segment, top);
            TitleBarDragArea.Children.Add(segment);
        }

        private static List<TitleBarRect> SubtractTitleBarRect(
            IReadOnlyList<TitleBarRect> sourceRects,
            TitleBarRect exclusion,
            double minWidth,
            double minHeight)
        {
            if (exclusion.Width <= 0 || exclusion.Height <= 0)
            {
                return sourceRects.ToList();
            }

            var result = new List<TitleBarRect>();
            foreach (var rect in sourceRects)
            {
                if (!rect.Intersects(exclusion))
                {
                    result.Add(rect);
                    continue;
                }

                var overlap = rect.Intersection(exclusion);
                AddTitleBarRectIfUseful(result, TitleBarRect.FromEdges(rect.Left, rect.Top, rect.Right, overlap.Top), minWidth, minHeight);
                AddTitleBarRectIfUseful(result, TitleBarRect.FromEdges(rect.Left, overlap.Bottom, rect.Right, rect.Bottom), minWidth, minHeight);
                AddTitleBarRectIfUseful(result, TitleBarRect.FromEdges(rect.Left, overlap.Top, overlap.Left, overlap.Bottom), minWidth, minHeight);
                AddTitleBarRectIfUseful(result, TitleBarRect.FromEdges(overlap.Right, overlap.Top, rect.Right, overlap.Bottom), minWidth, minHeight);
            }

            return result;
        }

        private static void AddTitleBarRectIfUseful(
            ICollection<TitleBarRect> rects,
            TitleBarRect rect,
            double minWidth,
            double minHeight)
        {
            if (rect.Width >= minWidth && rect.Height >= minHeight)
            {
                rects.Add(rect);
            }
        }

        public readonly struct TitleBarInteractiveRect
        {
            public TitleBarInteractiveRect(double left, double top, double width, double height)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            public double Left { get; }
            public double Top { get; }
            public double Width { get; }
            public double Height { get; }
            public double Right => Left + Width;
            public double Bottom => Top + Height;
        }

        private readonly struct TitleBarRect
        {
            public TitleBarRect(double left, double top, double width, double height)
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            public double Left { get; }
            public double Top { get; }
            public double Width { get; }
            public double Height { get; }
            public double Right => Left + Width;
            public double Bottom => Top + Height;

            public static TitleBarRect FromEdges(double left, double top, double right, double bottom) =>
                new TitleBarRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

            public bool Intersects(TitleBarRect other) =>
                Left < other.Right
                && Right > other.Left
                && Top < other.Bottom
                && Bottom > other.Top;

            public TitleBarRect Intersection(TitleBarRect other) =>
                FromEdges(
                    Math.Max(Left, other.Left),
                    Math.Max(Top, other.Top),
                    Math.Min(Right, other.Right),
                    Math.Min(Bottom, other.Bottom));
        }

        private void MainPage_CoreWindowActivated(CoreWindow sender, WindowActivatedEventArgs args)
        {
            bool isActive = args.WindowActivationState != CoreWindowActivationState.Deactivated;
            IsWindowActive = isActive;
            TitleBarAppName.Opacity = isActive ? 1.0 : 0.5;
            GetContainerPage()?.SetHostedWindowActive(isActive);
        }

        public async void OpenExternalLink(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string btnUrl)
            {
                await PromptOpenExternalAsync(btnUrl);
            }
            else if (sender is HyperlinkButton link && link.Tag is string linkUrl)
            {
                await PromptOpenExternalAsync(linkUrl);
            }
        }

        public async Task<bool> PromptOpenExternalAsync(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var dialog = new Dialogs.ExternalOpenDialog();
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return false;
            }

            return await Launcher.LaunchUriAsync(uri);
        }

        public static bool TryNormalizeEnteredUrl(string? value, out string url)
        {
            url = "";
            var input = value?.Trim();
            if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsWhiteSpace))
            {
                return false;
            }

            if (Uri.TryCreate(input, UriKind.Absolute, out var absoluteUri))
            {
                return TryAcceptHttpUrl(absoluteUri, out url);
            }

            if (input.Contains("://"))
            {
                return false;
            }

            return Uri.TryCreate($"http://{input}", UriKind.Absolute, out var prefixedUri)
                && TryAcceptHttpUrl(prefixedUri, out url);
        }

        public async Task OpenUrlInNewContainerAsync(string url, string? displayName = null, string iconPath = "")
        {
            if (!IsSupportedUrl(url))
            {
                return;
            }

            var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? CreateDisplayNameFromUrl(url)
                : displayName.Trim();
            var container = SettingsManager.Instance.CreateOrUpdateContainer(resolvedDisplayName, url, iconPath, activate: false);
            SettingsManager.Instance.AddContainerHomeUrlHistory(container.Id, url);
            await App.LaunchContainerInNewViewAsync(container.Id);
        }

        private static bool TryAcceptHttpUrl(Uri uri, out string url)
        {
            url = "";
            if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !IsValidUserHost(uri.Host))
            {
                return false;
            }

            url = uri.AbsoluteUri;
            return true;
        }

        private static bool IsValidUserHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)
                || !host.Contains(".")
                || host.StartsWith(".")
                || host.EndsWith("."))
            {
                return false;
            }

            return host.Split('.').All(part => !string.IsNullOrWhiteSpace(part));
        }

        private static string CreateDisplayNameFromUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                return url;
            }

            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host.Substring(4)
                : uri.Host;
            var firstLabel = host.Split('.').FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstLabel) ? host : firstLabel;
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
                "msVisualRejuvMica",
                "msWebView2EnableDraggableRegions"
            };

            if (forceDark)
            {
                features.Add("WebContentsForceDark");
            }

            return $"--enable-features={string.Join(",", features)}";
        }

        private ContainerPage? GetContainerPage() =>
            ContentFrame.Content as ContainerPage ?? _containerPage;

        public ContainerPage? GetContainerPageForSettings() => GetContainerPage();

        public string ContainerHomeUrl => SettingsManager.Instance.GetContainerHomeUrl(_containerId);
        public string ContainerWebViewDataFolder => SettingsManager.Instance.GetContainerWebViewDataFolder(_containerId);

        public void ApplyCurrentContainerDevToolsSetting()
        {
            GetContainerPage()?.RefreshDevToolsAvailability();
        }

        public void ActivateCurrentContainer()
        {
            _containerId = SettingsManager.Instance.ActiveContainerId;
            RefreshContainerIdentity();
            AppThemeManager.LoadSettings(_containerId);
            AppThemeManager.ApplyTheme();
            AppThemeManager.ApplyMaterial();
            _hostedDocumentTitle = "";
            _hostedManifestName = SettingsManager.Instance.GetContainerManifestName(_containerId);
            _hostedDocumentIconUri = null;
            _hostedDocumentIconCandidates = Array.Empty<Uri>();
            LoadCachedHostedTitleBarState();
            UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
            ContentFrame.Navigate(typeof(Pages.ContainerPage), this);
            ApplyHostedDocumentInfo();
        }

        public void RefreshContainerIdentity()
        {
            _containerDisplayName = SettingsManager.Instance.GetContainerDisplayName(_containerId);
            _hostedManifestName = SettingsManager.Instance.GetContainerManifestName(_containerId);
            TitleBarAppName.Text = _containerDisplayName;
            ImgAppIcon.Source = new BitmapImage(SettingsManager.Instance.GetContainerIconUri(_containerId));
            UpdateWindowTitle();
        }

        private void LoadCachedHostedTitleBarState()
        {
            _hostedTitleBarInteractiveRects = Array.Empty<TitleBarInteractiveRect>();
            _hostedTitleBarForegroundColor = null;
            _hasHostedTitleBarThemeOverride = false;
            var cachedHeight = SettingsManager.Instance.GetContainerHostedTitleBarHeight(_containerId);
            if (cachedHeight > 0)
            {
                _hostedTitleBarHeight = CoerceHostedTitleBarHeight(cachedHeight);
                _requestedHostedTitleBarVisible = true;
                _isHostedTitleBarVisible = true;
                TitleBarIdentityArea.Visibility = Visibility.Collapsed;
                ContentFrame.Margin = new Thickness(0);
                return;
            }

            _hostedTitleBarHeight = DefaultTitleBarHeight;
            _requestedHostedTitleBarVisible = false;
            _isHostedTitleBarVisible = false;
            TitleBarIdentityArea.Visibility = Visibility.Visible;
            ContentFrame.Margin = new Thickness(0, DefaultTitleBarHeight, 0, 0);
        }

        public void SetHostedTitleBarVisible(bool isVisible, double? titleBarHeight = null)
        {
            if (!isVisible && !_isHostedPageLoaded && _isHostedTitleBarVisible)
            {
                return;
            }

            _requestedHostedTitleBarVisible = isVisible;
            if (isVisible && titleBarHeight.HasValue)
            {
                _hostedTitleBarHeight = CoerceHostedTitleBarHeight(titleBarHeight.Value);
            }
            else if (!isVisible)
            {
                _hostedTitleBarHeight = DefaultTitleBarHeight;
                ResetHostedTitleBarAppearance();
            }

            if (isVisible || _isHostedPageLoaded)
            {
                SettingsManager.Instance.SetContainerHostedTitleBarHeight(
                    _containerId,
                    isVisible ? _hostedTitleBarHeight : 0);
            }

            ApplyHostedTitleBarVisibility(_isSettingsHostOpen ? false : isVisible);
        }

        public void SetHostedTitleBarHeight(double titleBarHeight)
        {
            var nextHeight = CoerceHostedTitleBarHeight(titleBarHeight);
            if (Math.Abs(_hostedTitleBarHeight - nextHeight) < 0.5)
            {
                return;
            }

            _hostedTitleBarHeight = nextHeight;
            if (_isHostedTitleBarVisible)
            {
                UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
            }
        }

        private static double CoerceHostedTitleBarHeight(double titleBarHeight)
        {
            if (double.IsNaN(titleBarHeight) || double.IsInfinity(titleBarHeight))
            {
                return DefaultTitleBarHeight;
            }

            return Math.Max(DefaultTitleBarHeight, Math.Min(MaxHostedTitleBarHeight, Math.Ceiling(titleBarHeight)));
        }

        private void ApplyHostedTitleBarVisibility(bool isVisible)
        {
            if (_isHostedTitleBarVisible == isVisible)
            {
                UpdateHostedTitleBarGeometry();
                return;
            }

            _isHostedTitleBarVisible = isVisible;
            TitleBarIdentityArea.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            ContentFrame.Margin = isVisible ? new Thickness(0) : new Thickness(0, 32, 0, 0);
            UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
            ApplyHostedTitleBarForeground();
        }

        public void UpdateHostedDocumentInfo(string? title, Uri? iconUri, IReadOnlyList<Uri>? iconCandidates = null)
        {
            _hostedDocumentTitle = string.IsNullOrWhiteSpace(title) ? "" : title.Trim();
            _hostedDocumentIconUri = iconUri;
            if (iconCandidates != null && iconCandidates.Count > 0)
            {
                _hostedDocumentIconCandidates = iconCandidates;
            }
            else
            {
                _hostedDocumentIconCandidates = iconUri == null ? Array.Empty<Uri>() : new[] { iconUri! };
            }
            System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Document icon candidates: {string.Join(", ", _hostedDocumentIconCandidates.Select(item => item.AbsoluteUri))}");

            if (_isHostedPageLoaded)
            {
                _ = PrepareHostedTileIconsAsync();
            }

            if (_isSettingsHostOpen)
            {
                return;
            }

            ApplyHostedDocumentInfo();
        }

        public void UpdateHostedManifestInfo(string? siteName)
        {
            var name = siteName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _hostedManifestName = name;
            SettingsManager.Instance.SetContainerManifestName(_containerId, name);
            if (!_isSettingsHostOpen
                && ContentFrame?.CurrentSourcePageType == typeof(Pages.ContainerPage))
            {
                ApplyHostedDocumentInfo();
            }
        }

        private void ApplyHostedDocumentInfo()
        {
            TitleBarAppName.Text = GetHostedTitleBarText();

            if (_hostedDocumentIconUri != null)
            {
                try
                {
                    ImgAppIcon.Source = new BitmapImage(_hostedDocumentIconUri);
                }
                catch
                {
                    ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
                }
            }
            else
            {
                ImgAppIcon.Source = new BitmapImage(SettingsManager.Instance.GetContainerIconUri(_containerId));
            }

            UpdateWindowTitle();
        }

        private string GetHostedTitleBarText()
        {
            if (ShouldUseHostedManifestName())
            {
                return _hostedManifestName;
            }

            return _containerDisplayName;
        }

        private void UpdateWindowTitle()
        {
            ApplicationView.GetForCurrentView().Title = ShouldUseHostedManifestName()
                ? _hostedManifestName
                : "";
        }

        private bool ShouldUseHostedManifestName()
        {
            return !_isSettingsHostOpen
                && ContentFrame?.CurrentSourcePageType == typeof(Pages.ContainerPage)
                && !string.IsNullOrWhiteSpace(_hostedManifestName);
        }

        private void MoreFlyout_Opening(object sender, object e)
        {
            GetContainerPage()?.SetHostedWindowActive(true);
            var isLauncherContainer = IsPrimaryContainer;
            var pinnedContainerId = isLauncherContainer ? null : GetPinnedContainerIdForCurrentSite();
            UpdateMoreFlyoutHeader(pinnedContainerId);
            if (string.IsNullOrWhiteSpace(pinnedContainerId))
            {
                UrlAutoSuggestBox.Text = isLauncherContainer ? "" : ContainerHomeUrl;
                RefreshUrlHistoryItems();
            }
            SiteActionsTopDivider.Visibility = isLauncherContainer ? Visibility.Collapsed : Visibility.Visible;
            SitePermissionsEntryButton.Visibility = isLauncherContainer ? Visibility.Collapsed : Visibility.Visible;
            UrlAutoSuggestBox.IsSuggestionListOpen = false;
            UrlAutoSuggestBox.IsEnabled = false;
            HideUrlError();
            UpdatePinContainerState();
        }

        private void MoreFlyout_Opened(object sender, object e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UrlAutoSuggestBox.IsEnabled = EditableUrlPanel.Visibility == Visibility.Visible;
            });
        }

        private void UpdateMoreFlyoutHeader(string? pinnedContainerId)
        {
            var isPinned = !string.IsNullOrWhiteSpace(pinnedContainerId);
            PinnedSiteInfoPanel.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
            EditableUrlPanel.Visibility = isPinned ? Visibility.Collapsed : Visibility.Visible;

            if (!isPinned)
            {
                return;
            }

            var siteName = SettingsManager.Instance.GetContainerSiteName(pinnedContainerId!);
            if (string.IsNullOrWhiteSpace(siteName))
            {
                siteName = _containerDisplayName;
            }

            PinnedSiteInfoHeaderText.Text = GetResourceOrFallback("PinnedSiteInfoHeaderText");
            PinnedSiteInfoNameText.Text = siteName;
            PinnedSiteInfoPublisherText.Text = string.Format(
                GetResourceOrFallback("PinnedSiteInfoPublisherFormat"),
                GetCurrentSitePublisher());
            PinnedSiteInfoIcon.Source = new BitmapImage(SettingsManager.Instance.GetContainerIconUri(pinnedContainerId!));
        }

        private string GetCurrentSitePublisher()
        {
            var url = GetCurrentSiteUrl();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }

            return "";
        }

        private string GetCurrentSiteUrl() => GetContainerPage()?.CurrentUrl ?? ContainerHomeUrl;

        private string? GetPinnedContainerIdForCurrentSite()
        {
            if (IsPrimaryContainer)
            {
                return null;
            }

            var url = !string.IsNullOrWhiteSpace(_pinCandidateUrl)
                ? _pinCandidateUrl
                : GetCurrentSiteUrl();
            if (IsSupportedUrl(url))
            {
                var existingContainerId = SettingsManager.Instance.GetContainerIdForHomeUrl(url);
                if (!string.IsNullOrWhiteSpace(existingContainerId)
                    && SecondaryTile.Exists(existingContainerId))
                {
                    return existingContainerId;
                }
            }

            return SecondaryTile.Exists(_containerId) ? _containerId : null;
        }

        private void UrlAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                if (!string.IsNullOrWhiteSpace(sender.Text)
                    && !TryNormalizeEnteredUrl(sender.Text, out _))
                {
                    UrlErrorText.Visibility = Visibility.Visible;
                    sender.IsSuggestionListOpen = false;
                    return;
                }

                HideUrlError();
                ShowUrlHistorySuggestions();
                return;
            }

            HideUrlError();
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
                SettingsManager.Instance.RemoveContainerHomeUrlHistory(_containerId, url);
                ShowUrlHistorySuggestions();
                UrlAutoSuggestBox.Focus(FocusState.Programmatic);
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ShowUrlHistorySuggestions);
            }
        }

        private void RestoreUrlButton_Click(object sender, RoutedEventArgs e)
        {
            var defaultUrl = _loader.GetString("DefaultHomeUrl");
            SettingsManager.Instance.SetContainerHomeUrl(_containerId, defaultUrl);
            UrlAutoSuggestBox.Text = defaultUrl;
            HideUrlError();
            GetContainerPage()?.Navigate(defaultUrl);
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
            NavigateSettingsPage(typeof(Pages.AboutPage), CreateDefaultSettingsTransition());
        }

        private void SitePermissionsEntryButton_Click(object sender, RoutedEventArgs e)
        {
            MoreFlyout.Hide();
            NavigateSettingsPage(typeof(Pages.SitePermissionsPage), CreateDefaultSettingsTransition());
        }

        public void OpenContainerManagementPage()
        {
            NavigateSettingsPage(
                typeof(Pages.ContainerManagementPage),
                CreateSettingsSlideTransition(SlideNavigationTransitionEffect.FromRight));
        }

        private void NavigateSettingsPage(Type pageType, NavigationTransitionInfo? transitionInfo = null)
        {
            if (ContentFrame.CurrentSourcePageType == pageType) return;
            ContentFrame.Navigate(pageType, null, transitionInfo ?? CreateDefaultSettingsTransition());
        }

        private async void PinContainerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isHostedPageLoaded)
            {
                ShowPinUnavailableTeachingTip();
                return;
            }

            var url = GetContainerPage()?.CurrentUrl ?? ContainerHomeUrl;
            if (!IsSupportedUrl(url))
            {
                UrlErrorText.Visibility = Visibility.Visible;
                return;
            }

            var displayName = GetTileDisplayName().Trim();
            var existingContainerId = SettingsManager.Instance.GetContainerIdForHomeUrl(url);
            var reservedNames = GetPinnedTileNames(existingContainerId);
            if (string.IsNullOrWhiteSpace(displayName)
                || reservedNames.Any(item => string.Equals(item, displayName, StringComparison.OrdinalIgnoreCase)))
            {
                var dialog = new Dialogs.RenameTileDialog(displayName, reservedNames);
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }

                displayName = dialog.TileName;
            }

            var tileIcons = await PrepareHostedTileIconsAsync();
            var container = SettingsManager.Instance.CreateOrUpdateContainer(displayName, url, tileIcons.Square150Path, activate: false);
            if (!string.IsNullOrWhiteSpace(tileIcons.Square150Path))
            {
                SettingsManager.Instance.UpdateContainerIcon(container.Id, tileIcons.Square150Path);
            }

            var logoUri = string.IsNullOrWhiteSpace(tileIcons.Square150Path)
                ? GetDefaultTileLogoUri()
                : CreateLocalUri(tileIcons.Square150Path);

            SecondaryTile tile;
            try
            {
                tile = CreateSecondaryTile(container.Id, container.DisplayName, logoUri);
            }
            catch (ArgumentException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Create secondary tile failed with page icon: {ex.Message}");
                try
                {
                    tile = CreateSecondaryTile(container.Id, container.DisplayName, GetDefaultTileLogoUri());
                }
                catch (ArgumentException fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Create secondary tile failed with default icon: {fallbackEx.Message}");
                    UrlErrorText.Visibility = Visibility.Visible;
                    return;
                }
            }

            ApplyTileVisualElements(tile, tileIcons);
            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            tile.VisualElements.ShowNameOnWide310x150Logo = true;

            var existedBeforeRequest = SecondaryTile.Exists(container.Id);
            bool isPinned;
            try
            {
                isPinned = existedBeforeRequest
                    ? await tile.UpdateAsync()
                    : await tile.RequestCreateAsync();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Secondary tile request failed: 0x{ex.HResult:X8} {ex.Message}");
                isPinned = SecondaryTile.Exists(container.Id);
                if (isPinned && !existedBeforeRequest)
                {
                    try
                    {
                        await tile.UpdateAsync();
                    }
                    catch (System.Runtime.InteropServices.COMException updateEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Secondary tile update after request failed: 0x{updateEx.HResult:X8} {updateEx.Message}");
                    }
                }
            }

            if (!isPinned)
            {
                return;
            }

            await HideMoreFlyoutAsync();
            UpdatePinContainerState();
        }

        private async Task HideMoreFlyoutAsync()
        {
            if (Dispatcher.HasThreadAccess)
            {
                TryHideMoreFlyout();
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, TryHideMoreFlyout);
        }

        private void TryHideMoreFlyout()
        {
            try
            {
                MoreFlyout.Hide();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Flyout] Hide failed: 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        private void PinUnavailableInfoButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPinUnavailableTeachingTip();
        }

        public void CloseSettingsHost()
        {
            NavigateSettingsBack();
        }

        private void SystemNavigationManager_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (ContentFrame.CanGoBack)
            {
                e.Handled = true;
                NavigateSettingsBack();
            }
        }

        private void ContentFrame_Navigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.Content is ContainerPage containerPage)
            {
                _containerPage = containerPage;
                UpdateHostedTitleBarGeometry();
            }

            _currentSettingsPageType = e.SourcePageType;
            UpdateNavigationState(e.SourcePageType);
        }

        private void NavigateSettingsBack()
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        private void UpdateNavigationState(Type? pageType)
        {
            ContentFrame.Visibility = Visibility.Visible;

            if (pageType == typeof(Pages.ContainerPage))
            {
                _isSettingsHostOpen = false;
                _currentSettingsPageType = null;
                HideSettingsTitle();
                MoreButton.Visibility = Visibility.Visible;
                UpdateDownloadTitleBarButton(false);
                ApplyHostedTitleBarVisibility(_requestedHostedTitleBarVisible);
                ApplyHostedDocumentInfo();
                GetContainerPage()?.SetHostedWindowActive(IsWindowActive);
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = ContentFrame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;
                return;
            }

            if (!IsSettingsPage(pageType))
            {
                _isSettingsHostOpen = false;
                HideSettingsTitle();
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = ContentFrame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;
                return;
            }

            _isSettingsHostOpen = true;
            ApplyAppTitleBarIdentity();
            ApplyHostedTitleBarVisibility(false);
            ContentFrame.Margin = new Thickness(0, 96, 0, 0);
            UpdateDownloadTitleBarButton(false);
            MoreButton.Visibility = Visibility.Collapsed;
            UpdateSettingsTitleForPage(pageType, GetSettingsBreadcrumb(pageType));
        }

        private static bool IsSettingsPage(Type? pageType)
        {
            return pageType == typeof(Pages.AboutPage)
                || pageType == typeof(Pages.SitePermissionsPage)
                || pageType == typeof(Pages.ContainerManagementPage);
        }

        private string GetSettingsBreadcrumb(Type? pageType)
        {
            return pageType == typeof(Pages.ContainerManagementPage)
                ? _loader.GetString("ContainerManagement_Breadcrumb")
                : pageType == typeof(Pages.SitePermissionsPage)
                    ? _loader.GetString("SitePermissions_Breadcrumb")
                    : _loader.GetString("Settings_Breadcrumb");
        }

        private void UpdateSettingsTitleForPage(Type? pageType, string breadcrumb)
        {
            if (pageType == typeof(Pages.SitePermissionsPage))
            {
                ShowSettingsTitle(breadcrumb, false);
                return;
            }

            ShowSettingsTitle(breadcrumb, true);
        }

        private void ShowSettingsTitle(string? breadcrumb = null, bool includeSettingsRoot = true)
        {
            SettingsHost.Visibility = Visibility.Visible;
            BreadcrumbItems.Clear();
            var settingsBreadcrumb = _loader.GetString("Settings_Breadcrumb");
            if (includeSettingsRoot)
            {
                BreadcrumbItems.Add(settingsBreadcrumb);
                if (!string.IsNullOrWhiteSpace(breadcrumb)
                    && !string.Equals(breadcrumb, settingsBreadcrumb, StringComparison.OrdinalIgnoreCase))
                {
                    BreadcrumbItems.Add(breadcrumb);
                }
            }
            else if (!string.IsNullOrWhiteSpace(breadcrumb))
            {
                BreadcrumbItems.Add(breadcrumb);
            }
            BreadcrumbPanel.Visibility = BreadcrumbItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;
            UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
            ApplyAppTitleBarIdentity();
        }

        private void ApplyAppTitleBarIdentity()
        {
            TitleBarAppName.Text = Package.Current.DisplayName;
            ImgAppIcon.Source = new BitmapImage(Package.Current.Logo);
            ApplicationView.GetForCurrentView().Title = "";
        }

        private void BreadcrumbNav_ItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
        {
            if (args.Index == 0)
            {
                NavigateSettingsBack();
            }
        }

        private static NavigationTransitionInfo CreateSettingsSlideTransition(SlideNavigationTransitionEffect effect)
        {
            return new SlideNavigationTransitionInfo
            {
                Effect = effect
            };
        }

        private static NavigationTransitionInfo CreateDefaultSettingsTransition()
        {
            return new CommonNavigationTransitionInfo();
        }

        private void HideSettingsTitle()
        {
            BreadcrumbItems.Clear();
            BreadcrumbPanel.Visibility = Visibility.Collapsed;
            SettingsHost.Visibility = Visibility.Collapsed;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
            UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
            ApplyHostedDocumentInfo();
        }

        private async void TryApplyUrl()
        {
            if (!TryNormalizeEnteredUrl(UrlAutoSuggestBox.Text, out var url))
            {
                UrlErrorText.Visibility = Visibility.Visible;
                return;
            }

            SettingsManager.Instance.AddContainerHomeUrlHistory(_containerId, url);
            RefreshUrlHistoryItems();
            UrlAutoSuggestBox.Text = url;
            MoreFlyout.Hide();
            try
            {
                await OpenUrlInNewContainerAsync(url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Launcher] Open URL failed: {ex.Message}");
            }
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
            var defaultUrl = _loader.GetString("DefaultHomeUrl");
            if (IsSupportedUrl(defaultUrl))
            {
                _urlHistoryItems.Add(defaultUrl);
            }

            foreach (var url in SettingsManager.Instance.GetContainerHomeUrlHistory(_containerId).Where(IsSupportedUrl))
            {
                if (!_urlHistoryItems.Any(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase)))
                {
                    _urlHistoryItems.Add(url);
                }
            }
        }

        private void ShowUrlHistorySuggestions()
        {
            RefreshUrlHistoryItems();
            UrlAutoSuggestBox.IsSuggestionListOpen = _urlHistoryItems.Count > 0;
        }

        public void SetHostedPageLoading(string? url = null)
        {
            _isHostedPageLoaded = false;
            _hostedManifestName = SettingsManager.Instance.GetContainerManifestName(_containerId);
            _preparedTileIconUri = null;
            _preparedTileIconKey = "";
            _preparedTileIcons = TileIconSet.Empty;
            if (!string.IsNullOrWhiteSpace(url))
            {
                _pinCandidateUrl = url;
            }
            UpdatePinContainerState();
        }

        public void SetHostedPageLoaded(bool isLoaded)
        {
            _isHostedPageLoaded = isLoaded;
            if (isLoaded)
            {
                _ = PrepareHostedTileIconsAsync();
            }
            UpdatePinContainerState();
        }

        private void UpdatePinContainerState()
        {
            if (!Dispatcher.HasThreadAccess)
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdatePinContainerStateCore);
                return;
            }

            UpdatePinContainerStateCore();
        }

        private void UpdatePinContainerStateCore()
        {
            if (PinContainerRow == null)
            {
                return;
            }

            if (IsPrimaryContainer)
            {
                PinContainerRow.Visibility = Visibility.Collapsed;
                PinUnavailableInfoButton.Visibility = Visibility.Collapsed;
                return;
            }

            var isAlreadyPinned = GetPinnedContainerIdForCurrentSite() != null;

            PinContainerRow.Visibility = isAlreadyPinned ? Visibility.Collapsed : Visibility.Visible;
            PinContainerButton.IsEnabled = _isHostedPageLoaded;
            PinUnavailableInfoButton.Visibility = _isHostedPageLoaded ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowPinUnavailableTeachingTip()
        {
            PinUnavailableTeachingTip.Target = PinUnavailableInfoButton;
            PinUnavailableTeachingTip.IsOpen = true;
        }

        public async Task<string> CreateDownloadFilePathAsync(string? suggestedFileName)
        {
            var folder = await GetDownloadsFolderForDownloadAsync();
            var fileName = SanitizeFileName(suggestedFileName, GetResourceOrFallback("DownloadDefaultFileName"));
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);

            for (var index = 0; index < 1000; index++)
            {
                var candidateName = index == 0
                    ? fileName
                    : $"{baseName} ({index}){extension}";
                var candidatePath = Path.Combine(folder.Path, candidateName);
                if (await folder.TryGetItemAsync(candidateName) == null)
                {
                    return candidatePath;
                }
            }

            return Path.Combine(folder.Path, $"{baseName}-{Guid.NewGuid():N}{extension}");
        }

        public async Task<bool> EnsureDownloadsAccessForDownloadAsync()
        {
            if (await CanAccessDownloadFolderAsync())
            {
                return true;
            }

            await ShowDownloadFolderAccessDialogAsync();
            return false;
        }

        public async Task CheckDownloadAccessOnFirstLaunchAsync()
        {
            if (SettingsManager.Instance.HasShownDownloadAccessPrompt
                || await CanAccessDownloadFolderAsync())
            {
                return;
            }

            SettingsManager.Instance.HasShownDownloadAccessPrompt = true;
            await ShowDownloadFolderAccessDialogAsync();
        }

        public void AddDownload(CoreWebView2DownloadOperation operation)
        {
            var item = new DownloadItemViewModel(operation, _loader);
            _isDownloadCompletionAcknowledged = false;
            _downloadItems.Insert(0, item);
            item.PersistDownloadHistory(true);
            UpdateDownloadsEmptyState();
            UpdateDownloadTitleBarStatus();
            ShowTransientDownloadsButton();
            ShowDownloadsFlyout();

            operation.BytesReceivedChanged += (downloadOperation, args) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    item.Refresh();
                    UpdateDownloadTitleBarStatus();
                });
            };
            operation.EstimatedEndTimeChanged += (downloadOperation, args) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    item.Refresh();
                    UpdateDownloadTitleBarStatus();
                });
            };
            operation.StateChanged += (downloadOperation, args) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    item.Refresh();
                    UpdateDownloadsEmptyState();
                    if (item.State == CoreWebView2DownloadState.Completed)
                    {
                        item.ShowOpenAction();
                        _isDownloadCompletionAcknowledged = _isDownloadsFlyoutOpen;
                    }
                    item.PersistDownloadHistory(true);
                    UpdateDownloadTitleBarStatus();
                });
            };
        }

        private void LoadDownloadHistory()
        {
            _downloadItems.Clear();
            foreach (var entry in SettingsManager.Instance.DownloadHistory)
            {
                _downloadItems.Add(new DownloadItemViewModel(entry, _loader));
            }
        }

        private void DownloadsEntryButton_Click(object sender, RoutedEventArgs e)
        {
            MoreFlyout.Hide();
            ShowTransientDownloadsButton();
            ShowDownloadsFlyout();
        }

        private void DownloadsFlyout_Opened(object sender, object e)
        {
            _isDownloadsFlyoutOpen = true;
            _isDownloadCompletionAcknowledged = true;
            ShowTransientDownloadsButton();
            _ = RefreshDownloadFileAvailabilityAsync();
            UpdateDownloadTitleBarStatus();
            _downloadsFileRefreshTimer.Start();
        }

        private void DownloadsFlyout_Closed(object sender, object e)
        {
            _isDownloadsFlyoutOpen = false;
            _downloadsFileRefreshTimer.Stop();
            if (!_isDownloadsButtonPinned && !_isDownloadsContextFlyoutOpen)
            {
                HideTransientDownloadsButton();
            }
        }

        private void DownloadContextFlyout_Opening(object sender, object e)
        {
            _isDownloadsContextFlyoutOpen = true;
            ShowTransientDownloadsButton();
        }

        private void DownloadContextFlyout_Closed(object sender, object e)
        {
            _isDownloadsContextFlyoutOpen = false;
            if (!_isDownloadsButtonPinned && !_isDownloadsFlyoutOpen)
            {
                HideTransientDownloadsButton();
            }
        }

        private async void DownloadsFileRefreshTimer_Tick(object sender, object e)
        {
            if (!_isDownloadsFlyoutOpen || _isRefreshingDownloadFileAvailability)
            {
                return;
            }

            _isRefreshingDownloadFileAvailability = true;
            try
            {
                await RefreshDownloadFileAvailabilityAsync();
            }
            finally
            {
                _isRefreshingDownloadFileAvailability = false;
            }
        }

        private async void OpenDownloadsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (await TryGetDownloadFolderAsync() is StorageFolder folder)
            {
                await Launcher.LaunchFolderAsync(folder);
            }
            else
            {
                await ShowDownloadFolderAccessDialogAsync();
            }
        }

        private async Task<StorageFolder> GetDownloadsFolderForDownloadAsync()
        {
            var folder = await TryGetDownloadFolderAsync();
            if (folder == null)
            {
                throw new UnauthorizedAccessException("Download folder is not accessible.");
            }

            return folder;
        }

        private static async Task<StorageFolder?> TryGetDownloadFolderAsync()
        {
            var token = SettingsManager.Instance.DownloadFolderToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                    if (folder != null)
                    {
                        return folder;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download folder token unavailable: {ex.Message}");
                }
            }

            try
            {
                var path = SettingsManager.Instance.DownloadFolderPath;
                return await StorageFolder.GetFolderFromPathAsync(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download folder unavailable: {ex.Message}");
                return null;
            }
        }

        internal static async Task<StorageFile?> TryGetDownloadStorageFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            var token = SettingsManager.Instance.DownloadFolderToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                    var folderPath = folder.Path.TrimEnd('\\');
                    if (filePath.StartsWith(folderPath + "\\", StringComparison.OrdinalIgnoreCase)
                        && await folder.TryGetItemAsync(Path.GetFileName(filePath)) is StorageFile tokenFile)
                    {
                        return tokenFile;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download file token lookup failed: {ex.Message}");
                }
            }

            try
            {
                return await StorageFile.GetFileFromPathAsync(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download file path lookup failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<StorageFolder?> TryGetDownloadFolderForFileAsync(string filePath)
        {
            var token = SettingsManager.Instance.DownloadFolderToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                try
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                    var folderPath = folder.Path.TrimEnd('\\');
                    if (filePath.StartsWith(folderPath + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return folder;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download folder token lookup failed: {ex.Message}");
                }
            }

            try
            {
                var folderPath = Path.GetDirectoryName(filePath);
                return string.IsNullOrWhiteSpace(folderPath)
                    ? null
                    : await StorageFolder.GetFolderFromPathAsync(folderPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download folder path lookup failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<bool> CanAccessDownloadFolderAsync()
        {
            try
            {
                var folder = await TryGetDownloadFolderAsync();
                if (folder == null)
                {
                    return false;
                }

                var file = await folder.CreateFileAsync(
                    ".winuionweb-access-check.tmp",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, "ok");
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download folder access check failed: {ex.Message}");
                return false;
            }
        }

        private async Task ShowDownloadFolderAccessDialogAsync()
        {
            if (_isDownloadAccessDialogOpen)
            {
                return;
            }

            try
            {
                _isDownloadAccessDialogOpen = true;
                var dialog = new Dialogs.DownloadFolderAccessDialog();
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-broadfilesystemaccess"));
                }
            }
            finally
            {
                _isDownloadAccessDialogOpen = false;
            }
        }

        private void PinDownloadsPanelButton_Click(object sender, RoutedEventArgs e)
        {
            SetDownloadsButtonPinned(!_isDownloadsButtonPinned);
        }

        private void PinDownloadsToTitleBarMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetDownloadsButtonPinned(true);
        }

        private void UnpinDownloadsFromTitleBarMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetDownloadsButtonPinned(false);
        }

        private async void OpenDownloadItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is DownloadItemViewModel item)
            {
                await TryLaunchDownloadFileAsync(item);
            }
        }

        private void CancelDownloadItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is DownloadItemViewModel item)
            {
                item.Cancel();
            }
        }

        private void ToggleDownloadItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is DownloadItemViewModel item)
            {
                item.TogglePauseResume();
            }
        }

        private void OpenDownloadItemMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenDownloadItemButton_Click(sender, e);
        }

        private async void ShowDownloadInFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is DownloadItemViewModel item)
            {
                await TryLaunchDownloadFolderAsync(item);
            }
        }

        private async void ShowDownloadInFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is DownloadItemViewModel item)
            {
                await TryLaunchDownloadFolderAsync(item);
            }
        }

        private async void DeleteDownloadFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is DownloadItemViewModel item)
            {
                await TryDeleteDownloadFileAsync(item);
                UpdateDownloadTitleBarStatus();
            }
        }

        private void CopyDownloadLinkMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is DownloadItemViewModel item
                && !string.IsNullOrWhiteSpace(item.SourceUri))
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(item.SourceUri);
                Clipboard.SetContent(dataPackage);
            }
        }

        private async void ReportUnsafeDownloadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element
                && element.Tag is DownloadItemViewModel item
                && !string.IsNullOrWhiteSpace(item.SourceUri))
            {
                var builder = new UriBuilder("https://feedback.smartscreen.microsoft.com/feedback.aspx")
                {
                    Query = string.Join("&", new[]
                    {
                        "url=" + Uri.EscapeDataString(item.SourceUri),
                        "filename=" + Uri.EscapeDataString(item.FileName),
                        "product=" + Uri.EscapeDataString("WinUIonWebUWP")
                    })
                };
                await Launcher.LaunchUriAsync(builder.Uri);
            }
        }

        private static async Task<bool> TryLaunchDownloadFileAsync(DownloadItemViewModel item)
        {
            try
            {
                await item.RefreshFileAvailabilityAsync();
                if (string.IsNullOrWhiteSpace(item.FilePath))
                {
                    return false;
                }

                var file = await TryGetDownloadStorageFileAsync(item.FilePath);
                if (file == null)
                {
                    item.MarkFileDeleted();
                    return false;
                }

                var launched = await Launcher.LaunchFileAsync(file);
                if (!launched)
                {
                    await TryLaunchDownloadFolderAsync(item);
                }

                return launched;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Open file failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> TryLaunchDownloadFolderAsync(DownloadItemViewModel item)
        {
            try
            {
                await item.RefreshFileAvailabilityAsync();
                var folder = await TryGetDownloadFolderForFileAsync(item.FilePath);
                if (folder == null)
                {
                    return false;
                }

                var file = await TryGetDownloadStorageFileAsync(item.FilePath);
                if (file != null)
                {
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    return await Launcher.LaunchFolderAsync(folder, options);
                }

                var launched = await Launcher.LaunchFolderAsync(folder);
                return launched;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Show in folder failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> TryDeleteDownloadFileAsync(DownloadItemViewModel item)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(item.FilePath))
                {
                    return false;
                }

                var file = await TryGetDownloadStorageFileAsync(item.FilePath);
                if (file != null)
                {
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                else
                {
                    File.Delete(item.FilePath);
                }

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (!await IsDownloadFilePresentAsync(item.FilePath))
                    {
                        item.MarkFileDeleted();
                        return true;
                    }

                    await Task.Delay(100);
                }

                System.Diagnostics.Debug.WriteLine("[WinUIonWeb Download] Delete file reported success, but file still exists.");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Delete file failed: {ex.Message}");
                try
                {
                    File.Delete(item.FilePath);
                    if (!await IsDownloadFilePresentAsync(item.FilePath))
                    {
                        item.MarkFileDeleted();
                        return true;
                    }
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Delete file fallback failed: {fallbackEx.Message}");
                }
                return false;
            }
        }

        internal static async Task<bool> IsDownloadFilePresentAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            if (await TryGetDownloadStorageFileAsync(filePath) != null)
            {
                return true;
            }

            try
            {
                return File.Exists(filePath);
            }
            catch
            {
                return false;
            }
        }

        private void PauseDownloadItemMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is DownloadItemViewModel item)
            {
                item.Pause();
            }
        }

        private void ResumeDownloadItemMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is DownloadItemViewModel item)
            {
                item.Resume();
            }
        }

        private void CancelDownloadItemMenuItem_Click(object sender, RoutedEventArgs e)
        {
            CancelDownloadItemButton_Click(sender, e);
        }

        private void RemoveDownloadItemMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is DownloadItemViewModel item)
            {
                _downloadItems.Remove(item);
                SettingsManager.Instance.RemoveDownloadHistory(item.Id);
                UpdateDownloadsEmptyState();
                UpdateDownloadTitleBarStatus();
            }
        }

        private void DownloadsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is not ListViewItem container)
            {
                return;
            }

            container.PointerEntered -= DownloadListViewItem_PointerEntered;
            container.PointerExited -= DownloadListViewItem_PointerExited;
            container.ContextRequested -= DownloadListViewItem_ContextRequested;

            if (args.InRecycleQueue)
            {
                if (ReferenceEquals(container.Tag, _hoveredDownloadItem))
                {
                    SetHoveredDownloadItem(null);
                }

                container.Tag = null;
                return;
            }

            container.Tag = args.Item;
            container.PointerEntered += DownloadListViewItem_PointerEntered;
            container.PointerExited += DownloadListViewItem_PointerExited;
            container.ContextRequested += DownloadListViewItem_ContextRequested;
        }

        private void DownloadListViewItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (GetDownloadItemFromContainer(sender) is not DownloadItemViewModel item)
            {
                return;
            }

            SetHoveredDownloadItem(item);
            _ = item.RefreshFileAvailabilityAsync();
        }

        private void DownloadListViewItem_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (GetDownloadItemFromContainer(sender) is DownloadItemViewModel item
                && ReferenceEquals(item, _hoveredDownloadItem))
            {
                SetHoveredDownloadItem(null);
            }
        }

        private async void DownloadListViewItem_ContextRequested(object sender, ContextRequestedEventArgs args)
        {
            if (sender is not ListViewItem container
                || GetDownloadItemFromContainer(container) is not DownloadItemViewModel item)
            {
                return;
            }

            args.Handled = true;
            SetHoveredDownloadItem(item);
            await item.RefreshFileAvailabilityAsync();

            if (!args.TryGetPosition(container, out var position))
            {
                position = new Windows.Foundation.Point(
                    Math.Max(0, container.ActualWidth - 12),
                    Math.Max(0, container.ActualHeight / 2));
            }

            CreateDownloadItemMenuFlyout(item).ShowAt(container, position);
        }

        private void SetHoveredDownloadItem(DownloadItemViewModel? item)
        {
            if (ReferenceEquals(item, _hoveredDownloadItem))
            {
                return;
            }

            _hoveredDownloadItem?.SetPointerOver(false);
            _hoveredDownloadItem = item;
            _hoveredDownloadItem?.SetPointerOver(true);
        }

        private static DownloadItemViewModel? GetDownloadItemFromContainer(object? source)
        {
            if (source is FrameworkElement element)
            {
                if (element.Tag is DownloadItemViewModel taggedItem)
                {
                    return taggedItem;
                }

                if (element.DataContext is DownloadItemViewModel dataItem)
                {
                    return dataItem;
                }
            }

            return null;
        }

        private async Task RefreshDownloadFileAvailabilityAsync()
        {
            foreach (var item in _downloadItems.ToList())
            {
                await item.RefreshFileAvailabilityAsync();
            }
        }

        private MenuFlyout CreateDownloadItemMenuFlyout(DownloadItemViewModel item)
        {
            var flyout = new MenuFlyout();
            flyout.Items.Add(CreateDownloadMenuItem("OpenDownloadItemMenuItem/Text", "\uE8E5", item.OpenVisibility, item, OpenDownloadItemMenuItem_Click));
            flyout.Items.Add(CreateDownloadMenuItem("ShowDownloadInFolderMenuItem/Text", "\uE838", item.FileActionVisibility, item, ShowDownloadInFolderMenuItem_Click));
            flyout.Items.Add(CreateDownloadMenuItem("PauseDownloadItemMenuItem/Text", "\uE769", item.PauseVisibility, item, PauseDownloadItemMenuItem_Click));
            flyout.Items.Add(CreateDownloadMenuItem("ResumeDownloadItemMenuItem/Text", "\uE768", item.ResumeVisibility, item, ResumeDownloadItemMenuItem_Click));
            flyout.Items.Add(CreateDownloadMenuItem("CancelDownloadItemMenuItem/Text", "\uE711", item.CancelVisibility, item, CancelDownloadItemMenuItem_Click));
            if (!item.IsFileDeleted)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }
            flyout.Items.Add(CreateDownloadMenuItem("CopyDownloadLinkMenuItem/Text", "\uE71B", item.SourceActionVisibility, item, CopyDownloadLinkMenuItem_Click));
            flyout.Items.Add(CreateDownloadMenuItem("ReportUnsafeDownloadMenuItem/Text", "\uE7BA", item.SourceActionVisibility, item, ReportUnsafeDownloadMenuItem_Click));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateDownloadMenuItem("DeleteDownloadFileMenuItem/Text", "\uE74D", item.FileActionVisibility, item, DeleteDownloadFileButton_Click));
            flyout.Items.Add(CreateDownloadMenuItem("RemoveDownloadItemMenuItem/Text", "\uE711", Visibility.Visible, item, RemoveDownloadItemMenuItem_Click));
            return flyout;
        }

        private MenuFlyoutItem CreateDownloadMenuItem(
            string textKey,
            string glyph,
            Visibility visibility,
            DownloadItemViewModel item,
            RoutedEventHandler clickHandler)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = GetResourceOrFallback(textKey),
                Visibility = visibility,
                Tag = item,
                Icon = new FontIcon { Glyph = glyph }
            };
            menuItem.Click += clickHandler;
            return menuItem;
        }

        private void DownloadProgressBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Microsoft.UI.Xaml.Controls.ProgressBar progressBar
                || progressBar.DataContext is not DownloadItemViewModel item)
            {
                return;
            }

            progressBar.Value = item.Progress;
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName != nameof(DownloadItemViewModel.Progress))
                {
                    return;
                }

                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    progressBar.Value = item.Progress;
                });
            };

            item.PropertyChanged += handler;
            progressBar.Tag = new DownloadProgressBarSubscription(item, handler);
        }

        private void DownloadProgressBar_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.ProgressBar progressBar
                && progressBar.Tag is DownloadProgressBarSubscription subscription)
            {
                subscription.Item.PropertyChanged -= subscription.Handler;
                progressBar.Tag = null;
            }
        }

        private sealed class DownloadProgressBarSubscription
        {
            public DownloadProgressBarSubscription(DownloadItemViewModel item, PropertyChangedEventHandler handler)
            {
                Item = item;
                Handler = handler;
            }

            public DownloadItemViewModel Item { get; }
            public PropertyChangedEventHandler Handler { get; }
        }

        private void ShowDownloadsFlyout()
        {
            if (DownloadTitleBarButton.Visibility != Visibility.Visible)
            {
                ShowTransientDownloadsButton();
            }

            if (!_isDownloadsFlyoutOpen)
            {
                DownloadsFlyout.ShowAt(DownloadTitleBarButton);
            }
        }

        private void SetDownloadsButtonPinned(bool isPinned)
        {
            _isDownloadsButtonPinned = isPinned;
            SettingsManager.Instance.IsDownloadsButtonPinned = isPinned;
            UpdateDownloadsPinMenuItems();
            UpdateDownloadTitleBarButton(true);
        }

        private void ShowTransientDownloadsButton()
        {
            _isDownloadsButtonTransient = true;
            UpdateDownloadTitleBarButton(true);
        }

        private void HideTransientDownloadsButton()
        {
            _isDownloadsButtonTransient = false;
            UpdateDownloadTitleBarButton(true);
        }

        private void UpdateDownloadTitleBarButton(bool animate)
        {
            if (_isSettingsHostOpen)
            {
                DownloadTitleBarButton.Visibility = Visibility.Collapsed;
                UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
                return;
            }

            var shouldShow = _isDownloadsButtonPinned
                || _isDownloadsFlyoutOpen
                || _isDownloadsContextFlyoutOpen
                || _isDownloadsButtonTransient
                || HasLiveDownloadStatusIndicator();
            if (shouldShow)
            {
                if (DownloadTitleBarButton.Visibility != Visibility.Visible)
                {
                    DownloadTitleBarButton.Visibility = Visibility.Visible;
                    UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
                    AnimateDownloadTitleBarButton(1, animate, null);
                }
                else
                {
                    UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
                    if (DownloadTitleBarButton.Opacity < 1)
                    {
                        AnimateDownloadTitleBarButton(1, animate, null);
                    }
                }
                return;
            }

            if (DownloadTitleBarButton.Visibility != Visibility.Visible)
            {
                UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
                return;
            }

            AnimateDownloadTitleBarButton(0, animate, () =>
            {
                if (!_isDownloadsButtonPinned
                    && !_isDownloadsFlyoutOpen
                    && !_isDownloadsContextFlyoutOpen
                    && !_isDownloadsButtonTransient)
                {
                    DownloadTitleBarButton.Visibility = Visibility.Collapsed;
                    UpdateTitleBarButtonInset(CoreApplication.GetCurrentView().TitleBar);
                }
            });
        }

        private void AnimateDownloadTitleBarButton(double targetOpacity, bool animate, Action? completed)
        {
            if (!animate)
            {
                DownloadTitleBarButton.Opacity = targetOpacity;
                completed?.Invoke();
                return;
            }

            var animation = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = TimeSpan.FromMilliseconds(targetOpacity <= 0 ? 80 : 140),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, DownloadTitleBarButton);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            if (completed != null)
            {
                storyboard.Completed += (_, __) => completed();
            }
            storyboard.Begin();
        }

        private void UpdateDownloadsEmptyState()
        {
            DownloadsEmptyText.Visibility = _downloadItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadsListView.Visibility = _downloadItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void InitializeDownloadTitleBarStatus()
        {
            if (Application.Current.Resources.ContainsKey("SystemFillColorSuccessBrush")
                && Application.Current.Resources["SystemFillColorSuccessBrush"] is Brush successBrush)
            {
                DownloadTitleBarCompleteBadge.Foreground = successBrush;
                return;
            }

            DownloadTitleBarCompleteBadge.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16));
        }

        private void UpdateDownloadTitleBarStatus()
        {
            var activeItems = _downloadItems.Where(item => item.IsLiveInProgressDownload).ToList();
            if (activeItems.Count > 0)
            {
                var progress = CalculateAggregateDownloadProgress(activeItems);
                DownloadTitleBarProgressRing.Value = progress;
                DownloadTitleBarProgressRing.IsActive = true;
                DownloadTitleBarProgressRing.Visibility = Visibility.Visible;
                DownloadTitleBarCompleteBadge.Visibility = Visibility.Collapsed;
                ToolTipService.SetToolTip(
                    DownloadTitleBarButton,
                    string.Format(GetResourceOrFallback("DownloadTitleBarProgressToolTipFormat"), Math.Round(progress)));
                UpdateDownloadTitleBarButton(true);
                return;
            }

            DownloadTitleBarProgressRing.IsActive = false;
            DownloadTitleBarProgressRing.Visibility = Visibility.Collapsed;
            DownloadTitleBarCompleteBadge.Visibility = !_isDownloadCompletionAcknowledged
                && _downloadItems.Any(item => item.IsLiveCompletedDownload)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ToolTipService.SetToolTip(
                DownloadTitleBarButton,
                GetResourceOrFallback("DownloadTitleBarButton/ToolTipService/ToolTip"));
            UpdateDownloadTitleBarButton(true);
        }

        private static double CalculateAggregateDownloadProgress(IReadOnlyList<DownloadItemViewModel> items)
        {
            if (items.All(item => item.TotalBytesToReceive > 0))
            {
                var received = items.Sum(item => Math.Max(0, item.BytesReceived));
                var total = items.Sum(item => Math.Max(0, item.TotalBytesToReceive));
                if (total > 0)
                {
                    return Math.Max(0, Math.Min(100, received * 100.0 / total));
                }
            }

            return Math.Max(0, Math.Min(100, items.Average(item => item.Progress)));
        }

        private bool HasLiveDownloadStatusIndicator()
        {
            return _downloadItems.Any(item => item.IsLiveInProgressDownload)
                || (!_isDownloadCompletionAcknowledged && _downloadItems.Any(item => item.IsLiveCompletedDownload));
        }

        private void UpdateDownloadsPinMenuItems()
        {
            PinDownloadsToTitleBarMenuItem.Visibility = _isDownloadsButtonPinned ? Visibility.Collapsed : Visibility.Visible;
            UnpinDownloadsFromTitleBarMenuItem.Visibility = _isDownloadsButtonPinned ? Visibility.Visible : Visibility.Collapsed;
            PinDownloadsPanelPinnedGlyph.Visibility = _isDownloadsButtonPinned ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(
                PinDownloadsPanelButton,
                GetResourceOrFallback(_isDownloadsButtonPinned
                    ? "UnpinDownloadsPanelButtonToolTip"
                    : "PinDownloadsPanelButtonToolTip"));
        }

        private string GetResourceOrFallback(string key)
        {
            var value = _loader.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }

        private static string SanitizeFileName(string? fileName, string fallbackFileName)
        {
            var value = string.IsNullOrWhiteSpace(fileName)
                ? fallbackFileName
                : Path.GetFileName(fileName.Trim());
            if (string.IsNullOrWhiteSpace(value))
            {
                value = fallbackFileName;
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Length > 180 ? value.Substring(0, 180) : value;
        }

        private string GetTileDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(_hostedManifestName))
            {
                return _hostedManifestName;
            }

            return SettingsManager.Instance.GetContainerManifestName(_containerId);
        }

        private static IReadOnlyList<string> GetPinnedTileNames(string? excludedContainerId)
        {
            return SettingsManager.Instance.Containers
                .Where(item => item.Id != excludedContainerId && SecondaryTile.Exists(item.Id))
                .Select(item => item.DisplayName?.Trim() ?? "")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        public async Task RefreshCurrentContainerIconAsync()
        {
            var icons = await PrepareHostedTileIconsAsync();
            if (!string.IsNullOrWhiteSpace(icons.Square150Path))
            {
                SettingsManager.Instance.UpdateContainerIcon(_containerId, icons.Square150Path);
                ImgAppIcon.Source = new BitmapImage(SettingsManager.Instance.GetContainerIconUri(_containerId));
            }
        }

        private async Task<TileIconSet> PrepareHostedTileIconsAsync()
        {
            var iconCandidates = _hostedDocumentIconCandidates;
            if (iconCandidates.Count == 0)
            {
                return TileIconSet.Empty;
            }

            var url = !string.IsNullOrWhiteSpace(_pinCandidateUrl)
                ? _pinCandidateUrl
                : GetContainerPage()?.CurrentUrl ?? ContainerHomeUrl;

            if (_preparedTileIconUri != null
                && iconCandidates.Any(item => item.AbsoluteUri == _preparedTileIconUri.AbsoluteUri)
                && !_preparedTileIcons.IsEmpty)
            {
                return _preparedTileIcons;
            }

            foreach (var iconUri in iconCandidates)
            {
                var key = CreateTileIconCacheKey(url, iconUri.AbsoluteUri);
                if (_preparedTileIconUri != null
                    && _preparedTileIconUri.AbsoluteUri == iconUri.AbsoluteUri
                    && _preparedTileIconKey == key
                    && !_preparedTileIcons.IsEmpty)
                {
                    return _preparedTileIcons;
                }

                var resolvedIconUri = ResolveLocalViteIconUri(url, iconUri);
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Preparing tile icon: {resolvedIconUri}");
                var icons = await CacheTileIconsAsync(key, resolvedIconUri);
                if (icons.IsEmpty)
                {
                    continue;
                }

                if (_hostedDocumentIconCandidates.Any(item => item.AbsoluteUri == iconUri.AbsoluteUri))
                {
                    _preparedTileIconUri = iconUri;
                    _preparedTileIconKey = key;
                    _preparedTileIcons = icons;
                }

                return icons;
            }

            return TileIconSet.Empty;
        }

        private static async Task<TileIconSet> CacheTileIconsAsync(string cacheKey, Uri iconUri)
        {
            try
            {
                var bytes = await ReadIconBytesAsync(iconUri);
                if (bytes.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Empty icon bytes: {iconUri}");
                    return TileIconSet.Empty;
                }

                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "container-icons",
                    CreationCollisionOption.OpenIfExists);

                var filePrefix = cacheKey + "-" + Guid.NewGuid().ToString("N");
                var square150File = await folder.CreateFileAsync(
                    filePrefix + ".square150.png",
                    CreationCollisionOption.FailIfExists);
                var square70File = await folder.CreateFileAsync(
                    filePrefix + ".square70.png",
                    CreationCollisionOption.FailIfExists);
                var square71File = await folder.CreateFileAsync(
                    filePrefix + ".square71.png",
                    CreationCollisionOption.FailIfExists);
                var square44File = await folder.CreateFileAsync(
                    filePrefix + ".square44.png",
                    CreationCollisionOption.FailIfExists);
                var square30File = await folder.CreateFileAsync(
                    filePrefix + ".square30.png",
                    CreationCollisionOption.FailIfExists);

                if (!await TryConvertImageToPngAsync(bytes, square150File, 150))
                {
                    return TileIconSet.Empty;
                }

                var square70Path = await TryConvertImageToPngAsync(bytes, square70File, 70)
                    ? "container-icons/" + square70File.Name
                    : "";
                var square71Path = await TryConvertImageToPngAsync(bytes, square71File, 71)
                    ? "container-icons/" + square71File.Name
                    : "";
                var square44Path = await TryConvertImageToPngAsync(bytes, square44File, 44)
                    ? "container-icons/" + square44File.Name
                    : "";
                var square30Path = await TryConvertImageToPngAsync(bytes, square30File, 30)
                    ? "container-icons/" + square30File.Name
                    : "";

                var icons = new TileIconSet(
                    "container-icons/" + square150File.Name,
                    square70Path,
                    square71Path,
                    square44Path,
                    square30Path);
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Cached tile icons: 150={icons.Square150Path}, 70={icons.Square70Path}, 71={icons.Square71Path}, 44={icons.Square44Path}, 30={icons.Square30Path}");
                return icons;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Tile] Pin container icon failed for {iconUri}: {ex.Message}");
                return TileIconSet.Empty;
            }
        }

        private static async Task<byte[]> ReadIconBytesAsync(Uri iconUri)
        {
            if (iconUri.Scheme == Uri.UriSchemeHttp || iconUri.Scheme == Uri.UriSchemeHttps)
            {
                return await HttpClient.GetByteArrayAsync(iconUri);
            }

            if (iconUri.Scheme == Uri.UriSchemeFile)
            {
                return await File.ReadAllBytesAsync(iconUri.LocalPath);
            }

            if (iconUri.Scheme == "ms-appx")
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(iconUri);
                var buffer = await FileIO.ReadBufferAsync(file);
                return buffer.ToArray();
            }

            return Array.Empty<byte>();
        }

        private static Uri ResolveLocalViteIconUri(string pageUrl, Uri iconUri)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)
                || !pageUri.IsFile
                || !iconUri.IsFile
                || !Path.GetFileName(iconUri.LocalPath).Equals("favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                return iconUri;
            }

            var indexDirectory = Path.GetDirectoryName(pageUri.LocalPath);
            if (string.IsNullOrWhiteSpace(indexDirectory))
            {
                return iconUri;
            }

            var iconDirectory = Path.GetDirectoryName(iconUri.LocalPath);
            var iconIsAtDriveRoot = string.Equals(
                iconDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetPathRoot(iconUri.LocalPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            if (!iconIsAtDriveRoot && File.Exists(iconUri.LocalPath))
            {
                return iconUri;
            }

            var publicIconPath = Path.Combine(indexDirectory, "public", "favicon.ico");
            if (File.Exists(publicIconPath))
            {
                return new Uri(publicIconPath);
            }

            var siblingIconPath = Path.Combine(indexDirectory, "favicon.ico");
            return File.Exists(siblingIconPath) ? new Uri(siblingIconPath) : iconUri;
        }

        private static async Task<bool> TryConvertImageToPngAsync(byte[] bytes, StorageFile file, uint targetSize)
        {
            try
            {
                using var input = new InMemoryRandomAccessStream();
                await input.WriteAsync(bytes.AsBuffer());
                input.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(input);
                var frame = await GetBestBitmapFrameAsync(decoder);
                var pixelWidth = frame.PixelWidth;
                var pixelHeight = frame.PixelHeight;
                if (pixelWidth <= 0 || pixelHeight <= 0)
                {
                    return false;
                }

                var maxSide = Math.Max(pixelWidth, pixelHeight);
                var scale = targetSize / (double)maxSide;
                var scaledWidth = (uint)Math.Max(1, Math.Round(pixelWidth * scale));
                var scaledHeight = (uint)Math.Max(1, Math.Round(pixelHeight * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = scaledWidth,
                    ScaledHeight = scaledHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                var softwareBitmap = await frame.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var sourcePixels = new byte[scaledWidth * scaledHeight * 4];
                softwareBitmap.CopyToBuffer(sourcePixels.AsBuffer());

                var targetPixels = new byte[targetSize * targetSize * 4];
                var xOffset = (int)((targetSize - scaledWidth) / 2);
                var yOffset = (int)((targetSize - scaledHeight) / 2);
                var sourceStride = (int)scaledWidth * 4;
                var targetStride = (int)targetSize * 4;

                for (var y = 0; y < scaledHeight; y++)
                {
                    System.Buffer.BlockCopy(
                        sourcePixels,
                        (int)y * sourceStride,
                        targetPixels,
                        (yOffset + (int)y) * targetStride + xOffset * 4,
                        sourceStride);
                }

                using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
                output.Size = 0;
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    targetSize,
                    targetSize,
                    96,
                    96,
                    targetPixels);
                await encoder.FlushAsync();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Convert tile icon to PNG failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<BitmapFrame> GetBestBitmapFrameAsync(BitmapDecoder decoder)
        {
            var frameCount = Math.Max(1u, decoder.FrameCount);
            var bestFrameIndex = 0u;
            var bestFramePixels = 0ul;

            for (var index = 0u; index < frameCount; index++)
            {
                var frame = await decoder.GetFrameAsync(index);
                var pixels = (ulong)frame.PixelWidth * frame.PixelHeight;
                if (pixels > bestFramePixels)
                {
                    bestFramePixels = pixels;
                    bestFrameIndex = index;
                }
            }

            return await decoder.GetFrameAsync(bestFrameIndex);
        }

        private static void ApplyTileVisualElements(SecondaryTile tile, TileIconSet icons)
        {
            if (icons.IsEmpty)
            {
                tile.VisualElements.Square150x150Logo = GetDefaultTileLogoUri();
                tile.VisualElements.Square44x44Logo = GetDefaultSmallTileLogoUri();
                return;
            }

            tile.VisualElements.Square150x150Logo = CreateLocalUri(icons.Square150Path);
            if (!string.IsNullOrWhiteSpace(icons.Square70Path))
            {
                tile.VisualElements.Square70x70Logo = CreateLocalUri(icons.Square70Path);
            }
            if (!string.IsNullOrWhiteSpace(icons.Square71Path))
            {
                tile.VisualElements.Square71x71Logo = CreateLocalUri(icons.Square71Path);
            }
            if (!string.IsNullOrWhiteSpace(icons.Square44Path))
            {
                tile.VisualElements.Square44x44Logo = CreateLocalUri(icons.Square44Path);
            }
            if (!string.IsNullOrWhiteSpace(icons.Square30Path))
            {
                tile.VisualElements.Square30x30Logo = CreateLocalUri(icons.Square30Path);
            }
        }

        private static string CreateTileIconCacheKey(string url, string iconUri)
        {
            var input = $"{url}|{iconUri}";
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return "tile-" + BitConverter.ToString(bytes).Replace("-", "").Substring(0, 20).ToLowerInvariant();
        }

        private static SecondaryTile CreateSecondaryTile(string containerId, string displayName, Uri logoUri)
        {
            return new SecondaryTile(
                containerId,
                displayName,
                $"containerId={Uri.EscapeDataString(containerId)}",
                logoUri,
                TileSize.Default);
        }

        private static Uri GetDefaultTileLogoUri()
        {
            return new Uri("ms-appx:///Assets/Square150x150Logo.png");
        }

        private static Uri GetDefaultSmallTileLogoUri()
        {
            return new Uri("ms-appx:///Assets/Square44x44Logo.png");
        }

        private static Uri CreateLocalUri(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            return new Uri("ms-appdata:///local/" + normalized);
        }

    }

    public sealed class DownloadItemViewModel : INotifyPropertyChanged
        {
            private readonly CoreWebView2DownloadOperation? _operation;
            private readonly ResourceLoader _loader;
            private readonly DownloadHistoryEntry _history;
            private readonly CoreDispatcher? _operationDispatcher;
            private long _lastBytes;
            private DateTimeOffset _lastRefreshTime = DateTimeOffset.UtcNow;
            private DateTimeOffset _lastPersistTime = DateTimeOffset.MinValue;
            private long _currentSpeed;
            private bool _showOpenAction;
            private bool _isPaused;
            private double _pausedProgress;
            private bool _fileMissing;
            private bool _isPointerOver;
            private bool _isLoadingFileIcon;
            private bool _hasLoadedFileIcon;

            public DownloadItemViewModel(CoreWebView2DownloadOperation operation, ResourceLoader loader)
            {
                _operation = operation;
                _loader = loader;
                _operationDispatcher = CoreWindow.GetForCurrentThread().Dispatcher;
                _history = new DownloadHistoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FilePath = operation.ResultFilePath ?? "",
                    SourceUri = operation.Uri ?? "",
                    State = operation.State.ToString(),
                    BytesReceived = operation.BytesReceived,
                    TotalBytesToReceive = operation.TotalBytesToReceive,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _lastBytes = operation.BytesReceived;
                Refresh();
            }

            public DownloadItemViewModel(DownloadHistoryEntry history, ResourceLoader loader)
            {
                _operation = null;
                _loader = loader;
                _operationDispatcher = null;
                _history = history;
                if (string.IsNullOrWhiteSpace(_history.Id))
                {
                    _history.Id = Guid.NewGuid().ToString("N");
                }

                _lastPersistTime = DateTimeOffset.UtcNow;
                if (string.Equals(_history.State, nameof(CoreWebView2DownloadState.InProgress), StringComparison.OrdinalIgnoreCase))
                {
                    _history.State = nameof(CoreWebView2DownloadState.Interrupted);
                    _history.UpdatedAt = DateTimeOffset.UtcNow;
                    PersistDownloadHistory(true);
                }

                _lastBytes = history.BytesReceived;
                _showOpenAction = string.Equals(_history.State, nameof(CoreWebView2DownloadState.Completed), StringComparison.OrdinalIgnoreCase);
                _fileMissing = string.Equals(_history.State, "Deleted", StringComparison.OrdinalIgnoreCase);
                Refresh();
                if (_showOpenAction)
                {
                    _ = LoadFileIconAsync();
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Id => _history.Id;
            public string FilePath => _operation?.ResultFilePath ?? _history.FilePath;
            public string FileName => string.IsNullOrWhiteSpace(FilePath)
                ? GetString("DownloadItemUnknownFile")
                : Path.GetFileName(FilePath);
            public double Progress { get; private set; }
            public string StatusText { get; private set; } = "";
            public Visibility ProgressVisibility { get; private set; } = Visibility.Visible;
            public Visibility CancelVisibility { get; private set; } = Visibility.Visible;
            public Visibility ToggleVisibility { get; private set; } = Visibility.Visible;
            public Visibility OpenVisibility { get; private set; } = Visibility.Collapsed;
            public Visibility FileActionVisibility { get; private set; } = Visibility.Collapsed;
            public Visibility SourceActionVisibility { get; private set; } = Visibility.Collapsed;
            public Visibility StatusTextVisibility { get; private set; } = Visibility.Visible;
            public Visibility HoverActionsVisibility { get; private set; } = Visibility.Collapsed;
            public Visibility PauseVisibility { get; private set; } = Visibility.Visible;
            public Visibility ResumeVisibility { get; private set; } = Visibility.Collapsed;
            public ImageSource FileIconSource { get; private set; } = new BitmapImage();
            public Visibility FileIconVisibility { get; private set; } = Visibility.Collapsed;
            public Visibility DefaultIconVisibility { get; private set; } = Visibility.Visible;
            public string ToggleGlyph { get; private set; } = "\uE769";
            public CoreWebView2DownloadState State => _operation?.State ?? GetHistoryState();
            public DownloadItemViewModel Item => this;
            public string SourceUri => _operation?.Uri ?? _history.SourceUri;
            public bool IsFileDeleted => _fileMissing || string.Equals(_history.State, "Deleted", StringComparison.OrdinalIgnoreCase);
            public bool IsLiveInProgressDownload => _operation != null && State == CoreWebView2DownloadState.InProgress;
            public bool IsLiveCompletedDownload => _operation != null && State == CoreWebView2DownloadState.Completed && !IsFileDeleted;
            public long BytesReceived => _operation?.BytesReceived ?? _history.BytesReceived;
            public long TotalBytesToReceive => _operation?.TotalBytesToReceive ?? _history.TotalBytesToReceive;

            public void Cancel()
            {
                _ = CancelAsync();
            }

            public void Pause()
            {
                _ = PauseAsync();
            }

            public void Resume()
            {
                _ = ResumeAsync();
            }

            private async Task CancelAsync()
            {
                if (_operation == null)
                {
                    return;
                }

                try
                {
                    await RunOnOperationDispatcherAsync(() =>
                    {
                        _operation.Cancel();
                        _isPaused = false;
                        Refresh();
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Cancel failed: {ex.Message}");
                }
            }

            private async Task PauseAsync()
            {
                if (_operation == null)
                {
                    return;
                }

                try
                {
                    await RunOnOperationDispatcherAsync(() =>
                    {
                        if (_operation.State == CoreWebView2DownloadState.InProgress)
                        {
                            _operation.Pause();
                        }

                        _pausedProgress = Progress;
                        _isPaused = true;
                        Refresh();
                    });

                    await Task.Delay(120);
                    await RunOnOperationDispatcherAsync(() =>
                    {
                        if (_operation.State == CoreWebView2DownloadState.InProgress)
                        {
                            _operation.Pause();
                        }

                        System.Diagnostics.Debug.WriteLine(
                            $"[WinUIonWeb Download] Pause requested. State={_operation.State}, Reason={_operation.InterruptReason}, CanResume={_operation.CanResume}");
                        Refresh();
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Pause failed: {ex.Message}");
                }
            }

            private async Task ResumeAsync()
            {
                if (_operation == null)
                {
                    return;
                }

                try
                {
                    await RunOnOperationDispatcherAsync(() =>
                    {
                        if (_operation.CanResume)
                        {
                            _operation.Resume();
                        }

                        _isPaused = false;
                        Refresh();
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Resume failed: {ex.Message}");
                }
            }

            private async Task RunOnOperationDispatcherAsync(DispatchedHandler action)
            {
                if (_operationDispatcher == null || _operationDispatcher.HasThreadAccess)
                {
                    action();
                    return;
                }

                await _operationDispatcher.RunAsync(CoreDispatcherPriority.Normal, action);
            }

            public void TogglePauseResume()
            {
                if (IsPausedState(State))
                {
                    Resume();
                    return;
                }

                Pause();
            }

            public void ShowOpenAction()
            {
                if (State == CoreWebView2DownloadState.Completed)
                {
                    _showOpenAction = true;
                    Refresh();
                    _ = LoadFileIconAsync();
                }
            }

            public void SetPointerOver(bool isPointerOver)
            {
                if (_isPointerOver == isPointerOver)
                {
                    return;
                }

                _isPointerOver = isPointerOver;
                Refresh();
            }

            public void MarkFileDeleted()
            {
                _fileMissing = true;
                _showOpenAction = false;
                _history.State = "Deleted";
                _history.UpdatedAt = DateTimeOffset.UtcNow;
                ProgressVisibility = Visibility.Collapsed;
                OpenVisibility = Visibility.Collapsed;
                FileActionVisibility = Visibility.Collapsed;
                HoverActionsVisibility = Visibility.Collapsed;
                FileIconSource = new BitmapImage();
                FileIconVisibility = Visibility.Collapsed;
                DefaultIconVisibility = Visibility.Visible;
                StatusTextVisibility = Visibility.Visible;
                StatusText = GetString("DownloadStatusDeleted");
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(OpenVisibility));
                OnPropertyChanged(nameof(FileActionVisibility));
                OnPropertyChanged(nameof(HoverActionsVisibility));
                OnPropertyChanged(nameof(FileIconSource));
                OnPropertyChanged(nameof(FileIconVisibility));
                OnPropertyChanged(nameof(DefaultIconVisibility));
                OnPropertyChanged(nameof(StatusTextVisibility));
                PersistDownloadHistory(true);
            }

            public async Task RefreshFileAvailabilityAsync()
            {
                if (string.IsNullOrWhiteSpace(FilePath)
                    || _fileMissing
                    || string.Equals(_history.State, "Deleted", StringComparison.OrdinalIgnoreCase)
                    || (_operation != null && State == CoreWebView2DownloadState.InProgress))
                {
                    return;
                }

                var fileExists = await MainPage.IsDownloadFilePresentAsync(FilePath);
                if (!fileExists)
                {
                    if (!_fileMissing && (State == CoreWebView2DownloadState.Completed || _showOpenAction))
                    {
                        MarkFileDeleted();
                    }

                    return;
                }
            }

            public void Refresh()
            {
                var total = _operation?.TotalBytesToReceive ?? _history.TotalBytesToReceive;
                var received = _operation?.BytesReceived ?? _history.BytesReceived;
                var now = DateTimeOffset.UtcNow;
                var speed = UpdateDownloadSpeed(received, now);

                var calculatedProgress = total > 0 ? Math.Max(0, Math.Min(100, received * 100.0 / total)) : 0;
                var state = State;
                if (state == CoreWebView2DownloadState.Completed)
                {
                    _isPaused = false;
                }
                var isPaused = IsPausedState(state);
                if (isPaused && _pausedProgress <= 0)
                {
                    _pausedProgress = calculatedProgress;
                }

                Progress = isPaused ? _pausedProgress : calculatedProgress;

                var isInProgress = _operation != null && state == CoreWebView2DownloadState.InProgress;
                var canResume = _operation != null
                    && isPaused
                    && (_operation.CanResume || state == CoreWebView2DownloadState.InProgress);
                var isCompleted = state == CoreWebView2DownloadState.Completed;
                if (_operation != null)
                {
                    _history.FilePath = FilePath;
                    _history.SourceUri = SourceUri;
                    _history.State = _fileMissing ? "Deleted" : state.ToString();
                    _history.BytesReceived = received;
                    _history.TotalBytesToReceive = total;
                    _history.UpdatedAt = DateTimeOffset.UtcNow;
                }

                ProgressVisibility = !_fileMissing && !isPaused && (isInProgress || (isCompleted && !_showOpenAction))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                CancelVisibility = _operation != null && (isInProgress || canResume)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ToggleVisibility = _operation != null && (isInProgress || canResume)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                OpenVisibility = isCompleted && _showOpenAction
                    && !_fileMissing
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                FileActionVisibility = isCompleted && !_fileMissing
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                HoverActionsVisibility = isCompleted && !_fileMissing && _isPointerOver
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                StatusTextVisibility = OpenVisibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                SourceActionVisibility = string.IsNullOrWhiteSpace(SourceUri)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                PauseVisibility = _operation != null && isInProgress && !isPaused
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ResumeVisibility = canResume
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ToggleGlyph = isPaused ? "\uE768" : "\uE769";
                StatusText = _fileMissing
                    ? GetString("DownloadStatusDeleted")
                    : CreateStatusText(received, total, speed, isPaused);

                OnPropertyChanged(nameof(FilePath));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(CancelVisibility));
                OnPropertyChanged(nameof(ToggleVisibility));
                OnPropertyChanged(nameof(OpenVisibility));
                OnPropertyChanged(nameof(FileActionVisibility));
                OnPropertyChanged(nameof(HoverActionsVisibility));
                OnPropertyChanged(nameof(StatusTextVisibility));
                OnPropertyChanged(nameof(SourceActionVisibility));
                OnPropertyChanged(nameof(PauseVisibility));
                OnPropertyChanged(nameof(ResumeVisibility));
                OnPropertyChanged(nameof(ToggleGlyph));
                PersistDownloadHistory(false);
            }

            private long UpdateDownloadSpeed(long received, DateTimeOffset now)
            {
                if (_operation == null)
                {
                    _currentSpeed = 0;
                    return 0;
                }

                if (received < _lastBytes)
                {
                    _lastBytes = received;
                    _lastRefreshTime = now;
                    _currentSpeed = 0;
                    return 0;
                }

                var elapsed = (now - _lastRefreshTime).TotalSeconds;
                var byteDelta = received - _lastBytes;
                if (byteDelta > 0 && elapsed >= 0.2)
                {
                    _currentSpeed = Math.Max(0, (long)(byteDelta / elapsed));
                    _lastBytes = received;
                    _lastRefreshTime = now;
                }
                else if (byteDelta == 0 && elapsed >= 2)
                {
                    _currentSpeed = 0;
                    _lastRefreshTime = now;
                }

                return _currentSpeed;
            }

            private bool IsPausedState(CoreWebView2DownloadState state)
            {
                if (_operation == null)
                {
                    return _isPaused;
                }

                if (state == CoreWebView2DownloadState.Completed)
                {
                    return false;
                }

                if (_isPaused)
                {
                    return true;
                }

                if (state != CoreWebView2DownloadState.Interrupted || !_operation.CanResume)
                {
                    return false;
                }

                var interruptReason = _operation.InterruptReason.ToString();
                return interruptReason.IndexOf("User", StringComparison.OrdinalIgnoreCase) >= 0
                    || interruptReason.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            public void PersistDownloadHistory(bool force)
            {
                if (!force && (DateTimeOffset.UtcNow - _lastPersistTime).TotalSeconds < 1)
                {
                    return;
                }

                _lastPersistTime = DateTimeOffset.UtcNow;
                SettingsManager.Instance.UpsertDownloadHistory(_history);
            }

            private async Task LoadFileIconAsync()
            {
                if (_fileMissing || _isLoadingFileIcon || _hasLoadedFileIcon || string.IsNullOrWhiteSpace(FilePath))
                {
                    return;
                }

                try
                {
                    _isLoadingFileIcon = true;
                    var file = await MainPage.TryGetDownloadStorageFileAsync(FilePath);
                    if (file == null)
                    {
                        return;
                    }

                    using (var thumbnail = await file.GetThumbnailAsync(
                        ThumbnailMode.SingleItem,
                        32,
                        ThumbnailOptions.UseCurrentScale))
                    {
                        if (thumbnail == null || thumbnail.Size == 0)
                        {
                            return;
                        }

                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(thumbnail);
                        FileIconSource = bitmap;
                        FileIconVisibility = Visibility.Visible;
                        DefaultIconVisibility = Visibility.Collapsed;
                        _hasLoadedFileIcon = true;
                        OnPropertyChanged(nameof(FileIconSource));
                        OnPropertyChanged(nameof(FileIconVisibility));
                        OnPropertyChanged(nameof(DefaultIconVisibility));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] File icon load failed: {ex.Message}");
                    FileIconSource = new BitmapImage();
                    FileIconVisibility = Visibility.Collapsed;
                    DefaultIconVisibility = Visibility.Visible;
                    OnPropertyChanged(nameof(FileIconSource));
                    OnPropertyChanged(nameof(FileIconVisibility));
                    OnPropertyChanged(nameof(DefaultIconVisibility));
                }
                finally
                {
                    _isLoadingFileIcon = false;
                }
            }

            private string CreateStatusText(long received, long total, long speed, bool isPaused)
            {
                if (_fileMissing)
                {
                    return GetString("DownloadStatusDeleted");
                }

                if (isPaused)
                {
                    return GetString("DownloadStatusPaused");
                }

                return State switch
                {
                    CoreWebView2DownloadState.Completed => GetString("DownloadStatusCompleted"),
                    CoreWebView2DownloadState.Interrupted => GetString("DownloadStatusInterrupted"),
                    _ => FormatStatus(received, total, speed)
                };
            }

            private string FormatStatus(long received, long total, long speed)
            {
                var receivedText = FormatBytes(received);
                var speedText = string.Format(GetString("DownloadSpeedFormat"), FormatBytes(speed));
                if (total > 0)
                {
                    return string.Format(GetString("DownloadStatusInProgressWithTotalFormat"), receivedText, FormatBytes(total), speedText);
                }

                return string.Format(GetString("DownloadStatusInProgressFormat"), receivedText, speedText);
            }

            private string GetString(string key)
            {
                var value = _loader.GetString(key);
                return string.IsNullOrEmpty(value) ? key : value;
            }

            private string FormatBytes(long bytes)
            {
                string[] suffixes =
                {
                    GetString("DownloadUnitBytes"),
                    GetString("DownloadUnitKilobytes"),
                    GetString("DownloadUnitMegabytes"),
                    GetString("DownloadUnitGigabytes")
                };
                var value = Math.Max(0, bytes);
                var unit = 0;
                var displayValue = (double)value;
                while (displayValue >= 1024 && unit < suffixes.Length - 1)
                {
                    displayValue /= 1024;
                    unit++;
                }

                return unit == 0
                    ? $"{displayValue:0} {suffixes[unit]}"
                    : $"{displayValue:0.0} {suffixes[unit]}";
            }

            private CoreWebView2DownloadState GetHistoryState()
            {
                return _history.State switch
                {
                    nameof(CoreWebView2DownloadState.InProgress) => CoreWebView2DownloadState.Interrupted,
                    nameof(CoreWebView2DownloadState.Interrupted) => CoreWebView2DownloadState.Interrupted,
                    _ => CoreWebView2DownloadState.Completed
                };
            }

            private void OnPropertyChanged(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

    public readonly struct TileIconSet
        {
            public static TileIconSet Empty { get; } = new TileIconSet("", "", "", "", "");

            public TileIconSet(string square150Path, string square70Path, string square71Path, string square44Path, string square30Path)
            {
                Square150Path = square150Path;
                Square70Path = square70Path;
                Square71Path = square71Path;
                Square44Path = square44Path;
                Square30Path = square30Path;
            }

            public string Square150Path { get; }
            public string Square70Path { get; }
            public string Square71Path { get; }
            public string Square44Path { get; }
            public string Square30Path { get; }
            public bool IsEmpty => string.IsNullOrWhiteSpace(Square150Path);
    }
}
