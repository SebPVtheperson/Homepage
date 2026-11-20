using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Homepage;

public sealed partial class MainWindow : Window
{
    const string DragFormat = "Homepage.Shortcut.Id";
    const double TileWidth = 88, TileHeight = 88, RowHeight = 36;
    static readonly SolidColorBrush Ink = new SolidColorBrush(Color.FromRgb(74, 80, 87));
    static readonly SolidColorBrush Line = new SolidColorBrush(Color.FromRgb(224, 227, 231));
    static readonly SolidColorBrush Accent = new SolidColorBrush(Color.FromRgb(82, 125, 155));
    static readonly SolidColorBrush Paper = new SolidColorBrush(Color.FromRgb(249, 250, 251));
    readonly LayoutStore store;
    readonly IconAssets iconAssets;
    readonly WebsiteMetadataService websiteMetadata;
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentDictionary<string, byte> pendingMetadata = new();
    readonly UndoHistory undo = new();
    readonly ScrollViewer viewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
    readonly Canvas canvas = new() { Width = 1600, Height = 1000, Background = Paper, AllowDrop = true };
    readonly Canvas dragOverlay = new() { IsHitTestVisible = false, ClipToBounds = true };
    readonly Border landingMarker = new() { BorderBrush = Accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(35, 82, 125, 155)), Visibility = Visibility.Collapsed, Tag = "shortcut-landing" };
    Border? dragGhost;
    FrameworkElement? dragSource;
    Shortcut? draggedShortcut;
    Point dragGrab;
    double originalDragOpacity;
    readonly TextBlock status = new() { FontSize = 11, Foreground = Ink, TextWrapping = TextWrapping.Wrap, MaxWidth = 680, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel tools = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl pageTabs = new() { Margin = new Thickness(10, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
    bool restoringScroll;
    readonly Button editButton;
    readonly Button snapButton;
    readonly DispatcherTimer scrollSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Layout layout;
    bool editing;
    string? selected;
    Point? panStart;
    double panX, panY;
    Point? shortcutStart;
    string? shortcutPressed;
    bool dirty;
    bool loaded;
    readonly bool testMode;
    public Layout CurrentLayout => layout;
    public Canvas Surface => canvas;
    public ScrollViewer Viewport => viewport;

    public MainWindow(LayoutStore store, bool testMode = false, WebsiteMetadataService? websiteMetadata = null)
    {
        this.store = store; this.testMode = testMode; layout = store.Load();
        iconAssets = new IconAssets(store); this.websiteMetadata = websiteMetadata ?? new WebsiteMetadataService(store);
        Title = "Homepage"; Width = 1080; Height = 720; MinWidth = 480; MinHeight = 320;
        Background = Paper; Foreground = Ink; FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        UseLayoutRounding = true; SnapsToDevicePixels = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Resources.Add(typeof(ScrollBar), SlimScrollbarStyle());
        Resources.Add(SystemParameters.VerticalScrollBarWidthKey, 8.0);
        Resources.Add(SystemParameters.HorizontalScrollBarHeightKey, 8.0);
        var root = new Grid { Background = Paper }; root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(12, 3, 10, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition());
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Tiny("⚙", "Settings", (_, _) => OpenSettings()));
        header.Children.Add(brand);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(Tiny("+", "Add shortcut or group", (_, _) => OpenAddMenu()));
        tools.Children.Add(Tiny("↶", "Undo · Ctrl+Z", (_, _) => Undo()));
        snapButton = Tiny("⌗", "Toggle 16 px grid snapping", (_, _) => Change(() => layout.Snap = !layout.Snap)); tools.Children.Add(snapButton);
        toolbar.Children.Add(tools);
        editButton = Tiny("✎", "Edit layout · Ctrl+E", (_, _) => SetEditing(!editing)); toolbar.Children.Add(editButton);
        brand.Children.Add(toolbar);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pageTabs, 1); header.Children.Add(pageTabs);
        status.MaxWidth = 300; status.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(status, 2); header.Children.Add(status);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5, 0, 5, 5), GlassFrameThickness = new Thickness(1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var windowButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Button Caption(string glyph, string tip, Action action)
        {
            var button = Tiny(glyph, tip, (_, _) => action()); button.Width = 28; button.Height = 24; button.FontSize = 10; button.FontFamily = new FontFamily("Segoe MDL2 Assets"); button.Cursor = Cursors.Arrow; return button;
        }
        var maximizeButton = Caption("", "Maximize", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        windowButtons.Children.Add(Caption("", "Minimize", () => WindowState = WindowState.Minimized));
        windowButtons.Children.Add(maximizeButton);
        windowButtons.Children.Add(Caption("", "Close", Close));
        Grid.SetColumn(windowButtons, 3); header.Children.Add(windowButtons);
        var headerBar = new Border { Background = Brushes.Transparent }; root.Children.Add(headerBar);
        void DragWindow(object sender, MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d is ButtonBase or ScrollBar) return;
            if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; e.Handled = true; return; }
            if (WindowState == WindowState.Maximized)
            {
                var inside = e.GetPosition(this); var screen = PointToScreen(inside); var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity; var at = scale.Transform(screen);
                double ratio = inside.X / Math.Max(1, ActualWidth); WindowState = WindowState.Normal; Left = at.X - RestoreBounds.Width * ratio; Top = at.Y - 14;
            }
            try { DragMove(); } catch (InvalidOperationException) { }
            e.Handled = true;
        }
        headerBar.MouseLeftButtonDown += DragWindow; header.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(DragWindow), true);
        headerBar.Cursor = Cursors.SizeAll; header.Cursor = Cursors.SizeAll;
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            root.Margin = new Thickness(maximized ? 8 : 0); maximizeButton.Content = maximized ? "" : ""; maximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        };
        root.Children.Add(header);
        viewport.Content = canvas; Grid.SetRow(viewport, 1); root.Children.Add(viewport); Content = root;
        Grid.SetRow(dragOverlay, 1); root.Children.Add(dragOverlay); dragOverlay.Children.Add(landingMarker);
        BuildExtras(root, toolbar);
        root.AddHandler(DragDrop.DragOverEvent, new DragEventHandler(PreviewShortcutDrag), true);
        root.AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, e) =>
        {
            if (!new Rect(dragOverlay.RenderSize).Contains(e.GetPosition(dragOverlay))) HideDragPreview();
        }), true);
        canvas.ContextMenu = CanvasMenu();
        canvas.MouseLeftButtonDown += BeginPan;
        canvas.MouseMove += Pan;
        canvas.MouseLeftButtonUp += EndPan;
        canvas.LostMouseCapture += (_, _) => panStart = null;
        canvas.DragOver += CanvasDragOver;
        canvas.Drop += (_, e) => DropOn(e, null);
        viewport.PreviewMouseWheel += (_, e) =>
        {
            // Let each group's own ScrollViewer consume its wheel events first.
            if (FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject) is { } scroller && scroller != viewport && scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Auto) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset - e.Delta);
            else viewport.ScrollToVerticalOffset(viewport.VerticalOffset - e.Delta);
            e.Handled = true;
        };
        viewport.ScrollChanged += (_, e) =>
        {
            if (!loaded || restoringScroll || e.OriginalSource != viewport) return;
            if (e.HorizontalChange > 0 && viewport.HorizontalOffset + viewport.ViewportWidth >= canvas.Width - 100) canvas.Width += 600;
            if (e.VerticalChange > 0 && viewport.VerticalOffset + viewport.ViewportHeight >= canvas.Height - 100) canvas.Height += 600;
            layout.ScrollX = viewport.HorizontalOffset; layout.ScrollY = viewport.VerticalOffset;
            dirty = true; scrollSaveTimer.Stop(); scrollSaveTimer.Start();
        };
        scrollSaveTimer.Tick += (_, _) => { scrollSaveTimer.Stop(); Persist(); };
        PreviewKeyDown += KeyPressed;
        Loaded += (_, _) =>
        {
            viewport.ScrollToHorizontalOffset(layout.ScrollX); viewport.ScrollToVerticalOffset(layout.ScrollY);
            loaded = true;
            if (store.LoadMessage is { } message) ShowStatus(message);
            if ((!File.Exists(store.SavePath) || store.NeedsUpgrade) && store.CanSave && !testMode) { dirty = true; Persist(); }
            if (!testMode) foreach (var item in layout.AllItems().Where(i => LayoutStore.IsWebsite(i.Target) && (!i.WebsiteMetadataFetched || SharePointMetadata.IsSharePoint(i.Target))).ToArray()) _ = RefreshWebsiteAsync(item);
        };
        Closing += ClosingWindow;
        Closed += (_, _) => { DisposeExtras(); EndShortcutDrag(); lifetime.Cancel(); this.websiteMetadata.Dispose(); };
        Render();
    }

    static Button Tiny(string glyph, string tip, RoutedEventHandler action)
    {
        var button = new Button { Content = glyph, ToolTip = tip, Width = 20, Height = 20, FontSize = 12, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Foreground = Ink, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        button.Click += action; return button;
    }
    static Style SlimScrollbarStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="8"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True"
                       Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                       Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}">
                  <Track.DecreaseRepeatButton><RepeatButton x:Name="Before" Command="ScrollBar.PageUpCommand" Focusable="False" Opacity="0"/></Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb MinWidth="8" MinHeight="8">
                      <Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="#C9CFD5" CornerRadius="3" Margin="2"/></ControlTemplate></Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton><RepeatButton x:Name="After" Command="ScrollBar.PageDownCommand" Focusable="False" Opacity="0"/></Track.IncreaseRepeatButton>
                </Track>
                <ControlTemplate.Triggers>
                  <Trigger Property="Orientation" Value="Horizontal">
                    <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                    <Setter TargetName="Before" Property="Command" Value="ScrollBar.PageLeftCommand"/>
                    <Setter TargetName="After" Property="Command" Value="ScrollBar.PageRightCommand"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="8"/></Trigger>
          </Style.Triggers>
        </Style>
        """);
    static MenuItem Entry(string text, Action action)
    {
        var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); return item;
    }
    static MenuItem Toggle(string text, bool value, Action action)
    {
        var item = Entry(text, action); item.IsCheckable = true; item.IsChecked = value; return item;
    }
    ContextMenu CanvasMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            var point = Mouse.GetPosition(canvas); menu.Items.Clear();
            AddEntries(menu, null, point);
            menu.Items.Add(new Separator());
            menu.Items.Add(Toggle("Edit layout · Ctrl+E", editing, () => SetEditing(!editing)));
            menu.Items.Add(Toggle("Snap to grid", layout.Snap, () => Change(() => layout.Snap = !layout.Snap)));
            var undoEntry = Entry("Undo · Ctrl+Z", Undo); undoEntry.IsEnabled = undo.Count > 0; menu.Items.Add(undoEntry);
            menu.Items.Add(Entry("Open saved-data folder", OpenData));
            if (!store.CanSave) menu.Items.Add(Entry("Recover saved layout…", Recover));
        };
        return menu;
    }
    void AddEntries(ContextMenu menu, Group? group, Point point)
    {
        menu.Items.Add(Entry("Add files or applications…", () => AddFiles(group, point)));
        menu.Items.Add(Entry("Add directory…", () => AddDirectory(group, point)));
        menu.Items.Add(Entry("Add website…", () => AddWebsite(group, point)));
        if (group is null) menu.Items.Add(Entry("New group…", () => AddGroup(point)));
        menu.Items.Add(Entry(group is null ? "New page tab…" : "New group tab…", () =>
        {
            var name = Prompt(group is null ? "New page tab" : "New group tab", "Name", group is null ? "New page" : "New tab");
            if (name is null) return;
            if (group is null) AddPage(name); else AddGroupTab(group, name);
        }));
        if (group is not null) menu.Items.Add(Entry("New nested tab…", () => { var name = Prompt("New nested tab", "Name", "New tab"); if (name is not null) AddNestedTab(group, group.ActiveTab, name); }));
    }
    void OpenSettings()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        menu.Items.Add(Entry("Import bookmarks…", () => { var dialog = new BookmarkDialog(this); if (dialog.ShowDialog() == true && dialog.SelectedNode is { } node) ImportBookmarks(node); }));
        AddExtraSettings(menu);
        menu.PlacementTarget = MainWindow.FindDescendants<Button>(this).FirstOrDefault(b => b.Content as string == "⚙"); menu.IsOpen = true;
    }
    public Group? ImportBookmarks(BookmarkNode node)
    {
        if (!node.Folder && !node.Supported) { ShowStatus("This bookmark is not an HTTP/HTTPS website."); return null; }
        Group? group = null; List<Shortcut> imported = [];
        var point = new Point(Snap(viewport.HorizontalOffset + 60), Snap(viewport.VerticalOffset + 60));
        if (node.Folder) { group = BookmarkReader.MakeGroup(node, point.X, point.Y); imported = group.AllTabs().SelectMany(t => t.Items).ToList(); }
        else { var item = BookmarkReader.MakeShortcut(node); item.X = point.X; item.Y = point.Y; imported.Add(item); }
        Change(() => { if (group is not null) layout.Groups.Add(group); else layout.Items.AddRange(imported); });
        ShowStatus("Imported " + imported.Count + " bookmark" + (imported.Count == 1 ? "" : "s") + (node.Skipped > 0 ? " · Skipped " + node.Skipped + " unsupported link(s) or separator(s)." : "."));
        if (!testMode) foreach (var item in imported) _ = RefreshWebsiteAsync(item);
        return group;
    }
    void OpenAddMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom, PlacementTarget = tools };
        AddEntries(menu, null, new Point(viewport.HorizontalOffset + 60, viewport.VerticalOffset + 60)); menu.IsOpen = true;
    }
    ScrollViewer TabStrip(IEnumerable<(string Id, string Name)> tabs, string active, Action<string> select,
        Func<string, ContextMenu> menu, Action<string, string> reorder, string format)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var scroller = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        foreach (var (id, name) in tabs)
        {
            var button = new Button { Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 }, ToolTip = name,
                FontSize = 11, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(1, 0, 1, 0), MinHeight = 18,
                Background = Brushes.Transparent, Foreground = id == active ? Accent : Ink,
                BorderBrush = id == active ? Accent : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1), AllowDrop = true, Tag = id, Cursor = Cursors.Arrow };
            button.Click += (_, _) => select(id);
            button.ContextMenu = menu(id);
            Point? start = null;
            button.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(button);
            button.PreviewMouseLeftButtonUp += (_, _) => start = null;
            button.PreviewMouseMove += (_, e) =>
            {
                if (!editing || id.StartsWith("contents:", StringComparison.Ordinal) || e.LeftButton != MouseButtonState.Pressed || start is not { } p) return;
                var now = e.GetPosition(button);
                if (Math.Abs(now.X - p.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - p.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                start = null; DragDrop.DoDragDrop(button, new DataObject(format, id), DragDropEffects.Move); e.Handled = true;
            };
            button.DragOver += (_, e) => { e.Effects = editing && e.Data.GetDataPresent(format) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            button.Drop += (_, e) => { if (editing && e.Data.GetDataPresent(format) && e.Data.GetData(format) is string source) reorder(source, id); e.Handled = true; };
            if (id == active) button.Loaded += (_, _) => { var x = button.TranslatePoint(new Point(), panel).X; if (x + button.ActualWidth > scroller.HorizontalOffset + scroller.ViewportWidth) scroller.ScrollToHorizontalOffset(x + button.ActualWidth - scroller.ViewportWidth); };
            panel.Children.Add(button);
        }
        scroller.PreviewMouseWheel += (_, e) => { scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta); e.Handled = true; };
        scroller.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) =>
        {
            if (!editing || !e.Data.GetDataPresent(format)) return;
            var x = e.GetPosition(scroller).X;
            if (x < 20) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - 14);
            else if (x > scroller.ActualWidth - 20) scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + 14);
        }), true);
        return scroller;
    }
    int scrollRestoreGeneration;
    void RestoreCanvas()
    {
        restoringScroll = true;
        int generation = ++scrollRestoreGeneration;
        canvas.Width = 1600; canvas.Height = 1000;
        Render(); UpdateLayout();
        viewport.ScrollToHorizontalOffset(layout.ScrollX); viewport.ScrollToVerticalOffset(layout.ScrollY);
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { if (generation == scrollRestoreGeneration) restoringScroll = false; }));
    }
    void RememberCanvas()
    {
        if (!restoringScroll) { layout.ScrollX = viewport.HorizontalOffset; layout.ScrollY = viewport.VerticalOffset; }
    }
    public void SelectPage(CanvasPage page)
    {
        if (layout.ActivePage == page) return;
        RememberCanvas(); layout.SelectedPageId = page.Id; selected = null; picked.Clear(); RestoreCanvas(); dirty = true; Persist();
    }
    public void SelectGroupTab(Group group, GroupTab tab, bool ownContents = false)
    {
        group.Select(tab, ownContents); selected = null; Render(); dirty = true; Persist();
    }
    public CanvasPage AddPage(string name)
    {
        RememberCanvas(); var page = new CanvasPage { Name = name };
        Change(() => { layout.Pages.Add(page); layout.SelectedPageId = page.Id; }); RestoreCanvas(); return page;
    }
    public GroupTab AddGroupTab(Group group, string name)
    {
        var tab = new GroupTab { Name = name };
        Change(() => { group.Tabs.Add(tab); group.SelectedTabId = tab.Id; }); return tab;
    }
    public GroupTab AddNestedTab(Group group, GroupTab parent, string name)
    {
        var tab = new GroupTab { Name = name };
        Change(() => { parent.Children.Add(tab); group.Select(tab, true); }); return tab;
    }
    public void RenamePage(CanvasPage page, string name) => Change(() => page.Name = name);
    public void RenameGroupTab(GroupTab tab, string name) => Change(() => tab.Name = name);
    public void RemovePage(CanvasPage page)
    {
        if (layout.Pages.Count == 1) return;
        Change(() => { layout.Pages.Remove(page); layout.SelectedPageId = layout.ActivePage.Id; }); RestoreCanvas();
    }
    public void RemoveGroupTab(Group group, GroupTab tab)
    {
        var siblings = group.Siblings(tab); var parent = group.AllTabs().FirstOrDefault(t => t.Children.Contains(tab));
        if (siblings == group.Tabs && siblings.Count == 1) return;
        Change(() => { siblings.Remove(tab); if (parent?.SelectedChildId == tab.Id) parent.SelectedChildId = null; group.SelectedTabId = group.RootTab.Id; });
    }
    static void Reorder<T>(List<T> list, Func<T, string> id, string source, string target)
    {
        int from = list.FindIndex(t => id(t) == source), to = list.FindIndex(t => id(t) == target);
        if (from < 0 || to < 0 || from == to) return;
        var item = list[from]; list.RemoveAt(from); list.Insert(to, item);
    }
    public void ReorderPage(string source, string target) => Change(() => Reorder(layout.Pages, p => p.Id, source, target));
    public void ReorderGroupTab(Group group, string source, string target)
    {
        var tab = group.AllTabs().FirstOrDefault(t => t.Id == source); if (tab is null) return;
        var siblings = group.Siblings(tab); if (!siblings.Any(t => t.Id == target)) return;
        Change(() => Reorder(siblings, t => t.Id, source, target));
    }
    ContextMenu PageTabMenu(CanvasPage page)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Rename page tab…", () => { var name = Prompt("Rename page tab", "Name", page.Name); if (name is not null) RenamePage(page, name); }));
        var remove = Entry("Remove page tab…", () =>
        {
            if ((page.Items.Count > 0 || page.Groups.Count > 0) && !ConfirmTabRemoval(page.Name)) return;
            RemovePage(page);
        }); remove.IsEnabled = layout.Pages.Count > 1; menu.Items.Add(remove); return menu;
    }
    ContextMenu GroupTabMenu(Group group, GroupTab tab)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Open all", () => OpenAll(tab.Items)));
        menu.Items.Add(Entry("Rename group tab…", () => { var name = Prompt("Rename group tab", "Name", tab.Name); if (name is not null) RenameGroupTab(tab, name); }));
        var remove = Entry("Remove group tab…", () => { if (tab.AllItems().Any() && !ConfirmTabRemoval(tab.Name)) return; RemoveGroupTab(group, tab); });
        remove.IsEnabled = group.Siblings(tab) != group.Tabs || group.Tabs.Count > 1; menu.Items.Add(remove); return menu;
    }
    bool ConfirmTabRemoval(string name) => MessageBox.Show(this, "Remove “" + name + "” and its launcher entries? Your files will remain untouched. You can undo this change.", "Remove tab", MessageBoxButton.OKCancel) == MessageBoxResult.OK;
    MenuItem MoveMenu(Shortcut item)
    {
        var menu = new MenuItem { Header = "Move to…" };
        foreach (var page in layout.Pages)
        {
            var destination = new MenuItem { Header = page.Name };
            destination.Items.Add(Entry("Canvas", () => TransferShortcut(item, page)));
            foreach (var group in page.Groups)
            {
                var entry = new MenuItem { Header = group.Label };
                MenuItem Destination(GroupTab tab)
                {
                    if (tab.Children.Count == 0) return Entry(tab.Name, () => TransferShortcut(item, page, group, tab));
                    var folder = new MenuItem { Header = tab.Name }; folder.Items.Add(Entry("Main", () => TransferShortcut(item, page, group, tab)));
                    foreach (var child in tab.Children) folder.Items.Add(Destination(child)); return folder;
                }
                foreach (var tab in group.Tabs) entry.Items.Add(Destination(tab));
                destination.Items.Add(entry);
            }
            menu.Items.Add(destination);
        }
        return menu;
    }
    public void TransferShortcut(Shortcut item, CanvasPage page, Group? group = null, GroupTab? tab = null)
    {
        RememberCanvas();
        Change(() =>
        {
            Detach(item); layout.SelectedPageId = page.Id;
            if (group is null) { item.X = Snap(page.ScrollX + 60); item.Y = Snap(page.ScrollY + 60); page.Items.Add(item); }
            else { tab ??= group.ActiveTab; group.Select(tab, true); tab.Items.Add(item); }
        }); RestoreCanvas();
        if (group is not null)
        {
            viewport.ScrollToHorizontalOffset(group.X - 20); viewport.ScrollToVerticalOffset(group.Y - 20);
            UpdateLayout(); page.ScrollX = viewport.HorizontalOffset; page.ScrollY = viewport.VerticalOffset; dirty = true; Persist();
        }
    }
    public void TransferGroup(Group group, CanvasPage destination)
    {
        var source = layout.Pages.FirstOrDefault(p => p.Groups.Contains(group));
        if (source is null || source == destination || !layout.Pages.Contains(destination)) return;
        RememberCanvas();
        Change(() => { source.Groups.Remove(group); destination.Groups.Add(group); layout.SelectedPageId = destination.Id; selected = null; });
        RestoreCanvas();
        viewport.ScrollToHorizontalOffset(Math.Max(0, group.X - 20));
        viewport.ScrollToVerticalOffset(Math.Max(0, group.Y - 20));
        UpdateLayout(); destination.ScrollX = viewport.HorizontalOffset; destination.ScrollY = viewport.VerticalOffset;
        dirty = true; Persist();
    }
    void SetEditing(bool value) { editing = value; Render(); }
    public void SetEditingForTest(bool value) => SetEditing(value);
    public void SetLayoutForTest(Layout value) { layout = value; Render(); }
    public void Render()
    {
        canvas.Children.Clear();
        pageTabs.Content = TabStrip(layout.Pages.Select(p => (p.Id, p.Name)), layout.ActivePage.Id,
            id => SelectPage(layout.Pages.First(p => p.Id == id)), id => PageTabMenu(layout.Pages.First(p => p.Id == id)),
            (source, target) => ReorderPage(source, target), "Homepage.PageTab");
        tools.Visibility = Visibility.Visible;
        editButton.Foreground = editing ? Accent : Ink;
        editButton.ToolTip = editing ? "Finish editing · Escape" : "Edit layout · Ctrl+E";
        snapButton.Foreground = layout.Snap ? Accent : Ink;
        snapButton.Background = layout.Snap ? new SolidColorBrush(SnapOn) : Brushes.Transparent;
        snapButton.ToolTip = layout.Snap ? "Grid snapping on · 16 px · Click to turn off" : "Grid snapping off · Click to turn on";
        canvas.Background = layout.Snap ? GridBrush() : Paper;
        foreach (var group in layout.Groups) DrawGroup(group);
        foreach (var item in layout.Items)
        {
            var tile = DrawShortcut(item, false); Canvas.SetLeft(tile, item.X); Canvas.SetTop(tile, item.Y); canvas.Children.Add(tile);
        }
        if (layout.Items.Count == 0 && layout.Groups.Count == 0)
        {
            var hint = new StackPanel { IsHitTestVisible = false };
            hint.Children.Add(new TextBlock { Text = "A little space for everything.", FontSize = 21, Foreground = new SolidColorBrush(Color.FromRgb(133, 142, 152)) });
            hint.Children.Add(new TextBlock { Text = "Drop files here, or right-click to add a shortcut or group.", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(159, 166, 174)), Margin = new Thickness(0, 8, 0, 0) });
            Canvas.SetLeft(hint, 72); Canvas.SetTop(hint, 76); canvas.Children.Add(hint);
        }
        EnsureExtent();
    }
    static Brush GridBrush()
    {
        var drawing = new GeometryDrawing(new SolidColorBrush(GridDot), null, new EllipseGeometry(new Point(1, 1), .65, .65));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        var group = new DrawingGroup(); group.Children.Add(new GeometryDrawing(Paper, null, new RectangleGeometry(new Rect(0, 0, 16, 16)))); group.Children.Add(drawing);
        brush.Drawing = group; return brush;
    }
    public static double GroupHeight(Group group)
    {
        int rows = TabRowCount(group);
        if (group.Scroll) return Math.Max(group.Height, 80 + rows * 26);
        int columns = Math.Max(1, (int)((group.Width - 24) / TileWidth));
        var body = (group.Rows ? group.Items.Count * RowHeight : Math.Ceiling(group.Items.Count / (double)columns) * TileHeight) * (group.Condensed ? .5 : 1);
        return Math.Max(group.Height, 44 + body + 12 + rows * 26);
    }
    static int TabRowCount(Group group) => (group.Tabs.Count > 1 ? 1 : 0) + group.ActivePath().Count(t => t.Children.Count > 0);
    void DrawGroup(Group group)
    {
        var border = new Border { Width = group.Width, Height = GroupHeight(group), Background = Dark ? PanelBrush : Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), AllowDrop = true, Tag = group.Id };
        Canvas.SetLeft(border, group.X); Canvas.SetTop(border, group.Y); canvas.Children.Add(border);
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TabRowCount(group) * 26) }); grid.RowDefinitions.Add(new RowDefinition()); border.Child = grid;
        var header = new Grid { Margin = new Thickness(12, 0, 8, 0), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = group.Label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });
        if (editing)
        {
            var move = MakeThumb(Cursors.SizeAll);
            double dragX = group.X, dragY = group.Y;
            move.ToolTip = "Move group"; move.HorizontalAlignment = HorizontalAlignment.Stretch; move.VerticalAlignment = VerticalAlignment.Stretch;
            move.DragStarted += (_, _) => { undo.Push(layout); dragX = group.X; dragY = group.Y; };
            move.DragDelta += (_, e) =>
            {
                dragX = Math.Max(0, dragX + e.HorizontalChange); dragY = Math.Max(0, dragY + e.VerticalChange);
                group.X = Snap(dragX); group.Y = Snap(dragY);
                Canvas.SetLeft(border, group.X); Canvas.SetTop(border, group.Y); EnsureExtent();
            };
            move.DragCompleted += (_, _) => { group.X = Snap(group.X); group.Y = Snap(group.Y); FinishChange(); };
            header.Children.Insert(0, move);
        }
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(Tiny("+", "Add to group", (_, _) => { var menu = new ContextMenu(); AddEntries(menu, group, new Point()); menu.PlacementTarget = header; menu.IsOpen = true; }));
            controls.Children.Add(Tiny("⋯", "Group settings", (_, _) => { border.ContextMenu = GroupMenu(group); border.ContextMenu.PlacementTarget = header; border.ContextMenu.IsOpen = true; }));
            Grid.SetColumn(controls, 1); header.Children.Add(controls);
        grid.Children.Add(header);
        var tabRows = new StackPanel(); Grid.SetRow(tabRows, 1); grid.Children.Add(tabRows);
        if (group.Tabs.Count > 1)
        {
            var strip = TabStrip(group.Tabs.Select(t => (t.Id, t.Name)), group.RootTab.Id,
                id => SelectGroupTab(group, group.Tabs.First(t => t.Id == id)),
                id => GroupTabMenu(group, group.Tabs.First(t => t.Id == id)),
                (source, target) => ReorderGroupTab(group, source, target), "Homepage.GroupTab." + group.Id);
            strip.Height = 26; strip.Margin = new Thickness(10, 0, 10, 0); tabRows.Children.Add(strip);
        }
        foreach (var parent in group.ActivePath().Where(t => t.Children.Count > 0))
        {
            var own = "contents:" + parent.Id;
            var strip = TabStrip(new[] { (own, "Main") }.Concat(parent.Children.Select(t => (t.Id, t.Name))), parent.SelectedChildId ?? own,
                id => SelectGroupTab(group, id == own ? parent : parent.Children.First(t => t.Id == id), id == own),
                id => id == own ? new ContextMenu { Items = { Entry("Open all", () => OpenAll(parent.Items)) } } : GroupTabMenu(group, parent.Children.First(t => t.Id == id)),
                (source, target) => ReorderGroupTab(group, source, target), "Homepage.GroupTab." + parent.Id);
            strip.Height = 26; strip.Margin = new Thickness(10, 0, 10, 0); tabRows.Children.Add(strip);
        }
        Panel items;
        if (group.Rows) items = new StackPanel();
        else items = new WrapPanel { ItemWidth = TileWidth, ItemHeight = TileHeight * (group.Condensed ? .5 : 1), Width = Math.Max(TileWidth, group.Width - (group.Scroll ? 42 : 24)) };
        foreach (var item in group.Items) items.Children.Add(DrawShortcut(item, group.Rows, group));
        var inner = new ScrollViewer { Content = items, Margin = new Thickness(10, 0, 10, 10), VerticalScrollBarVisibility = group.Scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Tag = "group-scroll" };
        var tab = group.ActiveTab;
        bool innerReady = false;
        inner.Loaded += (_, _) => { inner.ScrollToVerticalOffset(tab.ScrollY); inner.ScrollToHorizontalOffset(tab.ScrollX); inner.UpdateLayout(); innerReady = true; };
        inner.ScrollChanged += (_, e) => { if (!innerReady || e.OriginalSource != inner) return; tab.ScrollY = inner.VerticalOffset; tab.ScrollX = inner.HorizontalOffset; dirty = true; scrollSaveTimer.Stop(); scrollSaveTimer.Start(); };
        inner.PreviewMouseWheel += (_, e) =>
        {
            if (!group.Scroll) return;
            inner.ScrollToVerticalOffset(inner.VerticalOffset - e.Delta); e.Handled = true;
        };
        Grid.SetRow(inner, 2); grid.Children.Add(inner);
        if (group.Items.Count == 0)
        {
            var hint = new TextBlock { Text = "Drop shortcuts here", Margin = new Thickness(16, 12, 0, 0), FontSize = 11, Foreground = Brushes.LightSlateGray, IsHitTestVisible = false };
            Grid.SetRow(hint, 2); grid.Children.Add(hint);
        }
        if (editing)
        {
            var resize = MakeThumb(Cursors.SizeNWSE, "⌟"); resize.Width = 16; resize.Height = 16; resize.HorizontalAlignment = HorizontalAlignment.Right; resize.VerticalAlignment = VerticalAlignment.Bottom;
            double dragWidth = group.Width, dragHeight = group.Height;
            resize.ToolTip = "Resize group"; Grid.SetRowSpan(resize, 3); grid.Children.Add(resize);
            resize.DragStarted += (_, _) => { undo.Push(layout); dragWidth = group.Width; dragHeight = group.Height; };
            resize.DragDelta += (_, e) =>
            {
                dragWidth = Math.Clamp(dragWidth + e.HorizontalChange, 180, 10000);
                dragHeight = Math.Clamp(dragHeight + e.VerticalChange, 100, 10000);
                group.Width = Math.Clamp(Snap(dragWidth), 180, 10000);
                group.Height = Math.Clamp(Snap(dragHeight), 100, 10000);
                border.Width = group.Width; border.Height = GroupHeight(group);
                if (items is WrapPanel wrap) wrap.Width = Math.Max(TileWidth, group.Width - (group.Scroll ? 42 : 24));
                EnsureExtent();
            };
            resize.DragCompleted += (_, _) => { group.Width = Math.Max(180, Snap(group.Width)); group.Height = Math.Max(100, Snap(group.Height)); FinishChange(); };
        }
        border.ContextMenu = GroupMenu(group);
        border.ContextMenuOpening += (_, _) => border.ContextMenu = GroupMenu(group);
        border.Drop += (_, e) => DropOn(e, group, items);
        border.DragOver += (_, e) => { SetDragEffect(e); e.Handled = true; };
    }
    static Thumb MakeThumb(Cursor cursor, string? glyph = null)
    {
        var thumb = new Thumb { Cursor = cursor, Focusable = false };
        var factory = new FrameworkElementFactory(typeof(Border)); factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        if (glyph is not null)
        {
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.TextProperty, glyph); label.SetValue(TextBlock.FontSizeProperty, 12.0); label.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Ink.Color)); // A copy: templates lock their brushes, and Ink must stay changeable for dark mode. factory.AppendChild(label);
        }
        thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory }; return thumb;
    }
    ContextMenu GroupMenu(Group group)
    {
        var menu = new ContextMenu(); AddEntries(menu, group, new Point()); menu.Items.Add(new Separator());
        var tabMenu = new MenuItem { Header = "Current tab · " + group.ActiveTab.Name };
        var currentMenu = GroupTabMenu(group, group.ActiveTab);
        while (currentMenu.Items.Count > 0) { var entry = currentMenu.Items[0]; currentMenu.Items.RemoveAt(0); tabMenu.Items.Add(entry); }
        menu.Items.Add(tabMenu);
        menu.Items.Add(Entry("Rename…", () => RenameGroup(group)));
        var move = new MenuItem { Header = "Move to page…", IsEnabled = layout.Pages.Count > 1 };
        foreach (var page in layout.Pages)
        {
            var destination = Entry(page.Name, () => TransferGroup(group, page));
            destination.IsEnabled = !page.Groups.Contains(group); move.Items.Add(destination);
        }
        menu.Items.Add(move);
        menu.Items.Add(Toggle("Show as rows", group.Rows, () => Change(() => group.Rows = !group.Rows)));
        menu.Items.Add(Toggle("Condensed", group.Condensed, () => Change(() => group.Condensed = !group.Condensed)));
        menu.Items.Add(Toggle("Scroll inside group", group.Scroll, () => Change(() => group.Scroll = !group.Scroll)));
        menu.Items.Add(Entry("Ungroup shortcuts", () => Change(() => Ungroup(group))));
        menu.Items.Add(Entry("Remove group…", () =>
        {
            if (group.AllTabs().Any(t => t.Items.Count > 0) && MessageBox.Show(this, "Remove this group and its launcher shortcuts? Your files will remain untouched.", "Remove group", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            Change(() => layout.Groups.Remove(group));
        })); return menu;
    }
    Border DrawShortcut(Shortcut item, bool row, Group? group = null)
    {
        bool condensed = group?.Condensed == true;
        var tile = new Border { Width = row ? double.NaN : TileWidth, Height = (row ? RowHeight : TileHeight) * (condensed ? .5 : 1), CornerRadius = new CornerRadius(4), Padding = condensed ? row ? new Thickness(4, 0, 4, 0) : new Thickness(2) : new Thickness(row ? 6 : 4), Background = selected == item.Id || picked.Contains(item.Id) ? new SolidColorBrush(SelectFill) : Brushes.Transparent, BorderBrush = selected == item.Id || picked.Contains(item.Id) ? new SolidColorBrush(SelectLine) : Brushes.Transparent, BorderThickness = new Thickness(1), ToolTip = item.Label + "\n" + item.Target, Cursor = editing ? Cursors.SizeAll : Cursors.Hand, Tag = item.Id, Focusable = true };
        var content = new Grid();
        double iconSize = (row ? 24 : 48) * (condensed ? .5 : 1);
        var image = new Image { Source = iconAssets.For(item), Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        var label = new TextBlock { Text = item.Label, FontSize = condensed ? 10 : 11, Foreground = broken.Contains(item.Id) ? Brushes.IndianRed : Ink, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
        if (row)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(condensed ? 20 : 30) }); content.ColumnDefinitions.Add(new ColumnDefinition());
            label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(condensed ? 4 : 6, 0, 0, 0); Grid.SetColumn(label, 1);
        }
        else
        {
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(condensed ? 24 : 54) }); content.RowDefinitions.Add(new RowDefinition());
            label.TextAlignment = TextAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(label, 1);
        }
        content.Children.Add(image); content.Children.Add(label); tile.Child = content;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { shortcutStart = null; Open(item); e.Handled = true; return; }
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (selected is { } first) picked.Add(first);
                if (!picked.Remove(item.Id)) picked.Add(item.Id);
                Select(null); tile.Focus(); e.Handled = true; return;
            }
            picked.Clear(); Select(item.Id); tile.Focus(); shortcutStart = e.GetPosition(tile); shortcutPressed = item.Id; e.Handled = true;
        };
        tile.MouseLeftButtonUp += (_, e) => { shortcutStart = null; shortcutPressed = null; e.Handled = true; };
        tile.MouseMove += (_, e) =>
        {
            if (!editing || e.LeftButton != MouseButtonState.Pressed || shortcutStart is not { } start || shortcutPressed != item.Id) return;
            var now = e.GetPosition(tile);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            shortcutStart = null; shortcutPressed = null;
            var data = new DataObject(DragFormat, item.Id);
            BeginShortcutDrag(item, tile, start);
            try { DragDrop.DoDragDrop(tile, data, DragDropEffects.Move); }
            finally { EndShortcutDrag(); }
            e.Handled = true;
        };
        var menu = new ContextMenu();
        menu.Items.Add(Entry("Open", () => Open(item)));
        var openPicked = Entry("Open selected", OpenPicked); menu.Items.Add(openPicked);
        menu.Opened += (_, _) => { int count = PickedItems().Count; openPicked.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed; openPicked.Header = "Open selected (" + count + ")"; };
        menu.Items.Add(Entry("Rename…", () => { var name = Prompt("Rename shortcut", "Name", item.Label); if (name is not null) Change(() => { item.Label = name; item.CustomLabel = true; }); }));
        if (LayoutStore.IsWebsite(item.Target))
        {
            menu.Items.Add(Entry("Refresh website details", () => _ = RefreshWebsiteAsync(item, true)));
            menu.Items.Add(Entry("Use webpage title", () =>
            {
                Change(() => { item.CustomLabel = false; item.Label = item.WebsiteTitle ?? new Uri(item.Target).Host; });
                if (item.WebsiteTitle is null) _ = RefreshWebsiteAsync(item, true);
            }));
        }
        menu.Items.Add(Entry("Change target…", () => EditTarget(item)));
        menu.Items.Add(Entry("Remove shortcut", () => Remove(item)));
        menu.Items.Add(MoveMenu(item));
        if (group is not null) menu.Items.Add(Entry("Move onto canvas", () => Change(() => { group.Items.Remove(item); item.X = group.X + group.Width + 24; item.Y = group.Y; layout.Items.Add(item); })));
        menu.Items.Add(Toggle("Edit layout", editing, () => SetEditing(!editing))); tile.ContextMenu = menu;
        return tile;
    }
    void Select(string? id)
    {
        selected = id;
        foreach (var border in FindDescendants<Border>(canvas).Where(b => b.Tag is string s && layout.AllItems().Any(i => i.Id == s)))
        {
            bool chosen = (string?)border.Tag == id || picked.Contains((string)border.Tag);
            border.Background = chosen ? new SolidColorBrush(SelectFill) : Brushes.Transparent;
            border.BorderBrush = chosen ? new SolidColorBrush(SelectLine) : Brushes.Transparent;
        }
    }
    void BeginPan(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != canvas) return;
        Select(null); panStart = e.GetPosition(viewport); panX = viewport.HorizontalOffset; panY = viewport.VerticalOffset;
        canvas.CaptureMouse(); canvas.Cursor = Cursors.ScrollAll; e.Handled = true;
    }
    void Pan(object sender, MouseEventArgs e)
    {
        if (panStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(viewport);
        viewport.ScrollToHorizontalOffset(panX + start.X - point.X); viewport.ScrollToVerticalOffset(panY + start.Y - point.Y);
    }
    void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (panStart is null) return; panStart = null; canvas.ReleaseMouseCapture(); canvas.Cursor = Cursors.Arrow;
    }
    void SetDragEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) && editing ? DragDropEffects.Move : e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    void CanvasDragOver(object sender, DragEventArgs e)
    {
        SetDragEffect(e);
        var p = e.GetPosition(viewport);
        if (p.X < 32) viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset - 16);
        if (p.Y < 32) viewport.ScrollToVerticalOffset(viewport.VerticalOffset - 16);
        if (p.X > viewport.ViewportWidth - 32) viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset + 16);
        if (p.Y > viewport.ViewportHeight - 32) viewport.ScrollToVerticalOffset(viewport.VerticalOffset + 16);
        e.Handled = true;
    }
    public void BeginShortcutDrag(Shortcut item, FrameworkElement source, Point grab)
    {
        EndShortcutDrag(); source.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(source);
        var snapshot = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(source) { Stretch = Stretch.Fill }, null, new Rect(source.RenderSize));
        snapshot.Render(visual);
        dragGhost = new Border { Width = source.ActualWidth, Height = source.ActualHeight, Opacity = .6, IsHitTestVisible = false, Tag = "shortcut-ghost", Child = new Image { Source = snapshot, Stretch = Stretch.Fill } };
        dragOverlay.Children.Add(dragGhost);
        dragSource = source; originalDragOpacity = source.Opacity; source.Opacity = .25;
        draggedShortcut = item; dragGrab = grab;
    }
    void HideDragPreview() { landingMarker.Visibility = Visibility.Collapsed; if (dragGhost is not null) dragGhost.Visibility = Visibility.Collapsed; }
    public void EndShortcutDrag()
    {
        if (dragSource is not null) dragSource.Opacity = originalDragOpacity;
        if (dragGhost is not null) dragOverlay.Children.Remove(dragGhost);
        dragSource = null; dragGhost = null; draggedShortcut = null; landingMarker.Visibility = Visibility.Collapsed;
    }
    void PreviewShortcutDrag(object sender, DragEventArgs e)
    {
        if (draggedShortcut is not { } item || !e.Data.GetDataPresent(DragFormat)) return;
        Group? group = null; Panel? panel = null;
        foreach (var border in FindDescendants<Border>(canvas).Where(b => b.Tag is string id && layout.Groups.Any(g => g.Id == id)))
        {
            var original = e.OriginalSource as DependencyObject ?? canvas;
            if (border != original && !border.IsAncestorOf(original)) continue;
            group = layout.Groups.First(g => g.Id == (string)border.Tag);
            panel = FindDescendants<ScrollViewer>(border).First(s => s.Tag as string == "group-scroll").Content as Panel;
            break;
        }
        if (e.Effects != DragDropEffects.Move || !editing)
        {
            MoveDragGhost(e.GetPosition(canvas)); landingMarker.Visibility = Visibility.Collapsed; return;
        }
        if (panel is not null && FindAncestor<ScrollViewer>(panel) is { } inner && inner.VerticalScrollBarVisibility == ScrollBarVisibility.Auto)
        {
            var point = e.GetPosition(inner);
            if (point.Y < 24) inner.ScrollToVerticalOffset(inner.VerticalOffset - 16);
            else if (point.Y > inner.ActualHeight - 24) inner.ScrollToVerticalOffset(inner.VerticalOffset + 16);
            inner.UpdateLayout();
        }
        UpdateShortcutDragPreview(item, e.GetPosition(canvas), group, panel);
    }
    void MoveDragGhost(Point canvasPoint)
    {
        if (dragGhost is null) return;
        var pointer = canvas.TranslatePoint(canvasPoint, dragOverlay);
        Canvas.SetLeft(dragGhost, pointer.X - dragGrab.X); Canvas.SetTop(dragGhost, pointer.Y - dragGrab.Y); dragGhost.Visibility = Visibility.Visible;
    }
    public static int ShortcutInsertionIndex(Group group, Panel panel, Point local)
    {
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = (FrameworkElement)panel.Children[i]; var origin = child.TranslatePoint(new Point(), panel);
            if (group.Rows ? local.Y < origin.Y + child.ActualHeight / 2 : local.Y < origin.Y + child.ActualHeight && local.X < origin.X + child.ActualWidth / 2) return i;
        }
        return group.Items.Count;
    }
    public int UpdateShortcutDragPreview(Shortcut item, Point canvasPoint, Group? group = null, Panel? panel = null)
    {
        MoveDragGhost(canvasPoint);
        Rect rectangle; Rect? clip = null; int index = 0;
        if (group is null)
        {
            rectangle = new Rect(canvas.TranslatePoint(new Point(Snap(canvasPoint.X - TileWidth / 2), Snap(canvasPoint.Y - 24)), dragOverlay), new Size(TileWidth, TileHeight));
        }
        else
        {
            panel ??= FindDescendants<ScrollViewer>(canvas.Children.OfType<Border>().First(b => b.Tag as string == group.Id)).First(s => s.Tag as string == "group-scroll").Content as Panel;
            if (panel is null) { landingMarker.Visibility = Visibility.Collapsed; return 0; }
            index = ShortcutInsertionIndex(group, panel, canvas.TranslatePoint(canvasPoint, panel));
            int effective = index;
            int sourceIndex = group.Items.IndexOf(item);
            if (sourceIndex >= 0 && sourceIndex < effective) effective--;
            effective = Math.Clamp(effective, 0, group.Items.Count - (sourceIndex >= 0 ? 1 : 0));
            double height = (group.Rows ? RowHeight : TileHeight) * (group.Condensed ? .5 : 1);
            int columns = group.Rows ? 1 : Math.Max(1, (int)(panel.ActualWidth / TileWidth));
            var local = new Point(group.Rows ? 0 : effective % columns * TileWidth, effective / columns * height);
            rectangle = new Rect(panel.TranslatePoint(local, dragOverlay), new Size(group.Rows ? Math.Max(24, panel.ActualWidth) : TileWidth, height));
            if (FindAncestor<ScrollViewer>(panel) is { } inner) clip = new Rect(inner.TranslatePoint(new Point(), dragOverlay), inner.RenderSize);
        }
        landingMarker.Width = rectangle.Width; landingMarker.Height = rectangle.Height;
        Canvas.SetLeft(landingMarker, rectangle.X); Canvas.SetTop(landingMarker, rectangle.Y);
        if (clip is { } bounds)
        {
            bounds.Intersect(rectangle);
            landingMarker.Clip = bounds.IsEmpty ? Geometry.Empty : new RectangleGeometry(new Rect(bounds.X - rectangle.X, bounds.Y - rectangle.Y, bounds.Width, bounds.Height));
        }
        else landingMarker.Clip = null;
        landingMarker.Visibility = Visibility.Visible;
        return index;
    }
    void DropOn(DragEventArgs e, Group? group, Panel? panel = null)
    {
        e.Handled = true;
        var point = e.GetPosition(canvas);
        if (e.Data.GetDataPresent(DragFormat))
        {
            if (!editing) return;
            var id = e.Data.GetData(DragFormat) as string;
            var item = layout.AllItems().FirstOrDefault(i => i.Id == id); if (item is null) return;
            int index = group is not null && panel is not null ? ShortcutInsertionIndex(group, panel, e.GetPosition(panel)) : group?.Items.Count ?? 0;
            EndShortcutDrag();
            MoveShortcut(item, group, index, point); return;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) AddTargets((string[])e.Data.GetData(DataFormats.FileDrop), group, point);
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = ((string)e.Data.GetData(DataFormats.UnicodeText)).Trim();
            if (LayoutStore.IsWebsite(text)) AddTargets([text], group, point);
            else ShowStatus("Drop a file, directory, or HTTP/HTTPS website address.");
        }
    }
    public void MoveShortcut(Shortcut item, Group? destination, int index, Point point)
    {
        Change(() =>
        {
            var source = layout.AllGroups().SelectMany(g => g.AllTabs()).FirstOrDefault(t => t.Items.Contains(item));
            if (source == destination?.ActiveTab && source is not null && source.Items.IndexOf(item) < index) index--;
            Detach(item);
            if (destination is null) { item.X = Snap(point.X - TileWidth / 2); item.Y = Snap(point.Y - 24); layout.Items.Add(item); }
            else destination.Items.Insert(Math.Clamp(index, 0, destination.Items.Count), item);
        });
    }
    public void AddTargets(IEnumerable<string> targets, Group? group, Point point)
    {
        var entries = new List<Shortcut>();
        try
        {
            foreach (var target in targets)
            {
                bool web = LayoutStore.IsWebsite(target);
                if (!web && !File.Exists(target) && !Directory.Exists(target)) { ShowStatus("That file or directory was not found. Choose an existing target."); continue; }
                var stored = store.StoreTarget(target);
                entries.Add(new Shortcut { Target = stored, Label = web ? new Uri(target).Host : Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : target, X = Snap(point.X + entries.Count % 8 * TileWidth), Y = Snap(point.Y + entries.Count / 8 * TileHeight) });
            }
            if (entries.Count > 0)
            {
                Change(() => { if (group is null) layout.Items.AddRange(entries); else group.Items.AddRange(entries); });
                if (!testMode) foreach (var item in entries.Where(i => LayoutStore.IsWebsite(i.Target))) _ = RefreshWebsiteAsync(item);
            }
        }
        catch (Exception e) { ShowStatus("Could not add shortcut: " + e.Message); }
    }
    void AddFiles(Group? group, Point point)
    {
        var dialog = new OpenFileDialog { Title = "Choose files, applications, or shortcuts", Multiselect = true, Filter = "All files|*.*", DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true) AddTargets(dialog.FileNames, group, point);
    }
    void AddDirectory(Group? group, Point point)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a directory", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddTargets(dialog.FolderNames, group, point);
    }
    void AddWebsite(Group? group, Point point)
    {
        var url = Prompt("Add website", "Website address", "https://", value => LayoutStore.IsWebsite(value) ? null : "Enter a complete http:// or https:// address.");
        if (url is not null) AddTargets([url], group, point);
    }
    void AddGroup(Point point)
    {
        var name = Prompt("New group", "Name", "New group");
        if (name is not null) { Change(() => layout.Groups.Add(new Group { Label = name, X = Snap(point.X), Y = Snap(point.Y) })); SetEditing(true); }
    }
    void RenameGroup(Group group)
    {
        var label = Prompt("Rename group", "Name", group.Label); if (label is not null) Change(() => group.Label = label);
    }
    void EditTarget(Shortcut item)
    {
        var target = Prompt("Change target", "File, directory, or website address", item.Target, value =>
        {
            try { var resolved = store.ResolveTarget(value); return LayoutStore.IsWebsite(value) || File.Exists(resolved) || Directory.Exists(resolved) ? null : "Choose an existing path or HTTP/HTTPS website."; }
            catch { return "This path is invalid."; }
        });
        if (target is not null)
        {
            Change(() =>
            {
                item.Target = store.StoreTarget(store.ResolveTarget(target)); item.WebsiteIcon = null; item.WebsiteTitle = null; item.WebsiteMetadataFetched = false;
                if (!item.CustomLabel) item.Label = LayoutStore.IsWebsite(item.Target) ? new Uri(item.Target).Host : Path.GetFileName(item.Target.TrimEnd(Path.DirectorySeparatorChar));
            });
            if (LayoutStore.IsWebsite(item.Target)) _ = RefreshWebsiteAsync(item);
        }
    }
    public async Task RefreshWebsiteAsync(Shortcut item, bool userInitiated = false)
    {
        var target = item.Target; if (!LayoutStore.IsWebsite(target) || lifetime.IsCancellationRequested) return;
        var request = item.Id + ":" + target; if (!pendingMetadata.TryAdd(request, 0)) return;
        try
        {
            var details = await websiteMetadata.FetchAsync(target, lifetime.Token, userInitiated).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
            // An undo, deletion, or target change must invalidate an outstanding result.
            var current = layout.AllItems().FirstOrDefault(i => i.Id == item.Id);
            if (lifetime.IsCancellationRequested || !ReferenceEquals(current, item) || item.Target != target) return;
            if (!details.PageRead && details.Title is null && details.IconPng is null)
            {
                if (userInitiated) ShowStatus("Could not fetch website details. Your shortcut is unchanged. Try Refresh website details again when the site is available.");
                return;
            }
            string? relative = details.IconPng is not null ? iconAssets.StorePng(details.IconPng) : null;
            if (userInitiated) undo.Push(layout);
            if (details.Title is { } title)
            {
                bool usePageTitle = !item.CustomLabel && (item.Label == new Uri(target).Host || item.Label == item.WebsiteTitle);
                item.WebsiteTitle = title;
                if (usePageTitle) item.Label = title;
            }
            if (relative is not null) item.WebsiteIcon = relative;
            item.WebsiteMetadataFetched = true;
            // Update the tile in place so a background fetch does not interrupt dragging or scroll a group back to the top.
            foreach (var tile in FindDescendants<Border>(canvas).Where(b => b.Tag as string == item.Id))
            {
                var image = FindDescendants<Image>(tile).FirstOrDefault(); if (image is not null) image.Source = iconAssets.For(item);
                var label = FindDescendants<TextBlock>(tile).FirstOrDefault(); if (label is not null) label.Text = item.Label;
                tile.ToolTip = item.Label + "\n" + item.Target;
            }
            dirty = true; Persist();
            if (userInitiated && details.IconPng is null && item.WebsiteIcon is null && !dirty) ShowStatus("Page title refreshed; no usable website icon was available. The globe icon will stay until one is found.");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => ShowStatus("Could not update website details: " + error.Message + " · Right-click → Refresh website details."));
        }
        finally { pendingMetadata.TryRemove(request, out _); }
    }
    readonly HashSet<string> picked = new();
    List<Shortcut> PickedItems() => layout.AllItems().Where(i => picked.Contains(i.Id)).ToList();
    void OpenPicked() => OpenAll(PickedItems());
    void OpenAll(IEnumerable<Shortcut> items) { foreach (var item in items.ToList()) Open(item); }
    void Open(Shortcut item)
    {
        try
        {
            var target = store.ResolveTarget(item.Target);
            if (!LayoutStore.IsWebsite(target) && !File.Exists(target) && !Directory.Exists(target)) { ShowStatus("Target missing: " + item.Label + ". Right-click it → Change target to repair the link."); return; }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = LayoutStore.IsWebsite(target) ? store.BaseDirectory : Directory.Exists(target) ? target : Path.GetDirectoryName(target)! });
        }
        catch (Exception e) { ShowStatus("Could not open " + item.Label + ": " + e.Message + " · Right-click → Change target."); }
    }
    void Detach(Shortcut item) { foreach (var p in layout.Pages) { p.Items.Remove(item); foreach (var g in p.Groups) foreach (var t in g.AllTabs()) t.Items.Remove(item); } }
    void Remove(Shortcut item) => Change(() => { Detach(item); selected = null; });
    void Ungroup(Group group)
    {
        var all = group.AllTabs().SelectMany(t => t.Items).ToArray();
        for (int i = 0; i < all.Length; i++) { var item = all[i]; item.X = group.X + i % 4 * TileWidth; item.Y = group.Y + i / 4 * TileHeight; layout.Items.Add(item); }
        layout.Groups.Remove(group);
    }
    void Change(Action action) { undo.Push(layout); ShowStatus(""); action(); FinishChange(); }
    void FinishChange() { dirty = true; Render(); Persist(); }
    public void Undo()
    {
        if (undo.Pop() is not { } previous) return; layout = previous; selected = null; RestoreCanvas(); dirty = true; Persist();
    }
    double Snap(double value) => Math.Max(0, layout.Snap ? Math.Round(value / 16) * 16 : value);
    void EnsureExtent()
    {
        double maxX = layout.Items.Select(i => i.X + TileWidth).Concat(layout.Groups.Select(g => g.X + g.Width)).DefaultIfEmpty(0).Max();
        double maxY = layout.Items.Select(i => i.Y + TileHeight).Concat(layout.Groups.Select(g => g.Y + GroupHeight(g))).DefaultIfEmpty(0).Max();
        canvas.Width = Math.Max(canvas.Width, Math.Max(maxX + 400, layout.ScrollX + Math.Max(1000, viewport.ViewportWidth) + 400));
        canvas.Height = Math.Max(canvas.Height, Math.Max(maxY + 400, layout.ScrollY + Math.Max(700, viewport.ViewportHeight) + 400));
    }
    void Persist()
    {
        if (!dirty || testMode) return;
        try { store.Save(layout); dirty = false; if (!status.Text.StartsWith("Imported ", StringComparison.Ordinal)) ShowStatus(""); }
        catch (Exception e) { ShowStatus("Not saved: " + e.Message + " · Check that the EXE folder is writable, then Ctrl+S."); }
    }
    void ShowStatus(string message) { status.Text = message; status.ToolTip = message; }
    void ClosingWindow(object? sender, CancelEventArgs e)
    {
        scrollSaveTimer.Stop(); if (testMode) return; Persist();
        if (HideInsteadOfClose(e)) return;
        if (dirty && MessageBox.Show(this, "Your latest changes could not be saved. Close and discard those changes?", "Homepage", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
    }
    void OpenData()
    {
        try { Directory.CreateDirectory(store.DataDirectory); Process.Start(new ProcessStartInfo(store.DataDirectory) { UseShellExecute = true }); }
        catch (Exception e) { ShowStatus("Could not open saved-data folder: " + e.Message); }
    }
    void Recover()
    {
        if (MessageBox.Show(this, "Preserve the unreadable save and start saving this canvas? You can inspect the preserved file in Homepage.data.", "Recover saved layout", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try { store.Recover(); dirty = true; Persist(); } catch (Exception e) { ShowStatus("Could not recover save: " + e.Message); }
    }
    void KeyPressed(object sender, KeyEventArgs e)
    {
        if (ExtraKey(e)) return;
        if (e.Key == Key.Escape) { if (picked.Count > 0) { picked.Clear(); Select(selected); } SetEditing(false); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && picked.Count > 0) { OpenPicked(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.E) { SetEditing(!editing); e.Handled = true; }
            if (e.Key == Key.Z) { Undo(); e.Handled = true; }
            if (e.Key == Key.S) { dirty = true; Persist(); e.Handled = true; }
        }
        else if (editing && e.Key == Key.Delete && layout.AllItems().FirstOrDefault(i => i.Id == selected) is { } item) { Remove(item); e.Handled = true; }
    }
    string? Prompt(string title, string caption, string initial, Func<string, string?>? validate = null)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Paper, FontFamily = FontFamily, FontSize = 12, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(new TextBlock { Text = caption, Foreground = Ink, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = initial, Padding = new Thickness(8), MinHeight = 30 }; panel.Children.Add(input);
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) }; buttons.Children.Add(cancel);
        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(12, 4, 12, 4) }; buttons.Children.Add(ok);
        ok.Click += (_, _) => { var value = input.Text.Trim(); var message = value.Length == 0 ? "Enter a value." : validate?.Invoke(value); if (message is not null) error.Text = message; else dialog.DialogResult = true; };
        panel.Children.Add(buttons); dialog.Content = panel; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null) { if (source is T result) return result; source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); } return null;
    }
    public static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T result) yield return result;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }
}
