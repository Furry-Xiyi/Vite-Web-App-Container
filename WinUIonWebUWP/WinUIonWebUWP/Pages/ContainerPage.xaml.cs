using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WinUIonWebUWP.Dialogs;
using Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace WinUIonWebUWP.Pages
{
    public sealed partial class ContainerPage : Page
    {
        private readonly ResourceLoader _loader = new ResourceLoader();
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly System.Threading.SemaphoreSlim WebViewInitializationLock = new System.Threading.SemaphoreSlim(1, 1);
        private MainPage? _owner;
        private bool _isInitialized;
        private bool _transparentScriptRegistered;
        private bool _hostTitleBarScriptRegistered;
        private bool _suppressNextNavigationFailureForDownload;
        private string _lastNavigationUrl = "";
        private CoreWebView2WebErrorStatus _lastWebErrorStatus = CoreWebView2WebErrorStatus.Unknown;

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
if(window.__WINUI_ON_WEB_DOCUMENT_INFO_OBSERVER__)return;
window.__WINUI_ON_WEB_DOCUMENT_INFO_OBSERVER__=true;
const resolve=(value)=>{try{return value?new URL(value,document.baseURI).href:null;}catch{return null;}};
const unique=(items)=>items.filter((item,index)=>item&&items.indexOf(item)===index);
const getIconCandidates=()=>{
  const links=Array.from(document.querySelectorAll('link[rel]')).filter((link)=>{
    const rel=(link.getAttribute('rel')||'').toLowerCase().split(/\s+/);
    return rel.includes('icon')||rel.includes('shortcut')||rel.includes('apple-touch-icon');
  });
  const hrefOf=(link)=>resolve(link&&link.getAttribute('href'));
  const rank=(link)=>{
    const rel=(link.getAttribute('rel')||'').toLowerCase();
    const type=(link.getAttribute('type')||'').toLowerCase();
    const href=(link.getAttribute('href')||'').toLowerCase();
    if(href.endsWith('.ico')||type.includes('icon'))return 0;
    if(rel.includes('shortcut'))return 1;
    if(!href.endsWith('.svg')&&!type.includes('svg'))return 2;
    return 3;
  };
  const explicit=links.sort((a,b)=>rank(a)-rank(b)).map(hrefOf);
  const fallbacks=[resolve('favicon.ico')];
  if(location.protocol==='http:'||location.protocol==='https:')fallbacks.push(location.origin+'/favicon.ico');
  return unique(explicit.concat(fallbacks));
};
const post=()=>{
  const icons=getIconCandidates();
  if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'documentInfoChanged',title:document.title||'',icon:icons[0]||null,icons:icons});
  }
};
post();
new MutationObserver(post).observe(document.head||document.documentElement,{childList:true,subtree:true,attributes:true,attributeFilter:['href','rel']});
new MutationObserver(post).observe(document.querySelector('title')||document.documentElement,{childList:true,subtree:true,characterData:true});
})()";

        public ContainerPage()
        {
            this.InitializeComponent();
            this.Loaded += ContainerPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _owner = e.Parameter as MainPage ?? MainPage.Current;
        }

        public string CurrentUrl => RootWebView?.Source?.AbsoluteUri ?? _owner?.ContainerHomeUrl ?? SettingsManager.Instance.HomeUrl;

        private async void ContainerPage_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
            Navigate(_owner?.ContainerHomeUrl ?? SettingsManager.Instance.HomeUrl);
        }

        public void Navigate(string url)
        {
            if (!_isInitialized)
            {
                return;
            }

            _owner?.SetHostedPageLoading(url);
            RootWebView.Source = new Uri(url);
        }

        public async void SetHostedWindowActive(bool isActive)
        {
            try
            {
                await RunOnUiThreadAsync(async () =>
                {
                    if (RootWebView.CoreWebView2 == null)
                    {
                        return;
                    }

                    var script = isActive
                        ? "window.__WINUI_ON_WEB_APP_ACTIVE__=true;window.dispatchEvent(new Event('focus'));"
                        : "window.__WINUI_ON_WEB_APP_ACTIVE__=false;window.dispatchEvent(new Event('blur'));";

                    await RootWebView.CoreWebView2.ExecuteScriptAsync(script);
                });
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

            var userDataFolder = _owner?.ContainerWebViewDataFolder ?? SettingsManager.Instance.ActiveContainerWebViewDataFolder;
            Directory.CreateDirectory(userDataFolder);

            await WebViewInitializationLock.WaitAsync();
            try
            {
                await RunOnUiThreadAsync(async () =>
                {
                    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
                    await RootWebView.EnsureCoreWebView2Async();
                });
            }
            finally
            {
                WebViewInitializationLock.Release();
            }
            await RunOnUiThreadAsync(() =>
            {
                RootWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                RootWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                RootWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                RootWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                RootWebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
                RootWebView.CoreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;
                return Task.CompletedTask;
            });

            await RegisterHostTitleBarScriptAsync();
            await ApplyTransparentBackgroundAsync();

            _isInitialized = true;
        }

        private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            _lastNavigationUrl = args.Uri;
            LoadingOverlay.Visibility = Visibility.Visible;
            NavigationErrorOverlay.Visibility = Visibility.Collapsed;
            _owner?.SetHostedPageLoading(args.Uri);
            _owner?.SetHostedTitleBarVisible(false);
        }

        private async void CoreWebView2_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri))
                {
                    await RunOnUiThreadAsync(async () =>
                    {
                        if (_owner != null)
                        {
                            await _owner.PromptOpenExternalAsync(args.Uri);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] New window request failed: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void CoreWebView2_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                if (_owner == null)
                {
                    args.Cancel = true;
                    args.Handled = true;
                    return;
                }

                if (!await _owner.EnsureDownloadsAccessForDownloadAsync())
                {
                    args.Cancel = true;
                    args.Handled = true;
                    return;
                }

                var suggestedName = Path.GetFileName(args.ResultFilePath);
                args.ResultFilePath = await _owner.CreateDownloadFilePathAsync(suggestedName);
                args.Handled = true;
                _suppressNextNavigationFailureForDownload = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                NavigationErrorOverlay.Visibility = Visibility.Collapsed;
                _owner.SetHostedPageLoaded(true);
                _owner.AddDownload(args.DownloadOperation);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Download] Download start failed: {ex.Message}");
                args.Cancel = true;
                args.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void CoreWebView2_PermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                await RunOnUiThreadAsync(async () =>
                {
                    var origin = GetOrigin(args.Uri);
                    var permissionKind = args.PermissionKind.ToString();
                    var savedState = SettingsManager.Instance.GetSitePermissionState(origin, permissionKind);
                    if (savedState == "Allow" || savedState == "Deny")
                    {
                        args.State = savedState == "Allow"
                            ? CoreWebView2PermissionState.Allow
                            : CoreWebView2PermissionState.Deny;
                        args.SavesInProfile = false;
                        args.Handled = true;
                        return;
                    }

                    var dialog = new PermissionRequestDialog(
                        args.Uri,
                        permissionKind,
                        args.IsUserInitiated);
                    var result = await dialog.ShowAsync();
                    args.State = result == ContentDialogResult.Primary
                        ? CoreWebView2PermissionState.Allow
                        : CoreWebView2PermissionState.Deny;
                    args.SavesInProfile = dialog.RememberDecision;
                    if (dialog.RememberDecision)
                    {
                        SettingsManager.Instance.SetSitePermission(
                            origin,
                            permissionKind,
                            args.State == CoreWebView2PermissionState.Allow ? "Allow" : "Deny");
                    }
                    args.Handled = true;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb Permission] Permission request failed: {ex.Message}");
                args.State = CoreWebView2PermissionState.Deny;
            }
            finally
            {
                deferral.Complete();
            }
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

                    var iconCandidates = new List<Uri>();
                    if (root.TryGetProperty("icons", out var iconsValue)
                        && iconsValue.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in iconsValue.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String
                                && Uri.TryCreate(item.GetString(), UriKind.Absolute, out var candidateUri)
                                && !iconCandidates.Any(existing => existing.AbsoluteUri == candidateUri.AbsoluteUri))
                            {
                                iconCandidates.Add(candidateUri);
                            }
                        }
                    }

                    if (iconUri != null
                        && !iconCandidates.Any(existing => existing.AbsoluteUri == iconUri.AbsoluteUri))
                    {
                        iconCandidates.Insert(0, iconUri);
                    }

                    _owner?.UpdateHostedDocumentInfo(title, iconUri, iconCandidates);
                    return;
                }

                if (type.GetString() == "titleBarChanged"
                    && root.TryGetProperty("visible", out var visible)
                    && (visible.ValueKind == JsonValueKind.True || visible.ValueKind == JsonValueKind.False))
                {
                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Host title bar visible = {visible.GetBoolean()}");
                    _owner?.SetHostedTitleBarVisible(visible.GetBoolean());
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
            LoadingOverlay.Visibility = Visibility.Collapsed;
            _owner?.SetHostedPageLoaded(args.IsSuccess);
            if (!args.IsSuccess)
            {
                if (_suppressNextNavigationFailureForDownload
                    || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    _suppressNextNavigationFailureForDownload = false;
                    NavigationErrorOverlay.Visibility = Visibility.Collapsed;
                    _owner?.SetHostedPageLoaded(true);
                    return;
                }

                _lastWebErrorStatus = args.WebErrorStatus;
                ShowNavigationError(args.WebErrorStatus);
                return;
            }

            _suppressNextNavigationFailureForDownload = false;
            NavigationErrorOverlay.Visibility = Visibility.Collapsed;
            await ApplyTransparentBackgroundAsync();
            await UpdateDocumentInfoAsync();
            await DetectHostTitleBarFromIndexAsync();
            await DetectHostTitleBarAsync();
            SetHostedWindowActive(_owner?.IsWindowActive ?? true);
        }

        private void ShowNavigationError(CoreWebView2WebErrorStatus errorStatus)
        {
            NavigationErrorOverlay.Visibility = Visibility.Visible;
            NavigationErrorUrlText.Text = _lastNavigationUrl;
            NavigationErrorMessage.Text = string.Format(
                GetResourceString("NavigationErrorMessageFormat"),
                GetNavigationErrorText(errorStatus));
        }

        private string GetNavigationErrorText(CoreWebView2WebErrorStatus errorStatus)
        {
            var key = "NavigationError_" + errorStatus;
            var value = GetResourceString(key);
            return value == key ? errorStatus.ToString() : value;
        }

        private string GetResourceString(string key)
        {
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }

        private void RetryNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_lastNavigationUrl))
            {
                Navigate(_lastNavigationUrl);
            }
        }

        private void CopyNavigationUrlButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastNavigationUrl))
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(_lastNavigationUrl);
            Clipboard.SetContent(dataPackage);
        }

        private async void OpenNavigationExternalButton_Click(object sender, RoutedEventArgs e)
        {
            if (_owner != null)
            {
                await _owner.PromptOpenExternalAsync(_lastNavigationUrl);
            }
        }

        private async Task DetectHostTitleBarFromIndexAsync()
        {
            try
            {
                var source = await GetWebViewSourceAsync();
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

                await RunOnUiThreadAsync(async () =>
                {
                    await RegisterHostTitleBarScriptCoreAsync();
                    _owner?.SetHostedTitleBarVisible(hasWindowControlsOverlay);

                    if (RootWebView.CoreWebView2 != null)
                    {
                        await RootWebView.CoreWebView2.ExecuteScriptAsync(
                            $"window.__WINUI_ON_WEB_SET_WCO_VISIBLE__&&window.__WINUI_ON_WEB_SET_WCO_VISIBLE__({hasWindowControlsOverlay.ToString().ToLowerInvariant()});");
                    }
                    SetHostedWindowActive(_owner?.IsWindowActive ?? true);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest detection failed: {ex.Message}");
            }
        }

        private Task<string?> GetWebViewSourceAsync()
        {
            return RunOnUiThreadAsync(() =>
            {
                return Task.FromResult(RootWebView.CoreWebView2?.Source);
            });
        }

        private async Task RunOnUiThreadAsync(Func<Task> action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                await action();
                return;
            }

            var completion = new TaskCompletionSource<bool>();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    await action();
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            await completion.Task;
        }

        private async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                return await action();
            }

            var completion = new TaskCompletionSource<T>();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return await completion.Task;
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

        private static string GetOrigin(string uriText)
        {
            if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }

            return uriText;
        }

        private async Task RegisterHostTitleBarScriptAsync()
        {
            await RunOnUiThreadAsync(RegisterHostTitleBarScriptCoreAsync);
        }

        private async Task RegisterHostTitleBarScriptCoreAsync()
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
            await RunOnUiThreadAsync(async () =>
            {
                if (RootWebView.CoreWebView2 == null)
                {
                    return;
                }

                await RootWebView.CoreWebView2.ExecuteScriptAsync("window.__WINUI_ON_WEB_DETECT_MANIFEST_TITLEBAR__&&window.__WINUI_ON_WEB_DETECT_MANIFEST_TITLEBAR__();window.__WINUI_ON_WEB_DETECT_TITLEBAR__&&window.__WINUI_ON_WEB_DETECT_TITLEBAR__();");
            });
        }

        private async Task ApplyTransparentBackgroundAsync()
        {
            await RunOnUiThreadAsync(async () =>
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
            });
        }

        private async Task UpdateDocumentInfoAsync()
        {
            await RunOnUiThreadAsync(async () =>
            {
                if (RootWebView.CoreWebView2 == null)
                {
                    return;
                }

                await RootWebView.CoreWebView2.ExecuteScriptAsync(DocumentInfoScript);
            });
        }

        private string GetTransparentWebViewScript()
        {
            var css = new StringBuilder();
            string currentHost = "";
            if (Uri.TryCreate(RootWebView?.Source?.AbsoluteUri, UriKind.Absolute, out var currentUri))
            {
                currentHost = currentUri.Host;
            }

            foreach (var rule in SettingsManager.Instance.TransparentCssInjectionRules)
            {
                if (!TransparentCssRule.IsBuiltInId(rule.Id)
                    && SettingsManager.Instance.UsePerSiteTransparentCss
                    && !string.IsNullOrWhiteSpace(rule.Host)
                    && !rule.Host.Equals(currentHost, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(rule.Selector) && !string.IsNullOrWhiteSpace(rule.Css))
                {
                    css.Append(rule.Selector).Append('{').Append(rule.Css).Append('}');
                }
            }

            var cssJson = ToJavaScriptStringLiteral(css.ToString());
            var themeClass = AppThemeManager.GetIsDarkTheme() ? "theme-dark" : "theme-light";
            var themeClassJson = ToJavaScriptStringLiteral(themeClass);
            return $@"(()=>{{window.__WINUI_ON_WEB_UWP_APP__=true;const root=document.documentElement;if(root){{root.classList.add('winui-webview-host');root.classList.remove('theme-dark','theme-light');root.classList.add({themeClassJson});}}const id='winui-on-web-transparent-background';let style=document.getElementById(id);if(!style){{style=document.createElement('style');style.id=id;(document.head||root||document.documentElement).appendChild(style);}}style.textContent={cssJson};}})()";
        }

        private static string ToJavaScriptStringLiteral(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        builder.Append(ch);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}
