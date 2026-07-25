using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace findrbordr
{
    public partial class MainWindow : Window
    {
        // --- Win32 & DWM API Imports ---
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(
            IntPtr hWnd,
            StringBuilder lpClassName,
            int nMaxCount
        );

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags
        );

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            out RECT pvAttribute,
            int cbAttribute
        );

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags
        );

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam
        );

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(
            int uAction,
            int uParam,
            StringBuilder lpvParam,
            int fuWinIni
        );

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate void WinEventProc(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        );

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int GWL_HWNDPARENT = -8;

        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_RESTORE = 0xF120;
        private const int SPI_GETDESKWALLPAPER = 0x0073;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        private IntPtr targetExplorerHwnd = IntPtr.Zero;
        private IntPtr windowHookHandle = IntPtr.Zero;

        private WinEventProc? hookDelegate;
        private EnumWindowsProc? enumWindowsDelegate;

        private DispatcherTimer? stateSyncTimer;
        private DispatcherTimer? searchExplorerTimer;

        private bool isSidebarReady = false;
        private bool isToolbarReady = false;
        private string lastTrackedPath = "";

        private dynamic? shellApplicationInstance;
        private dynamic? wshellInstance; // Reusable WScript.Shell instance

        public class CustomShortcut
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public string? Path { get; set; }
        }

        private List<CustomShortcut> customShortcuts = new List<CustomShortcut>();
        private readonly string jsonFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "local.json"
        );
        private DateTime lastSyncTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            // Ambil style extended window saat ini
            long currentStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

            // Terapkan WS_EX_NOACTIVATE agar jendela tidak pernah mengaktifkan/merebut status Active Window
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(currentStyle | WS_EX_NOACTIVATE));
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                // Beritahu Windows: Izinkan klik masuk ke elemen/UI, tapi JANGAN pernah aktifkan window ini!
                handled = true;
                return (IntPtr)MA_NOACTIVATE;
            }
            return IntPtr.Zero;
        }

        private void LoadCustomShortcuts()
        {
            if (File.Exists(jsonFilePath))
            {
                try
                {
                    string json = File.ReadAllText(jsonFilePath);
                    customShortcuts =
                        JsonSerializer.Deserialize<List<CustomShortcut>>(json)
                        ?? new List<CustomShortcut>();
                }
                catch
                {
                    customShortcuts = new List<CustomShortcut>();
                }
            }
        }

        private void SaveCustomShortcuts()
        {
            try
            {
                string json = JsonSerializer.Serialize(
                    customShortcuts,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                File.WriteAllText(jsonFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save shortcuts: {ex.Message}");
            }
        }

        private static string GetActiveWallpaperFilePath()
        {
            try
            {
                StringBuilder sb = new StringBuilder(260);
                SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
                string wallpaperPath = sb.ToString();

                if (!string.IsNullOrEmpty(wallpaperPath) && File.Exists(wallpaperPath))
                {
                    return wallpaperPath;
                }
            }
            catch { }

            return string.Empty;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadCustomShortcuts();

                // Initialize COM Objects once
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                    shellApplicationInstance = Activator.CreateInstance(shellType);

                Type? wshellType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshellType != null)
                    wshellInstance = Activator.CreateInstance(wshellType);

                StringBuilder customShortcutsBuilder = new StringBuilder();
                if (customShortcuts.Count > 0)
                {
                    customShortcutsBuilder.Append(
                        "<div id='custom-title' class='section-title'>Custom</div>"
                    );
                    foreach (var sc in customShortcuts)
                    {
                        customShortcutsBuilder.Append(
                            $"<a id='{sc.Id}' class='nav-item' href='action://open?path={Uri.EscapeDataString(sc.Path ?? "")}'>{sc.Name}</a>"
                        );
                    }
                }

                string portableFolder = AppDomain.CurrentDomain.BaseDirectory;
                string webViewCachePath = Path.GetFullPath(
                    Path.Combine(portableFolder, "webview_cache")
                );

                if (!Directory.Exists(webViewCachePath))
                    Directory.CreateDirectory(webViewCachePath);

                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-gpu-sandbox --enable-features=UseSkiaRenderer"
                );
                var env = await CoreWebView2Environment.CreateAsync(
                    null,
                    webViewCachePath,
                    options
                );

                await WebViewSidebar.EnsureCoreWebView2Async(env);
                await WebViewToolbar.EnsureCoreWebView2Async(env);
                await WebViewSidebar.EnsureCoreWebView2Async(env);

                // Matikan status bar (URL preview/tooltip di pojok bawah saat hover link)
                WebViewSidebar.CoreWebView2.Settings.IsStatusBarEnabled = false;

                WebViewSidebar.CoreWebView2.ProcessFailed += (s, args) =>
                    Dispatcher.Invoke(() => WebViewSidebar.Reload());
                WebViewToolbar.CoreWebView2.ProcessFailed += (s, args) =>
                    Dispatcher.Invoke(() => WebViewToolbar.Reload());

                WebViewSidebar.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                WebViewSidebar.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                WebViewToolbar.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                WebViewSidebar.CoreWebView2.ContextMenuRequested += BuildSidebarContextMenu;

                string userProfile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile
                );
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string documentsPath = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments
                );
                string EscapePath(string path) => Uri.EscapeDataString(path);

                string baseStyles =
                    @"
