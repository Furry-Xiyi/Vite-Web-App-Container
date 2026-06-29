using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class HomePage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();
        private static readonly HttpClient _httpClient = new HttpClient();
        private bool _isInitialized;
        private bool _transparentScriptRegistered;
        private bool _hostTitleBarScriptRegistered;

        private const string HostTitleBarScript = @"(()=>{
window.__WINUI_ON_WEB_UWP_APP__=true;
const addHostClass=()=>{
  if(document.documentElement)document.documentElement.classList.add('winui-webview-host');
};
addHostClass();
window.__WINUI_ON_WEB_APP_ACTIVE__=true;
if(!window.__WINUI_ON_WEB_FOCUS_BRIDGE__){
  window.__WINUI_ON_WEB_FOCUS_BRIDGE__=true;
  window.addEventListener('blur',()=>{
    if(window.__WINUI_ON_WEB_UWP_APP__&&window.__WINUI_ON_WEB_APP_ACTIVE__){
      setTimeout(()=>window.dispatchEvent(new Event('focus')),0);
    }
  },true);
}
const listeners=new Set();
const overlay={
  visible:false,
  getTitlebarAreaRect(){
    const width=Math.max(0,window.innerWidth-180);
    return {x:0,y:0,width,height:32,top:0,left:0,right:width,bottom:32};
  },
  addEventListener(type,listener){
    if(type==='geometrychange'&&typeof listener==='function')listeners.add(listener);
  },
  removeEventListener(type,listener){
    if(type==='geometrychange')listeners.delete(listener);
  }
};
try{Object.defineProperty(Navigator.prototype,'windowControlsOverlay',{get(){return overlay;},configurable:true});}catch{}
try{Object.defineProperty(navigator,'windowControlsOverlay',{value:overlay,configurable:true});}catch{}
window.addEventListener('resize',()=>{
  const event=new Event('geometrychange');
  Object.defineProperty(event,'titlebarAreaRect',{value:overlay.getTitlebarAreaRect()});
  listeners.forEach(listener=>listener.call(overlay,event));
});
const postDebug=(message)=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'debug',message});
};
const dispatchGeometryChange=()=>{
  const event=new Event('geometrychange');
  Object.defineProperty(event,'titlebarAreaRect',{value:overlay.getTitlebarAreaRect()});
  listeners.forEach(listener=>listener.call(overlay,event));
};
const postTitleBarState=(visible)=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'titleBarChanged',visible});
};
const setOverlayVisible=(visible)=>{
  const next=Boolean(visible);
  if(overlay.visible===next)return;
  postDebug('windowControlsOverlay.visible -> '+next);
  overlay.visible=next;
  dispatchGeometryChange();
  postTitleBarState(next);
};
window.__WINUI_ON_WEB_SET_WCO_VISIBLE__=setOverlayVisible;
const detectManifestTitleBar=async()=>{
  const link=document.querySelector('link[rel~=""manifest""]');
  if(!link||!link.href){
    postDebug('No manifest link found.');
  }else{
    postDebug('Manifest link found: '+link.href);
  }
  const urls=[];
  if(link&&link.href)urls.push(link.href);
  urls.push(new URL('manifest.json',document.baseURI).href);
  urls.push(location.origin+'/manifest.json');
  const firstPathPart=location.pathname.split('/').filter(Boolean)[0];
  if(firstPathPart)urls.push(location.origin+'/'+firstPathPart+'/manifest.json');
  const candidates=[...new Set(urls)];
  postDebug('Manifest candidates: '+candidates.join(', '));
  for(const url of candidates){
    try{
      const response=await fetch(url,{credentials:'same-origin'});
      postDebug('Manifest fetch '+url+' -> '+response.status);
      if(!response.ok)continue;
      const manifest=await response.json();
      const displayOverride=Array.isArray(manifest.display_override)?manifest.display_override:[];
      postDebug('Manifest display_override: '+JSON.stringify(displayOverride));
      setOverlayVisible(displayOverride.includes('window-controls-overlay'));
      return;
    }catch(error){
      postDebug('Manifest fetch failed '+url+': '+String(error));
    }
  }
  if(link&&link.href){
    postDebug('No usable manifest with window-controls-overlay was found. Falling back to manifest link presence.');
    setOverlayVisible(true);
    return;
  }
  if(/WinUIonWeb/i.test(location.pathname)){
    postDebug('No manifest link was found, but path looks like WinUIonWeb. Falling back to enabled overlay.');
    setOverlayVisible(true);
    return;
  }
  postDebug('No usable manifest with window-controls-overlay was found.');
};
window.__WINUI_ON_WEB_DETECT_MANIFEST_TITLEBAR__=detectManifestTitleBar;
const detectTitleBar=()=>{
  const titleBar=document.querySelector('.win-titlebar,[data-winui-titlebar],[data-titlebar]');
  let visible=overlay.visible;
  if(titleBar){
    const style=getComputedStyle(titleBar);
    visible=style.display!=='none'&&style.visibility!=='hidden'&&titleBar.getClientRects().length>0;
  }
  if(window.__WINUI_ON_WEB_HOST_TITLEBAR_VISIBLE__!==visible){
    window.__WINUI_ON_WEB_HOST_TITLEBAR_VISIBLE__=visible;
    postDebug('DOM titlebar visible -> '+visible);
    postTitleBarState(visible);
  }
};
window.__WINUI_ON_WEB_DETECT_TITLEBAR__=detectTitleBar;
const startDetecting=()=>{
  addHostClass();
  detectManifestTitleBar();
  detectTitleBar();
  new MutationObserver(detectTitleBar).observe(document.documentElement||document,{childList:true,subtree:true,attributes:true,attributeFilter:['class','style','hidden']});
  setTimeout(detectTitleBar,0);
  setTimeout(detectTitleBar,500);
};
if(document.readyState==='loading'){
  document.addEventListener('DOMContentLoaded',startDetecting,{once:true});
}else{
  startDetecting();
}
})()";

        private const string DocumentInfoScript = @"(()=>{
