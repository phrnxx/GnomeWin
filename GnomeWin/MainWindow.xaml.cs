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

namespace GnomeWin
{
    // Простая локализация: на русской системе — русский, на любой другой (французской,
    // немецкой и т.д.) — английский. Полный перевод под каждый язык не поддерживается,
    // но интерфейс не остаётся "непонятным" — всегда падает в читаемый английский вариант.
    internal static class Loc
    {
        public static readonly bool IsRu =
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

        private static readonly Dictionary<string, (string Ru, string En)> Map = new()
        {
            ["panel_settings"] = ("Настройки панели", "Panel settings"),
            ["bg_opacity"] = ("Прозрачность фона", "Background opacity"),
            ["color"] = ("Цвет", "Color"),
            ["save"] = ("Сохранить", "Save"),
            ["choose_program"] = ("Выберите программу", "Select a program"),
            ["file_filter"] = ("Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Все файлы (*.*)|*.*", "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*"),
            ["add_program"] = ("Добавить программу", "Add program"),
            ["layout_toast"] = ("🌐  Раскладка: {0}", "🌐  Layout: {0}"),
        };

        public static string T(string key) => Map.TryGetValue(key, out var v) ? (IsRu ? v.Ru : v.En) : key;
    }

    public class AppSettings
    {
        public string ColorHex { get; set; } = "#0A0A0A";
        public double Opacity { get; set; } = 0.82;
        public bool UseImage { get; set; } = false;
        public string ImagePath { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int nBuff, IntPtr[]? lpList);
        [DllImport("shell32.dll", ExactSpelling = true)] private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public int lParam; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
        private const uint ABM_NEW = 0x00000000;
        private const uint ABM_REMOVE = 0x00000001;
        private const uint ABM_QUERYPOS = 0x00000002;
        private const uint ABM_SETPOS = 0x00000003;
        private const uint ABE_TOP = 1;

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
                    bool isFullscreen = (appRect.left <= mi.rcMonitor.left && appRect.top <= mi.rcMonitor.top &&
                                         appRect.right >= mi.rcMonitor.right && appRect.bottom >= mi.rcMonitor.bottom);
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

        private void StartLayoutWatcher()
        {
            _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _layoutTimer.Tick += (s, e) => UpdateLayoutText();
            _layoutTimer.Start();
        }