<style>
    /* Atur font-family fallback ke font ikon sistem */
.material-symbols-outlined, .icon {
    font-family: 'Segoe Fluent Icons', 'Segoe MDL2 Assets', sans-serif !important;
    font-weight: normal;
    font-style: normal;
    display: inline-block;
    line-height: 1;
    text-transform: none;
    letter-spacing: normal;
    word-wrap: normal;
    white-space: nowrap;
    direction: ltr;
    -webkit-font-smoothing: antialiased;
}
    body { margin: 0; padding: 0; width: 100vw; height: 100vh; background: transparent; overflow: hidden; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
    {outline: none !important; -webkit-tap-highlight-color: transparent;}

    body, .nav-item, .dot, .clickable-title {    user-select: none !important;    -webkit-user-select: none !important;}
    .base { width: 100%; height: 100%; position: relative; border-radius: 30px 0 0 30px; box-sizing: border-box; background: transparent; z-index: 1; overflow: hidden; }
    .glass-panel { width: calc(100% - 14px); height: calc(100% - 14px); margin: 6px; position: absolute; top: 0; left: 0; border-radius: 20px; z-index: 2; overflow: hidden; border: 1px solid rgba(255, 255, 255, 0.55); }
    .white-panel { width: calc(100% - 13.5px); height: calc(100% - 13px); margin: 6px; position: absolute; top: 0; left: 0; border-radius: 18px; z-index: 3; overflow: hidden; background: rgba(255,255,255,.85); }
    .glass-panel::before { content: ''; position: absolute; top: 0; left: 0; width: 100%; height: 100%; background-size: var(--sw, 1920px) var(--sh, 1080px) !important; background-position: var(--ox, 0px) var(--oy, 0px) !important; background-repeat: no-repeat !important; filter: blur(60px) saturate(180%); -webkit-filter: blur(50px) saturate(180%); transform: scale(1.1); transform-origin: center; z-index: -1; }
    .clickable-title { pointer-events: auto !important; cursor: pointer !important; user-select: none !important; display: inline-block; padding: 4px 8px; border-radius: 4px; transition: background 0.15s ease; }
    .clickable-title:hover { background: rgba(0, 0, 0, 0.06); }
    .sidebar-wrapper { position: absolute; top: 10px; bottom: 10px; left: 10px; right: 10px; z-index: 3; overflow-y: auto; box-sizing: border-box; }
    .mac-dots { display: flex; gap: 7px; margin-top: -6px; margin-bottom: 14px; margin-left: -6px; }
    .dot { width: 13px; height: 13px; border-radius: 50%; cursor: pointer; transition: filter 0.1s ease; }
    .dot:hover { filter: brightness(0.85); }
    .dot.red { background: #ff5f56; }
    .dot.yellow { background: #ffbd2e; }
    .dot.green { background: #27c93f; }
    .section-title { font-size: 11px; font-weight: 700; color: rgba(0, 0, 0, 0.45); margin: 14px 0 4px 8px; text-transform: uppercase; letter-spacing: 0.6px; user-select: none; }
    .nav-item { margin-left: -5px; display: flex; align-items: center; gap: 10px; padding: 10px 10px; border-radius: 8px; font-size: 14px; color: #1c1c1e; text-decoration: none; cursor: pointer; font-weight: 500; user-select: none; }
    .nav-item:hover { background: rgba(0, 0, 0, 0.05); }
    .nav-item.active { background: rgba(0, 0, 0, 0.08); font-weight: 600; }
    .toolbar { width: 100%; height: 100%; display: flex; align-items: center; padding: 0 8px; pointer-events: none !important; }
    .window-title { font-size: 14px; font-weight: 600; color: #2c3e50; user-select: none; pointer-events: auto; }
</style>";

                string sidebarHtml =
                    $@"<!DOCTYPE html><html><head><meta charset='UTF-8'>{baseStyles}</head><body>
<div class='base'>
    <div class='fixed-wallpaper'></div>
    <div class='white-panel'></div>
    <div class='glass-panel'></div>
    <div class='sidebar-wrapper' style='padding: 18px 14px;'>
      <div class='mac-dots'>
        <div class='dot red' onclick='sendAction(""close"")'></div>
        <div class='dot yellow' onclick='sendAction(""minimize"")'></div>
        <div class='dot green' onclick='sendAction(""maximize"")'></div>
      </div>
      <a id='nav-recents' class='nav-item' href='action://open?path={EscapePath("shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}")}'>Recents</a>
      <a id='nav-shared' class='nav-item'>Shared</a>
      <div class='section-title'>Favorites</div>
      <a id='nav-apps' class='nav-item' href='action://open?path={EscapePath("shell:AppsFolder")}'>Applications</a>
      <a id='nav-desktop' class='nav-item' href='action://open?path={EscapePath(desktopPath)}'>Desktop</a>
      <a id='nav-documents' class='nav-item' href='action://open?path={EscapePath(documentsPath)}'>Documents</a>
      <div class='section-title'>Locations</div>
      <a id='nav-user' class='nav-item active' href='action://open?path={EscapePath(userProfile)}'>UserDir</a>
      <a id='nav-drive' class='nav-item' href='action://open?path={EscapePath("C:\\")}'>OS Disk</a>
      {customShortcutsBuilder}
    </div>
</div>
<script>
  function sendAction(a) {{ window.chrome.webview.postMessage(a); }}
  window.updateActiveTab = function(targetId) {{
    document.querySelectorAll('.nav-item').forEach(el => el.classList.remove('active'));
    const activeEl = document.getElementById(targetId);
    if (activeEl) activeEl.classList.add('active');
  }};
  document.addEventListener('mousedown', function(e) {{
      e.preventDefault();
  }}, false);
</script>
</body></html>";

                string toolbarHtml =
                    $@"<!DOCTYPE html><html><head><meta charset='UTF-8'>{baseStyles}</head><body>
                <header class='toolbar glass-panel-toolbar'>
                    <span id='title-text' class='window-title clickable-title'>folder</span>
                </header>
                <script>
                function sendAction(a) {{ window.chrome.webview.postMessage(a); }}
                window.updateTitle = function(newTitle) {{
                    const titleEl = document.getElementById('title-text');
                    if (titleEl) titleEl.innerText = newTitle;
                }};
                </script>
            </body></html>";

                string wallpaperPath = GetActiveWallpaperFilePath();
                if (!string.IsNullOrEmpty(wallpaperPath))
                {
                    string wallpaperDir = Path.GetDirectoryName(wallpaperPath)!;
                    WebViewSidebar.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "appassets.local",
                        wallpaperDir,
                        CoreWebView2HostResourceAccessKind.Allow
                    );
                }

                var sidebarNavTcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> sidebarNavHandler = null!;
                sidebarNavHandler = (s, args) =>
                {
                    WebViewSidebar.NavigationCompleted -= sidebarNavHandler;
                    sidebarNavTcs.SetResult(args.IsSuccess);
                };
                WebViewSidebar.NavigationCompleted += sidebarNavHandler;

                WebViewSidebar.NavigateToString(sidebarHtml);
                WebViewToolbar.NavigateToString(toolbarHtml);

                await sidebarNavTcs.Task;

                if (!string.IsNullOrEmpty(wallpaperPath))
                {
                    string fileName = Path.GetFileName(wallpaperPath);
                    string localImageUrl = $"https://appassets.local/{fileName}";
                    string injectScript =
                        $@"
                        (function() {{
                            var styleTag = document.getElementById('wp-fix-style') || document.createElement('style');
                            styleTag.id = 'wp-fix-style';
                            styleTag.innerHTML = `.glass-panel::before {{ background-image: url('{localImageUrl}'); }}`;
                            document.head.appendChild(styleTag);
                        }})();";

                    await WebViewSidebar.ExecuteScriptAsync(injectScript);
                }

                isSidebarReady = true;
                isToolbarReady = true;

                hookDelegate = new WinEventProc(OnWinEventSignaled);
                windowHookHandle = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero,
                    hookDelegate,
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT
                );

                // Sedikit melonggarkan interval timer untuk efisiensi CPU
                stateSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                stateSyncTimer.Tick += SyncExplorerStateData;
                stateSyncTimer.Start();

                searchExplorerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                searchExplorerTimer.Tick += (s, ev) => FindAndAttachToExplorer();
                searchExplorerTimer.Start();

                ForceInitialExplorerScan();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Initialization Error: {ex.Message}",
                    "Launch Failure"
                );
            }
        }

        private void BuildSidebarContextMenu(
            object? sender,
            CoreWebView2ContextMenuRequestedEventArgs args
        )
        {
            args.MenuItems.Clear();
            var environment = WebViewSidebar.CoreWebView2.Environment;

            var addFolderBtn = environment.CreateContextMenuItem(
                "Add Folder to Sidebar",
                null,
                CoreWebView2ContextMenuItemKind.Command
            );
            addFolderBtn.CustomItemSelected += (s, ev) =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select a folder to pin to the sidebar",
                    UseDescriptionForTitle = true,
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;
                    string folderName = Path.GetFileName(selectedPath);
                    if (string.IsNullOrEmpty(folderName))
                        folderName = selectedPath;

                    string newId = "nav-custom-" + Guid.NewGuid().ToString("N");

                    customShortcuts.Add(
                        new CustomShortcut
                        {
                            Id = newId,
                            Name = folderName,
                            Path = selectedPath,
                        }
                    );
                    SaveCustomShortcuts();

                    string escapedPath = Uri.EscapeDataString(selectedPath);
                    string js =
                        $@"
                        var sidebar = document.querySelector('.sidebar-wrapper');
                        if (!document.getElementById('custom-title')) {{
                            var title = document.createElement('div');
                            title.id = 'custom-title';
                            title.className = 'section-title';
                            title.innerText = 'Custom';
                            sidebar.appendChild(title);
                        }}
                        var newItem = document.createElement('a');
                        newItem.id = '{newId}';
                        newItem.className = 'nav-item';
                        newItem.href = 'action://open?path={escapedPath}';
                        newItem.innerHTML = ""{folderName.Replace("'", "\\'")}""
                        sidebar.appendChild(newItem);";
                    WebViewSidebar.ExecuteScriptAsync(js);
                }
            };
            args.MenuItems.Add(addFolderBtn);

            var reloadBtn = environment.CreateContextMenuItem(
                "Restart Application",
                null,
                CoreWebView2ContextMenuItemKind.Command
            );
            reloadBtn.CustomItemSelected += (s, ev) =>
            {
                string? exePath = System
                    .Diagnostics.Process.GetCurrentProcess()
                    .MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Process.Start(exePath);
                    System.Windows.Application.Current.Shutdown();
                }
                else
                {
                    System.Windows.Forms.Application.Restart();
                    System.Windows.Application.Current.Shutdown();
                }
            };
            args.MenuItems.Add(reloadBtn);

            var exitBtn = environment.CreateContextMenuItem(
                "Exit Application",
                null,
                CoreWebView2ContextMenuItemKind.Command
            );
            exitBtn.CustomItemSelected += (s, ev) => System.Windows.Application.Current.Shutdown();
            args.MenuItems.Add(exitBtn);
        }

        private void ForceInitialExplorerScan()
        {
            var helper = new WindowInteropHelper(this);
            IntPtr overlayHwnd = helper.Handle;

            enumWindowsDelegate = new EnumWindowsProc(
                (hWnd, lParam) =>
                {
                    StringBuilder cName = new StringBuilder(256);
                    GetClassName(hWnd, cName, cName.Capacity);
                    if (cName.ToString() == "CabinetWClass" && IsWindowVisible(hWnd))
                    {
                        AttachToHwnd(hWnd, overlayHwnd);
                        return false;
                    }
                    return true;
                }
            );

            EnumWindows(enumWindowsDelegate, IntPtr.Zero);
        }

        private void FindAndAttachToExplorer()
        {
            IntPtr foregroundHwnd = GetForegroundWindow();
            IntPtr overlayHwnd = new WindowInteropHelper(this).Handle;

            if (foregroundHwnd == IntPtr.Zero)
                return;

            StringBuilder className = new StringBuilder(256);
            GetClassName(foregroundHwnd, className, className.Capacity);

            if (className.ToString() == "CabinetWClass" && IsWindowVisible(foregroundHwnd))
            {
                AttachToHwnd(foregroundHwnd, overlayHwnd);
            }
            else if (foregroundHwnd == overlayHwnd && targetExplorerHwnd == IntPtr.Zero)
            {
                ForceInitialExplorerScan();
            }
        }

        private void AttachToHwnd(IntPtr explorerHwnd, IntPtr overlayHwnd)
        {
            if (targetExplorerHwnd != explorerHwnd)
            {
                SetWindowLongPtr(overlayHwnd, GWL_HWNDPARENT, IntPtr.Zero);
                targetExplorerHwnd = explorerHwnd;
                SetWindowLongPtr(overlayHwnd, GWL_HWNDPARENT, targetExplorerHwnd);

                lastTrackedPath = "";
                SyncOverlayPosition();
            }
        }

        private void OnWinEventSignaled(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime
        )
        {
            if (idObject != 0)
                return;

            if (hwnd == targetExplorerHwnd && eventType == EVENT_OBJECT_LOCATIONCHANGE)
            {
                SyncOverlayPosition();
            }
            else if (hwnd == targetExplorerHwnd && eventType == EVENT_OBJECT_DESTROY)
            {
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        targetExplorerHwnd = IntPtr.Zero;
                        Hide();
                    }),
                    DispatcherPriority.Render
                );
            }
            else if (eventType == EVENT_OBJECT_CREATE || eventType == EVENT_OBJECT_DESTROY)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => FindAndAttachToExplorer()),
                    DispatcherPriority.Render
                );
            }
        }

        private void SyncOverlayPosition()
        {
            if (targetExplorerHwnd == IntPtr.Zero || !isSidebarReady || !isToolbarReady)
                return;

            if (!IsWindowVisible(targetExplorerHwnd))
            {
                if (Visibility == Visibility.Visible)
                    Hide();
                return;
            }

            // Limit redraw rate (~60 FPS)
            if ((DateTime.Now - lastSyncTime).TotalMilliseconds < 16)
                return;
            lastSyncTime = DateTime.Now;

            if (
                DwmGetWindowAttribute(
                    targetExplorerHwnd,
                    DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT rect,
                    Marshal.SizeOf(typeof(RECT))
                ) == 0
            )
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                var helper = new WindowInteropHelper(this);

                SetWindowPos(
                    helper.Handle,
                    IntPtr.Zero,
                    rect.Left - 7,
                    rect.Top - 7,
                    width + 14,
                    height + 14,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW
                );

                if (Visibility != Visibility.Visible)
                    Show();

                double screenW = SystemParameters.PrimaryScreenWidth;
                double screenH = SystemParameters.PrimaryScreenHeight;

                double offsetX = -(rect.Left - 7);
                double offsetY = -(rect.Top - 7);

                string updateParallaxJs =
                    $@"
                    document.documentElement.style.setProperty('--sw', '{screenW}px');
                    document.documentElement.style.setProperty('--sh', '{screenH}px');
                    document.documentElement.style.setProperty('--ox', '{offsetX}px');
                    document.documentElement.style.setProperty('--oy', '{offsetY}px');";

                WebViewSidebar.ExecuteScriptAsync(updateParallaxJs);
            }
        }

        private void SyncExplorerStateData(object? sender, EventArgs e)
        {
            if (
                targetExplorerHwnd == IntPtr.Zero
                || !isSidebarReady
                || !isToolbarReady
                || shellApplicationInstance == null
            )
                return;

            try
            {
                StringBuilder sbTitle = new StringBuilder(256);
                GetWindowText(targetExplorerHwnd, sbTitle, sbTitle.Capacity);
                string activeExplorerWindowTitle = sbTitle.ToString();

                dynamic? windows = shellApplicationInstance.Windows();
                if (windows == null)
                    return;

                int count = windows.Count;
                dynamic? activeTabWindow = null;

                for (int i = 0; i < count; i++)
                {
                    dynamic? window = windows.Item(i);
                    if (window != null)
                    {
                        IntPtr hwndVal = new IntPtr(Convert.ToInt64(window.HWND));
                        if (hwndVal == targetExplorerHwnd)
                        {
                            string tabName = window.LocationName ?? string.Empty;
                            if (
                                !string.IsNullOrEmpty(tabName)
                                && activeExplorerWindowTitle.StartsWith(
                                    tabName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                activeTabWindow = window;
                                break;
                            }
                            activeTabWindow ??= window;
                        }
                    }
                }

                if (activeTabWindow != null)
                {
                    string currentPath =
                        activeTabWindow.Document?.Folder?.Self?.Path ?? string.Empty;
                    string currentName = activeTabWindow.LocationName ?? string.Empty;

                    if (currentPath != lastTrackedPath && !string.IsNullOrEmpty(currentPath))
                    {
                        lastTrackedPath = currentPath;
                        string safeTitle = string.IsNullOrEmpty(currentName)
                            ? "explorer"
                            : currentName;

                        WebViewToolbar.ExecuteScriptAsync(
                            $"window.updateTitle('{safeTitle.Replace("'", "\\'")}');"
                        );

                        string targetNavId = "nav-shared";
                        string userProfile = Environment.GetFolderPath(
                            Environment.SpecialFolder.UserProfile
                        );
                        string desktop = Environment.GetFolderPath(
                            Environment.SpecialFolder.Desktop
                        );
                        string documents = Environment.GetFolderPath(
                            Environment.SpecialFolder.MyDocuments
                        );

                        if (currentPath.Equals(userProfile, StringComparison.OrdinalIgnoreCase))
                            targetNavId = "nav-user";
                        else if (currentPath.Equals(desktop, StringComparison.OrdinalIgnoreCase))
                            targetNavId = "nav-desktop";
                        else if (currentPath.Equals(documents, StringComparison.OrdinalIgnoreCase))
                            targetNavId = "nav-documents";
                        else if (currentPath.Equals("C:\\", StringComparison.OrdinalIgnoreCase))
                            targetNavId = "nav-drive";
                        else if (currentPath.Contains("679F85CB-0220-4080-B29B-5540CC05AAB6"))
                            targetNavId = "nav-recents";
                        else if (currentPath.Contains("AppsFolder"))
                            targetNavId = "nav-apps";
                        else
                        {
                            var customMatch = customShortcuts?.FirstOrDefault(c =>
                                !string.IsNullOrEmpty(c.Path)
                                && currentPath.Equals(c.Path, StringComparison.OrdinalIgnoreCase)
                            );
                            if (customMatch != null)
                                targetNavId = customMatch.Id ?? string.Empty;
                        }

                        WebViewSidebar.ExecuteScriptAsync(
                            $"window.updateActiveTab('{targetNavId}');"
                        );
                    }
                }
            }
            catch { }
        }

        private void CoreWebView2_WebMessageReceived(
            object? sender,
            CoreWebView2WebMessageReceivedEventArgs e
        )
        {
            string message = e.TryGetWebMessageAsString();
            if (targetExplorerHwnd == IntPtr.Zero)
                return;

            switch (message)
            {
                case "close":
                    SendMessage(targetExplorerHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    targetExplorerHwnd = IntPtr.Zero;
                    Hide();
                    break;

                case "minimize":
                    SendMessage(
                        targetExplorerHwnd,
                        WM_SYSCOMMAND,
                        (IntPtr)SC_MINIMIZE,
                        IntPtr.Zero
                    );
                    break;

                case "maximize":
                    SendMessage(
                        targetExplorerHwnd,
                        WM_SYSCOMMAND,
                        IsZoomed(targetExplorerHwnd) ? (IntPtr)SC_RESTORE : (IntPtr)SC_MAXIMIZE,
                        IntPtr.Zero
                    );
                    break;
            }
        }

        private void CoreWebView2_NavigationStarting(
            object? sender,
            CoreWebView2NavigationStartingEventArgs e
        )
        {
            string url = e.Uri;
            if (url.StartsWith("action://open"))
            {
                e.Cancel = true;
                try
                {
                    var uri = new Uri(url);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string targetPath = query["path"] ?? "";
                    if (!string.IsNullOrEmpty(targetPath))
                        NavigateActiveExplorer(targetPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Navigation failed: {ex.Message}");
                }
            }
        }

        private void NavigateActiveExplorer(string path)
        {
            if (targetExplorerHwnd == IntPtr.Zero)
                return;

            if (path.StartsWith("shell:"))
            {
                System.Diagnostics.Process.Start("explorer.exe", path);
                return;
            }

            try
            {
                SetForegroundWindow(targetExplorerHwnd);
                if (wshellInstance == null)
                    return;

                wshellInstance.SendKeys("^l");

                Task.Run(async () =>
                {
                    await Task.Delay(50);
                    try
                    {
                        AutomationElement focusedElement = AutomationElement.FocusedElement;
                        if (
                            focusedElement != null
                            && (
                                focusedElement.Current.ClassName == "Edit"
                                || focusedElement.Current.ControlType == ControlType.Edit
                            )
                        )
                        {
                            if (
                                focusedElement.TryGetCurrentPattern(
                                    ValuePattern.Pattern,
                                    out object pattern
                                )
                            )
                            {
                                ValuePattern valuePattern = (ValuePattern)pattern;
                                valuePattern.SetValue(path);
                                wshellInstance.SendKeys("{ENTER}");
                                return;
                            }
                        }

                        string escapedPath = path.Replace("~", "{~}")
                            .Replace("(", "{(}")
                            .Replace(")", "{)}")
                            .Replace("+", "{+}")
                            .Replace("^", "{^}")
                            .Replace("%", "{%}");
                        wshellInstance.SendKeys(escapedPath);
                        wshellInstance.SendKeys("{ENTER}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Focus injection failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Navigation failed: {ex.Message}");
            }
        }

        private void SendKeysToExplorer(string keys)
        {
            if (targetExplorerHwnd == IntPtr.Zero)
                return;
            // JANGKAN PANGGUL SetForegroundWindow DI SINI! (Ini yang bikin kedip)
            try
            {
                wshellInstance?.SendKeys(keys);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SendKeys failed: {ex.Message}");
            }
        }

        // Toolbar Button Event Handlers
        private void BtnBack_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("%{LEFT}");

        private void BtnUp_Click(object sender, RoutedEventArgs e) => SendKeysToExplorer("%{UP}");

        private void BtnForward_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("%{RIGHT}");

        private void BtnViewIcons_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("^+2");

        private void BtnViewList_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("^+5");

        private void BtnViewDetails_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("^+6");

        private void BtnViewTiles_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("^+7");

        private void BtnTogglePreview_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("%p");

        private void BtnToggleDetailsPane_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("+%p");

        private void BtnProperties_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("%{ENTER}");

        private void BtnContextMenu_Click(object sender, RoutedEventArgs e) =>
            SendKeysToExplorer("+{F10}");

        private void BtnSearch_Click(object sender, RoutedEventArgs e) => SendKeysToExplorer("^f");

        private void BtnFolderOptions_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("control.exe", "folders");
            }
            catch { }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            stateSyncTimer?.Stop();
            searchExplorerTimer?.Stop();

            if (windowHookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(windowHookHandle);
                windowHookHandle = IntPtr.Zero;
            }

            shellApplicationInstance = null;
            wshellInstance = null;
        }
    }
}
