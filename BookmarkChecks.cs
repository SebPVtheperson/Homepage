using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace Homepage;

public static class BookmarkChecks
{
    static void Wait(Task task)
    {
        if (!task.IsCompleted) { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }
    static void Pump() => Wait(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    public static void Run(Action<bool, string> check, string output)
    {
        var fixture = Path.Combine(output, "bookmark-fixtures");
        string local = Path.Combine(fixture, "local"), roaming = Path.Combine(fixture, "roaming");
        var folder = new BookmarkNode { Name = "Work & café", Folder = true, Children = [new() { Name = "My chosen name", Url = "https://example.com" }, new() { Name = "Bookmarklet", Url = "javascript:alert(1)" }, new() { Name = "Separator" }, new() { Name = "Empty", Folder = true }] };
        var deep = folder;
        for (int i = 1; i <= 4; i++) { var child = new BookmarkNode { Name = "Level " + i, Folder = true, Children = [new() { Name = "Link " + i, Url = "https://example.com/" + i }] }; deep.Children.Add(child); deep = child; }
        object Encode(BookmarkNode n) => n.Folder ? new { type = "folder", name = n.Name, children = n.Children.Select(Encode).ToArray() } : (object)new { type = n.Url is null ? "separator" : "url", name = n.Name, url = n.Url };
        string json = JsonSerializer.Serialize(new { roots = new { bookmark_bar = Encode(folder) } });
        foreach (var browserPath in new[] { "Google/Chrome/User Data", "Microsoft/Edge/User Data" })
        {
            var browserRoot = Path.Combine(local, browserPath);
            foreach (var profile in new[] { "Default", "Profile 1", "Guest Profile", "System Profile", "Snapshots/Old/Default" }) { Directory.CreateDirectory(Path.Combine(browserRoot, profile)); File.WriteAllText(Path.Combine(browserRoot, profile, "Bookmarks"), json); }
            File.WriteAllText(Path.Combine(browserRoot, "Local State"), "{\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Personal\"},\"Profile 1\":{\"name\":\"Work\"}}}}");
        }
        var chrome = BookmarkReader.Discover(BookmarkBrowser.Chrome, local, roaming);
        var edge = BookmarkReader.Discover(BookmarkBrowser.Edge, local, roaming);
        check(chrome.Count == 2 && edge.Count == 2 && chrome[0].Name.StartsWith("Personal") && chrome.All(p => !p.Path.Contains("Snapshots")), "Browser discovery finds named active profiles and excludes snapshots, guest and system profiles");
        var read = BookmarkReader.Read(BookmarkBrowser.Chrome, chrome[0].Path).Children[0];
        check(read.Name == folder.Name && read.Children[0].Name == "My chosen name" && read.Children[3].Folder && read.Children[3].Children.Count == 0 && read.Skipped == 2, "Chromium importer preserves Unicode names, ordering, empty folders and counts unsupported entries");
        check(BookmarkReader.Read(BookmarkBrowser.Edge, edge[0].Path).Children[0].Descendants().Count() == read.Descendants().Count(), "Edge and Chrome use the same complete folder parser");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool cancelledRead = false; try { BookmarkReader.Read(BookmarkBrowser.Chrome, chrome[0].Path, cancelled.Token); } catch (OperationCanceledException) { cancelledRead = true; }
        check(cancelledRead, "Cancelled bookmark reading leaves no import to commit");
        var damaged = Path.Combine(fixture, "damaged.json"); File.WriteAllText(damaged, "broken"); bool failed = false; try { BookmarkReader.Read(BookmarkBrowser.Chrome, damaged); } catch (JsonException) { failed = true; }
        check(failed, "Damaged bookmark sources fail before launcher changes");
        using (var locked = new FileStream(chrome[0].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { failed = false; try { BookmarkReader.Read(BookmarkBrowser.Chrome, chrome[0].Path); } catch (IOException) { failed = true; } check(failed, "Locked browser bookmark files return an actionable read failure"); }

        var firefoxProfile = Path.Combine(roaming, "Mozilla/Firefox/Profiles/fixture.default"); Directory.CreateDirectory(firefoxProfile);
        File.WriteAllText(Path.Combine(roaming, "Mozilla/Firefox/profiles.ini"), "[Profile0]\nName=Firefox fixture\nIsRelative=1\nPath=Profiles/fixture.default\n");
        var database = Path.Combine(firefoxProfile, "places.sqlite");
        using (var live = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            live.Open(); using var command = live.CreateCommand(); command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
            CREATE TABLE moz_places(id INTEGER PRIMARY KEY,url TEXT);
            CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY,parent INTEGER,position INTEGER,title TEXT,type INTEGER,fk INTEGER,guid TEXT);
            INSERT INTO moz_places VALUES(1,'https://example.com'),(2,'https://example.com/latest');
            INSERT INTO moz_bookmarks VALUES(1,0,0,NULL,2,NULL,'root________'),(2,1,0,NULL,2,NULL,'toolbar_____'),(3,2,0,'Folder',2,NULL,'folder______'),(4,3,0,'Firefox name',1,1,'bookmark____'),(5,3,1,'Latest WAL bookmark',1,2,'latest______');
            """; command.ExecuteNonQuery();
            check(File.Exists(database + "-wal") && new FileInfo(database + "-wal").Length > 0, "Firefox fixture includes committed bookmark changes in an active WAL file");
            var profiles = BookmarkReader.Discover(BookmarkBrowser.Firefox, local, roaming);
            var firefox = BookmarkReader.Read(BookmarkBrowser.Firefox, profiles.Single().Path);
            check(profiles.Single().Name == "Firefox fixture" && firefox.Descendants().Count(n => n.Supported) == 2 && firefox.Descendants().Any(n => n.Name == "Latest WAL bookmark"), "Firefox profile discovery and consistent read-only snapshot include current WAL bookmarks");
            command.CommandText = "SELECT COUNT(*) FROM moz_bookmarks"; check(Convert.ToInt32(command.ExecuteScalar()) == 5, "Firefox importing leaves the browser bookmark database untouched");
        }
        foreach (var browser in new[] { BookmarkBrowser.Chrome, BookmarkBrowser.Edge })
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var actual = BookmarkReader.Discover(browser, Path.Combine(user, "AppData/Local"), Path.Combine(user, "AppData/Roaming"));
            if (actual.Count > 0)
            {
                var bytes = File.ReadAllBytes(actual[0].Path); var actualTree = BookmarkReader.Read(browser, actual[0].Path);
                check(actualTree.Folder && bytes.SequenceEqual(File.ReadAllBytes(actual[0].Path)), "Local " + browser + " bookmarks parse read-only without changing the source file");
            }
        }
        var store = new LayoutStore(Path.Combine(output, "bookmark-ui"));
        var window = new MainWindow(store, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show(); Pump();
        check(MainWindow.FindDescendants<Button>(window).Any(b => b.Content as string == "⚙" && b.Width == 20) && !MainWindow.FindDescendants<TextBlock>(window).Any(t => t.Text == "homepage"), "Tiny settings gear replaces the lowercase homepage label");
        var single = read.Children[0]; window.ImportBookmarks(single);
        check(window.CurrentLayout.Items.Single().Label == "My chosen name" && window.CurrentLayout.Items.Single().CustomLabel, "Individual bookmarks import on the current canvas with their browser names preserved");
        window.Undo(); var group = window.ImportBookmarks(read)!; Pump();
        window.Undo(); check(window.CurrentLayout.Groups.Count == 0 && !window.CurrentLayout.AllItems().Any(), "One undo removes an entire imported folder tree");
        group = window.ImportBookmarks(read)!; window.ImportBookmarks(read); check(window.CurrentLayout.Groups.Count == 2 && window.CurrentLayout.AllItems().Count() == 10, "Repeated imports create independent copies rather than synchronizing or merging"); window.Undo(); group = window.CurrentLayout.Groups[0]; Pump();
        check(group.Label == read.Name && group.ActiveTab.Name == "Main" && group.AllTabs().All(t => t.Rows && t.Scroll) && group.Items.Count == 1 && group.Tabs.Any(t => t.Name == "Empty"), "Folder import creates a row group with Main, ordered folder tabs and empty collections");
        check(window.CurrentLayout.AllItems().Count() == 5 && group.AllTabs().Count() == 6, "Four nested folder levels import without flattening or duplication");
        var deepest = group.AllTabs().First(t => t.Name == "Level 4"); window.SelectGroupTab(group, deepest, true); Pump();
        check(group.ActivePath().Count() == 4 && group.ActiveTab == deepest && MainWindow.FindDescendants<Button>(window).Count(b => b.Tag as string is { } id && id.StartsWith("contents:")) == 3, "Nested selection reveals stacked child tab rows with Main navigation at each level");
        var rootTab = group.Tabs[0]; window.SelectGroupTab(group, rootTab, true); group.Rows = false; group.Scroll = false; window.SelectGroupTab(group, deepest, true);
        check(group.Rows && group.Scroll && !rootTab.Rows && !rootTab.Scroll, "Nested collections remember view and scrolling settings independently of parent collections");
        window.AddTargets(["https://example.com/added"], group, new Point());
        check(deepest.Items.Count == 2 && group.Tabs[0].Items.Count == 1, "Additions use the selected nested collection");
        var extra = window.AddNestedTab(group, deepest, "Extra");
        window.RenameGroupTab(extra, "Renamed"); window.RemoveGroupTab(group, extra); window.Undo(); Pump();
        group = window.CurrentLayout.Groups[0]; deepest = group.AllTabs().First(t => t.Name == "Level 4");
        check(deepest.Children.Single().Name == "Renamed", "Nested creation, rename, removal and undo preserve the complete subtree");
        window.RemoveGroupTab(group, deepest.Children[0]);
        check(deepest.Children.Count == 0 && group.ActiveTab == deepest, "The final child tab may be removed because parent Main remains available");
        var sibling = window.AddNestedTab(group, deepest, "Sibling"); var sibling2 = window.AddNestedTab(group, deepest, "Sibling 2"); window.ReorderGroupTab(group, sibling2.Id, sibling.Id);
        check(deepest.Children[0] == sibling2, "Nested tab reorder stays within its sibling collection");
        var link = deepest.Items[0]; var page = window.AddPage("Destination"); var destination = new Group { Label = "Destination group" }; page.Groups.Add(destination);
        var nested = window.AddNestedTab(destination, destination.ActiveTab, "Inside"); window.TransferShortcut(link, page, destination, nested); Pump();
        check(destination.ActiveTab == nested && nested.Items.Contains(link) && !deepest.Items.Contains(link) && window.CurrentLayout.AllItems().Count(i => i.Id == link.Id) == 1, "Moving a nested shortcut across pages selects the full destination path without duplication");
        window.Undo(); Pump(); check(window.CurrentLayout.AllGroups().First(g => g.Id == group.Id).AllTabs().First(t => t.Id == deepest.Id).Items.Any(i => i.Id == link.Id), "Undo restores cross-page nested transfers");
        var state = window.CurrentLayout; var originalPage = state.Pages[0]; group = originalPage.Groups[0]; deepest = group.AllTabs().First(t => t.Name == "Level 4");
        var hidden = new Shortcut { Label = "Browser title", CustomLabel = true, Target = "https://thermofisher.sharepoint.com/sites/MaterialMasterTeam/Shared%20Documents/Forms/AllItems.aspx" }; deepest.Items.Add(hidden);
        Wait(window.RefreshWebsiteAsync(hidden)); check(hidden.Label == "Browser title" && hidden.WebsiteIcon is not null, "Website metadata reaches hidden descendants while preserving imported bookmark names");
        store.Save(state); var reloaded = store.Load();
        check(reloaded.Version == 3 && reloaded.AllItems().Count() == state.AllItems().Count() && reloaded.AllGroups().First(g => g.Id == group.Id).AllTabs().First(t => t.Id == deepest.Id).Items.Any(i => i.WebsiteIcon == hidden.WebsiteIcon), "Version 3 persists recursive tab contents, selections and website icons");
        var old = LayoutStore.Serialize(state).Replace("\"Version\": 3", "\"Version\": 2");
        var migration = new LayoutStore(Path.Combine(output, "v2-migration")); Directory.CreateDirectory(migration.DataDirectory); File.WriteAllText(migration.SavePath, old); var upgraded = migration.Load(); migration.Save(upgraded);
        check(upgraded.Version == 3 && File.ReadAllText(migration.Version2BackupPath) == old && upgraded.AllItems().Count() == state.AllItems().Count(), "Version 2 saves upgrade atomically while preserving the original separately");
        window.SelectPage(originalPage); window.SelectGroupTab(group, deepest, true); window.UpdateLayout(); Pump();
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) SelfTests.Render(window, Path.Combine(output, "homepage-nested-" + (int)(scale * 100) + ".png"), scale);
        var dialog = new BookmarkDialog(window, local, roaming) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        dialog.Show(); var browserCombo = MainWindow.FindDescendants<ComboBox>(dialog).First(c => c.Items.OfType<BookmarkBrowser>().Any()); browserCombo.SelectedItem = BookmarkBrowser.Chrome; Wait(dialog.PendingOperation); Pump();
        var profileCombo = MainWindow.FindDescendants<ComboBox>(dialog).First(c => c.DisplayMemberPath == "Name");
        check(profileCombo.IsEnabled && profileCombo.Items.Count == 2 && profileCombo.SelectedIndex == -1, "Importer asks for a profile when multiple bookmark profiles are available");
        profileCombo.SelectedIndex = 0; Wait(dialog.PendingOperation); Pump();
        var tree = MainWindow.FindDescendants<TreeView>(dialog).Single(); var root = (TreeViewItem)tree.Items[0]; var treeFolder = (TreeViewItem)root.Items[0]; treeFolder.IsExpanded = true; ((TreeViewItem)treeFolder.Items[0]).IsSelected = true; Pump();
        check(dialog.SelectedNode?.Name == "My chosen name" && MainWindow.FindDescendants<Button>(dialog).First(b => b.Content as string == "Import").IsEnabled, "Bookmark tree selection enables import of an individual link and displays its browser name");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) RenderDialog(dialog, Path.Combine(output, "bookmark-dialog-" + (int)(scale * 100) + ".png"), scale);
        int before = state.AllItems().Count(); dialog.Close(); check(state.AllItems().Count() == before, "Closing the bookmark chooser does not import or alter the launcher");
        window.Close();
    }
    static void RenderDialog(Window window, string path, double scale)
    {
        var root = (FrameworkElement)window.Content; root.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(root);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
}
