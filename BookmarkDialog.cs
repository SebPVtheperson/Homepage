using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Homepage;

public sealed class BookmarkDialog : Window
{
    readonly ComboBox browsers = new() { ItemsSource = Enum.GetValues<BookmarkBrowser>(), MinWidth = 110 };
    readonly ComboBox profiles = new() { MinWidth = 150, DisplayMemberPath = "Name", IsEnabled = false };
    readonly TreeView tree = new() { Margin = new Thickness(0, 12, 0, 8) };
    readonly TextBlock address = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
    readonly TextBlock message = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    readonly Button import = new() { Content = "Import", IsDefault = true, IsEnabled = false, Padding = new Thickness(14, 4, 14, 4) };
    readonly Button browse = new() { Content = "Browse for bookmark data…", IsEnabled = false, Padding = new Thickness(8, 3, 8, 3) };
    CancellationTokenSource? operation;
    int generation;
    readonly string? local, roaming;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public BookmarkNode? SelectedNode { get; private set; }
    public BookmarkDialog(Window owner, string? local = null, string? roaming = null)
    {
        this.local = local; this.roaming = roaming;
        Owner = owner; Title = "Import bookmarks"; Width = 640; Height = 520; MinWidth = 480; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(249, 250, 251));
        FontFamily = owner.FontFamily; FontSize = 12; ShowInTaskbar = false;
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new WrapPanel(); top.Children.Add(new TextBlock { Text = "Browser", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); top.Children.Add(browsers);
        profiles.Margin = new Thickness(8, 0, 8, 0); top.Children.Add(profiles); top.Children.Add(browse); root.Children.Add(top);
        Grid.SetRow(tree, 1); root.Children.Add(tree); Grid.SetRow(address, 2); root.Children.Add(address); Grid.SetRow(message, 3); root.Children.Add(message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) }; buttons.Children.Add(cancel); buttons.Children.Add(import); Grid.SetRow(buttons, 4); root.Children.Add(buttons); Content = new Border { Background = Background, Child = root };
        message.Text = "Choose a browser, then select a bookmark folder or individual link.";
        browsers.SelectionChanged += (_, _) => PendingOperation = DiscoverAsync();
        profiles.SelectionChanged += (_, _) => { if (profiles.SelectedItem is BookmarkProfile profile) PendingOperation = ReadAsync(profile.Path); };
        browse.Click += async (_, _) =>
        {
            if (browsers.SelectedItem is not BookmarkBrowser browser) return;
            var dialog = new OpenFileDialog { Title = "Choose bookmark data", Filter = browser == BookmarkBrowser.Firefox ? "Firefox Places database|places.sqlite;*.sqlite|All files|*.*" : "Bookmark data|Bookmarks;*.json|All files|*.*" };
            if (dialog.ShowDialog(this) == true) await ReadAsync(dialog.FileName);
        };
        tree.SelectedItemChanged += (_, _) =>
        {
            SelectedNode = (tree.SelectedItem as TreeViewItem)?.Tag as BookmarkNode;
            import.IsEnabled = SelectedNode is { Folder: true } or { Supported: true };
            address.Text = SelectedNode?.Url ?? (SelectedNode is null ? "" : "Import this entire folder, including its subfolders.");
        };
        import.Click += (_, _) => { if (import.IsEnabled && SelectedNode is not null) DialogResult = true; };
        Closed += (_, _) => { generation++; operation?.Cancel(); operation?.Dispose(); };
    }
    (int Generation, CancellationToken Token) Begin(string status)
    {
        operation?.Cancel(); operation?.Dispose(); operation = new();
        tree.Items.Clear(); SelectedNode = null; address.Text = ""; import.IsEnabled = false; message.Text = status;
        return (++generation, operation.Token);
    }
    async Task DiscoverAsync()
    {
        if (browsers.SelectedItem is not BookmarkBrowser browser) return;
        profiles.ItemsSource = null; profiles.IsEnabled = false; browse.IsEnabled = true;
        var (version, token) = Begin("Finding browser profiles…");
        try
        {
            var found = await Task.Run(() => BookmarkReader.Discover(browser, local, roaming, token), token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != generation || token.IsCancellationRequested) return;
                profiles.ItemsSource = found; profiles.IsEnabled = found.Count > 1;
                message.Text = found.Count == 0 ? "No local bookmarks were found. Use Browse for bookmark data to choose a file." : "Choose a profile to browse its bookmarks.";
                if (found.Count == 1) profiles.SelectedIndex = 0;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (version == generation) message.Text = "Could not find bookmarks: " + e.Message + " Use Browse for bookmark data, or choose the browser again to retry."; }); }
    }
    async Task ReadAsync(string path)
    {
        if (browsers.SelectedItem is not BookmarkBrowser browser) return;
        var (version, token) = Begin("Reading bookmarks…");
        try
        {
            var root = await Task.Run(() => BookmarkReader.Read(browser, path, token), token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != generation || token.IsCancellationRequested) return;
                tree.Items.Add(TreeNode(root));
                ((TreeViewItem)tree.Items[0]).IsExpanded = true;
                message.Text = "Select one folder or bookmark, then click Import. Bookmark names will be kept.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (version == generation) message.Text = "Could not read bookmarks: " + e.Message + " Close the browser if the file is locked, then select the profile again or browse to retry."; }); }
    }
    static TreeViewItem TreeNode(BookmarkNode node)
    {
        var item = new TreeViewItem { Header = (node.Folder ? "▸ " : "") + node.Name, Tag = node, ToolTip = node.Url ?? node.Name,
            Foreground = node.Folder || node.Supported ? Brushes.DimGray : Brushes.LightSlateGray };
        if (node.Children.Count > 0)
        {
            item.Items.Add(new TreeViewItem { Header = "Loading…" }); bool built = false;
            item.Expanded += (_, e) => { if (e.OriginalSource != item || built) return; built = true; item.Items.Clear(); foreach (var child in node.Children) item.Items.Add(TreeNode(child)); };
        }
        return item;
    }
}
