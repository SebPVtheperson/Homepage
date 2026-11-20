using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace Homepage;

// Search, paste, broken-link check, backup, dark mode, tray icon, global shortcut and start with Windows.
public sealed partial class MainWindow
{
    static bool Dark;
    static readonly SolidColorBrush PanelBrush = new(Colors.White);
    bool trayTipShown;
    readonly HashSet<string> broken = new();
    readonly TextBox searchBox = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Ink, CaretBrush = Ink, FontSize = 14, Padding = new Thickness(4) };
    readonly ListBox searchList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Ink, MaxHeight = 320, Margin = new Thickness(0, 4, 0, 0) };
    readonly Border searchPanel = new() { Width = 420, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0), Background = PanelBrush, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Visibility = Visibility.Collapsed };

    /// <summary>True for the normal app: keeps running in the tray and listens for Ctrl+Alt+Q.</summary>
    public bool Resident { get; set; }

    void BuildExtras(Grid root, StackPanel toolbar)
    {
        var stack = new StackPanel(); stack.Children.Add(searchBox); stack.Children.Add(searchList); searchPanel.Child = stack;
        Grid.SetRow(searchPanel, 1); root.Children.Add(searchPanel);
        var find = Tiny("", "Search · Ctrl+F, or just start typing", (_, _) => OpenSearch(""));
        find.FontFamily = new FontFamily("Segoe MDL2 Assets"); find.FontSize = 11; toolbar.Children.Add(find);
        searchBox.TextChanged += (_, _) => FillSearch();
        searchBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (!searchPanel.IsKeyboardFocusWithin) CloseSearch(); }));
        searchList.PreviewMouseLeftButtonUp += (_, e) => { if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.Tag is Shortcut item) { CloseSearch(); Open(item); } };
        PreviewTextInput += (_, e) =>
        {
            if (searchPanel.Visibility == Visibility.Visible || Keyboard.FocusedElement is TextBoxBase || e.Text.Length == 0 || char.IsControl(e.Text[0]) || char.IsWhiteSpace(e.Text[0])) return;
            OpenSearch(e.Text); e.Handled = true;
        };
        if (!testMode) { LoadSettings(); ApplyColors(); }
        SourceInitialized += (_, _) => { if (Resident) StartResident(); };
    }

    // ---- Search ----
    void OpenSearch(string text)
    {
        searchPanel.Visibility = Visibility.Visible; searchBox.Text = text; searchBox.CaretIndex = text.Length; searchBox.Focus(); FillSearch();
    }
    void CloseSearch()
    {
        if (searchPanel.Visibility != Visibility.Visible) return;
        searchPanel.Visibility = Visibility.Collapsed; searchBox.Text = ""; searchList.Items.Clear();
    }
    void FillSearch()
    {
        searchList.Items.Clear(); var query = searchBox.Text.Trim(); if (query.Length == 0) return;
        var found = new List<(Shortcut Item, string Where, int Rank)>();
        void Check(Shortcut item, string where)
        {
            int at = item.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) found.Add((item, where, at == 0 ? 0 : 1));
            else if (item.Target.Contains(query, StringComparison.OrdinalIgnoreCase)) found.Add((item, where, 2));
        }
        foreach (var page in layout.Pages)
        {
            foreach (var item in page.Items) Check(item, page.Name);
            foreach (var group in page.Groups)
            {
                var tabs = group.AllTabs().ToList();
                foreach (var tab in tabs) foreach (var item in tab.Items) Check(item, page.Name + " › " + group.Label + (tabs.Count > 1 ? " › " + tab.Name : ""));
            }
        }
        foreach (var (item, where, _) in found.OrderBy(f => f.Rank).ThenBy(f => f.Item.Label, StringComparer.OrdinalIgnoreCase).Take(12))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Image { Source = iconAssets.For(item), Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
            var label = new TextBlock { Text = item.Label, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1); row.Children.Add(label);
            var place = new TextBlock { Text = where, FontSize = 10, Foreground = Ink, Opacity = .6, Margin = new Thickness(10, 0, 0, 0), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(place, 2); row.Children.Add(place);
            searchList.Items.Add(new ListBoxItem { Content = row, Tag = item, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = item.Target });
        }
        if (searchList.Items.Count > 0) searchList.SelectedIndex = 0;
    }

    /// <summary>Handles search, Ctrl+F and Ctrl+V. Returns true when the key needs no further handling.</summary>
    bool ExtraKey(KeyEventArgs e)
    {
        if (searchPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { CloseSearch(); viewport.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                var item = (searchList.SelectedItem as ListBoxItem)?.Tag as Shortcut; CloseSearch(); viewport.Focus();
                if (item is not null) Open(item);
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Up && searchList.Items.Count > 0)
            {
                searchList.SelectedIndex = Math.Clamp(searchList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, searchList.Items.Count - 1);
                searchList.ScrollIntoView(searchList.SelectedItem); e.Handled = true;
            }
            return true;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return false;
        if (e.Key == Key.F) { OpenSearch(""); e.Handled = true; return true; }
        if (e.Key == Key.V && Keyboard.FocusedElement is not TextBoxBase) { PasteTargets(); e.Handled = true; return true; }
        return false;
    }

    // ---- Paste to add ----
    void PasteTargets()
    {
        try
        {
            var over = Mouse.GetPosition(viewport);
            var point = over.X >= 0 && over.Y >= 0 && over.X <= viewport.ActualWidth && over.Y <= viewport.ActualHeight ? Mouse.GetPosition(canvas) : new Point(viewport.HorizontalOffset + 60, viewport.VerticalOffset + 60);
            if (Clipboard.ContainsFileDropList()) { AddTargets(Clipboard.GetFileDropList().Cast<string>().ToList(), null, point); return; }
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim().Trim('"') : "";
            if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
            if (text.Length > 0 && !text.Contains('\n') && (LayoutStore.IsWebsite(text) || File.Exists(text) || Directory.Exists(text))) AddTargets([text], null, point);
            else ShowStatus("Nothing to paste. Copy a web address, file or folder first.");
        }
        catch (Exception e) when (e is COMException or ArgumentException or IOException) { ShowStatus("Could not read the clipboard."); }
    }

    // ---- Broken links ----
    void FindBroken()
    {
        broken.Clear();
        foreach (var item in layout.AllItems())
        {
            try { var target = store.ResolveTarget(item.Target); if (!LayoutStore.IsWebsite(target) && !File.Exists(target) && !Directory.Exists(target)) broken.Add(item.Id); }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { broken.Add(item.Id); }
        }
        Render();
        var names = layout.AllItems().Where(i => broken.Contains(i.Id)).Select(i => i.Label).ToList();
        ShowStatus(names.Count == 0 ? "No broken links. Websites are not checked." : names.Count + " broken link" + (names.Count == 1 ? "" : "s") + " marked red: " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? "…" : ""));
    }

    // ---- Backup ----
    void SaveBackup()
    {
        var dialog = new SaveFileDialog { Title = "Save backup", FileName = "Homepage-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip", Filter = "Homepage backup|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            dirty = true; Persist();
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
            using (var zip = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create))
                foreach (var file in Directory.EnumerateFiles(store.DataDirectory, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = zip.CreateEntry(Path.GetRelativePath(store.DataDirectory, file).Replace('\\', '/'));
                    using var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var to = entry.Open(); from.CopyTo(to);
                }
            ShowStatus("Backup saved.");
        }
        catch (Exception e) { ShowStatus("Could not save backup: " + e.Message); }
    }
    void RestoreBackup()
    {
        var dialog = new OpenFileDialog { Title = "Choose a Homepage backup", Filter = "Homepage backup|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "Replace everything in Homepage with this backup? Ctrl+Z undoes it.", "Restore backup", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            using var zip = ZipFile.OpenRead(dialog.FileName);
            var saved = zip.GetEntry("layout.json") ?? throw new InvalidDataException("This file is not a Homepage backup.");
            Layout restored; using (var reader = new StreamReader(saved.Open())) restored = LayoutStore.Deserialize(reader.ReadToEnd());
            var icons = Path.Combine(store.DataDirectory, "icons");
            foreach (var icon in zip.Entries.Where(i => i.Name.Length > 0 && i.FullName == "icons/" + i.Name))
            {
                Directory.CreateDirectory(icons); var to = Path.Combine(icons, icon.Name);
                if (!File.Exists(to)) icon.ExtractToFile(to);
            }
            undo.Push(layout); layout = restored; selected = null; picked.Clear(); broken.Clear(); RestoreCanvas(); dirty = true; Persist(); ShowStatus("Backup restored.");
        }
        catch (Exception e) { ShowStatus("Could not restore backup: " + e.Message); }
    }

    // ---- Settings and dark mode ----
    string SettingsPath => Path.Combine(store.DataDirectory, "settings.json");
    void LoadSettings()
    {
        Dark = false;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            Dark = doc.RootElement.TryGetProperty("Dark", out var dark) && dark.ValueKind == JsonValueKind.True;
            trayTipShown = doc.RootElement.TryGetProperty("TrayTipShown", out var tip) && tip.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    void SaveSettings()
    {
        try { Directory.CreateDirectory(store.DataDirectory); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Dark, TrayTipShown = trayTipShown })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowStatus("Could not save settings: " + e.Message); }
    }
    static Color SelectFill => Dark ? Color.FromRgb(47, 61, 82) : Color.FromRgb(231, 238, 243);
    static Color SelectLine => Dark ? Color.FromRgb(57, 69, 90) : Color.FromRgb(190, 209, 222);
    static Color SnapOn => Dark ? Color.FromRgb(47, 61, 82) : Color.FromRgb(224, 234, 242);
    static Color GridDot => Dark ? Color.FromRgb(34, 38, 47) : Color.FromRgb(215, 220, 226);
    void ApplyColors()
    {
        Ink.Color = Dark ? Color.FromRgb(232, 234, 239) : Color.FromRgb(74, 80, 87);
        Line.Color = Dark ? Color.FromRgb(42, 46, 56) : Color.FromRgb(224, 227, 231);
        Accent.Color = Dark ? Color.FromRgb(122, 150, 196) : Color.FromRgb(82, 125, 155);
        Paper.Color = Dark ? Color.FromRgb(11, 12, 15) : Color.FromRgb(249, 250, 251);
        PanelBrush.Color = Dark ? Color.FromRgb(18, 20, 26) : Colors.White;
        if (Dark) Resources[typeof(Button)] = FlatButtonStyle(); else Resources.Remove(typeof(Button));
    }
    // The stock button lights up pale blue on hover, which is unreadable on a dark background.
    static Style FlatButtonStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                  <Grid>
                    <Border x:Name="Hover" Background="#22FFFFFF" CornerRadius="3" Visibility="Collapsed"/>
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" Margin="{TemplateBinding Padding}"/>
                  </Grid>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hover" Property="Visibility" Value="Visible"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    void AddExtraSettings(ContextMenu menu)
    {
        menu.Items.Add(Entry("Find broken links", FindBroken));
        menu.Items.Add(Entry("Save backup…", SaveBackup));
        menu.Items.Add(Entry("Restore backup…", RestoreBackup));
        menu.Items.Add(new Separator());
        menu.Items.Add(Toggle("Dark mode", Dark, () => { Dark = !Dark; SaveSettings(); ApplyColors(); Render(); }));
        if (!Resident) return;
        menu.Items.Add(Toggle("Start with Windows", StartsWithWindows(), () => SetStartWithWindows(!StartsWithWindows())));
        menu.Items.Add(Entry("Quit Homepage", Quit));
    }

    // ---- Tray icon, Ctrl+Alt+Q, start with Windows ----
    const int HotkeyId = 1, HotkeyMessage = 0x0312;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    System.Windows.Forms.NotifyIcon? tray;
    HwndSource? windowSource;
    bool quitting;

    void StartResident()
    {
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource.AddHook(WindowMessage);
        // Alt (1) + Ctrl (2) + no auto-repeat (0x4000), key Q (0x51).
        if (!RegisterHotKey(windowSource.Handle, HotkeyId, 0x4003, 0x51)) ShowStatus("Ctrl+Alt+Q is taken by another app, so that shortcut is off.");
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Homepage", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        tray = new System.Windows.Forms.NotifyIcon { Icon = TrayIcon(), Text = "Homepage · Ctrl+Alt+Q", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
        // Never block Windows from signing out or shutting down.
        if (Application.Current is { } app) app.SessionEnding += (_, _) => quitting = true;
    }
    IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == HotkeyMessage && wParam.ToInt32() == HotkeyId)
        {
            if (IsVisible && IsActive && WindowState != WindowState.Minimized) HideToTray(); else ShowFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }
    public void ShowFromTray()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Topmost = true; Topmost = false; viewport.Focus();
    }
    void HideToTray()
    {
        CloseSearch(); Hide();
        if (trayTipShown || tray is null) return;
        trayTipShown = true; SaveSettings();
        tray.ShowBalloonTip(5000, "Homepage is still running", "Press Ctrl+Alt+Q to bring it back. Right-click this icon to quit.", System.Windows.Forms.ToolTipIcon.None);
    }
    void Quit() { quitting = true; Close(); }
    /// <summary>The X button hides the window instead of ending the app.</summary>
    bool HideInsteadOfClose(CancelEventArgs e)
    {
        if (!Resident || quitting || tray is null) return false;
        e.Cancel = true; HideToTray(); return true;
    }
    void DisposeExtras()
    {
        if (windowSource is not null) UnregisterHotKey(windowSource.Handle, HotkeyId);
        tray?.Dispose(); tray = null;
    }
    static System.Drawing.Icon TrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(82, 125, 155));
            foreach (var (x, y) in new[] { (3, 3), (18, 3), (3, 18), (18, 18) }) graphics.FillRectangle(brush, x, y, 11, 11);
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
    static string RunCommand => "\"" + Environment.ProcessPath + "\" --tray";
    static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue("Homepage") as string, RunCommand, StringComparison.OrdinalIgnoreCase);
    }
    void SetStartWithWindows(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("Homepage", RunCommand); else key.DeleteValue("Homepage", false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { ShowStatus("Could not change the startup setting: " + e.Message); }
    }
}