const resolve=(value)=>{try{return value?new URL(value,document.baseURI).href:null;}catch{return null;}};
const icon=document.querySelector('link[rel~=""icon""],link[rel~=""shortcut""]');
if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'documentInfoChanged',title:document.title||'',icon:resolve(icon&&icon.getAttribute('href'))});
}
})()";

        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
            Navigate(SettingsManager.Instance.HomeUrl);
        }

        public void Navigate(string url)
        {
            if (!_isInitialized)
            {
                return;
            }

            RootWebView.Source = new Uri(url);
        }

        public async void SetHostedWindowActive(bool isActive)
        {
            if (RootWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                var script = isActive
                    ? "window.__WINUI_ON_WEB_APP_ACTIVE__=true;window.dispatchEvent(new Event('focus'));"
                    : "window.__WINUI_ON_WEB_APP_ACTIVE__=false;window.dispatchEvent(new Event('blur'));";

                await RootWebView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Failed to set hosted active state: {ex.Message}");
            }
        }

        public async void RefreshTransparentCss()
        {
            await ApplyTransparentBackgroundAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            if (_isInitialized)
            {
                return;
            }

            Environment.SetEnvironmentVariable(
                "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                MainPage.GetWebView2BrowserArguments(false));

            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00000000");

            await RootWebView.EnsureCoreWebView2Async();
            RootWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            RootWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            RootWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            await RegisterHostTitleBarScriptAsync();
            await ApplyTransparentBackgroundAsync();

            _isInitialized = true;
        }

        private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            MainPage.Instance?.SetHostedTitleBarVisible(false);
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("source", out var source)
                    || source.GetString() != "WinUIonWeb"
                    || !root.TryGetProperty("type", out var type))
                {
                    return;
                }

                if (type.GetString() == "appSettingChanged"
                    && root.TryGetProperty("key", out var key)
                    && root.TryGetProperty("value", out var value))
                {
                    ApplyHostedSetting(key.GetString(), value.GetString());
                    return;
                }

                if (type.GetString() == "debug"
                    && root.TryGetProperty("message", out var message))
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] {message.GetString()}");
                    return;
                }

                if (type.GetString() == "documentInfoChanged")
                {
                    string? title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
                    Uri? iconUri = null;
                    if (root.TryGetProperty("icon", out var iconValue)
                        && Uri.TryCreate(iconValue.GetString(), UriKind.Absolute, out var parsedIconUri))
                    {
                        iconUri = parsedIconUri;
                    }

                    MainPage.Instance?.UpdateHostedDocumentInfo(title, iconUri);
                    return;
                }

                if (type.GetString() == "titleBarChanged"
                    && root.TryGetProperty("visible", out var visible)
                    && (visible.ValueKind == JsonValueKind.True || visible.ValueKind == JsonValueKind.False))
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Host title bar visible = {visible.GetBoolean()}");
                    MainPage.Instance?.SetHostedTitleBarVisible(visible.GetBoolean());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView message failed: {ex.Message}");
            }
        }

        private static void ApplyHostedSetting(string? key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (key == "theme")
            {
                var appTheme = value switch
                {
                    "light" => "Light",
                    "dark" => "Dark",
                    _ => "System"
                };

                SettingsManager.Instance.AppTheme = appTheme;
                AppThemeManager.CurrentTheme = appTheme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };

                AppThemeManager.ApplyTheme();
                AppThemeManager.ApplyMaterial();
                return;
            }

            if (key == "material")
            {
                var appMaterial = value.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                    ? "Acrylic"
                    : "Mica";

                SettingsManager.Instance.AppMaterial = appMaterial;
                AppThemeManager.CurrentMaterial = appMaterial == "Acrylic"
                    ? BackgroundMaterial.Acrylic
                    : BackgroundMaterial.Mica;

                AppThemeManager.ApplyMaterial();
            }
        }

        private async void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            await ApplyTransparentBackgroundAsync();
            await UpdateDocumentInfoAsync();
            await DetectHostTitleBarFromIndexAsync();
            await DetectHostTitleBarAsync();
            SetHostedWindowActive(MainPage.Instance?.IsWindowActive ?? true);
        }

        private async Task DetectHostTitleBarFromIndexAsync()
        {
            if (RootWebView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                var source = RootWebView.CoreWebView2.Source;
                if (string.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out var pageUri))
                {
                    System.Diagnostics.Debug.WriteLine("[WinUIonWeb WebView] Cannot detect manifest: invalid page uri.");
                    return;
                }

                var indexHtml = await ReadTextAsync(pageUri);
                if (string.IsNullOrWhiteSpace(indexHtml))
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Cannot detect manifest: empty index from {pageUri}.");
                    return;
                }

                var manifestHref = FindManifestHref(indexHtml);
                if (string.IsNullOrWhiteSpace(manifestHref))
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Cannot detect manifest: no manifest link in {pageUri}.");
                    return;
                }

                var manifestUri = ResolveManifestUri(pageUri, manifestHref);
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest href from index: {manifestHref}");
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest resolved uri: {manifestUri}");

                var manifestJson = await ReadTextAsync(manifestUri);
                if (string.IsNullOrWhiteSpace(manifestJson))
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Cannot detect manifest: empty manifest from {manifestUri}.");
                    return;
                }

                using var document = JsonDocument.Parse(manifestJson);
                var root = document.RootElement;
                var hasWindowControlsOverlay = root.TryGetProperty("display_override", out var displayOverride)
                    && displayOverride.ValueKind == JsonValueKind.Array
                    && displayOverride.EnumerateArray().Any(item => item.GetString() == "window-controls-overlay");

                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest has window-controls-overlay = {hasWindowControlsOverlay}");

                await RegisterHostTitleBarScriptAsync();
                MainPage.Instance?.SetHostedTitleBarVisible(hasWindowControlsOverlay);

                await RootWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.__WINUI_ON_WEB_SET_WCO_VISIBLE__&&window.__WINUI_ON_WEB_SET_WCO_VISIBLE__({hasWindowControlsOverlay.ToString().ToLowerInvariant()});");
                SetHostedWindowActive(MainPage.Instance?.IsWindowActive ?? true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest detection failed: {ex.Message}");
            }
        }

        private static async Task<string?> ReadTextAsync(Uri uri)
        {
            if (uri.IsFile)
            {
                return File.Exists(uri.LocalPath)
                    ? await File.ReadAllTextAsync(uri.LocalPath)
                    : null;
            }

            return await _httpClient.GetStringAsync(uri);
        }

        private static string? FindManifestHref(string html)
        {
            foreach (Match match in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var tag = match.Value;
                var rel = GetHtmlAttribute(tag, "rel");
                if (rel?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Equals("manifest", StringComparison.OrdinalIgnoreCase)) != true)
                {
                    continue;
                }

                return GetHtmlAttribute(tag, "href");
            }

            return null;
        }

        private static string? GetHtmlAttribute(string tag, string name)
        {
            var match = Regex.Match(tag, $@"\b{name}\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        private static Uri ResolveManifestUri(Uri pageUri, string manifestHref)
        {
            if (Uri.TryCreate(manifestHref, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri;
            }

            if (pageUri.IsFile)
            {
                var indexDirectory = Path.GetDirectoryName(pageUri.LocalPath) ?? string.Empty;
                var relativePath = manifestHref.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                var publicPath = Path.Combine(indexDirectory, "public", relativePath);
                if (File.Exists(publicPath))
                {
                    return new Uri(publicPath);
                }

                return new Uri(Path.Combine(indexDirectory, relativePath));
            }

            return new Uri(pageUri, manifestHref);
        }

        private async Task RegisterHostTitleBarScriptAsync()
        {
            if (RootWebView.CoreWebView2 == null)
            {
                return;
            }

            if (!_hostTitleBarScriptRegistered)
            {
                await RootWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HostTitleBarScript);
                _hostTitleBarScriptRegistered = true;
            }

            await RootWebView.CoreWebView2.ExecuteScriptAsync(HostTitleBarScript);
        }

        private async Task DetectHostTitleBarAsync()
        {
            if (RootWebView.CoreWebView2 == null)
            {
                return;
            }

            await RootWebView.CoreWebView2.ExecuteScriptAsync("window.__WINUI_ON_WEB_DETECT_MANIFEST_TITLEBAR__&&window.__WINUI_ON_WEB_DETECT_MANIFEST_TITLEBAR__();window.__WINUI_ON_WEB_DETECT_TITLEBAR__&&window.__WINUI_ON_WEB_DETECT_TITLEBAR__();");
        }

        private async Task ApplyTransparentBackgroundAsync()
        {
            if (RootWebView.CoreWebView2 == null)
            {
                return;
            }

            var script = GetTransparentWebViewScript();
            if (!_transparentScriptRegistered)
            {
                await RootWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
                _transparentScriptRegistered = true;
            }

            await RootWebView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private async Task UpdateDocumentInfoAsync()
        {
            if (RootWebView.CoreWebView2 == null)
            {
                return;
            }

            await RootWebView.CoreWebView2.ExecuteScriptAsync(DocumentInfoScript);
        }

        private static string GetTransparentWebViewScript()
        {
            var css = new StringBuilder();
            foreach (var rule in SettingsManager.Instance.TransparentCssRules)
            {
                if (!string.IsNullOrWhiteSpace(rule.Selector) && !string.IsNullOrWhiteSpace(rule.Css))
                {
                    css.Append(rule.Selector).Append('{').Append(rule.Css).Append('}');
                }
            }

            var cssJson = JsonSerializer.Serialize(css.ToString());
            return $@"(()=>{{window.__WINUI_ON_WEB_UWP_APP__=true;const root=document.documentElement;if(root)root.classList.add('winui-webview-host');const id='winui-on-web-transparent-background';let style=document.getElementById(id);if(!style){{style=document.createElement('style');style.id=id;(document.head||root||document.documentElement).appendChild(style);}}style.textContent={cssJson};}})()";
        }
    }
}