        private void UpdateLayoutText()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                uint threadId = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                string newLayout = new CultureInfo((int)(GetKeyboardLayout(threadId).ToInt64() & 0xFFFF)).TwoLetterISOLanguageName.ToUpper();
                if (_currentLayout != null && _currentLayout != newLayout) ShowToast(string.Format(Loc.T("layout_toast"), newLayout));
                _currentLayout = newLayout;
                LayoutText.Text = newLayout;
            }
            catch { }
        }

        private void Layout_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            IntPtr fgWnd = GetForegroundWindow();
            uint threadId = GetWindowThreadProcessId(fgWnd, IntPtr.Zero);
            int count = GetKeyboardLayoutList(0, null);
            if (count < 2) return;
            IntPtr[] layouts = new IntPtr[count];
            GetKeyboardLayoutList(count, layouts);
            int currentIndex = Array.IndexOf(layouts, GetKeyboardLayout(threadId));
            PostMessage(fgWnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layouts[(currentIndex + 1) % count]);
        }

        private void Volume_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("sndvol.exe") { UseShellExecute = true }); }
            catch { }
        }

        private void StartClock()
        {
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => { DateText.Text = DateTime.Now.ToString("ddd, d MMM"); ClockText.Text = DateTime.Now.ToString("HH:mm"); };
            _clockTimer.Start();
            DateText.Text = DateTime.Now.ToString("ddd, d MMM"); ClockText.Text = DateTime.Now.ToString("HH:mm");
        }

        private void StartNetSpeed()
        {
            GetTotalBytes(out _lastBytesReceived, out _lastBytesSent);
            _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _netTimer.Tick += (s, e) =>
            {
                GetTotalBytes(out long received, out long sent);
                double downMB = (received - _lastBytesReceived) / 1024.0 / 1024.0;
                double upMB = (sent - _lastBytesSent) / 1024.0 / 1024.0;
                _lastBytesReceived = received; _lastBytesSent = sent;
                NetDown.Text = downMB.ToString("F1", CultureInfo.InvariantCulture) + " ";
                NetUp.Text = upMB.ToString("F1", CultureInfo.InvariantCulture) + " MB/s";
            };
            _netTimer.Start();
        }

        private void GetTotalBytes(out long received, out long sent)
        {
            received = sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var stats = nic.GetIPv4Statistics();
                received += stats.BytesReceived; sent += stats.BytesSent;
            }
        }

        private async System.Threading.Tasks.Task FetchWeatherAsync()
        {
            try
            {
                string json = await _http.GetStringAsync("https://wttr.in/?format=j1");
                using var doc = JsonDocument.Parse(json);
                var current = doc.RootElement.GetProperty("current_condition")[0];
                string temp = current.GetProperty("temp_C").GetString()!;
                int code = int.Parse(current.GetProperty("weatherCode").GetString()!);

                string icon = code switch { 113 => "☀", 116 => "⛅", 119 or 122 => "☁", 143 or 248 or 260 => "🌫", >= 386 => "⛈", >= 323 or (>= 179 and <= 230) => "❄", _ => "🌧" };
                Dispatcher.Invoke(() => { WeatherIcon.Text = icon; WeatherText.Text = $"{temp}°C"; });
            }
            catch { Dispatcher.Invoke(() => { WeatherIcon.Text = "☁"; WeatherText.Text = "err"; }); }
            finally
            {
                if (_weatherTimer == null)
                {
                    _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
                    _weatherTimer.Tick += async (s, e) => await FetchWeatherAsync();
                    _weatherTimer.Start();
                }
            }
        }

        private void StartBattery()
        {
            UpdateBattery();
            _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _batteryTimer.Tick += (s, e) => UpdateBattery();
            _batteryTimer.Start();
        }

        private void UpdateBattery()
        {
            try
            {
                if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS ps)) return;

                if (ps.BatteryFlag == 128 || ps.BatteryLifePercent == 255)
                {
                    Dispatcher.Invoke(() => BatteryPanel.Visibility = Visibility.Collapsed);
                    _batteryTimer?.Stop();
                    return;
                }

                int percent = ps.BatteryLifePercent;
                bool isCharging = ps.ACLineStatus == 1;

                string icon = isCharging ? "\uE83E" : percent switch
                {
                    >= 90 => "\uE83F",
                    >= 70 => "\uE850",
                    >= 50 => "\uE851",
                    >= 30 => "\uE852",
                    >= 15 => "\uE853",
                    _ => "\uE854"
                };

                SolidColorBrush defaultFg = this.Resources["MainBarForeground"] as SolidColorBrush ?? new SolidColorBrush(Colors.White);

                var color = (!isCharging && percent < 15) ? new SolidColorBrush(Color.FromRgb(255, 80, 80))
                          : (!isCharging && percent < 30) ? new SolidColorBrush(Color.FromRgb(255, 210, 60)) : defaultFg;

                Dispatcher.Invoke(() =>
                {
                    BatteryPanel.Visibility = Visibility.Visible;
                    BatteryIcon.Text = icon;
                    BatteryIcon.Foreground = color;
                    BatteryText.Text = $"{percent}%";
                    BatteryText.Foreground = color;
                });
            }
            catch { }
        }

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        // Окна интерфейса
        private Window? _appsDockWindow;
        private Window? _settingsWindow;
        private Window? _rgbPickerWindow;
        private Window? _calendarWindow;

        private IntPtr _mouseHookId = IntPtr.Zero;
        private LowLevelMouseProc? _mouseProc;

        // ПРАВИЛЬНАЯ АНИМАЦИЯ POPUP'ов (Анимация контента, а не самого окна)
        private void ShowWindowWithAnimation(Window win)
        {
            win.Opacity = 0;
            win.Show();
            ClampWindowToScreen(win);

            if (win.Content is UIElement content)
            {
                var transform = new TranslateTransform(0, -10);
                content.RenderTransform = transform;

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                win.BeginAnimation(Window.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
                transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
            }
        }

        // Гарантированно возвращает popup внутрь экрана, если он "вылез" за край из-за
        // погрешностей DPI-расчёта позиции. Работает поверх реальных физических пикселей окна,
        // так что не зависит от масштаба/DPI — просто проверяет итоговый прямоугольник окна
        // и толкает его обратно в рабочую область монитора при необходимости.
        private void ClampWindowToScreen(Window win)
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!GetWindowRect(hwnd, out RECT wr)) return;

            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref mi)) return;

            int width = wr.right - wr.left;
            int height = wr.bottom - wr.top;

            int newLeft = wr.left;
            int newTop = wr.top;

            if (wr.right > mi.rcWork.right) newLeft = mi.rcWork.right - width;
            if (newLeft < mi.rcWork.left) newLeft = mi.rcWork.left;

            if (wr.bottom > mi.rcWork.bottom) newTop = mi.rcWork.bottom - height;
            if (newTop < mi.rcWork.top) newTop = mi.rcWork.top;

            if (newLeft != wr.left || newTop != wr.top)
                SetWindowPos(hwnd, IntPtr.Zero, newLeft, newTop, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void CloseWindowWithAnimation(Window win, Action? afterClose = null)
        {
            if (win == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var animOpacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };

            animOpacity.Completed += (s, e) => { win.Close(); afterClose?.Invoke(); };

            if (win.Content is UIElement content)
            {
                var transform = content.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
                content.RenderTransform = transform;
                var animMove = new DoubleAnimation(0, -10, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
                transform.BeginAnimation(TranslateTransform.YProperty, animMove);
            }

            win.BeginAnimation(Window.OpacityProperty, animOpacity);
        }

        // =======================
        //     ОКНО КАЛЕНДАРЯ
        // =======================
        private void Clock_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_calendarWindow != null) { CloseCalendarWindow(); return; }
            CloseAllPopups();

            var win = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false };
            win.Resources = this.Resources;

            var cal = new System.Windows.Controls.Calendar { Margin = new Thickness(4), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            cal.SetResourceReference(FrameworkElement.StyleProperty, "DarkCalendarStyle");

            // Обертка для стилизации: убираем белый фон и настраиваем рамку
            var rootBorder = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5, Direction = 270 } };

            rootBorder.SetResourceReference(Border.BackgroundProperty, "MainBarBackground");
            rootBorder.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
            rootBorder.Child = cal;
            win.Content = rootBorder;

            // Вычисляем размер до отрисовки для правильного позиционирования (спасает от съезжания)
            win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // Календарь всегда открывается под датой (в левой группе бара), независимо от того,
            // кликнули по дате или по центральным часам — это один и тот же попап.
            System.Windows.Point pt = new System.Windows.Point(20, 32);
            try
            {
                FrameworkElement dateBorder = (FrameworkElement)this.FindName("DateBorder");
                if (dateBorder != null) pt = dateBorder.PointToScreen(new System.Windows.Point(0, dateBorder.ActualHeight));
            }
            catch { }

            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = pt.X / scaleX;
            win.Top = (pt.Y / scaleY) + 6;

            _calendarWindow = win;
            UpdateHookState();
            ShowWindowWithAnimation(win);
        }

        private void CloseCalendarWindow()
        {
            if (_calendarWindow == null) return;
            var win = _calendarWindow;
            _calendarWindow = null;
            UpdateHookState();
            CloseWindowWithAnimation(win);
        }

        // =======================
        //    ОКНО НАСТРОЕК
        // =======================
        private void MainBar_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_settingsWindow != null) { CloseSettingsWindow(); return; }
            CloseAllPopups();
            OpenSettingsWindow();
        }

        private void OpenSettingsWindow()
        {
            var win = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, Content = BuildSettingsContent() };
            win.Resources = this.Resources;

            win.SourceInitialized += (s, e) => { SetWindowLong(new WindowInteropHelper(win).Handle, GWL_EXSTYLE, GetWindowLong(new WindowInteropHelper(win).Handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE); };
            win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            System.Windows.Point mousePos = PointToScreen(System.Windows.Input.Mouse.GetPosition(this));
            double scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            win.Left = mousePos.X / scaleX;
            win.Top = (mousePos.Y / scaleY) + 16;

            _settingsWindow = win;
            UpdateHookState();
            ShowWindowWithAnimation(win);
        }

        private void CloseSettingsWindow()
        {
            if (_rgbPickerWindow != null) CloseRgbPicker();
            if (_settingsWindow == null) return;
            var win = _settingsWindow;
            _settingsWindow = null;
            UpdateHookState();
            CloseWindowWithAnimation(win);
        }

        private Border BuildSettingsContent()
        {
            var panel = new StackPanel { Margin = new Thickness(14) };

            var title = new TextBlock { Text = Loc.T("panel_settings"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "MainBarForeground");
            panel.Children.Add(title);

            var opLabel = new TextBlock { Text = Loc.T("bg_opacity"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
            opLabel.SetResourceReference(TextBlock.ForegroundProperty, "MainBarForeground");
            panel.Children.Add(opLabel);

            var slider = new Slider { Minimum = 0.0, Maximum = 1.0, Value = _appSettings.Opacity, Width = 190, Margin = new Thickness(0, 0, 0, 14) };
            slider.ValueChanged += (s, e) => { _appSettings.Opacity = slider.Value; ApplyCurrentSettings(); SaveSettings(); };
            panel.Children.Add(slider);

            var colLabel = new TextBlock { Text = Loc.T("color"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
            colLabel.SetResourceReference(TextBlock.ForegroundProperty, "MainBarForeground");
            panel.Children.Add(colLabel);

            var colorWrap = new WrapPanel { MaxWidth = 190 };
            string[] presetColors = { "#0A0A0A", "#E0E0E0", "#111827", "#1E1B4B", "#3F1D38", "#064E3B", "#3B0764", "#7F1D1D" };

            foreach (var c in presetColors)
            {
                var btn = new Border { Width = 26, Height = 26, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
                btn.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
                btn.MouseLeftButtonDown += (s, e) => { _appSettings.ColorHex = c; ApplyCurrentSettings(); SaveSettings(); };
                colorWrap.Children.Add(btn);
            }

            var customBtn = new Border { Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
            customBtn.SetResourceReference(Border.BackgroundProperty, "MainBarButtonOverlay");
            customBtn.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
            customBtn.Child = new TextBlock { Text = "🎨", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            customBtn.MouseLeftButtonDown += (s, e) => OpenRgbPicker();
            colorWrap.Children.Add(customBtn);

            panel.Children.Add(colorWrap);

            var rootBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5, Direction = 270 }, Child = panel };
            rootBorder.SetResourceReference(Border.BackgroundProperty, "MainBarBackground");
            rootBorder.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
            return rootBorder;
        }

        private void OpenRgbPicker()
        {
            if (_rgbPickerWindow != null) return;

            var win = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight };
            win.Resources = this.Resources;

            var pnl = new StackPanel { Margin = new Thickness(14) };

            Color curr = (Color)ColorConverter.ConvertFromString(_appSettings.ColorHex);
            var (h0, s0, v0) = RgbToHsv(curr);
            double hue = h0, sat = s0, val = v0;

            const double svSize = 170;
            const double hueBarWidth = svSize;

            // Превью и подпись HEX объявляем заранее, чтобы локальные функции ниже могли их обновлять
            var preview = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(2), Background = new SolidColorBrush(curr) };
            preview.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");

            var hexText = new TextBlock { Text = _appSettings.ColorHex.ToUpperInvariant(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            hexText.SetResourceReference(TextBlock.ForegroundProperty, "MainBarForeground");

            // ===== Квадрат "насыщенность / яркость" =====
            var svGrid = new Grid { Width = svSize, Height = svSize, ClipToBounds = true, Cursor = System.Windows.Input.Cursors.Cross };
            var svClip = new RectangleGeometry { Rect = new Rect(0, 0, svSize, svSize), RadiusX = 12, RadiusY = 12 };
            svGrid.Clip = svClip;

            var svBase = new Border { Width = svSize, Height = svSize, Background = new SolidColorBrush(ColorFromHsv(hue, 1, 1)) };
            var svSatLayer = new System.Windows.Shapes.Rectangle { Width = svSize, Height = svSize, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new System.Windows.Point(0, 0), new System.Windows.Point(1, 0)) };
            var svValLayer = new System.Windows.Shapes.Rectangle { Width = svSize, Height = svSize, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)) };
            var svBorder = new Border { Width = svSize, Height = svSize, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            svBorder.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");

            var svCursor = new System.Windows.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 }
            };

            svGrid.Children.Add(svBase);
            svGrid.Children.Add(svSatLayer);
            svGrid.Children.Add(svValLayer);
            svGrid.Children.Add(svCursor);

            // ===== Полоса "оттенок" (Hue) =====
            var hueRect = new System.Windows.Shapes.Rectangle { Width = hueBarWidth, Height = 14 };
            var hueGradient = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            string[] hueStops = { "#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000" };
            for (int i = 0; i < hueStops.Length; i++)
                hueGradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hueStops[i]), i / (double)(hueStops.Length - 1)));
            hueRect.Fill = hueGradient;
            hueRect.RadiusX = 7; hueRect.RadiusY = 7;

            var hueCursor = new Border { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), IsHitTestVisible = false };

            var hueCanvas = new Canvas { Width = hueBarWidth, Height = 18, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 12, 0, 0) };
            Canvas.SetTop(hueRect, 2);
            hueCanvas.Children.Add(hueRect);
            hueCanvas.Children.Add(hueCursor);

            void UpdateSvCursorPos() => svCursor.Margin = new Thickness(sat * svSize - 7, (1 - val) * svSize - 7, 0, 0);
            void UpdateHueCursorPos() => Canvas.SetLeft(hueCursor, (hue / 360.0) * hueBarWidth - 2);
            void UpdateSvBaseColor() => svBase.Background = new SolidColorBrush(ColorFromHsv(hue, 1, 1));

            void ApplyColor()
            {
                Color nc = ColorFromHsv(hue, sat, val);
                preview.Background = new SolidColorBrush(nc);
                hexText.Text = $"#{nc.R:X2}{nc.G:X2}{nc.B:X2}".ToUpperInvariant();
                _appSettings.ColorHex = $"#{nc.R:X2}{nc.G:X2}{nc.B:X2}";
                ApplyCurrentSettings();
            }

            UpdateSvCursorPos();
            UpdateHueCursorPos();
            UpdateSvBaseColor();

            bool draggingSv = false, draggingHue = false;

            void HandleSvPointer(System.Windows.Point p)
            {
                sat = Math.Clamp(p.X, 0, svSize) / svSize;
                val = 1 - (Math.Clamp(p.Y, 0, svSize) / svSize);
                UpdateSvCursorPos();
                ApplyColor();
            }

            svGrid.MouseLeftButtonDown += (s, e) => { draggingSv = true; svGrid.CaptureMouse(); HandleSvPointer(e.GetPosition(svGrid)); };
            svGrid.MouseMove += (s, e) => { if (draggingSv) HandleSvPointer(e.GetPosition(svGrid)); };
            svGrid.MouseLeftButtonUp += (s, e) => { draggingSv = false; svGrid.ReleaseMouseCapture(); };

            void HandleHuePointer(System.Windows.Point p)
            {
                double x = Math.Clamp(p.X, 0, hueBarWidth);
                hue = (x / hueBarWidth) * 359.999;
                UpdateHueCursorPos();
                UpdateSvBaseColor();
                ApplyColor();
            }

            hueCanvas.MouseLeftButtonDown += (s, e) => { draggingHue = true; hueCanvas.CaptureMouse(); HandleHuePointer(e.GetPosition(hueCanvas)); };
            hueCanvas.MouseMove += (s, e) => { if (draggingHue) HandleHuePointer(e.GetPosition(hueCanvas)); };
            hueCanvas.MouseLeftButtonUp += (s, e) => { draggingHue = false; hueCanvas.ReleaseMouseCapture(); };

            var svHost = new Grid { Width = svSize, Height = svSize };
            svHost.Children.Add(svGrid);
            svHost.Children.Add(svBorder);

            pnl.Children.Add(svHost);
            pnl.Children.Add(hueCanvas);

            var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 14) };
            previewRow.Children.Add(preview);
            previewRow.Children.Add(hexText);
            pnl.Children.Add(previewRow);

            var btnOkText = new TextBlock { Text = Loc.T("save"), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
            btnOkText.SetResourceReference(TextBlock.ForegroundProperty, "MainBarForeground");
            var btnOk = new Border { CornerRadius = new CornerRadius(6), Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0, 6, 0, 6), Child = btnOkText };
            btnOk.SetResourceReference(Border.BackgroundProperty, "MainBarButtonOverlay");
            btnOk.MouseLeftButtonDown += (s, e) => { SaveSettings(); CloseRgbPicker(); };
            pnl.Children.Add(btnOk);

            var rootBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5, Direction = 270 }, Child = pnl };
            rootBorder.SetResourceReference(Border.BackgroundProperty, "MainBarBackground");
            rootBorder.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
            win.Content = rootBorder;
            win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            if (_settingsWindow != null)
            {
                win.Left = _settingsWindow.Left + _settingsWindow.ActualWidth + 10;
                win.Top = _settingsWindow.Top;
            }

            _rgbPickerWindow = win;
            UpdateHookState();
            ShowWindowWithAnimation(win);
        }

        // ===== Конвертация HSV <-> RGB для цветового пикера =====
        private static Color ColorFromHsv(double h, double s, double v)
        {
            h = h % 360; if (h < 0) h += 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            byte R = (byte)Math.Round((r1 + m) * 255);
            byte G = (byte)Math.Round((g1 + m) * 255);
            byte B = (byte)Math.Round((b1 + m) * 255);
            return Color.FromRgb(R, G, B);
        }

        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;
            double h = 0;
            if (delta > 0.00001)
            {
                if (max == r) h = 60 * (((g - b) / delta) % 6);
                else if (max == g) h = 60 * (((b - r) / delta) + 2);
                else h = 60 * (((r - g) / delta) + 4);
            }
            if (h < 0) h += 360;
            double s = max <= 0 ? 0 : delta / max;
            double v = max;
            return (h, s, v);
        }

        private void CloseRgbPicker()
        {
            if (_rgbPickerWindow == null) return;
            var win = _rgbPickerWindow;
            _rgbPickerWindow = null;
            UpdateHookState();
            CloseWindowWithAnimation(win);
        }

        // =======================
        //   ДОК ПРИЛОЖЕНИЙ
        // =======================
        private void AppsToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_appsDockWindow != null) { CloseAppsDock(); return; }
            CloseAllPopups();
            OpenAppsDock();
        }

        private void OpenAppsDock()
        {
            var win = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, Content = BuildDockContent() };
            win.Resources = this.Resources;

            win.SourceInitialized += (s, e) => { SetWindowLong(new WindowInteropHelper(win).Handle, GWL_EXSTYLE, GetWindowLong(new WindowInteropHelper(win).Handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE); };
            win.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            System.Windows.Point btnScreenPos = AppsToggleBtn.PointToScreen(new System.Windows.Point(0, AppsToggleBtn.ActualHeight));
            double scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            win.Left = btnScreenPos.X / scaleX;
            win.Top = btnScreenPos.Y / scaleY + 6;

            _appsDockWindow = win;
            AppsToggleArrow.Text = "▴";
            UpdateHookState();
            ShowWindowWithAnimation(win);
        }

        private void CloseAppsDock()
        {
            if (_appsDockWindow == null) return;
            var win = _appsDockWindow;
            _appsDockWindow = null;
            AppsToggleArrow.Text = "▾";
            UpdateHookState();
            CloseWindowWithAnimation(win);
        }

        private void CloseAllPopups()
        {
            if (_appsDockWindow != null) CloseAppsDock();
            if (_settingsWindow != null) CloseSettingsWindow();
            if (_calendarWindow != null) CloseCalendarWindow();
        }

        private void AddApp_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StopMouseHook();
            var dialog = new OpenFileDialog { Title = Loc.T("choose_program"), Filter = Loc.T("file_filter"), CheckFileExists = true };
            bool added = false;

            if (dialog.ShowDialog(this) == true)
            {
                var extra = LoadExtraApps();
                if (!extra.Exists(p => string.Equals(p, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    extra.Add(dialog.FileName);
                    SaveExtraApps(extra);
                }
                added = true;
            }

            if (added) { CloseAppsDock(); OpenAppsDock(); }
            else UpdateHookState();
        }

        private List<string> LoadExtraApps()
        {
            try { if (File.Exists(ExtraAppsFile)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ExtraAppsFile)) ?? new List<string>(); }
            catch { }
            return new List<string>();
        }

        private void SaveExtraApps(List<string> paths)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ExtraAppsFile)!); File.WriteAllText(ExtraAppsFile, JsonSerializer.Serialize(paths)); }
            catch { }
        }

        private Border BuildDockContent()
        {
            var appsPanel = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 180, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            var apps = new List<(string Name, string TargetPath)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string taskbarDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (Directory.Exists(taskbarDir))
                {
                    Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        foreach (var lnk in Directory.GetFiles(taskbarDir, "*.lnk"))
                        {
                            try
                            {
                                dynamic shortcut = shell.CreateShortcut(lnk);
                                string target = shortcut.TargetPath;
                                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target) && seen.Add(target)) apps.Add((Path.GetFileNameWithoutExtension(lnk), target));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            foreach (var path in LoadExtraApps()) if (File.Exists(path) && seen.Add(path)) apps.Add((Path.GetFileNameWithoutExtension(path), path));
            foreach (var app in apps) appsPanel.Children.Add(CreateAppDockButton(app.Name, app.TargetPath));

            var addBtnText = new TextBlock { Text = "+", Opacity = 0.3, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
            addBtnText.SetResourceReference(TextBlock.ForegroundProperty, "MainBarForeground");

            var addBtn = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = Loc.T("add_program"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2), Child = addBtnText };
            addBtn.MouseEnter += (s, e) => { addBtn.SetResourceReference(Border.BackgroundProperty, "MainBarButtonOverlay"); addBtnText.Opacity = 0.8; };
            addBtn.MouseLeave += (s, e) => { addBtn.Background = Brushes.Transparent; addBtnText.Opacity = 0.3; };
            addBtn.MouseLeftButtonDown += AddApp_Click;

            var rootGrid = new Grid();
            rootGrid.Children.Add(appsPanel);
            rootGrid.Children.Add(addBtn);

            var rootBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5, Direction = 270 }, Child = rootGrid };
            rootBorder.SetResourceReference(Border.BackgroundProperty, "MainBarBackground");
            rootBorder.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
            return rootBorder;
        }

        private Border CreateAppDockButton(string name, string targetPath)
        {
            var btn = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Margin = new Thickness(4), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = name };
            var grid = new Grid();

            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(targetPath);
                if (icon != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    var img = new Image { Source = src, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    grid.Children.Add(img);
                }
            }
            catch { }

            // Кнопка удаления — в едином стиле с кнопкой "+" (тёмный фон бара + hover-подсветка)
            var delBtn = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -4, 0),
                Opacity = 0,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            delBtn.SetResourceReference(Border.BackgroundProperty, "MainBarBackground");
            delBtn.SetResourceReference(Border.BorderBrushProperty, "MainBarSeparatorBrush");
            delBtn.Child = new TextBlock { Text = "−", Foreground = Brushes.White, Opacity = 0.85, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
            delBtn.MouseEnter += (s, e) => delBtn.SetResourceReference(Border.BackgroundProperty, "MainBarButtonOverlay");
            delBtn.MouseLeave += (s, e) => delBtn.SetResourceReference(Border.BackgroundProperty, "MainBarBackground");

            grid.Children.Add(delBtn);
            btn.Child = grid;

            btn.MouseEnter += (s, e) => { btn.SetResourceReference(Border.BackgroundProperty, "MainBarButtonOverlay"); delBtn.Opacity = 1; };
            btn.MouseLeave += (s, e) => { btn.Background = Brushes.Transparent; delBtn.Opacity = 0; };

            // Удаление приложения
            delBtn.MouseLeftButtonDown += (s, e) => {
                e.Handled = true; // Запрещаем запуск программы при клике на минус
                var extra = LoadExtraApps();
                if (extra.RemoveAll(x => string.Equals(x, targetPath, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    SaveExtraApps(extra);
                    CloseAppsDock();
                    OpenAppsDock();
                }
            };

            // Запуск приложения
            btn.MouseLeftButtonDown += (s, e) => { try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); } catch { } CloseAppsDock(); };

            return btn;
        }

        // =======================
        //   ГЛОБАЛЬНЫЙ ХУК МЫШИ
        // =======================
        private void UpdateHookState()
        {
            bool needsHook = _appsDockWindow != null || _settingsWindow != null || _rgbPickerWindow != null || _calendarWindow != null;
            if (needsHook && _mouseHookId == IntPtr.Zero) StartMouseHook();
            else if (!needsHook && _mouseHookId != IntPtr.Zero) StopMouseHook();
        }

        private void StartMouseHook()
        {
            _mouseProc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(curModule!.ModuleName!), 0);
        }

        private void StopMouseHook()
        {
            if (_mouseHookId != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHookId); _mouseHookId = IntPtr.Zero; }
            _mouseProc = null;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (int)wParam == WM_LBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int mx = hookStruct.pt.x; int my = hookStruct.pt.y;

                // Получаем UI-элементы по их именам через FindName
                var clockBorder = this.FindName("ClockBorder") as FrameworkElement;
                var dateBorder = this.FindName("DateBorder") as FrameworkElement;

                bool closeDock = _appsDockWindow != null && !IsScreenPointInsideWindow(_appsDockWindow, mx, my) && !IsScreenPointInsideElement(AppsToggleBtn, mx, my);
                bool closeSettings = _settingsWindow != null && !IsScreenPointInsideWindow(_settingsWindow, mx, my);

                bool isClickOnCalendarTrigger = (clockBorder != null && IsScreenPointInsideElement(clockBorder, mx, my)) ||
                                                (dateBorder != null && IsScreenPointInsideElement(dateBorder, mx, my));
                bool closeCalendar = _calendarWindow != null && !IsScreenPointInsideWindow(_calendarWindow, mx, my) && !isClickOnCalendarTrigger;

                if (_rgbPickerWindow != null && IsScreenPointInsideWindow(_rgbPickerWindow, mx, my)) closeSettings = false;

                if (closeDock) Dispatcher.BeginInvoke(new Action(CloseAppsDock));
                if (closeSettings) Dispatcher.BeginInvoke(new Action(CloseSettingsWindow));
                if (closeCalendar) Dispatcher.BeginInvoke(new Action(CloseCalendarWindow));
            }
            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
        }

        private bool IsScreenPointInsideWindow(Window window, int screenX, int screenY)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return false;
            return screenX >= r.left && screenX <= r.right && screenY >= r.top && screenY <= r.bottom;
        }

        private bool IsScreenPointInsideElement(FrameworkElement element, int screenX, int screenY)
        {
            var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
            double scaleX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            double left = topLeft.X, top = topLeft.Y;
            double right = left + element.ActualWidth * scaleX;
            double bottom = top + element.ActualHeight * scaleY;
            return screenX >= left && screenX <= right && screenY >= top && screenY <= bottom;
        }

        // =======================
        //   МУЗЫКА (ЭКВАЛАЙЗЕР)
        // =======================
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private GlobalSystemMediaTransportControlsSession? _currentMediaSession;
        private bool _isMusicPlaying = false;

        private async System.Threading.Tasks.Task StartMediaWatcherAsync()
        {
            try { _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); _mediaManager.CurrentSessionChanged += (s, e) => Dispatcher.Invoke(HookCurrentSession); HookCurrentSession(); } catch { }
        }

        private void HookCurrentSession()
        {
            if (_currentMediaSession != null) _currentMediaSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentMediaSession = _mediaManager?.GetCurrentSession();
            if (_currentMediaSession != null) _currentMediaSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            UpdatePlaybackState();
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Dispatcher.Invoke(UpdatePlaybackState);

        private void UpdatePlaybackState()
        {
            bool playing = _currentMediaSession?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing == _isMusicPlaying) return;
            _isMusicPlaying = playing;
            AnimateClockSwap(playing);
        }

        private void AnimateClockSwap(bool showEqualizer)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var duration = TimeSpan.FromMilliseconds(320);

            ClockText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(showEqualizer ? 0 : 1, duration) { EasingFunction = ease });
            ClockTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(showEqualizer ? -26 : 0, duration) { EasingFunction = ease });

            EqualizerPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(showEqualizer ? 1 : 0, duration) { EasingFunction = ease });
            EqualizerTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(showEqualizer ? 0 : 18, duration) { EasingFunction = ease });

            string[] keys = { "EqAnim1", "EqAnim2", "EqAnim3", "EqAnim4", "EqAnim5" };
            foreach (var key in keys)
            {
                var sb = (Storyboard)FindResource(key);
                if (showEqualizer) sb.Begin(this, true); else sb.Stop(this);
            }
        }
    }
}