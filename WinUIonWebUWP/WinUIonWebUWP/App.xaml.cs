using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace WinUIonWebUWP
{
    public partial class App : Application
    {
        private static readonly object ViewRegistryLock = new object();
        private static readonly Dictionary<string, int> ContainerViewIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public App()
        {
            this.InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            var launchedContainerId = SettingsManager.Instance.GetContainerIdFromLaunchArguments(args.Arguments);
            if (!string.IsNullOrWhiteSpace(launchedContainerId)
                && SettingsManager.Instance.HasContainer(launchedContainerId)
                && Window.Current.Content != null)
            {
                await LaunchContainerInNewViewAsync(launchedContainerId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(launchedContainerId))
            {
                MainPage.SetPendingContainerId(launchedContainerId);
            }

            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;

                AppThemeManager.LoadSettings();
                AppThemeManager.ApplyTheme();
                AppThemeManager.ApplyMaterial();
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage));
            }
            else
            {
                var currentMainPage = MainPage.Current;
                var defaultContainerId = SettingsManager.Instance.ActiveContainerId;
                if (currentMainPage != null
                    && currentMainPage.ContainerId != defaultContainerId
                    && SettingsManager.Instance.HasContainer(defaultContainerId))
                {
                    await LaunchContainerInNewViewAsync(defaultContainerId);
                    return;
                }
            }

            Window.Current.Activate();
            if (rootFrame.Content is MainPage mainPage)
            {
                await mainPage.CheckDownloadAccessOnFirstLaunchAsync();
            }
        }

        internal static async System.Threading.Tasks.Task LaunchContainerInNewViewAsync(string containerId)
        {
            if (TryGetRegisteredViewId(containerId, out var existingViewId)
                && await ApplicationViewSwitcher.TryShowAsStandaloneAsync(existingViewId))
            {
                return;
            }

            var newView = CoreApplication.CreateNewView();
            var newViewId = 0;

            await newView.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                MainPage.SetPendingContainerId(containerId);

                var rootFrame = new Frame();
                Window.Current.Content = rootFrame;

                AppThemeManager.LoadSettings();
                AppThemeManager.ApplyTheme();
                AppThemeManager.ApplyMaterial();

                rootFrame.Navigate(typeof(MainPage));
                Window.Current.Activate();
                newViewId = ApplicationView.GetForCurrentView().Id;
            });

            if (newViewId != 0)
            {
                await ApplicationViewSwitcher.TryShowAsStandaloneAsync(newViewId);
            }
        }

        internal static void RegisterContainerView(string containerId, int viewId)
        {
            if (string.IsNullOrWhiteSpace(containerId) || viewId == 0)
            {
                return;
            }

            lock (ViewRegistryLock)
            {
                ContainerViewIds[containerId] = viewId;
            }
        }

        internal static void UnregisterContainerView(string containerId, int viewId)
        {
            if (string.IsNullOrWhiteSpace(containerId) || viewId == 0)
            {
                return;
            }

            lock (ViewRegistryLock)
            {
                if (ContainerViewIds.TryGetValue(containerId, out var registeredViewId)
                    && registeredViewId == viewId)
                {
                    ContainerViewIds.Remove(containerId);
                }
            }
        }

        private static bool TryGetRegisteredViewId(string containerId, out int viewId)
        {
            lock (ViewRegistryLock)
            {
                return ContainerViewIds.TryGetValue(containerId, out viewId);
            }
        }
    }

    // ── 主题管理器 ─────────────────────────────────────────────────
    public static class AppThemeManager
    {
        public static ElementTheme CurrentTheme = ElementTheme.Default;
        public static BackgroundMaterial CurrentMaterial = BackgroundMaterial.Mica;

        public static void LoadSettings()
        {
            var s = SettingsManager.Instance;

            try
            {
                CurrentTheme = s.AppTheme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
            catch { CurrentTheme = ElementTheme.Default; }

            try
            {
                CurrentMaterial = s.AppMaterial == "Acrylic"
                    ? BackgroundMaterial.Acrylic
                    : BackgroundMaterial.Mica;
            }
            catch { CurrentMaterial = BackgroundMaterial.Mica; }

            try
            {
                ElementSoundPlayer.State = s.EnableSound
                    ? ElementSoundPlayerState.On
                    : ElementSoundPlayerState.Off;
            }
            catch { ElementSoundPlayer.State = ElementSoundPlayerState.On; }
        }

        public static void ApplyTheme()
        {
            try
            {
                if (Window.Current.Content is FrameworkElement rootElement)
                    rootElement.RequestedTheme = CurrentTheme;

                CustomizeTitleBar();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyTheme failed: {ex.Message}");
            }
        }

        public static void ApplyMaterial()
        {
            try
            {
                var rootFrame = Window.Current.Content as Frame;
                if (rootFrame == null) return;
                if (rootFrame is FrameworkElement el)
                {
                    el.ActualThemeChanged -= OnActualThemeChanged;
                    el.ActualThemeChanged += OnActualThemeChanged;
                }

                if (CurrentMaterial == BackgroundMaterial.Mica)
                {
                    rootFrame.Background = null;
                    Microsoft.UI.Xaml.Controls.BackdropMaterial.SetApplyToRootOrPageBackground(rootFrame, true);
                }
                else
                {
                    Microsoft.UI.Xaml.Controls.BackdropMaterial.SetApplyToRootOrPageBackground(rootFrame, false);

                    var isDark = GetIsDarkTheme();
                    var tintColor = isDark
                        ? Color.FromArgb(255, 44, 44, 44)
                        : Color.FromArgb(255, 252, 252, 252);
                    var fallbackColor = isDark
                        ? Color.FromArgb(255, 44, 44, 44)
                        : Color.FromArgb(255, 249, 249, 249);
                    var tintOpacity = isDark ? 0.15 : 0.0;

                    rootFrame.Background = new Microsoft.UI.Xaml.Media.AcrylicBrush
                    {
                        BackgroundSource = Microsoft.UI.Xaml.Media.AcrylicBackgroundSource.HostBackdrop,
                        TintColor = tintColor,
                        TintOpacity = tintOpacity,
                        FallbackColor = fallbackColor,
                        TintLuminosityOpacity = isDark ? 0.96 : 0.85
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyMaterial failed: {ex.Message}");
            }
        }

        public static void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            // 此处同步更新标题栏颜色
            CustomizeTitleBar();

            if (CurrentMaterial == BackgroundMaterial.Acrylic)
            {
                var rootFrame = Window.Current.Content as Frame;
                if (rootFrame == null) return;

                if (rootFrame.Background is Microsoft.UI.Xaml.Media.AcrylicBrush brush)
                {
                    var isDark = GetIsDarkTheme();
                    brush.TintColor = isDark
                        ? Color.FromArgb(255, 44, 44, 44)
                        : Color.FromArgb(255, 252, 252, 252);
                    brush.FallbackColor = isDark
                        ? Color.FromArgb(255, 44, 44, 44)
                        : Color.FromArgb(255, 249, 249, 249);
                    brush.TintOpacity = isDark ? 0.15 : 0.0;
                    brush.TintLuminosityOpacity = isDark ? 0.96 : 0.85;
                }
            }
        }

        public static bool GetIsDarkTheme()
        {
            if (Window.Current?.Content is FrameworkElement rootElement)
            {
                var actual = rootElement.ActualTheme;
                if (actual != ElementTheme.Default)
                    return actual == ElementTheme.Dark;
            }
            if (CurrentTheme == ElementTheme.Default)
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            return CurrentTheme == ElementTheme.Dark;
        }

        public static void CustomizeTitleBar()
        {
            try
            {
                var coreTitleBar = Windows.ApplicationModel.Core.CoreApplication.GetCurrentView().TitleBar;
                coreTitleBar.ExtendViewIntoTitleBar = true;

                var titleBar = ApplicationView.GetForCurrentView().TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                var isDark = GetIsDarkTheme();
                var fg = isDark ? Colors.White : Colors.Black;

                // 与 WinUI 3 模板保持一致：失焦按钮用不透明灰，效果更稳定
                var inactiveFg = isDark
                    ? Color.FromArgb(255, 128, 128, 128)
                    : Color.FromArgb(255, 160, 160, 160);

                var hoverBg = isDark
                    ? Color.FromArgb(20, 255, 255, 255)
                    : Color.FromArgb(20, 0, 0, 0);

                titleBar.ButtonForegroundColor = fg;
                titleBar.ButtonInactiveForegroundColor = inactiveFg;
                titleBar.ButtonHoverBackgroundColor = hoverBg;
                titleBar.ButtonHoverForegroundColor = fg;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(30, hoverBg.R, hoverBg.G, hoverBg.B);
                titleBar.ButtonPressedForegroundColor = fg;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CustomizeTitleBar failed: {ex.Message}");
            }
        }
    }

    public enum BackgroundMaterial
    {
        Mica,
        Acrylic
    }

    // ── AOT 安全的 JSON 源生成上下文 ──────────────────────────────
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(TransparentCssRule))]
    [JsonSerializable(typeof(WebContainer))]
    [JsonSerializable(typeof(SitePermissionSetting))]
    [JsonSerializable(typeof(DownloadHistoryEntry))]
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }

    // ── 设置管理器（与 App.xaml.cs 同文件，无需单独类文件）─────────
    public sealed class SettingsManager
    {
        public const string DownloadFolderAccessToken = "WinUIonWeb.DownloadFolder";

        private static SettingsManager? _instance;
        public static SettingsManager Instance => _instance ??= new SettingsManager();

        private readonly string _settingsFilePath;
        private AppSettings _settings;
        private const string DefaultContainerId = "default";

        private SettingsManager()
        {
            _settingsFilePath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "app_settings.json"
            );
            _settings = LoadSettingsFromFile();
        }

        public string AppTheme
        {
            get => _settings.AppTheme;
            set { _settings.AppTheme = value; SaveSettings(); }
        }

        public string AppMaterial
        {
            get => _settings.AppMaterial;
            set { _settings.AppMaterial = value; SaveSettings(); }
        }

        public string PanePosition
        {
            get => _settings.PanePosition;
            set { _settings.PanePosition = value; SaveSettings(); }
        }

        public bool EnableSound
        {
            get => _settings.EnableSound;
            set { _settings.EnableSound = value; SaveSettings(); }
        }

        public bool IsDownloadsButtonPinned
        {
            get => _settings.IsDownloadsButtonPinned;
            set { _settings.IsDownloadsButtonPinned = value; SaveSettings(); }
        }

        public string DownloadFolderToken
        {
            get => _settings.DownloadFolderToken;
            set { _settings.DownloadFolderToken = value; SaveSettings(); }
        }

        public string DownloadFolderPath
        {
            get => string.IsNullOrWhiteSpace(_settings.DownloadFolderPath)
                ? GetDefaultDownloadFolderPath()
                : _settings.DownloadFolderPath;
            set { _settings.DownloadFolderPath = value; SaveSettings(); }
        }

        public bool HasShownDownloadAccessPrompt
        {
            get => _settings.HasShownDownloadAccessPrompt;
            set { _settings.HasShownDownloadAccessPrompt = value; SaveSettings(); }
        }

        public IReadOnlyList<DownloadHistoryEntry> DownloadHistory
        {
            get
            {
                EnsureDownloadHistory();
                return _settings.DownloadHistory.Select(CloneDownloadHistoryEntry).ToList();
            }
        }

        public void UpsertDownloadHistory(DownloadHistoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
            }

            EnsureDownloadHistory();
            _settings.DownloadHistory.RemoveAll(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            _settings.DownloadHistory.Insert(0, CloneDownloadHistoryEntry(entry));
            SaveSettings();
        }

        public void RemoveDownloadHistory(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            EnsureDownloadHistory();
            _settings.DownloadHistory.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
        }

        public string HomeUrl
        {
            get => string.IsNullOrWhiteSpace(CurrentContainer.HomeUrl)
                ? new ResourceLoader().GetString("DefaultHomeUrl")
                : CurrentContainer.HomeUrl;
            set { CurrentContainer.HomeUrl = value; SaveSettings(); }
        }

        public IReadOnlyList<string> HomeUrlHistory => CurrentContainer.HomeUrlHistory;

        public IReadOnlyList<WebContainer> Containers
        {
            get
            {
                EnsureContainers();
                return _settings.Containers.ToList();
            }
        }

        public bool IsDefaultContainer(string containerId)
        {
            return string.Equals(containerId, DefaultContainerId, StringComparison.OrdinalIgnoreCase);
        }

        public string ActiveContainerId => CurrentContainer.Id;

        public bool HasContainer(string containerId)
        {
            EnsureContainers();
            return _settings.Containers.Any(item => item.Id == containerId);
        }

        public string ActiveContainerDisplayName
        {
            get
            {
                var name = CurrentContainer.DisplayName?.Trim();
                return string.IsNullOrWhiteSpace(name)
                    ? new ResourceLoader().GetString("AppDisplayName")
                    : name;
            }
        }

        public Uri ActiveContainerIconUri
        {
            get
            {
                var iconPath = CurrentContainer.IconPath;
                if (!string.IsNullOrWhiteSpace(iconPath))
                {
                    return CreateLocalUri(iconPath);
                }

                return Windows.ApplicationModel.Package.Current.Logo;
            }
        }

        public string ActiveContainerWebViewDataFolder =>
            Path.Combine(ApplicationData.Current.LocalFolder.Path, "webview2-profiles", ActiveContainerId);

        public string GetContainerDisplayName(string containerId)
        {
            var container = GetContainerOrDefault(containerId);
            var name = container.DisplayName?.Trim();
            return string.IsNullOrWhiteSpace(name)
                ? new ResourceLoader().GetString("AppDisplayName")
                : name;
        }

        public Uri GetContainerIconUri(string containerId)
        {
            var iconPath = GetContainerOrDefault(containerId).IconPath;
            return string.IsNullOrWhiteSpace(iconPath)
                ? Windows.ApplicationModel.Package.Current.Logo
                : CreateLocalUri(iconPath);
        }

        public string GetContainerHomeUrl(string containerId)
        {
            var homeUrl = GetContainerOrDefault(containerId).HomeUrl;
            return string.IsNullOrWhiteSpace(homeUrl)
                ? new ResourceLoader().GetString("DefaultHomeUrl")
                : homeUrl;
        }

        public IReadOnlyList<string> GetContainerHomeUrlHistory(string containerId)
        {
            return GetContainerOrDefault(containerId).HomeUrlHistory.ToList();
        }

        public string GetContainerWebViewDataFolder(string containerId)
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, "webview2-profiles", containerId);
        }

        public void SetContainerHomeUrl(string containerId, string homeUrl)
        {
            GetContainerOrDefault(containerId).HomeUrl = homeUrl;
            SaveSettings();
        }

        public void UpdateContainer(string containerId, string displayName, string homeUrl)
        {
            EnsureContainers();
            var container = GetContainerOrDefault(containerId);
            container.DisplayName = SanitizeDisplayName(displayName, homeUrl);
            container.HomeUrl = homeUrl;
            container.HomeUrlHistory.RemoveAll(item => string.Equals(item, homeUrl, StringComparison.OrdinalIgnoreCase));
            container.HomeUrlHistory.Insert(0, homeUrl);
            SaveSettings();
        }

        public void AddContainerHomeUrlHistory(string containerId, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var history = GetContainerOrDefault(containerId).HomeUrlHistory;
            history.RemoveAll(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, url);
            SaveSettings();
        }

        public void RemoveContainerHomeUrlHistory(string containerId, string url)
        {
            GetContainerOrDefault(containerId).HomeUrlHistory.RemoveAll(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
        }

        public IReadOnlyList<TransparentCssRule> TransparentCssRules =>
            _settings.TransparentCssRules
                .Where(rule => !TransparentCssRule.IsBuiltInId(rule.Id))
                .ToList();

        public IReadOnlyList<TransparentCssRule> TransparentCssInjectionRules =>
            TransparentCssRule.CreateDefaults()
                .Concat(_settings.TransparentCssRules.Where(rule => !TransparentCssRule.IsBuiltInId(rule.Id)))
                .ToList();

        public bool UsePerSiteTransparentCss
        {
            get => _settings.UsePerSiteTransparentCss;
            set { _settings.UsePerSiteTransparentCss = value; SaveSettings(); }
        }

        public void AddHomeUrlHistory(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var history = CurrentContainer.HomeUrlHistory;
            history.RemoveAll(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, url);
            SaveSettings();
        }

        public void RemoveHomeUrlHistory(string url)
        {
            CurrentContainer.HomeUrlHistory.RemoveAll(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
        }

        public WebContainer CreateOrUpdateContainer(string displayName, string homeUrl, string iconPath, bool activate = true)
        {
            EnsureContainers();

            var container = _settings.Containers.FirstOrDefault(item =>
                item.Id != DefaultContainerId
                && string.Equals(item.HomeUrl, homeUrl, StringComparison.OrdinalIgnoreCase));

            if (container == null)
            {
                container = new WebContainer
                {
                    Id = CreateContainerId()
                };
                _settings.Containers.Add(container);
            }
            else if (container.Id != DefaultContainerId && !IsTileIdSafe(container.Id))
            {
                container.Id = CreateContainerId();
            }

            container.DisplayName = SanitizeDisplayName(displayName, homeUrl);
            container.HomeUrl = homeUrl;
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                container.IconPath = iconPath;
            }

            if (activate)
            {
                _settings.ActiveContainerId = container.Id;
            }
            container.HomeUrlHistory.RemoveAll(item => string.Equals(item, homeUrl, StringComparison.OrdinalIgnoreCase));
            container.HomeUrlHistory.Insert(0, homeUrl);
            SaveSettings();
            return container;
        }

        public string? GetContainerIdForHomeUrl(string homeUrl)
        {
            EnsureContainers();
            return _settings.Containers.FirstOrDefault(item =>
                item.Id != DefaultContainerId
                && string.Equals(item.HomeUrl, homeUrl, StringComparison.OrdinalIgnoreCase))?.Id
                ?? _settings.Containers.FirstOrDefault(item =>
                string.Equals(item.HomeUrl, homeUrl, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        public void UpdateContainerIcon(string containerId, string iconPath)
        {
            EnsureContainers();
            var container = _settings.Containers.FirstOrDefault(item => item.Id == containerId);
            if (container == null || string.IsNullOrWhiteSpace(iconPath))
            {
                return;
            }

            container.IconPath = iconPath;
            SaveSettings();
        }

        public void ClearContainerIcon(string containerId)
        {
            EnsureContainers();
            var container = _settings.Containers.FirstOrDefault(item => item.Id == containerId);
            if (container == null)
            {
                return;
            }

            container.IconPath = "";
            SaveSettings();
        }

        public bool DeleteContainer(string containerId)
        {
            EnsureContainers();
            if (IsDefaultContainer(containerId))
            {
                return false;
            }

            var removed = _settings.Containers.RemoveAll(item => item.Id == containerId) > 0;
            if (!removed)
            {
                return false;
            }

            if (string.Equals(_settings.ActiveContainerId, containerId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.ActiveContainerId = DefaultContainerId;
            }

            SaveSettings();
            return true;
        }

        public IReadOnlyList<SitePermissionSetting> GetSitePermissions(string origin)
        {
            EnsurePermissionSettings();
            return _settings.SitePermissions
                .Where(item => string.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase))
                .Select(item => new SitePermissionSetting
                {
                    Origin = item.Origin,
                    PermissionKind = item.PermissionKind,
                    State = item.State
                })
                .ToList();
        }

        public string GetSitePermissionState(string origin, string permissionKind)
        {
            EnsurePermissionSettings();
            return _settings.SitePermissions.FirstOrDefault(item =>
                string.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.PermissionKind, permissionKind, StringComparison.OrdinalIgnoreCase))?.State ?? "Default";
        }

        public void SetSitePermission(string origin, string permissionKind, string state)
        {
            EnsurePermissionSettings();
            _settings.SitePermissions.RemoveAll(item =>
                string.Equals(item.Origin, origin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.PermissionKind, permissionKind, StringComparison.OrdinalIgnoreCase));

            if (!string.Equals(state, "Default", StringComparison.OrdinalIgnoreCase))
            {
                _settings.SitePermissions.Add(new SitePermissionSetting
                {
                    Origin = origin,
                    PermissionKind = permissionKind,
                    State = state
                });
            }

            SaveSettings();
        }

        public bool ActivateContainerFromLaunchArguments(string? arguments)
        {
            var containerId = GetContainerIdFromLaunchArguments(arguments);
            if (string.IsNullOrWhiteSpace(containerId))
            {
                EnsureContainers();
                return false;
            }

            return ActivateContainer(containerId);
        }

        public string? GetContainerIdFromLaunchArguments(string? arguments)
        {
            return TryGetLaunchArgument(arguments, "containerId");
        }

        public bool ActivateContainer(string containerId)
        {
            EnsureContainers();
            if (!_settings.Containers.Any(item => item.Id == containerId))
            {
                return false;
            }

            _settings.ActiveContainerId = containerId;
            SaveSettings();
            return true;
        }

        public void UpdateTransparentCssRule(string id, string host, string selector, string css)
        {
            var rule = _settings.TransparentCssRules.FirstOrDefault(item => item.Id == id);
            if (rule == null)
            {
                _settings.TransparentCssRules.Add(new TransparentCssRule
                {
                    Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                    Host = host,
                    Selector = selector,
                    Css = css
                });
            }
            else
            {
                rule.Host = host;
                rule.Selector = selector;
                rule.Css = css;
            }

            SaveSettings();
        }

        public void RemoveTransparentCssRule(string id)
        {
            _settings.TransparentCssRules.RemoveAll(item => item.Id == id);
            SaveSettings();
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_settings, AppSettingsJsonContext.Default.AppSettings);
                File.WriteAllText(_settingsFilePath, json);
                Debug.WriteLine($"[SettingsManager] 已保存: {_settingsFilePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsManager] 保存失败: {ex.Message}");
            }
        }

        private AppSettings LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                    if (settings != null)
                    {
                        settings.TransparentCssRules ??= new List<TransparentCssRule>();
                        settings.HomeUrlHistory ??= new List<string>();
                        settings.Containers ??= new List<WebContainer>();
                        settings.SitePermissions ??= new List<SitePermissionSetting>();
                        settings.DownloadHistory ??= new List<DownloadHistoryEntry>();
                        EnsureContainers(settings);

                        Debug.WriteLine("[SettingsManager] 从文件加载");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsManager] 加载失败: {ex.Message}");
            }

            Debug.WriteLine("[SettingsManager] 使用默认设置");
            var defaults = new AppSettings();
            EnsureContainers(defaults);
            return defaults;
        }

        private WebContainer CurrentContainer
        {
            get
            {
                EnsureContainers();
                return GetContainerOrDefault(_settings.ActiveContainerId);
            }
        }

        private WebContainer GetContainerOrDefault(string containerId)
        {
            EnsureContainers();
            var container = _settings.Containers.FirstOrDefault(item => item.Id == containerId)
                ?? _settings.Containers.First(item => item.Id == DefaultContainerId);
            container.HomeUrlHistory ??= new List<string>();
            return container;
        }

        private void EnsureContainers() => EnsureContainers(_settings);

        private void EnsurePermissionSettings()
        {
            _settings.SitePermissions ??= new List<SitePermissionSetting>();
        }

        private void EnsureDownloadHistory()
        {
            _settings.DownloadHistory ??= new List<DownloadHistoryEntry>();
        }

        private static void EnsureContainers(AppSettings settings)
        {
            settings.Containers ??= new List<WebContainer>();
            settings.HomeUrlHistory ??= new List<string>();
            settings.SitePermissions ??= new List<SitePermissionSetting>();
            settings.DownloadHistory ??= new List<DownloadHistoryEntry>();
            settings.DownloadFolderToken ??= "";

            var defaultContainer = settings.Containers.FirstOrDefault(item => item.Id == DefaultContainerId);
            if (defaultContainer == null)
            {
                defaultContainer = new WebContainer
                {
                    Id = DefaultContainerId,
                    DisplayName = new ResourceLoader().GetString("AppDisplayName"),
                    HomeUrl = settings.HomeUrl,
                    HomeUrlHistory = settings.HomeUrlHistory
                };
                settings.Containers.Insert(0, defaultContainer);
            }

            foreach (var container in settings.Containers)
            {
                container.Id ??= "";
                container.DisplayName ??= "";
                container.HomeUrl ??= "";
                container.IconPath ??= "";
                container.HomeUrlHistory ??= new List<string>();
            }

            var defaultDownloadFolderPath = GetDefaultDownloadFolderPath();
            settings.DownloadFolderPath = string.IsNullOrWhiteSpace(settings.DownloadFolderPath)
                || string.Equals(settings.DownloadFolderPath, GetLegacyDefaultDownloadFolderPath(), StringComparison.OrdinalIgnoreCase)
                ? defaultDownloadFolderPath
                : settings.DownloadFolderPath;

            if (string.IsNullOrWhiteSpace(settings.ActiveContainerId)
                || !settings.Containers.Any(item => item.Id == settings.ActiveContainerId))
            {
                settings.ActiveContainerId = DefaultContainerId;
            }
        }

        private static Uri CreateLocalUri(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            return new Uri("ms-appdata:///local/" + normalized);
        }

        private static string CreateContainerId()
        {
            return "container" + Guid.NewGuid().ToString("N");
        }

        private static bool IsTileIdSafe(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 64
                && value.All(char.IsLetterOrDigit);
        }

        private static string SanitizeDisplayName(string displayName, string homeUrl)
        {
            var value = string.IsNullOrWhiteSpace(displayName) ? "" : displayName.Trim();
            if (string.IsNullOrWhiteSpace(value)
                && Uri.TryCreate(homeUrl, UriKind.Absolute, out var uri))
            {
                value = uri.Host;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = new ResourceLoader().GetString("AppDisplayName");
            }

            return value.Length > 48 ? value.Substring(0, 48) : value;
        }

        private static string? TryGetLaunchArgument(string? arguments, string key)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return null;
            }

            foreach (var part in arguments.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[0] == key)
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            return null;
        }

        private static string GetDefaultDownloadFolderPath()
        {
            try
            {
                var downloads = UserDataPaths.GetDefault().Downloads;
                if (!string.IsNullOrWhiteSpace(downloads))
                {
                    return downloads;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Get default downloads path failed: {ex.Message}");
            }

            return GetLegacyDefaultDownloadFolderPath();
        }

        private static string GetLegacyDefaultDownloadFolderPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(userProfile)
                ? Path.Combine(ApplicationData.Current.LocalFolder.Path, "Downloads")
                : Path.Combine(userProfile, "Downloads");
        }

        private static DownloadHistoryEntry CloneDownloadHistoryEntry(DownloadHistoryEntry entry)
        {
            return new DownloadHistoryEntry
            {
                Id = entry.Id,
                FilePath = entry.FilePath,
                SourceUri = entry.SourceUri,
                State = entry.State,
                BytesReceived = entry.BytesReceived,
                TotalBytesToReceive = entry.TotalBytesToReceive,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt
            };
        }

    }

    // ── 设置数据类（强类型，无装箱/拆箱，AOT 安全）─────────────────
    public sealed class AppSettings
    {
        public string AppTheme { get; set; } = "System";
        public string AppMaterial { get; set; } = "Mica";
        public string PanePosition { get; set; } = "Left";
        public bool EnableSound { get; set; } = true;
        public bool IsDownloadsButtonPinned { get; set; } = false;
        public string DownloadFolderToken { get; set; } = "";
        public string DownloadFolderPath { get; set; } = "";
        public bool HasShownDownloadAccessPrompt { get; set; } = false;
        public List<DownloadHistoryEntry> DownloadHistory { get; set; } = new List<DownloadHistoryEntry>();
        public string HomeUrl { get; set; } = "";
        public List<string> HomeUrlHistory { get; set; } = new List<string>();
        public string ActiveContainerId { get; set; } = "default";
        public List<WebContainer> Containers { get; set; } = new List<WebContainer>();
        public List<TransparentCssRule> TransparentCssRules { get; set; } = new List<TransparentCssRule>();
        public List<SitePermissionSetting> SitePermissions { get; set; } = new List<SitePermissionSetting>();
        public bool UsePerSiteTransparentCss { get; set; } = false;
    }

    public sealed class WebContainer
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string HomeUrl { get; set; } = "";
        public string IconPath { get; set; } = "";
        public List<string> HomeUrlHistory { get; set; } = new List<string>();
    }

    public sealed class SitePermissionSetting
    {
        public string Origin { get; set; } = "";
        public string PermissionKind { get; set; } = "";
        public string State { get; set; } = "Default";
    }

    public sealed class DownloadHistoryEntry
    {
        public string Id { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string SourceUri { get; set; } = "";
        public string State { get; set; } = "Completed";
        public long BytesReceived { get; set; }
        public long TotalBytesToReceive { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class TransparentCssRule
    {
        public string Id { get; set; } = "";
        public string Host { get; set; } = "";
        public string Selector { get; set; } = "";
        public string Css { get; set; } = "";

        public static List<TransparentCssRule> CreateDefaults()
        {
            return new List<TransparentCssRule>
            {
                new TransparentCssRule
                {
                    Id = "root",
                    Selector = "html.winui-webview-host",
                    Css = "--app-bg:transparent!important;background:transparent!important;background-color:transparent!important;"
                },
                new TransparentCssRule
                {
                    Id = "light-vars",
                    Selector = "html.winui-webview-host.theme-light",
                    Css = "--host-layer-default:rgba(255,255,255,.50);--host-nav-pane-bg:rgba(243,243,243,.72);--host-flyout-bg:rgba(252,252,252,.78);"
                },
                new TransparentCssRule
                {
                    Id = "dark-vars",
                    Selector = "html.winui-webview-host.theme-dark",
                    Css = "--host-layer-default:rgba(58,58,58,.25);--host-nav-pane-bg:rgba(32,32,32,.72);--host-flyout-bg:rgba(44,44,44,.78);"
                },
                new TransparentCssRule
                {
                    Id = "transparent-shell",
                    Selector = "html.winui-webview-host body,html.winui-webview-host #app,html.winui-webview-host .win-nav-shell",
                    Css = "background:transparent!important;background-color:transparent!important;"
                },
                new TransparentCssRule
                {
                    Id = "nav-content",
                    Selector = "html.winui-webview-host .win-nav-content",
                    Css = "background:var(--host-layer-default,light-dark(rgba(255,255,255,.50),rgba(58,58,58,.25)))!important;background-color:var(--host-layer-default,light-dark(rgba(255,255,255,.50),rgba(58,58,58,.25)))!important;"
                },
                new TransparentCssRule
                {
                    Id = "overlay-pane",
                    Selector = "html.winui-webview-host .win-nav-shell.is-overlay-left .win-nav-left-panel:not(.is-compact)",
                    Css = "background:var(--host-nav-pane-bg,light-dark(rgba(243,243,243,.72),rgba(32,32,32,.72)))!important;background-color:var(--host-nav-pane-bg,light-dark(rgba(243,243,243,.72),rgba(32,32,32,.72)))!important;backdrop-filter:blur(28px) saturate(1.35)!important;-webkit-backdrop-filter:blur(28px) saturate(1.35)!important;"
                },
                new TransparentCssRule
                {
                    Id = "flyout",
                    Selector = "html.winui-webview-host .win-menu-flyout",
                    Css = "background:var(--host-flyout-bg,light-dark(rgba(252,252,252,.78),rgba(44,44,44,.78)))!important;background-color:var(--host-flyout-bg,light-dark(rgba(252,252,252,.78),rgba(44,44,44,.78)))!important;backdrop-filter:var(--flyout-backdrop)!important;-webkit-backdrop-filter:var(--flyout-backdrop)!important;"
                }
            };
        }

        public static bool IsBuiltInId(string? id)
        {
            return id is "root"
                or "light-vars"
                or "dark-vars"
                or "transparent-shell"
                or "nav-content"
                or "overlay-pane"
                or "flyout";
        }
    }
}
