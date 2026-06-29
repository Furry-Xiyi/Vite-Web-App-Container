using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace WinUIonWebUWP
{
    public partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
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

            Window.Current.Activate();
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
    internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }

    // ── 设置管理器（与 App.xaml.cs 同文件，无需单独类文件）─────────
    public sealed class SettingsManager
    {
        private static SettingsManager? _instance;
        public static SettingsManager Instance => _instance ??= new SettingsManager();

        private readonly string _settingsFilePath;
        private AppSettings _settings;

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

        public string HomeUrl
        {
            get => string.IsNullOrWhiteSpace(_settings.HomeUrl)
                ? new ResourceLoader().GetString("DefaultHomeUrl")
                : _settings.HomeUrl;
            set { _settings.HomeUrl = value; SaveSettings(); }
        }

        public IReadOnlyList<string> HomeUrlHistory => _settings.HomeUrlHistory;

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

            _settings.HomeUrlHistory.RemoveAll(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
            _settings.HomeUrlHistory.Insert(0, url);
            SaveSettings();
        }

        public void RemoveHomeUrlHistory(string url)
        {
            _settings.HomeUrlHistory.RemoveAll(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
            SaveSettings();
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
            return new AppSettings();
        }
    }

    // ── 设置数据类（强类型，无装箱/拆箱，AOT 安全）─────────────────
    public sealed class AppSettings
    {
        public string AppTheme { get; set; } = "System";
        public string AppMaterial { get; set; } = "Mica";
        public string PanePosition { get; set; } = "Left";
        public bool EnableSound { get; set; } = true;
        public string HomeUrl { get; set; } = "";
        public List<string> HomeUrlHistory { get; set; } = new List<string>();
        public List<TransparentCssRule> TransparentCssRules { get; set; } = new List<TransparentCssRule>();
        public bool UsePerSiteTransparentCss { get; set; } = false;
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
                    Css = "background:var(--host-layer-default)!important;background-color:var(--host-layer-default)!important;"
                },
                new TransparentCssRule
                {
                    Id = "overlay-pane",
                    Selector = "html.winui-webview-host .win-nav-shell.is-overlay-left .win-nav-left-panel:not(.is-compact)",
                    Css = "background:var(--host-nav-pane-bg)!important;background-color:var(--host-nav-pane-bg)!important;backdrop-filter:blur(28px) saturate(1.35)!important;-webkit-backdrop-filter:blur(28px) saturate(1.35)!important;"
                },
                new TransparentCssRule
                {
                    Id = "flyout",
                    Selector = "html.winui-webview-host .win-menu-flyout",
                    Css = "background:var(--host-flyout-bg)!important;background-color:var(--host-flyout-bg)!important;backdrop-filter:var(--flyout-backdrop)!important;-webkit-backdrop-filter:var(--flyout-backdrop)!important;"
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
