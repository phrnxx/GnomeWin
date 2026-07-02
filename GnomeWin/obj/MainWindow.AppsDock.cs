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
    // Док закреплённых программ слева (иконки, добавление/удаление)
    public partial class MainWindow : Window
    {
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

        private void AddApp_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StopMouseHook();
            var dialog = new OpenFileDialog { Title = "Выберите программу", Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Все файлы (*.*)|*.*", CheckFileExists = true };
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

            var addBtn = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Добавить программу", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2), Child = addBtnText };
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
                    DestroyIcon(icon.Handle); // ФИКС: очищаем память от системной иконки GDI

                    var img = new Image { Source = src, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    grid.Children.Add(img);
                }
            }
            catch { }

            var delBtn = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.Red, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -4, 0), Opacity = 0 };
            delBtn.Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };

            grid.Children.Add(delBtn);
            btn.Child = grid;

            btn.MouseEnter += (s, e) => { btn.SetResourceReference(Border.BackgroundProperty, "MainBarButtonOverlay"); delBtn.Opacity = 1; };
            btn.MouseLeave += (s, e) => { btn.Background = Brushes.Transparent; delBtn.Opacity = 0; };

            delBtn.MouseLeftButtonDown += (s, e) => {
                e.Handled = true;
                var extra = LoadExtraApps();
                if (extra.RemoveAll(x => string.Equals(x, targetPath, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    SaveExtraApps(extra);
                    CloseAppsDock();
                    OpenAppsDock();
                }
            };

            btn.MouseLeftButtonDown += (s, e) => { try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true }); } catch { } CloseAppsDock(); };

            return btn;
        }

    }
}
