using System;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using Microsoft.Win32;
using Windows.Media.Control;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GnomeWin
{
    public class AppSettings
    {
        public string ColorHex { get; set; } = "#0A0A0A";
        public double Opacity { get; set; } = 0.82;
        public bool UseImage { get; set; } = false;
        public string ImagePath { get; set; } = "";
        public double YtMusicVolume { get; set; } = 1.0;
    }

    // ЯДРО: поля, конструктор, настройки, AppBar, autoHide в fullscreen, тосты
    public partial class MainWindow : Window
    {
        private IntPtr _hwnd;
        private string? _currentLayout;

        private DispatcherTimer? _clockTimer, _layoutTimer, _netTimer, _weatherTimer, _fullscreenTimer, _batteryTimer;
        private Window? _currentToast;
        private DispatcherTimer? _toastHideTimer;
        private long _lastBytesReceived = 0, _lastBytesSent = 0;
        private bool _isAppBarRegistered = false;

        private static readonly HttpClient _http = new HttpClient();

        private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GnomeWin");
        private static readonly string ExtraAppsFile = Path.Combine(DataFolder, "pinned_apps.json");
        private static readonly string SettingsFile = Path.Combine(DataFolder, "settings.json");

        private AppSettings _appSettings = new AppSettings();

        static MainWindow()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GnomeWin/1.0");
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        public MainWindow()
        {
            InitializeComponent();
            this.Width = SystemParameters.PrimaryScreenWidth;

            LoadSettings();

            StartClock();
            StartLayoutWatcher();
            StartNetSpeed();
            StartBattery();
            StartFullscreenWatcher();
            _ = FetchWeatherAsync();
            _ = StartMediaWatcherAsync();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    _appSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
            }
            catch { }
            ApplyCurrentSettings();
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_appSettings));
            }
            catch { }
        }

        private void ApplyCurrentSettings()
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(_appSettings.ColorHex); }
            catch { c = Color.FromRgb(10, 10, 10); }

            c.A = (byte)(_appSettings.Opacity * 255);
            var newBrush = new SolidColorBrush(c);

            this.Resources["MainBarBackground"] = newBrush;
            UpdateForegroundColors(Color.FromRgb(c.R, c.G, c.B));
        }

        private void UpdateForegroundColors(Color bgColor)
        {
            double luminance = (0.299 * bgColor.R + 0.587 * bgColor.G + 0.114 * bgColor.B) / 255.0;
            bool isLight = luminance > 0.6;

            Color fgColor = isLight ? Colors.Black : Colors.White;
            Color sepColor = isLight ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(51, 255, 255, 255);
            Color btnColor = isLight ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255);

            this.Resources["MainBarForeground"] = new SolidColorBrush(fgColor);
            this.Resources["MainBarSeparatorBrush"] = new SolidColorBrush(sepColor);
            this.Resources["MainBarButtonOverlay"] = new SolidColorBrush(btnColor);

            UpdateBattery();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;

            int extendedStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            RegisterAppBar();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;

            if (msg == WM_NCHITTEST)
            {
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                if (y / scaleY >= 32)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
            }
            return IntPtr.Zero;
        }

        private void RegisterAppBar()
        {
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = (uint)Marshal.SizeOf(abd);
            abd.hWnd = _hwnd;
            abd.uCallbackMessage = 10101;
            SHAppBarMessage(ABM_NEW, ref abd);

            double scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            abd.uEdge = ABE_TOP;
            abd.rc.left = 0;
            abd.rc.top = 0;
            abd.rc.right = (int)(SystemParameters.PrimaryScreenWidth * scaleX);
            abd.rc.bottom = (int)(32 * scaleY);

            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc.bottom = abd.rc.top + (int)(32 * scaleY);
            SHAppBarMessage(ABM_SETPOS, ref abd);

            _isAppBarRegistered = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveSettings();
            StopMouseHook();
            if (_isAppBarRegistered)
            {
                APPBARDATA abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA)), hWnd = _hwnd };
                SHAppBarMessage(ABM_REMOVE, ref abd);
            }
            if (_webView != null) _webView.Dispose();
            base.OnClosed(e);
        }

        private void StartFullscreenWatcher()
        {
            _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _fullscreenTimer.Tick += (s, e) => CheckFullscreen();
            _fullscreenTimer.Start();
        }

        private void CheckFullscreen()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { SetPanelVisible(true); return; }
            if (hwnd == _hwnd) return;

            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            string className = sb.ToString();

            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" || hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            {
                SetPanelVisible(true);
                return;
            }

            GetWindowRect(hwnd, out RECT appRect);
            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                MONITORINFO mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    bool coversMonitor = (appRect.left <= mi.rcMonitor.left && appRect.top <= mi.rcMonitor.top &&
                                         appRect.right >= mi.rcMonitor.right && appRect.bottom >= mi.rcMonitor.bottom);

                    // Обычное развёрнутое окно (Steam, браузер и т.п.) может по размеру совпасть
                    // с монитором, но у него есть рамка/заголовок — это не настоящий фуллскрин.
                    // Настоящие игры/видео в фуллскрине убирают WS_CAPTION полностью.
                    uint style = unchecked((uint)GetWindowLong(hwnd, GWL_STYLE));
                    bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;

                    bool isFullscreen = coversMonitor && !hasCaption;
                    SetPanelVisible(!isFullscreen);
                    return;
                }
            }
            SetPanelVisible(true);
        }

        private void SetPanelVisible(bool visible)
        {
            if (visible && this.Visibility != Visibility.Visible)
            {
                this.Topmost = true;
                this.Visibility = Visibility.Visible;
            }
            else if (!visible && this.Visibility == Visibility.Visible)
            {
                this.Topmost = false;
                this.Visibility = Visibility.Hidden;
            }
        }

        private void ShowToast(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (_currentToast != null) { _toastHideTimer?.Stop(); _currentToast.Close(); }

                _currentToast = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    IsHitTestVisible = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Top = 46,
                    Opacity = 0
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(20, 8, 20, 8),
                    Margin = new Thickness(15),
                    Effect = new DropShadowEffect { BlurRadius = 15, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black }
                };

                border.Child = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold };
                _currentToast.Content = border;

                _currentToast.Loaded += (s, ev) =>
                {
                    _currentToast.Left = (SystemParameters.PrimaryScreenWidth - _currentToast.ActualWidth) / 2;

                    if (_currentToast.Content is UIElement content)
                    {
                        var transform = new TranslateTransform(0, 10);
                        content.RenderTransform = transform;
                        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                        _currentToast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                    }
                };

                _currentToast.Show();

                _toastHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _toastHideTimer.Tick += (s, ev) =>
                {
                    _toastHideTimer.Stop();
                    if (_currentToast == null) return;

                    var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
                    var animOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn };
                    var moveOut = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(200)) { EasingFunction = easeIn };

                    animOut.Completed += (s2, ev2) => { _currentToast?.Close(); _currentToast = null; };

                    if (_currentToast.Content is UIElement content)
                    {
                        var transform = content.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
                        content.RenderTransform = transform;
                        transform.BeginAnimation(TranslateTransform.YProperty, moveOut);
                    }
                    _currentToast.BeginAnimation(UIElement.OpacityProperty, animOut);
                };
                _toastHideTimer.Start();
            });
        }

    }
}
