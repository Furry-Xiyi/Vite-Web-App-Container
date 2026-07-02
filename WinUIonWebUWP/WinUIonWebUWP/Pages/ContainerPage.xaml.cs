using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
using Windows.System;
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
        private string _transparentScriptId = "";
        private string _transparentScript = "";
        private bool _hostTitleBarScriptRegistered;
        private bool _suppressNextNavigationFailureForDownload;
        private string _lastNavigationUrl = "";
        private CoreWebView2WebErrorStatus _lastWebErrorStatus = CoreWebView2WebErrorStatus.Unknown;
        private int _lastHttpStatusCode;
        private double _hostTitleBarLeftInset;
        private double _hostTitleBarRightInset = 48;
        private double _hostTitleBarHeight = 32;
        private string _hostedAppTheme = "System";
        private readonly ObservableCollection<string> _launcherUrlSuggestions = new ObservableCollection<string>();
        private bool _isInitializingLauncherViteSettings = true;

        private const string HostTitleBarScript = @"(()=>{
window.__WINUI_ON_WEB_UWP_APP__=true;
const addHostClass=()=>{
  if(document.documentElement)document.documentElement.classList.add('winui-webview-host');
};
addHostClass();
if(window.__WINUI_ON_WEB_TITLEBAR_BRIDGE__){
  window.__WINUI_ON_WEB_APP_ACTIVE__=true;
  if(typeof window.__WINUI_ON_WEB_DETECT_TITLEBAR__==='function')window.__WINUI_ON_WEB_DETECT_TITLEBAR__();
  return;
}
window.__WINUI_ON_WEB_TITLEBAR_BRIDGE__=true;
window.__WINUI_ON_WEB_APP_ACTIVE__=true;
const clampNumber=(value,fallback)=>{
  const number=Number(value);
  return Number.isFinite(number)?Math.max(0,number):fallback;
};
const hostGeometry=window.__WINUI_ON_WEB_TITLEBAR_GEOMETRY__||{leftInset:0,rightInset:180,height:32};
window.__WINUI_ON_WEB_TITLEBAR_GEOMETRY__=hostGeometry;
let envPolyfillStyle=null;
let manifestDetectionPending=true;
let manifestDetectionInFlight=false;
let lastManifestHref='';
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
    const x=clampNumber(hostGeometry.leftInset,0);
    const rightInset=clampNumber(hostGeometry.rightInset,180);
    const height=clampNumber(hostGeometry.height,32)||32;
    const width=Math.max(0,window.innerWidth-x-rightInset);
    return {x,y:0,width,height,top:0,left:x,right:x+width,bottom:height};
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
  setTitleBarCssGeometry();
  scheduleTitleBarLayoutRefresh();
  const event=new Event('geometrychange');
  Object.defineProperty(event,'titlebarAreaRect',{value:overlay.getTitlebarAreaRect()});
  listeners.forEach(listener=>listener.call(overlay,event));
});
window.addEventListener('scroll',()=>scheduleTitleBarLayoutRefresh(),true);
const postDebug=(message)=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'debug',message});
};
const dispatchGeometryChange=()=>{
  const event=new Event('geometrychange');
  Object.defineProperty(event,'titlebarAreaRect',{value:overlay.getTitlebarAreaRect()});
  listeners.forEach(listener=>listener.call(overlay,event));
};
const setTitleBarCssGeometry=()=>{
  const rect=overlay.getTitlebarAreaRect();
  const root=document.documentElement;
  if(!root)return;
  root.classList.toggle('winui-window-controls-overlay-visible',overlay.visible);
  root.style.setProperty('--winui-titlebar-x',rect.x+'px');
  root.style.setProperty('--winui-titlebar-width',rect.width+'px');
  root.style.setProperty('--winui-titlebar-height',rect.height+'px');
  root.style.setProperty('--winui-titlebar-right-inset',Math.max(0,window.innerWidth-rect.right)+'px');
  ensureEnvPolyfillStyle();
};
const ensureEnvPolyfillStyle=()=>{
  if(envPolyfillStyle||!document.head)return;
  envPolyfillStyle=document.createElement('style');
  envPolyfillStyle.id='winui-window-controls-overlay-polyfill';
  envPolyfillStyle.textContent=`
.winui-window-controls-overlay-visible .winui-titlebar-region,
.winui-hosted-titlebar-visible .winui-titlebar-region,
.winui-window-controls-overlay-visible body > .drag:first-child {
  app-region: drag !important;
  -webkit-app-region: drag !important;
}
.winui-window-controls-overlay-visible .winui-titlebar-no-drag,
.winui-hosted-titlebar-visible .winui-titlebar-no-drag {
  app-region: no-drag !important;
  -webkit-app-region: no-drag !important;
}
.winui-window-controls-overlay-visible body > header:first-of-type {
  left: var(--winui-titlebar-x) !important;
  width: var(--winui-titlebar-width) !important;
}
.winui-window-controls-overlay-visible body > .drag:first-child {
  height: var(--winui-titlebar-height) !important;
}
.winui-window-controls-overlay-visible.narrow body > header:first-of-type,
.winui-window-controls-overlay-visible body.narrow > header:first-of-type {
  top: var(--winui-titlebar-height) !important;
  left: 0 !important;
  width: 100% !important;
}
`;
  document.head.appendChild(envPolyfillStyle);
};
const postTitleBarState=(visible,height)=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'titleBarChanged',visible,height});
};
let titleBarStateFrame=0;
let pendingTitleBarState=null;
let titleBarDetectionFrame=0;
let titleBarHideTimer=0;
let hostThemeFrame=0;
let stableHostTheme=null;
let pendingHostTheme=null;
let pendingHostThemeCount=0;
let lastPostedHostTheme='';
const titleBarHideDelayMs=220;
const titleBarLoadingHideDelayMs=900;
const getTitleBarHideDelay=()=>document.readyState==='complete'?titleBarHideDelayMs:titleBarLoadingHideDelayMs;
const scheduleTitleBarState=(visible,height)=>{
  pendingTitleBarState={visible,height};
  if(titleBarStateFrame)return;
  titleBarStateFrame=requestAnimationFrame(()=>{
    titleBarStateFrame=0;
    if(!pendingTitleBarState)return;
    const state=pendingTitleBarState;
    pendingTitleBarState=null;
    if(window.__WINUI_ON_WEB_HOST_TITLEBAR_VISIBLE__===state.visible&&window.__WINUI_ON_WEB_HOST_TITLEBAR_HEIGHT__===state.height)return;
    window.__WINUI_ON_WEB_HOST_TITLEBAR_VISIBLE__=state.visible;
    window.__WINUI_ON_WEB_HOST_TITLEBAR_HEIGHT__=state.height;
    postDebug('DOM titlebar visible -> '+state.visible+', height -> '+state.height);
    postTitleBarState(state.visible,state.height);
  });
};
const interactiveSelector='button,input,textarea,select,a[href],summary,[contenteditable],[role=""button""],[role=""link""],[role=""tab""],[role=""option""],[role=""switch""],[role=""checkbox""],[role=""textbox""],[role=""searchbox""],[role=""combobox""],[role=""menuitem""]';
const broadInteractiveSelector='[aria-haspopup],[aria-expanded],[tabindex],[data-e2e],[data-click],[data-testid],[onclick]';
let interactiveRectsFrame=0;
let lastInteractiveRectsJson='';
let titleBarLayoutRefreshFrame=0;
let titleBarResizeObserver=null;
let documentResizeObserver=null;
let observedTitleBarElement=null;
const getAppRegion=(element)=>{
  const style=getComputedStyle(element);
  return (style.getPropertyValue('app-region')||style.getPropertyValue('-webkit-app-region')||'').trim().toLowerCase();
};
const isLikelyInteractiveElement=(element)=>{
  if(element.matches(interactiveSelector))return true;
  const style=getComputedStyle(element);
  return element.matches(broadInteractiveSelector)||style.cursor==='pointer'||typeof element.onclick==='function';
};
const isButtonSizedTitlebarElement=(element)=>{
  const rects=Array.from(element.getClientRects());
  if(rects.length===0)return false;
  return rects.some((rect)=>{
    const width=rect.width;
    const height=rect.height;
    return width>=12&&height>=12&&width<=220&&height<=64;
  });
};
const isInteractiveElement=(element)=>{
  if(!element||element.disabled)return false;
  if(element.matches('input[type=""hidden""],[tabindex=""-1""]'))return false;
  if(element.hasAttribute('tabindex')){
    const tabIndex=Number(element.getAttribute('tabindex'));
    if(Number.isFinite(tabIndex)&&tabIndex<0)return false;
  }
  const explicitNoDrag=getAppRegion(element)==='no-drag';
  if(explicitNoDrag&&!isLikelyInteractiveElement(element))return false;
  return explicitNoDrag||element.matches(interactiveSelector)||(isLikelyInteractiveElement(element)&&isButtonSizedTitlebarElement(element));
};
const titleBarRegionClass='winui-titlebar-region';
const titleBarNoDragClass='winui-titlebar-no-drag';
let markedTitleBarElement=null;
let markedNoDragElements=[];
const getTitleBarInteractionRects=()=>{
  const overlayRect=overlay.getTitlebarAreaRect();
  const fullHeight=Math.max(32,clampNumber(hostGeometry.height,32)||32);
  const topButtonHeight=Math.min(32,fullHeight);
  const topRight=Math.max(overlayRect.left,Math.min(window.innerWidth,overlayRect.right));
  const rects=[];
  if(topRight>overlayRect.left&&topButtonHeight>0){
    rects.push({left:overlayRect.left,top:0,right:topRight,bottom:topButtonHeight});
  }
  if(fullHeight>topButtonHeight){
    rects.push({left:0,top:topButtonHeight,right:window.innerWidth,bottom:fullHeight});
  }
  return rects;
};
const intersectsAnyTitleBarInteractionRect=(clientRect)=>{
  return getTitleBarInteractionRects().some((titleRect)=>clientRect.left<titleRect.right&&clientRect.right>titleRect.left&&clientRect.top<titleRect.bottom&&clientRect.bottom>titleRect.top);
};
const getInteractiveCandidates=(scope=document)=>{
  const titleRects=getTitleBarInteractionRects();
  const intersectsTitleRects=(element)=>Array.from(element.getClientRects()).some((rect)=>titleRects.some((titleRect)=>rect.left<titleRect.right&&rect.right>titleRect.left&&rect.top<titleRect.bottom&&rect.bottom>titleRect.top));
  const explicitElements=Array.from(scope.querySelectorAll(interactiveSelector)).filter(intersectsTitleRects);
  const explicitSet=new Set(explicitElements);
  const broadElements=[];
  for(const element of Array.from(scope.querySelectorAll(broadInteractiveSelector))){
    if(explicitSet.has(element))continue;
    if(!intersectsTitleRects(element))continue;
    if(isButtonSizedTitlebarElement(element)||getAppRegion(element)==='no-drag')broadElements.push(element);
    if(broadElements.length>=96)break;
  }
  return Array.from(new Set(explicitElements.concat(broadElements)));
};
const clearMarkedTitleBarRegions=()=>{
  if(markedTitleBarElement)markedTitleBarElement.classList.remove(titleBarRegionClass);
  markedTitleBarElement=null;
  for(const element of markedNoDragElements)element.classList.remove(titleBarNoDragClass);
  markedNoDragElements=[];
};
const markTitleBarRegions=(titleBar)=>{
  if(!titleBar){
    clearMarkedTitleBarRegions();
    return;
  }
  if(markedTitleBarElement&&markedTitleBarElement!==titleBar){
    markedTitleBarElement.classList.remove(titleBarRegionClass);
  }
  markedTitleBarElement=titleBar;
  if(!titleBar.classList.contains(titleBarRegionClass))titleBar.classList.add(titleBarRegionClass);
  const elements=getInteractiveCandidates(document);
  const nextNoDragElements=[];
  for(const element of elements){
    if(!isInteractiveElement(element))continue;
    const style=getComputedStyle(element);
    if(style.display==='none'||style.visibility==='hidden'||style.pointerEvents==='none')continue;
    const intersectsTitleBar=Array.from(element.getClientRects()).some(intersectsAnyTitleBarInteractionRect);
    if(!intersectsTitleBar)continue;
    nextNoDragElements.push(element);
    if(nextNoDragElements.length>=64)break;
  }
  const nextNoDragSet=new Set(nextNoDragElements);
  for(const element of markedNoDragElements){
    if(!nextNoDragSet.has(element))element.classList.remove(titleBarNoDragClass);
  }
  for(const element of nextNoDragElements){
    if(!element.classList.contains(titleBarNoDragClass))element.classList.add(titleBarNoDragClass);
  }
  markedNoDragElements=nextNoDragElements;
};
const expandInteractiveRectForTitleBar=(clientRect,titleRect)=>{
  const left=clientRect.left;
  const right=clientRect.right;
  let top=clientRect.top;
  let bottom=clientRect.bottom;
  if(clientRect.top<titleRect.bottom&&clientRect.bottom>titleRect.top){
    top=titleRect.top;
    bottom=titleRect.bottom;
  }
  return {left,top,right,bottom};
};
const collectInteractiveRects=()=>{
  const titleRects=getTitleBarInteractionRects();
  const elements=getInteractiveCandidates(document);
  const rects=[];
  for(const element of elements){
    if(!isInteractiveElement(element))continue;
    const style=getComputedStyle(element);
    if(style.display==='none'||style.visibility==='hidden'||style.pointerEvents==='none')continue;
    for(const clientRect of Array.from(element.getClientRects())){
      for(const titleRect of titleRects){
        const expandedRect=expandInteractiveRectForTitleBar(clientRect,titleRect);
        const left=Math.max(titleRect.left,expandedRect.left);
        const top=Math.max(titleRect.top,expandedRect.top);
        const right=Math.min(titleRect.right,expandedRect.right);
        const bottom=Math.min(titleRect.bottom,expandedRect.bottom);
        if(right-left>=2&&bottom-top>=2){
          rects.push({left,top,width:right-left,height:bottom-top});
        }
      }
      if(rects.length>=32)break;
    }
    if(rects.length>=32)break;
  }
  return rects;
};
const postInteractiveRects=()=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  const rects=collectInteractiveRects();
  const json=JSON.stringify(rects);
  if(json===lastInteractiveRectsJson)return;
  lastInteractiveRectsJson=json;
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'titleBarInteractiveRectsChanged',rects});
};
const scheduleInteractiveRects=()=>{
  if(interactiveRectsFrame)return;
  interactiveRectsFrame=requestAnimationFrame(()=>{
    interactiveRectsFrame=0;
    postInteractiveRects();
  });
};
const scheduleRealtimeTitleBarRefresh=()=>{
  scheduleTitleBarLayoutRefresh();
  scheduleInteractiveRects();
  setTimeout(scheduleInteractiveRects,50);
  setTimeout(scheduleInteractiveRects,180);
};
window.__WINUI_ON_WEB_REFRESH_TITLEBAR_EXCLUSIONS__=()=>{
  scheduleRealtimeTitleBarRefresh();
};
let titleBarAppearanceFrame=0;
let lastTitleBarAppearanceJson='';
const parseCssColor=(value)=>{
  if(!value)return null;
  const match=String(value).match(/rgba?\(([^)]+)\)/i);
  if(!match)return null;
  const parts=match[1].split(',').map((part)=>Number.parseFloat(part.trim()));
  if(parts.length<3||parts.some((part,index)=>index<3&&!Number.isFinite(part)))return null;
  const alpha=parts.length>=4&&Number.isFinite(parts[3])?Math.max(0,Math.min(1,parts[3])):1;
  return {
    r:Math.max(0,Math.min(255,Math.round(parts[0]))),
    g:Math.max(0,Math.min(255,Math.round(parts[1]))),
    b:Math.max(0,Math.min(255,Math.round(parts[2]))),
    a:alpha
  };
};
const blendColor=(fg,bg)=>{
  if(!fg)return bg;
  if(fg.a>=0.98)return fg;
  bg=bg||{r:255,g:255,b:255,a:1};
  const alpha=fg.a+(bg.a||1)*(1-fg.a);
  if(alpha<=0)return {r:255,g:255,b:255,a:1};
  return {
    r:Math.round((fg.r*fg.a+bg.r*(bg.a||1)*(1-fg.a))/alpha),
    g:Math.round((fg.g*fg.a+bg.g*(bg.a||1)*(1-fg.a))/alpha),
    b:Math.round((fg.b*fg.a+bg.b*(bg.a||1)*(1-fg.a))/alpha),
    a:alpha
  };
};
const colorLuminance=(color)=>{
  if(!color)return 1;
  const linear=(component)=>{
    const value=component/255;
    return value<=0.03928?value/12.92:Math.pow((value+0.055)/1.055,2.4);
  };
  return 0.2126*linear(color.r)+0.7152*linear(color.g)+0.0722*linear(color.b);
};
const contrastRatio=(a,b)=>{
  const light=Math.max(colorLuminance(a),colorLuminance(b));
  const dark=Math.min(colorLuminance(a),colorLuminance(b));
  return (light+0.05)/(dark+0.05);
};
const bestContrastingForeground=(background)=>{
  const white={r:255,g:255,b:255,a:1};
  const black={r:0,g:0,b:0,a:1};
  return contrastRatio(white,background)>=contrastRatio(black,background)?white:black;
};
let lastButtonForegroundMode='';
const buttonForegroundForBackground=(background)=>{
  const luminance=colorLuminance(background);
  if(lastButtonForegroundMode==='light'&&luminance<0.52)return {r:255,g:255,b:255,a:1,mode:'light'};
  if(lastButtonForegroundMode==='dark'&&luminance>0.48)return {r:0,g:0,b:0,a:1,mode:'dark'};
  return luminance<0.50
    ? {r:255,g:255,b:255,a:1,mode:'light'}
    : {r:0,g:0,b:0,a:1,mode:'dark'};
};
const buttonForegroundForTheme=(theme)=>theme==='dark'
  ? {r:255,g:255,b:255,a:1,mode:'light'}
  : {r:0,g:0,b:0,a:1,mode:'dark'};
