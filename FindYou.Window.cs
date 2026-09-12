using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace FindYou
{
    static class WindowRuntime
    {
        public static void Register()
        {
            var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                string name = new AssemblyName(args.Name).Name;
                lock (loaded)
                {
                Assembly assembly;
                if (loaded.TryGetValue(name, out assembly)) return assembly;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FindYou.Runtime." + name + ".dll"))
                {
                    if (stream == null) return null;
                    using (var buffer = new MemoryStream()) { stream.CopyTo(buffer); assembly = Assembly.Load(buffer.ToArray()); loaded[name] = assembly; return assembly; }
                }
                }
            };
        }
    }

    sealed class AppWindow : Forms.Form
    {
        readonly NativeWorkspace workspace;
        readonly AppConfig config;
        readonly UserControl root;
        readonly NativeRadar radar;
        readonly DispatcherTimer timer;
        string view = "discover", lastPeerSignature = "", lastRows = "", lastSession = "";
        int peerPage, historyFilter = -1;
        DateTime noticeUntil;
        UpdateRelease availableUpdate;
        bool updateBusy;
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public AppWindow(NativeWorkspace workspace, Icon icon)
        {
            this.workspace = workspace; config = workspace.Config;
            Text = "FindYou"; Icon = icon; BackColor = System.Drawing.Color.FromArgb(13, 16, 18);
            ClientSize = new System.Drawing.Size(1280, 800); MinimumSize = new System.Drawing.Size(800, 600);
            StartPosition = Forms.FormStartPosition.CenterScreen; AutoScaleMode = Forms.AutoScaleMode.Dpi;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FindYou.Native.xaml")) root = (UserControl)XamlReader.Load(stream);
            var host = new ElementHost { Dock = Forms.DockStyle.Fill, Child = root }; Controls.Add(host);
            radar = new NativeRadar(); El<Grid>("RadarLayer").Children.Insert(0, radar);
            root.AddHandler(Button.ClickEvent, new RoutedEventHandler(Clicked));
            El<Grid>("TransferDrop").DragOver += DragOverFiles;
            El<Grid>("TransferDrop").DragLeave += (s,e) => El<Border>("DropOutline").Visibility = Visibility.Collapsed;
            El<Grid>("TransferDrop").Drop += (s,e) =>
            {
                El<Border>("DropOutline").Visibility = Visibility.Collapsed;
                var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
                if (files != null) SendPaths(files); e.Handled = true;
            };
            El<TextBox>("HistorySearch").TextChanged += (s,e) => { lastRows = ""; RenderRows(); };
            El<Canvas>("PeerNodes").SizeChanged += (s,e) => { lastPeerSignature = ""; RenderPeers(); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) }; timer.Tick += (s,e) => Update();
            VisibleChanged += (s,e) => { if (Visible) { timer.Start(); Update(); } else timer.Stop(); };
            workspace.Changed += OnChanged; workspace.Notice += Notify;
            El<TextBlock>("UpdateStatus").Text = "当前版本 " + AppUpdate.Current;
            Update();
        }
        T El<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        static Brush B(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
        static TextBlock TextNode(string text, double size, string color = "#EDF1EF") { return new TextBlock { Text = text, FontSize = size, Foreground = B(color), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; }
        void OnChanged() { if (!IsDisposed && IsHandleCreated) try { BeginInvoke((Action)Update); } catch { } }
        void Notify(string message)
        {
            if (InvokeRequired) { if (!IsDisposed) try { BeginInvoke((Action)(() => Notify(message))); } catch { } return; }
            El<TextBlock>("NoticeText").Text = message; El<Border>("Notice").Visibility = Visibility.Visible; noticeUntil = DateTime.Now.AddSeconds(5);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { } }
        public void ShowWorkspace() { Show(); if (WindowState == Forms.FormWindowState.Minimized) WindowState = Forms.FormWindowState.Normal; Activate(); }
        protected override void OnFormClosing(Forms.FormClosingEventArgs e) { if (e.CloseReason == Forms.CloseReason.UserClosing) { e.Cancel = true; Hide(); return; } base.OnFormClosing(e); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); radar.Stop(); workspace.Changed -= OnChanged; workspace.Notice -= Notify; }
            base.Dispose(disposing);
        }
        void SetView(string next)
        {
            view = next; lastRows = "";
            foreach (string name in new[] { "discover", "session", "history", "settings" }) El<Grid>(name).Visibility = name == view ? Visibility.Visible : Visibility.Collapsed;
            El<Button>("DiscoverNav").Foreground = B(view == "discover" ? "#7DEFDB" : "#919B9F");
            El<Button>("SessionNav").Foreground = B(view == "session" ? "#7DEFDB" : "#919B9F");
            if (view == "settings") FillSettings(); Update();
        }
        new void Update()
        {
            if (IsDisposed) return;
            El<TextBlock>("DeviceName").Text = config.DeviceName;
            var s = workspace.Session;
            El<Button>("SessionNav").Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
            El<Grid>("SessionContent").Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
            El<StackPanel>("NoSession").Visibility = s == null ? Visibility.Visible : Visibility.Collapsed;
            if (s != null)
            {
                El<TextBlock>("SessionName").Text = s.Peer.Name; El<TextBlock>("ConnectionStatus").Text = s.Status;
                El<Button>("SendFile").IsEnabled = El<Button>("SendDirectory").IsEnabled = s.Connected;
                if (lastSession != s.Id) { lastSession = s.Id; SetView("session"); }
            }
            else lastSession = "";
            if (DateTime.Now > noticeUntil) El<Border>("Notice").Visibility = Visibility.Collapsed;
            if (view == "discover") RenderPeers();
            if (view == "session" || view == "history") RenderRows();
        }
        async void Clicked(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button;
            if (button == null) { var current = e.OriginalSource as DependencyObject; while (current != null && !(current is Button)) current = VisualTreeHelper.GetParent(current); button = current as Button; }
            if (button == null || button.Tag == null) return;
            string action = button.Tag.ToString();
            try
            {
                if (action.StartsWith("view:")) SetView(action.Substring(5));
                else if (action == "check-update" || action == "download-update") await RunUpdate(action == "download-update");
                else if (action.StartsWith("connect:")) { if (workspace.Session != null) SetView("session"); else { await workspace.Connect(action.Substring(8)); SetView("session"); } }
                else if (action.StartsWith("cancel:")) workspace.Cancel(action.Substring(7));
                else if (action.StartsWith("reveal:")) Reveal(action.Substring(7));
                else if (action.StartsWith("retry:")) { var send = workspace.Sends.FirstOrDefault(x => x.History.Id == action.Substring(6)); if (send != null) workspace.Send(send.Source); }
                else if (action.StartsWith("filter:")) { historyFilter = int.Parse(action.Substring(7)); lastRows = ""; RenderRows(); }
                else if (action == "disconnect") workspace.Disconnect();
                else if (action == "files") { var files = NativeDialogs.PickFiles(this, "发送文件"); if (files != null) SendPaths(files); }
                else if (action == "folder") { string folder = NativeDialogs.PickFolder(this, "发送文件夹"); if (!string.IsNullOrEmpty(folder)) SendPaths(new[] { folder }); }
                else if (action == "downloads") { Directory.CreateDirectory(config.DownloadDir); Process.Start(config.DownloadDir); }
                else if (action == "previous" || action == "next") { peerPage += action == "next" ? 1 : -1; lastPeerSignature = ""; RenderPeers(); }
                else if (action == "rename") { El<TextBox>("DialogInput").Text = config.DeviceName; OpenDialog("设备名称", "rename-save"); }
                else if (action == "manual") { El<TextBox>("DialogInput").Text = ""; OpenDialog("通过 IP 连接", "manual-save"); }
                else if (action == "dialog-close") El<Border>("DialogOverlay").Visibility = Visibility.Collapsed;
                else if (action == "rename-save") { Save(new Dictionary<string, string> { { "name", El<TextBox>("DialogInput").Text.Trim() } }); El<Border>("DialogOverlay").Visibility = Visibility.Collapsed; }
                else if (action == "manual-save")
                {
                    string host = El<TextBox>("DialogInput").Text.Trim();
                    await Task.Run(() => workspace.Server.AddNativePeer(host)); El<Border>("DialogOverlay").Visibility = Visibility.Collapsed; lastPeerSignature = ""; RenderPeers();
                }
                else if (action == "pick-download") { string folder = NativeDialogs.PickFolder(this, "接收文件夹"); if (!string.IsNullOrEmpty(folder)) El<TextBox>("SettingDownload").Text = folder; }
                else if (action == "save-preferences") Save(new Dictionary<string, string> { { "name", El<TextBox>("SettingName").Text.Trim() }, { "dlDir", El<TextBox>("SettingDownload").Text.Trim() }, { "autostart", Checked("SettingAutostart") }, { "apush", Checked("SettingAutoAccept") }, { "sharing", Checked("SettingSharing") } });
                else if (action == "save-network") Save(new Dictionary<string, string> { { "relay", El<TextBox>("SettingRelay").Text.Trim() }, { "room", El<TextBox>("SettingRoom").Text.Trim() }, { "cloud", Checked("SettingCloud") } });
                else if (action == "firewall")
                {
                    bool ready = await Task.Run(() => Util.EnsureFirewallRules());
                    workspace.Config.FirewallDone = ready; workspace.Config.Save();
                    Notify(ready ? "防火墙放行已修复" : "防火墙放行未完成");
                }
            }
            catch (Exception ex) { Notify(ex.Message); }
        }
        void OpenDialog(string title, string action) { El<TextBlock>("DialogTitle").Text = title; El<Button>("DialogConfirm").Tag = action; El<Border>("DialogOverlay").Visibility = Visibility.Visible; El<TextBox>("DialogInput").Focus(); El<TextBox>("DialogInput").SelectAll(); }
        async Task RunUpdate(bool download)
        {
            if (updateBusy) return;
            updateBusy = true;
            El<Button>("CheckUpdate").IsEnabled = El<Button>("DownloadUpdate").IsEnabled = false;
            try
            {
                if (!download)
                {
                    availableUpdate = null;
                    El<Button>("DownloadUpdate").Visibility = Visibility.Collapsed;
                    El<TextBlock>("UpdateStatus").Text = "正在检查更新…";
                    availableUpdate = await Task.Run(() => AppUpdate.Check());
                    if (IsDisposed) return;
                    El<TextBlock>("UpdateStatus").Text = availableUpdate == null ? "已是最新版本 " + AppUpdate.Current : "发现新版本 " + availableUpdate.Version;
                }
                else if (availableUpdate != null)
                {
                    El<TextBlock>("UpdateStatus").Text = "正在下载更新…";
                    var progress = new Progress<int>(value => { if (!IsDisposed && updateBusy) El<TextBlock>("UpdateStatus").Text = "正在下载更新 " + value + "%"; });
                    string directory = config.DownloadDir;
                    string path = await Task.Run(() => AppUpdate.Download(availableUpdate, directory, value => ((IProgress<int>)progress).Report(value)));
                    if (IsDisposed) return;
                    El<TextBlock>("UpdateStatus").Text = "已校验并保存。传输结束后从托盘退出，将下载文件替换原程序。";
                    Reveal(path);
                }
            }
            catch (Exception ex) { if (!IsDisposed) El<TextBlock>("UpdateStatus").Text = ex.Message; }
            finally
            {
                updateBusy = false;
                if (!IsDisposed)
                {
                    El<Button>("CheckUpdate").IsEnabled = El<Button>("DownloadUpdate").IsEnabled = true;
                    El<Button>("DownloadUpdate").Visibility = availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
        void Save(Dictionary<string, string> values) { string error = workspace.Server.SaveNativeConfig(values); if (error != null) throw new IOException(error); Notify("设置已保存"); lastPeerSignature = ""; Update(); }
        string Checked(string name) { return El<CheckBox>(name).IsChecked == true ? "1" : "0"; }
        void FillSettings()
        {
            El<TextBox>("SettingName").Text = config.DeviceName; El<TextBox>("SettingDownload").Text = config.DownloadDir;
            El<TextBox>("SettingRelay").Text = config.RelayUrl; El<TextBox>("SettingRoom").Text = config.RoomCode;
            El<CheckBox>("SettingAutostart").IsChecked = Util.GetAutoStart(); El<CheckBox>("SettingAutoAccept").IsChecked = config.AutoAcceptPush; El<CheckBox>("SettingSharing").IsChecked = config.SharingEnabled; El<CheckBox>("SettingCloud").IsChecked = config.CloudEnabled;
        }
        void SendPaths(IEnumerable<string> files) { try { foreach (string file in files) workspace.Send(file); } catch (Exception ex) { Notify(ex.Message); } lastRows = ""; RenderRows(); }
        void DragOverFiles(object sender, System.Windows.DragEventArgs e) { bool valid = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && workspace.Session != null && workspace.Session.Connected; e.Effects = valid ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; El<Border>("DropOutline").Visibility = valid ? Visibility.Visible : Visibility.Collapsed; e.Handled = true; }
        static void Reveal(string path) { if (Directory.Exists(path)) Process.Start(path); else if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\""); }
        void RenderPeers()
        {
            var canvas = El<Canvas>("PeerNodes"); double w = canvas.ActualWidth, h = canvas.ActualHeight; if (w < 1 || h < 1) return;
            var peers = workspace.Discovery.GetPeers().Where(p => p.Online).OrderBy(p => p.Id).ToList();
            int count = w < 1000 ? 4 : 6, pages = Math.Max(1, (peers.Count + count - 1) / count); peerPage = Math.Max(0, Math.Min(pages - 1, peerPage));
            string signature = config.DeviceName + ":" + peerPage + ":" + w + ":" + h + ":" + string.Join("|", peers.Select(p => p.Id + p.Name));
            El<TextBlock>("RadarStatus").Text = peers.Count == 0 ? "搜索中" : peers.Count + " 台设备在线";
            El<StackPanel>("PeerPager").Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed; El<TextBlock>("PeerPage").Text = (peerPage + 1) + " / " + pages;
            if (signature == lastPeerSignature) return; lastPeerSignature = signature; canvas.Children.Clear();
            AddDevice(canvas, config.DeviceName, "本机", "rename", w * .5, h * .5, true);
            double[,] positions = { { .27, .28 }, { .75, .68 }, { .26, .73 }, { .76, .26 }, { .5, .18 }, { .5, .83 } };
            int index = 0; foreach (var peer in peers.Skip(peerPage * count).Take(count)) { AddDevice(canvas, peer.Name, "连接 →", "connect:" + peer.Id, w * positions[index, 0], h * positions[index, 1], false); index++; }
        }
        void AddDevice(Canvas canvas, string name, string hint, string action, double x, double y, bool self)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var device = new Border { Width = self ? 98 : 58, Height = self ? 98 : 58, BorderBrush = B(self ? "#7DEFDB" : "#E9C79A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(60), Background = B("#111719"), HorizontalAlignment = HorizontalAlignment.Center };
            device.Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M4,4 L20,4 20,16 4,16 Z M4,16 L2,20 22,20 20,16 M10,20 L14,20"), Stroke = device.BorderBrush, StrokeThickness = 1.2, Stretch = Stretch.Uniform, Width = self ? 36 : 24, Height = self ? 32 : 22 };
            content.Children.Add(device);var label = TextNode(name, 13); label.TextAlignment = TextAlignment.Center; label.Margin = new Thickness(0, 10, 0, 4); content.Children.Add(label);var help = TextNode(hint, 11, "#919B9F"); help.TextAlignment = TextAlignment.Center; content.Children.Add(help);
            var button = new Button { Content = content, Tag = action, Width = 164, ToolTip = name, Style = (Style)root.FindResource("DeviceButton") };
            canvas.Children.Add(button); Canvas.SetLeft(button, x - 82); Canvas.SetTop(button, y - (self ? 77 : 57));
        }
        static string Bytes(long bytes) { if (bytes < 1024) return bytes + " B"; if (bytes < 1048576) return (bytes / 1024d).ToString("0.0") + " KB"; if (bytes < 1073741824) return (bytes / 1048576d).ToString("0.0") + " MB"; return (bytes / 1073741824d).ToString("0.00") + " GB"; }
        void RenderRows()
        {
            bool history = view == "history"; if (!history && view != "session") return;
            var session = workspace.Session; var sends = workspace.Sends;
            string query = history ? El<TextBox>("HistorySearch").Text.Trim() : "";
            var rows = workspace.History.Snapshot().Where(x => x.Type == 0 || x.Type == 2);
            if (history) rows = rows.Where(x => (historyFilter < 0 || x.Type == historyFilter) && (x.Name + " " + x.Peer).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            else rows = session == null ? rows.Take(0) : rows.Where(x => x.Peer == session.Peer.Name && x.Time >= session.Started.AddSeconds(-1));
            var items = rows.Take(200).ToList();
            string signature = view + string.Join("|", items.Select(x => x.Id + x.Status + x.Progress + x.Size + x.LocalPath)) + string.Join("|", sends.Select(x => x.Rate / 1048576));
            if (signature == lastRows) return; lastRows = signature;
            var list = El<StackPanel>(history ? "HistoryRows" : "TransferRows"); list.Children.Clear();
            if (!history) El<TextBlock>("TransferCount").Text = items.Count == 0 ? "" : items.Count + " 项";
            if (items.Count == 0) { var empty = TextNode(history ? "暂无传输记录" : "暂无传输", 13, "#78868C"); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 90, 0, 0); list.Children.Add(empty); return; }
            foreach (var item in items)
            {
                var send = sends.FirstOrDefault(x => x.History.Id == item.Id); string color = item.Type == 0 ? "#E9C79A" : "#7DEFDB";
                var grid = new Grid { Margin = new Thickness(0, 18, 0, 18) }; foreach (var width in new[] { new GridLength(44), new GridLength(1, GridUnitType.Star), new GridLength(180), new GridLength(78) }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                var direction = TextNode(item.Type == 0 ? "↓" : "↑", 25, color); grid.Children.Add(direction);
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) }; names.Children.Add(TextNode(item.Name, 14)); var meta = TextNode((item.Type == 0 ? "接收" : "发送") + " · " + Bytes(item.Size) + (history ? " · " + item.Peer : ""), 11, "#919B9F"); meta.Margin = new Thickness(0, 7, 0, 0); names.Children.Add(meta); Grid.SetColumn(names, 1); grid.Children.Add(names);
                var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; status.Children.Add(TextNode(item.Status, 12, item.Progress < 0 ? "#FF928E" : color));
                if (item.Progress >= 0 && item.Progress < 100)
                {
                    long rate = send != null ? send.Rate : item.TransferRate;
                    var details = TextNode(item.Progress + "%" + (rate > 0 ? " · " + Bytes(rate) + "/s" : ""), 11, "#919B9F"); details.Margin = new Thickness(0, 6, 0, 0); status.Children.Add(details);
                    status.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = item.Progress, Height = 2, Margin = new Thickness(0, 7, 16, 0), Foreground = B(color), Background = B("#2A3034"), BorderThickness = new Thickness(0) });
                }
                else if (history) status.Children.Add(TextNode(item.Time.ToString("MM-dd HH:mm"), 11, "#919B9F"));
                Grid.SetColumn(status, 2); grid.Children.Add(status);
                var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(commands, 3); grid.Children.Add(commands);
                if (send != null && item.Progress >= 0 && item.Progress < 100) commands.Children.Add(RowButton("×", "取消传输", "cancel:" + item.Id));
                if (send != null && item.Progress < 0) commands.Children.Add(RowButton("↻", "重新发送", "retry:" + item.Id));
                if (!string.IsNullOrEmpty(item.LocalPath)) commands.Children.Add(RowButton("↗", "打开所在位置", "reveal:" + item.LocalPath));
                list.Children.Add(new Border { BorderBrush = B("#2A3034"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid });
            }
        }
        Button RowButton(string icon, string tooltip, string tag) { return new Button { Content = icon, ToolTip = tooltip, Tag = tag, Width = 34, Height = 34, FontSize = 19, Style = (Style)root.FindResource("QuietButton") }; }
    }

    sealed class NativeRadar : FrameworkElement
    {
        readonly DispatcherTimer timer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        public NativeRadar()
        {
            IsHitTestVisible = false;
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (s,e) => InvalidateVisual();
            IsVisibleChanged += (s,e) => { if (IsVisible && SystemParameters.ClientAreaAnimation) timer.Start(); else timer.Stop(); InvalidateVisual(); };
        }
        public void Stop() { timer.Stop(); }
        static Pen Line(byte alpha, double thickness = 1, bool warm = false) { var brush = new SolidColorBrush(Color.FromArgb(alpha, warm ? (byte)233 : (byte)125, warm ? (byte)199 : (byte)239, warm ? (byte)154 : (byte)219)); brush.Freeze();var pen = new Pen(brush, thickness); pen.Freeze(); return pen; }
        protected override void OnRender(DrawingContext d)
        {
            base.OnRender(d); double w = ActualWidth, h = ActualHeight, radius = Math.Min(w * .55, h * .83); if (w < 1 || h < 1) return;
            var center = new Point(w / 2, h / 2 - 28); double t = SystemParameters.ClientAreaAnimation ? clock.Elapsed.TotalSeconds : 1.6;
            d.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            for (int i = 1; i <= 9; i++) { double r = 65 + (radius - 65) * i / 9; d.DrawEllipse(null, Line(i % 3 == 0 ? (byte)42 : (byte)20), center, r * 1.18, r * .68); }
            double bearing = radius * .72;
            for (int i = 0; i < 120; i++) { double a = i * Math.PI / 60, tick = i % 5 == 0 ? 9 : 3;d.DrawLine(Line(i % 5 == 0 ? (byte)70 : (byte)32), new Point(center.X + Math.Cos(a) * bearing * 1.18, center.Y + Math.Sin(a) * bearing * .68), new Point(center.X + Math.Cos(a) * (bearing + tick) * 1.18, center.Y + Math.Sin(a) * (bearing + tick) * .68)); }
            for (int i = 0; i < 4; i++) { double phase = (t / 9 + i / 4d) % 1, r = 65 + phase * radius;byte alpha = (byte)(Math.Sin(phase * Math.PI) * 130);d.DrawEllipse(null, Line((byte)(alpha / 8), 7), center, r * 1.18, r * .68); d.DrawEllipse(null, Line(alpha, 1.2), center, r * 1.18, r * .68); }
            for (int i = 0; i < 3; i++)
            {
                double r = radius * new[] { .49, .72, .94 }[i], a = t * (i == 1 ? -.07 : .055) + i * 2.2;
                var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(new Point(center.X + Math.Cos(a) * r * 1.18, center.Y + Math.Sin(a) * r * .68), false, false); for (int j = 1; j <= 16; j++) { double angle = a + .4 * j / 16; context.LineTo(new Point(center.X + Math.Cos(angle) * r * 1.18, center.Y + Math.Sin(angle) * r * .68), true, false); } } geometry.Freeze(); d.DrawGeometry(null, Line(190, 1.6, i == 1), geometry);
            }
            d.Pop();
        }
    }
}