const toHexColor=(color)=>{
  if(!color)return null;
  const part=(value)=>Math.max(0,Math.min(255,Math.round(value))).toString(16).padStart(2,'0');
  return '#'+part(color.r)+part(color.g)+part(color.b);
};
const resolveColorSchemeTokens=(tokens)=>{
  const normalized=(tokens||[]).filter((token)=>token==='dark'||token==='light');
  if(normalized.length===1)return normalized[0];
  if(normalized.includes('dark')&&normalized.includes('light'))return getPreferredColorSchemeTheme();
  return null;
};
const getExplicitTheme=()=>{
  const root=document.documentElement;
  const body=document.body;
  const themeTokens=[
    root&&root.getAttribute('data-theme'),
    root&&root.getAttribute('theme'),
    root&&root.getAttribute('data-color-mode'),
    root&&root.getAttribute('data-prefers-color-scheme'),
    root&&root.className,
    body&&body.getAttribute('data-theme'),
    body&&body.getAttribute('theme'),
    body&&body.getAttribute('data-color-mode'),
    body&&body.className
  ].filter(Boolean).join(' ').toLowerCase();
  if(/(^|[\s_-])(dark|night|black|dim)([\s_-]|$)/.test(themeTokens)||themeTokens.includes('darkmode'))return 'dark';
  if(/(^|[\s_-])(light|day|white)([\s_-]|$)/.test(themeTokens)||themeTokens.includes('lightmode'))return 'light';
  const colorSchemeTokens=[
    getComputedStyle(root||document.documentElement).colorScheme,
    body?getComputedStyle(body).colorScheme:null,
    (document.querySelector('meta[name=color-scheme]')||{}).content
  ].filter(Boolean).join(' ').trim().toLowerCase().split(/\s+/);
  return resolveColorSchemeTokens(colorSchemeTokens);
};
const getPreferredColorSchemeTheme=()=>{
  try{
    if(window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches)return 'dark';
  }catch{}
  return 'light';
};
const getPageTheme=()=>{
  const explicitTheme=getExplicitTheme();
  if(explicitTheme)return explicitTheme;
  const rootBg=getRootBackground();
  if(rootBg)return colorLuminance(rootBg)<0.48?'dark':'light';
  return getPreferredColorSchemeTheme();
};
const postStableHostTheme=(theme)=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  if(theme!=='dark'&&theme!=='light')return;
  if(theme===lastPostedHostTheme)return;
  lastPostedHostTheme=theme;
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'hostThemeChanged',theme});
};
const scheduleHostThemeSync=()=>{
  if(hostThemeFrame)return;
  hostThemeFrame=requestAnimationFrame(()=>{
    hostThemeFrame=0;
    const nextTheme=getPageTheme();
    if(nextTheme!==pendingHostTheme){
      pendingHostTheme=nextTheme;
      pendingHostThemeCount=1;
      requestAnimationFrame(scheduleHostThemeSync);
      return;
    }
    pendingHostThemeCount+=1;
    if(pendingHostThemeCount<3){
      requestAnimationFrame(scheduleHostThemeSync);
      return;
    }
    if(nextTheme!==stableHostTheme){
      stableHostTheme=nextTheme;
      lastTitleBarAppearanceJson='';
      postStableHostTheme(nextTheme);
      scheduleTitleBarLayoutRefresh();
    }
  });
};
const getEffectiveBackground=(element,fallback)=>{
  let color=null;
  for(let current=element;current;current=current.parentElement){
    const next=blendColor(parseCssColor(getComputedStyle(current).backgroundColor),color);
    if(next&&next.a>0.05)color=next;
    if(color&&color.a>0.96)return color;
  }
  for(const selector of ['body','html']){
    const target=document.querySelector(selector);
    if(!target)continue;
    const next=blendColor(parseCssColor(getComputedStyle(target).backgroundColor),color);
    if(next&&next.a>0.05)color=next;
  }
  const themeColor=(document.querySelector('meta[name=theme-color]')||{}).content;
  if(!color&&themeColor){
    const probe=document.createElement('span');
    probe.style.color=themeColor;
    (document.body||document.documentElement).appendChild(probe);
    color=parseCssColor(getComputedStyle(probe).color);
    probe.remove();
  }
  return blendColor(color,fallback);
};
const hasPaintedBackground=(element)=>{
  for(let current=element;current;current=current.parentElement){
    const bg=parseCssColor(getComputedStyle(current).backgroundColor);
    if(bg&&bg.a>0.05)return true;
  }
  for(const selector of ['body','html']){
    const target=document.querySelector(selector);
    if(!target)continue;
    const bg=parseCssColor(getComputedStyle(target).backgroundColor);
    if(bg&&bg.a>0.05)return true;
  }
  return false;
};
const getRootBackground=()=>{
  const bodyBg=document.body?parseCssColor(getComputedStyle(document.body).backgroundColor):null;
  if(bodyBg&&bodyBg.a>0.05)return blendColor(bodyBg,{r:255,g:255,b:255,a:1});
  const rootBg=parseCssColor(getComputedStyle(document.documentElement).backgroundColor);
  if(rootBg&&rootBg.a>0.05)return blendColor(rootBg,{r:255,g:255,b:255,a:1});
  return null;
};
const getPointBackground=(x,y,fallback)=>{
  const elements=document.elementsFromPoint?document.elementsFromPoint(x,y):[];
  let transparentCandidate=null;
  for(const element of elements){
    if(!element||element===document.documentElement)continue;
    const style=getComputedStyle(element);
    if(style.display==='none'||style.visibility==='hidden'||Number(style.opacity)===0)continue;
    const bg=parseCssColor(style.backgroundColor);
    if(bg&&bg.a>0.05){
      return blendColor(bg,fallback);
    }
    transparentCandidate=transparentCandidate||element;
  }
  if(transparentCandidate){
    const inherited=getEffectiveBackground(transparentCandidate,null);
    if(inherited&&inherited.a>0.05)return inherited;
  }
  return getRootBackground()||fallback;
};
const getButtonUnderlayBackground=(theme)=>{
  const lightFallback={r:255,g:255,b:255,a:1};
  const darkFallback={r:0,g:0,b:0,a:1};
  const rootBg=getRootBackground();
  const fallback=rootBg||(theme==='dark'?darkFallback:lightFallback);
  const height=Math.max(1,hostGeometry.height||32);
  const y=Math.max(1,Math.min(window.innerHeight-1,height/2));
  const rightInset=Math.max(32,clampNumber(hostGeometry.rightInset,48));
  const left=Math.max(1,window.innerWidth-rightInset);
  const right=Math.max(left,window.innerWidth-1);
  const xs=[
    left+8,
    left+Math.max(16,rightInset*0.28),
    left+Math.max(24,rightInset*0.52),
    right-Math.max(12,rightInset*0.22),
    right-8
  ].map((value)=>Math.max(1,Math.min(window.innerWidth-1,value)))
    .filter((value,index,array)=>array.indexOf(value)===index);
  const samples=[];
  for(const x of xs){
    const color=getPointBackground(x,y,fallback);
    if(color)samples.push(color);
  }
  if(samples.length===0)return fallback;
  samples.sort((a,b)=>colorLuminance(a)-colorLuminance(b));
  return samples[Math.floor(samples.length/2)];
};
const getSampledTitleBarBackground=(titleBar,theme)=>{
  const darkFallback={r:0,g:0,b:0,a:1};
  const lightFallback={r:255,g:255,b:255,a:1};
  const rootBg=getEffectiveBackground(document.body||document.documentElement,null);
  const inferredTheme=theme||(rootBg&&colorLuminance(rootBg)<0.48?'dark':null);
  const fallback=inferredTheme==='dark'?darkFallback:lightFallback;
  const rect=overlay.getTitlebarAreaRect();
  const height=Math.max(1,rect.height||hostGeometry.height||32);
  const y=Math.max(1,Math.min(window.innerHeight-1,height/2));
  const xs=[
    Math.max(1,rect.left+12),
    Math.max(1,rect.left+rect.width/2),
    Math.max(1,rect.left+Math.max(24,rect.width*0.82)),
    Math.max(1,window.innerWidth-Math.max(12,hostGeometry.rightInset/2)),
    Math.max(1,window.innerWidth-12)
  ].filter((value,index,array)=>value<window.innerWidth&&array.indexOf(value)===index);
  const samples=[];
  for(const x of xs){
    const color=getPointBackground(x,y,fallback);
    if(color)samples.push(color);
  }
  if(titleBar){
    const color=getEffectiveBackground(titleBar,null);
    if(color&&color.a>0.05)samples.push(color);
  }
  if(samples.length===0)return fallback;
  samples.sort((a,b)=>colorLuminance(a)-colorLuminance(b));
  return samples[Math.floor(samples.length/2)];
};
const getEffectiveForeground=(titleBar)=>{
  const candidates=[];
  if(titleBar)candidates.push(titleBar);
  candidates.push(document.body,document.documentElement);
  for(const element of candidates){
    if(!element)continue;
    const color=parseCssColor(getComputedStyle(element).color);
    if(color&&color.a>0.4)return blendColor(color,null);
  }
  return null;
};
const postTitleBarAppearance=(titleBar,visible)=>{
  if(!window.chrome||!window.chrome.webview||!window.chrome.webview.postMessage)return;
  if(!visible)return;
  const explicitTheme=getExplicitTheme();
  const pageTheme=explicitTheme||getPageTheme();
  const background=getButtonUnderlayBackground(pageTheme)||getSampledTitleBarBackground(titleBar,pageTheme);
  const theme=pageTheme||(background?(colorLuminance(background)<0.48?'dark':'light'):null);
  const transparentTitleBarBackground=theme&&(titleBar?!hasPaintedBackground(titleBar):!getRootBackground());
  const buttonForeground=transparentTitleBarBackground
    ? buttonForegroundForTheme(theme)
    : buttonForegroundForBackground(background);
  lastButtonForegroundMode=buttonForeground.mode;
  const payload={
    source:'WinUIonWeb',
    type:'titleBarAppearanceChanged',
    theme,
    foreground:toHexColor(buttonForeground),
    background:transparentTitleBarBackground?null:toHexColor(background)
  };
  const json=JSON.stringify(payload);
  if(json===lastTitleBarAppearanceJson)return;
  lastTitleBarAppearanceJson=json;
  window.chrome.webview.postMessage(payload);
};
const scheduleTitleBarAppearance=(titleBar,visible)=>{
  window.__WINUI_ON_WEB_LAST_TITLEBAR_ELEMENT__=visible?titleBar:null;
  window.__WINUI_ON_WEB_LAST_TITLEBAR_VISIBLE__=Boolean(visible);
  if(titleBarAppearanceFrame)return;
  titleBarAppearanceFrame=requestAnimationFrame(()=>{
    titleBarAppearanceFrame=0;
    postTitleBarAppearance(window.__WINUI_ON_WEB_LAST_TITLEBAR_ELEMENT__,window.__WINUI_ON_WEB_LAST_TITLEBAR_VISIBLE__);
  });
};
const commitTitleBarState=(visible,height,titleBar)=>{
  if(titleBarHideTimer){
    clearTimeout(titleBarHideTimer);
    titleBarHideTimer=0;
  }
  if(document.documentElement)document.documentElement.classList.toggle('winui-hosted-titlebar-visible',visible);
  markTitleBarRegions(visible?titleBar:null);
  observeTitleBarLayoutTargets(visible?titleBar:null);
  scheduleTitleBarState(visible,height);
  scheduleInteractiveRects();
  scheduleTitleBarAppearance(titleBar,visible);
};
const scheduleTitleBarDetection=()=>{
  if(titleBarDetectionFrame)return;
  titleBarDetectionFrame=requestAnimationFrame(()=>{
    titleBarDetectionFrame=0;
    detectTitleBar();
  });
};
const scheduleTitleBarLayoutRefresh=()=>{
  if(titleBarLayoutRefreshFrame)return;
  titleBarLayoutRefreshFrame=requestAnimationFrame(()=>{
    titleBarLayoutRefreshFrame=0;
    detectTitleBar();
    scheduleInteractiveRects();
    scheduleTitleBarAppearance(window.__WINUI_ON_WEB_LAST_TITLEBAR_ELEMENT__,window.__WINUI_ON_WEB_LAST_TITLEBAR_VISIBLE__);
  });
};
const observeTitleBarLayoutTargets=(titleBar)=>{
  if(!window.ResizeObserver)return;
  if(!titleBarResizeObserver)titleBarResizeObserver=new ResizeObserver(scheduleTitleBarLayoutRefresh);
  if(observedTitleBarElement===titleBar)return;
  observedTitleBarElement=titleBar||null;
  titleBarResizeObserver.disconnect();
  if(titleBar)titleBarResizeObserver.observe(titleBar);
};
window.__WINUI_ON_WEB_SET_TITLEBAR_GEOMETRY__=(geometry)=>{
  if(!geometry)return;
  hostGeometry.leftInset=clampNumber(geometry.leftInset,hostGeometry.leftInset);
  hostGeometry.rightInset=clampNumber(geometry.rightInset,hostGeometry.rightInset);
  hostGeometry.height=clampNumber(geometry.height,hostGeometry.height)||32;
  setTitleBarCssGeometry();
  dispatchGeometryChange();
  scheduleTitleBarLayoutRefresh();
};
const setOverlayVisible=(visible)=>{
  const next=Boolean(visible);
  if(overlay.visible===next)return;
  postDebug('windowControlsOverlay.visible -> '+next);
  overlay.visible=next;
  setTitleBarCssGeometry();
  dispatchGeometryChange();
  if(next){
    commitTitleBarState(true,hostGeometry.height||32,window.__WINUI_ON_WEB_LAST_TITLEBAR_ELEMENT__||findTitleBarElement());
  }
  requestAnimationFrame(scheduleTitleBarLayoutRefresh);
};
window.__WINUI_ON_WEB_SET_WCO_VISIBLE__=setOverlayVisible;
const fetchManifestJson=async(url)=>{
  const controller=typeof AbortController==='function'?new AbortController():null;
  const timer=controller?setTimeout(()=>controller.abort(),1200):0;
  try{
    const response=await fetch(url,{credentials:'same-origin',signal:controller?controller.signal:undefined});
    postDebug('Manifest fetch '+url+' -> '+response.status);
    if(!response.ok)return null;
    return await response.json();
  }finally{
    if(timer)clearTimeout(timer);
  }
};
const detectManifestTitleBar=async()=>{
  if(manifestDetectionInFlight)return;
  manifestDetectionInFlight=true;
  try{
  const link=document.querySelector('link[rel~=""manifest""]');
  if(!link||!link.href){
    postDebug('No manifest link found.');
  }else{
    lastManifestHref=link.href;
    postDebug('Manifest link found: '+link.href);
    setOverlayVisible(true);
    manifestDetectionPending=false;
    detectTitleBar();
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
      const manifest=await fetchManifestJson(url);
      if(!manifest)continue;
      const displayOverride=Array.isArray(manifest.display_override)?manifest.display_override:[];
      postDebug('Manifest display_override: '+JSON.stringify(displayOverride));
      const hasWindowControlsOverlay=displayOverride.includes('window-controls-overlay');
      setOverlayVisible(hasWindowControlsOverlay);
      manifestDetectionPending=false;
      detectTitleBar();
      return;
    }catch(error){
      postDebug('Manifest fetch failed '+url+': '+String(error));
    }
  }
  if(link&&link.href){
    postDebug('No usable manifest with window-controls-overlay was found. Falling back to manifest link presence.');
    setOverlayVisible(true);
    manifestDetectionPending=false;
    detectTitleBar();
    return;
  }
  if(/WinUIonWeb/i.test(location.pathname)){
    postDebug('No manifest link was found, but path looks like WinUIonWeb. Falling back to enabled overlay.');
    setOverlayVisible(true);
    manifestDetectionPending=false;
    detectTitleBar();
    return;
  }
  manifestDetectionPending=false;
  postDebug('No usable manifest with window-controls-overlay was found.');
  detectTitleBar();
  }finally{
    manifestDetectionInFlight=false;
  }
};
window.__WINUI_ON_WEB_DETECT_MANIFEST_TITLEBAR__=detectManifestTitleBar;
const detectTitleBar=(allowStableHide=false)=>{
  const titleBar=findTitleBarElement();
  let visible=overlay.visible;
  let height=hostGeometry.height;
  let hasVisibleTitleBarElement=false;
  if(titleBar){
    const style=getComputedStyle(titleBar);
    const rect=titleBar.getBoundingClientRect();
    hasVisibleTitleBarElement=style.display!=='none'&&style.visibility!=='hidden'&&titleBar.getClientRects().length>0;
    visible=hasVisibleTitleBarElement;
    if(visible)height=Math.max(32,Math.ceil(Math.max(rect.height||0,rect.bottom||0,hostGeometry.height||32)));
  }
  if(hasVisibleTitleBarElement){
    commitTitleBarState(true,height,titleBar);
    return;
  }
  if(!visible&&manifestDetectionPending&&window.__WINUI_ON_WEB_HOST_TITLEBAR_VISIBLE__===undefined){
    return;
  }
  if(!allowStableHide&&!visible&&window.__WINUI_ON_WEB_HOST_TITLEBAR_VISIBLE__===true){
    if(!titleBarHideTimer){
      titleBarHideTimer=setTimeout(()=>{
        titleBarHideTimer=0;
        detectTitleBar(true);
      },getTitleBarHideDelay());
    }
    return;
  }
  commitTitleBarState(visible,height,titleBar);
};
window.__WINUI_ON_WEB_DETECT_TITLEBAR__=detectTitleBar;
const findTitleBarElement=()=>{
  const explicit=document.querySelector('.win-titlebar,[data-winui-titlebar],[data-titlebar]');
  if(explicit)return explicit;
  if(!overlay.visible)return null;
  const candidates=Array.from(document.querySelectorAll('body > header:first-of-type,header,[role=""banner""],.titlebar,.title-bar,.toolbar,.appbar,.app-bar'));
  return candidates.find((element)=>{
    const style=getComputedStyle(element);
    if(style.display==='none'||style.visibility==='hidden')return false;
    const rect=element.getBoundingClientRect();
    return rect.height>=24&&rect.top<=hostGeometry.height+4&&rect.bottom>0;
  })||null;
};
const startDetecting=()=>{
  addHostClass();
  setTitleBarCssGeometry();
  scheduleHostThemeSync();
  detectManifestTitleBar();
  detectTitleBar();
  observeTitleBarLayoutTargets(window.__WINUI_ON_WEB_LAST_TITLEBAR_ELEMENT__||null);
  if(window.ResizeObserver&&!documentResizeObserver){
    documentResizeObserver=new ResizeObserver(()=>scheduleRealtimeTitleBarRefresh());
    if(document.documentElement)documentResizeObserver.observe(document.documentElement);
    if(document.body)documentResizeObserver.observe(document.body);
  }
  new MutationObserver(()=>{
    scheduleHostThemeSync();
    const manifestLink=document.querySelector('link[rel~=""manifest""]');
    if(manifestLink&&manifestLink.href&&manifestLink.href!==lastManifestHref){
      detectManifestTitleBar();
    }
    scheduleRealtimeTitleBarRefresh();
  }).observe(document.documentElement||document,{childList:true,subtree:true,attributes:true,attributeFilter:['class','style','hidden','disabled','type','href','rel','role','aria-haspopup','aria-expanded','tabindex','contenteditable','data-theme','theme','data-color-mode','data-prefers-color-scheme','content','data-winui-titlebar','data-titlebar','data-e2e','data-click','data-testid']});
  try{
    const colorSchemeQuery=window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)');
    if(colorSchemeQuery){
      const onColorSchemeChanged=()=>{scheduleHostThemeSync();scheduleTitleBarLayoutRefresh();};
      if(colorSchemeQuery.addEventListener)colorSchemeQuery.addEventListener('change',onColorSchemeChanged);
      else if(colorSchemeQuery.addListener)colorSchemeQuery.addListener(onColorSchemeChanged);
    }
  }catch{}
  window.addEventListener('load',()=>window.__WINUI_ON_WEB_REFRESH_TITLEBAR_EXCLUSIONS__&&window.__WINUI_ON_WEB_REFRESH_TITLEBAR_EXCLUSIONS__(),{once:true});
  window.addEventListener('transitionrun',scheduleTitleBarLayoutRefresh,true);
  window.addEventListener('transitionend',scheduleTitleBarLayoutRefresh,true);
  window.addEventListener('animationstart',scheduleTitleBarLayoutRefresh,true);
  window.addEventListener('animationiteration',scheduleTitleBarLayoutRefresh,true);
  window.addEventListener('animationend',scheduleTitleBarLayoutRefresh,true);
  window.addEventListener('pointerup',()=>setTimeout(scheduleTitleBarLayoutRefresh,80),true);
  window.addEventListener('click',()=>setTimeout(scheduleTitleBarLayoutRefresh,120),true);
  setTimeout(scheduleTitleBarLayoutRefresh,0);
  setTimeout(scheduleTitleBarLayoutRefresh,250);
  setTimeout(scheduleTitleBarLayoutRefresh,500);
  setInterval(scheduleTitleBarLayoutRefresh,500);
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
let lastManifestName='';
const getManifestUrl=()=>{
  const link=Array.from(document.querySelectorAll('link[rel]')).find((item)=>{
    const rel=(item.getAttribute('rel')||'').toLowerCase().split(/\s+/);
    return rel.includes('manifest');
  });
  return resolve(link&&link.getAttribute('href'));
};
const postManifest=async()=>{
  const manifestUrl=getManifestUrl();
  if(!manifestUrl)return;
  try{
    const response=await fetch(manifestUrl,{credentials:'same-origin'});
    if(!response.ok)return;
    const manifest=await response.json();
    const name=String(manifest.name||manifest.short_name||'').trim();
    if(!name||name===lastManifestName)return;
    lastManifestName=name;
    if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
      window.chrome.webview.postMessage({source:'WinUIonWeb',type:'manifestInfoChanged',name});
    }
  }catch{}
};
const post=()=>{
  const icons=getIconCandidates();
  if(window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage){
  window.chrome.webview.postMessage({source:'WinUIonWeb',type:'documentInfoChanged',title:document.title||'',icon:icons[0]||null,icons:icons});
  }
};
post();
postManifest();
new MutationObserver(()=>{post();postManifest();}).observe(document.head||document.documentElement,{childList:true,subtree:true,attributes:true,attributeFilter:['href','rel']});
new MutationObserver(post).observe(document.querySelector('title')||document.documentElement,{childList:true,subtree:true,characterData:true});
})()";

        public ContainerPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;
            LauncherUrlTextBox.ItemsSource = _launcherUrlSuggestions;
            this.Loaded += ContainerPage_Loaded;
            this.Unloaded += ContainerPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _owner = e.Parameter as MainPage ?? MainPage.Current;
        }

        public string CurrentUrl => RootWebView?.Source?.AbsoluteUri ?? _owner?.ContainerHomeUrl ?? SettingsManager.Instance.HomeUrl;

        private async void ContainerPage_Loaded(object sender, RoutedEventArgs e)
        {
            CoreWindow.GetForCurrentThread().Dispatcher.AcceleratorKeyActivated -= Dispatcher_AcceleratorKeyActivated;
            CoreWindow.GetForCurrentThread().Dispatcher.AcceleratorKeyActivated += Dispatcher_AcceleratorKeyActivated;

            if (IsLauncherContainer)
            {
                ShowLauncher();
                return;
            }

            LauncherPanel.Visibility = Visibility.Collapsed;
            RootWebView.Visibility = Visibility.Visible;
            await InitializeWebViewAsync();
            if (RootWebView.Source == null)
            {
                Navigate(_owner?.ContainerHomeUrl ?? SettingsManager.Instance.HomeUrl);
            }
        }

        private void ContainerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            CoreWindow.GetForCurrentThread().Dispatcher.AcceleratorKeyActivated -= Dispatcher_AcceleratorKeyActivated;
        }

        private void Dispatcher_AcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.EventType != CoreAcceleratorKeyEventType.KeyDown
                && args.EventType != CoreAcceleratorKeyEventType.SystemKeyDown)
            {
                return;
            }

            if (args.VirtualKey != VirtualKey.F12 || IsLauncherContainer)
            {
                return;
            }

            if (!IsDevToolsEnabledForCurrentContainer())
            {
                return;
            }

            args.Handled = true;
            OpenDevToolsWindow();
        }

        private bool IsLauncherContainer =>
            _owner != null && SettingsManager.Instance.IsDefaultContainer(_owner.ContainerId);

        private void ShowLauncher()
        {
            RootWebView.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            NavigationErrorOverlay.Visibility = Visibility.Collapsed;
            LauncherPanel.Visibility = Visibility.Visible;
            LoadLauncherViteDevServerSettings();
            HideLauncherUrlError();
            LauncherAddButton.IsEnabled = false;
            RefreshLauncherUrlSuggestions();
            _owner?.SetHostedPageLoaded(false);
            _owner?.SetHostedTitleBarVisible(false);
            _owner?.SetHostedTitleBarInteractiveRects(Array.Empty<MainPage.TitleBarInteractiveRect>());
        }

        private void LauncherUrlTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ShowLauncherUrlSuggestions();
        }

        private void LauncherUrlTextBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            var hasText = !string.IsNullOrWhiteSpace(LauncherUrlTextBox.Text);
            HideLauncherUrlError();
            LauncherAddButton.IsEnabled = hasText;
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ShowLauncherUrlSuggestions();
            }
        }

        private async void LauncherUrlTextBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string url)
            {
                sender.Text = url;
            }

            await OpenLauncherUrlAsync();
        }

        private void LauncherUrlTextBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string url)
            {
                sender.Text = url;
            }
        }

        private async void LauncherAddButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenLauncherUrlAsync();
        }

        private async Task OpenLauncherUrlAsync()
        {
            if (_owner == null)
            {
                return;
            }

            if (!MainPage.TryNormalizeEnteredUrl(LauncherUrlTextBox.Text, out var url))
            {
                ShowLauncherUrlError();
                LauncherAddButton.IsEnabled = !string.IsNullOrWhiteSpace(LauncherUrlTextBox.Text);
                return;
            }

            HideLauncherUrlError();
            LauncherUrlTextBox.Text = url;
            LauncherAddButton.IsEnabled = false;
            try
            {
                await _owner.OpenUrlInNewContainerAsync(url);
            }
            finally
            {
                LauncherAddButton.IsEnabled = true;
            }
        }

        private void ShowLauncherUrlError()
        {
            LauncherUrlErrorText.Opacity = 1;
        }

        private void HideLauncherUrlError()
        {
            LauncherUrlErrorText.Opacity = 0;
        }

        private void RefreshLauncherUrlSuggestions()
        {
            var defaultUrl = GetResourceString("DefaultHomeUrl");
            _launcherUrlSuggestions.Clear();
            if (!string.IsNullOrWhiteSpace(defaultUrl))
            {
                _launcherUrlSuggestions.Add(defaultUrl);
            }
        }

        private void ShowLauncherUrlSuggestions()
        {
            RefreshLauncherUrlSuggestions();
            LauncherUrlTextBox.IsSuggestionListOpen = _launcherUrlSuggestions.Count > 0;
        }

        private void LoadLauncherViteDevServerSettings()
        {
            _isInitializingLauncherViteSettings = true;
            var settings = SettingsManager.Instance;
            LauncherVitePortNumberBox.Value = settings.ViteDevServerPort;
            LauncherViteUsePathCheckBox.IsChecked = settings.ViteDevServerUsePath;
            LauncherVitePathBox.Text = settings.ViteDevServerPath;
            UpdateLauncherVitePathInputState();
            UpdateLauncherViteUrlText();
            _isInitializingLauncherViteSettings = false;
        }

        private void LauncherVitePortNumberBox_ValueChanged(Microsoft.UI.Xaml.Controls.NumberBox sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializingLauncherViteSettings)
            {
                return;
            }

            SaveLauncherViteDevServerSettingsFromUi();
        }

        private void LauncherViteUsePathCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingLauncherViteSettings)
            {
                return;
            }

            SaveLauncherViteDevServerSettingsFromUi();
        }

        private void LauncherVitePathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializingLauncherViteSettings)
            {
                return;
            }

            SaveLauncherViteDevServerSettingsFromUi();
        }

        private void SaveLauncherViteDevServerSettingsFromUi()
        {
            var settings = SettingsManager.Instance;
            if (!double.IsNaN(LauncherVitePortNumberBox.Value)
                && !double.IsInfinity(LauncherVitePortNumberBox.Value)
                && LauncherVitePortNumberBox.Value >= 1
                && LauncherVitePortNumberBox.Value <= 65535)
            {
                settings.ViteDevServerPort = (int)Math.Round(LauncherVitePortNumberBox.Value);
            }

            settings.ViteDevServerUsePath = LauncherViteUsePathCheckBox.IsChecked == true;
            settings.ViteDevServerPath = LauncherVitePathBox.Text;
            UpdateLauncherVitePathInputState();
            UpdateLauncherViteUrlText();
        }

        private void UpdateLauncherVitePathInputState()
        {
            LauncherVitePathBox.IsEnabled = LauncherViteUsePathCheckBox.IsChecked == true;
        }

        private void UpdateLauncherViteUrlText()
        {
            LauncherViteUrlText.Text = SettingsManager.Instance.ViteDevServerUrl;
        }

        private async void LauncherViteUseUrlButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLauncherViteDevServerSettingsFromUi();
            HideLauncherUrlError();
            if (_owner == null)
            {
                return;
            }

            await _owner.OpenUrlInNewContainerAsync(
                SettingsManager.Instance.ViteDevServerUrl,
                GetResourceString("ViteDevContainerDisplayName"));
        }

        public void Navigate(string url)
        {
            if (!_isInitialized)
            {
                return;
            }

            ResetHostTitleBarForNavigation();
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

        public async void ReinjectHostScripts()
        {
            await RegisterHostTitleBarScriptAsync();
            await ApplyTransparentBackgroundAsync();
            await UpdateDocumentInfoAsync();
            await DetectHostTitleBarAsync();
        }

        public async void OpenDevToolsWindow()
        {
            try
            {
                await ApplyDevToolsAvailabilityAsync();
                RootWebView.CoreWebView2?.OpenDevToolsWindow();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Open DevTools failed: {ex.Message}");
            }
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
                RootWebView.CoreWebView2.ContentLoading += CoreWebView2_ContentLoading;
                RootWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                RootWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                RootWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                RootWebView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
                RootWebView.CoreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;
                return Task.CompletedTask;
            });

            await ApplyDevToolsAvailabilityAsync();
            await RegisterHostTitleBarScriptAsync();
            await ApplyTransparentBackgroundAsync();

            _isInitialized = true;
        }

        private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            _lastNavigationUrl = args.Uri;
            _lastHttpStatusCode = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
            NavigationErrorOverlay.Visibility = Visibility.Collapsed;
            _owner?.SetHostedPageLoading(args.Uri);
            ResetHostTitleBarForNavigation();
        }

        private void ResetHostTitleBarForNavigation()
        {
            _owner?.SetHostedTitleBarInteractiveRects(Array.Empty<MainPage.TitleBarInteractiveRect>());
            _owner?.SetHostedTitleBarVisible(false);
        }

        private async void CoreWebView2_ContentLoading(CoreWebView2 sender, CoreWebView2ContentLoadingEventArgs args)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            await RegisterHostTitleBarScriptAsync();
            await DetectHostTitleBarAsync();
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

                if (type.GetString() == "manifestInfoChanged"
                    && root.TryGetProperty("name", out var manifestName)
                    && manifestName.ValueKind == JsonValueKind.String)
                {
                    _owner?.UpdateHostedManifestInfo(manifestName.GetString());
                    return;
                }

                if (type.GetString() == "hostThemeChanged"
                    && root.TryGetProperty("theme", out var hostTheme)
                    && hostTheme.ValueKind == JsonValueKind.String)
                {
                    if (_hostedAppTheme == "System")
                    {
                        _owner?.ApplyHostedPageTheme(hostTheme.GetString());
                    }
                    return;
                }

                if (type.GetString() == "titleBarAppearanceChanged")
                {
                    string? theme = root.TryGetProperty("theme", out var themeValue)
                        && themeValue.ValueKind == JsonValueKind.String
                            ? themeValue.GetString()
                            : null;
                    string? foreground = root.TryGetProperty("foreground", out var foregroundValue)
                        && foregroundValue.ValueKind == JsonValueKind.String
                            ? foregroundValue.GetString()
                            : null;
                    string? background = root.TryGetProperty("background", out var backgroundValue)
                        && backgroundValue.ValueKind == JsonValueKind.String
                            ? backgroundValue.GetString()
                            : null;

                    _owner?.SetHostedTitleBarAppearance(theme, foreground, background);
                    return;
                }

                if (type.GetString() == "titleBarChanged"
                    && root.TryGetProperty("visible", out var visible)
                    && (visible.ValueKind == JsonValueKind.True || visible.ValueKind == JsonValueKind.False))
                {
                    double? height = null;
                    if (root.TryGetProperty("height", out var heightValue)
                        && heightValue.ValueKind == JsonValueKind.Number
                        && heightValue.TryGetDouble(out var parsedHeight))
                    {
                        height = parsedHeight;
                    }

                    System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Host title bar visible = {visible.GetBoolean()}, height = {height}");
                    _owner?.SetHostedTitleBarVisible(visible.GetBoolean(), height);
                    if (!visible.GetBoolean())
                    {
                        _owner?.SetHostedTitleBarInteractiveRects(Array.Empty<MainPage.TitleBarInteractiveRect>());
                    }
                    return;
                }

                if (type.GetString() == "titleBarInteractiveRectsChanged"
                    && root.TryGetProperty("rects", out var rectsValue)
                    && rectsValue.ValueKind == JsonValueKind.Array)
                {
                    var rects = new List<MainPage.TitleBarInteractiveRect>();
                    foreach (var item in rectsValue.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !TryGetFiniteDouble(item, "left", out var left)
                            || !TryGetFiniteDouble(item, "top", out var top)
                            || !TryGetFiniteDouble(item, "width", out var width)
                            || !TryGetFiniteDouble(item, "height", out var height)
                            || width <= 0
                            || height <= 0)
                        {
                            continue;
                        }

                        rects.Add(new MainPage.TitleBarInteractiveRect(left, top, width, height));
                    }

                    _owner?.SetHostedTitleBarInteractiveRects(rects);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView message failed: {ex.Message}");
            }
        }

        private static bool TryGetFiniteDouble(JsonElement item, string propertyName, out double value)
        {
            value = 0;
            return item.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out value)
                && !double.IsNaN(value)
                && !double.IsInfinity(value);
        }

        public void RefreshDevToolsAvailability()
        {
            _ = ApplyDevToolsAvailabilityAsync();
        }

        private async Task ApplyDevToolsAvailabilityAsync()
        {
            try
            {
                await RunOnUiThreadAsync(() =>
                {
                    var settings = RootWebView.CoreWebView2?.Settings;
                    if (settings != null)
                    {
                        settings.AreDevToolsEnabled = IsDevToolsEnabledForCurrentContainer();
                    }

                    return Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Apply DevTools setting failed: {ex.Message}");
            }
        }

        private bool IsDevToolsEnabledForCurrentContainer()
        {
            var containerId = _owner?.ContainerId ?? SettingsManager.Instance.ActiveContainerId;
            return SettingsManager.Instance.IsF12DevToolsEnabled
                && SettingsManager.Instance.IsContainerDevToolsEnabled(containerId);
        }

        private void ApplyHostedSetting(string? key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var containerId = _owner?.ContainerId ?? SettingsManager.Instance.ActiveContainerId;

            if (key == "material")
            {
                var appMaterial = value.Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                    ? "Acrylic"
                    : "Mica";

                SettingsManager.Instance.SetContainerAppMaterial(containerId, appMaterial);
                _owner?.ApplyCurrentContainerTheme();
            }
        }

        private async void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            _lastHttpStatusCode = args.HttpStatusCode;

            if (_suppressNextNavigationFailureForDownload
                || args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                _suppressNextNavigationFailureForDownload = false;
                NavigationErrorOverlay.Visibility = Visibility.Collapsed;
                _owner?.SetHostedPageLoaded(true);
                return;
            }

            var hasHttpError = IsHttpErrorStatusCode(_lastHttpStatusCode);
            _owner?.SetHostedPageLoaded(args.IsSuccess && !hasHttpError);

            if (!args.IsSuccess || hasHttpError)
            {
                if (hasHttpError)
                {
                    ShowNavigationHttpError(_lastHttpStatusCode);
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
            await RefreshHostTitleBarExclusionsAsync();
            SetHostedWindowActive(_owner?.IsWindowActive ?? true);
        }

        private static bool IsHttpErrorStatusCode(int statusCode) =>
            statusCode >= 400 && statusCode <= 599;

        private void ShowNavigationHttpError(int statusCode)
        {
            NavigationErrorOverlay.Visibility = Visibility.Visible;
            NavigationErrorUrlText.Text = _lastNavigationUrl;
            NavigationErrorMessage.Text = string.Format(
                GetResourceString("NavigationErrorMessageFormat"),
                GetHttpStatusErrorText(statusCode));
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

        private string GetHttpStatusErrorText(int statusCode)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "HTTP {0}",
                statusCode);
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
                var manifestSiteName = GetManifestSiteName(root);
                var hasWindowControlsOverlay = root.TryGetProperty("display_override", out var displayOverride)
                    && displayOverride.ValueKind == JsonValueKind.Array
                    && displayOverride.EnumerateArray().Any(item => item.GetString() == "window-controls-overlay");

                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest site name = {manifestSiteName}");
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Manifest has window-controls-overlay = {hasWindowControlsOverlay}");

                await RunOnUiThreadAsync(async () =>
                {
                    _owner?.UpdateHostedManifestInfo(manifestSiteName);
                    await RegisterHostTitleBarScriptCoreAsync();

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

        private static string GetManifestSiteName(JsonElement manifestRoot)
        {
            if (TryGetManifestString(manifestRoot, "name", out var name))
            {
                return name;
            }

            return TryGetManifestString(manifestRoot, "short_name", out var shortName)
                ? shortName
                : "";
        }

        private static bool TryGetManifestString(JsonElement manifestRoot, string propertyName, out string value)
        {
            value = "";
            if (!manifestRoot.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = property.GetString()?.Trim() ?? "";
            return !string.IsNullOrWhiteSpace(value);
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
            await ApplyHostTitleBarGeometryAsync();
        }

        public async Task SetHostTitleBarGeometryAsync(double leftInset, double rightInset, double height)
        {
            _hostTitleBarLeftInset = leftInset;
            _hostTitleBarRightInset = rightInset;
            _hostTitleBarHeight = height;
            await ApplyHostTitleBarGeometryAsync();
        }

        private async Task ApplyHostTitleBarGeometryAsync()
        {
            try
            {
                await RunOnUiThreadAsync(async () =>
                {
                    if (RootWebView.CoreWebView2 == null)
                    {
                        return;
                    }

                    var payload = string.Format(
                        CultureInfo.InvariantCulture,
                        "{{leftInset:{0},rightInset:{1},height:{2}}}",
                        _hostTitleBarLeftInset,
                        _hostTitleBarRightInset,
                        _hostTitleBarHeight);
                    await RootWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.__WINUI_ON_WEB_SET_TITLEBAR_GEOMETRY__&&window.__WINUI_ON_WEB_SET_TITLEBAR_GEOMETRY__({payload});");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Failed to apply title bar geometry: {ex.Message}");
            }
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

        private async Task RefreshHostTitleBarExclusionsAsync()
        {
            await RunOnUiThreadAsync(async () =>
            {
                if (RootWebView.CoreWebView2 == null)
                {
                    return;
                }

                await RootWebView.CoreWebView2.ExecuteScriptAsync("window.__WINUI_ON_WEB_REFRESH_TITLEBAR_EXCLUSIONS__&&window.__WINUI_ON_WEB_REFRESH_TITLEBAR_EXCLUSIONS__();");
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
                if (!string.Equals(_transparentScript, script, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(_transparentScriptId))
                    {
                        RootWebView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_transparentScriptId);
                    }

                    _transparentScriptId = await RootWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
                    _transparentScript = script;
                }

                await RootWebView.CoreWebView2.ExecuteScriptAsync(script);
            });
        }

        public async void RefreshHostTheme()
        {
            try
            {
                await ApplyTransparentBackgroundAsync();
                await DetectHostTitleBarAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinUIonWeb WebView] Refresh host theme failed: {ex.Message}");
            }
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
            css.Append("html.winui-webview-host,html.winui-webview-host *{scrollbar-width:thin;}");
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
            return $@"(()=>{{window.__WINUI_ON_WEB_UWP_APP__=true;const root=document.documentElement;if(root){{root.classList.add('winui-webview-host');}}const id='winui-on-web-transparent-background';let style=document.getElementById(id);if(!style){{style=document.createElement('style');style.id=id;(document.head||root||document.documentElement).appendChild(style);}}style.textContent={cssJson};}})()";
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
