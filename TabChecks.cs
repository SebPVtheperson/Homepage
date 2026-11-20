using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Homepage;

public static class TabChecks
{
    static void Pump() => Wait(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    public static void Run(Action<bool, string> check, string output)
    {
        var store = new LayoutStore(Path.Combine(output, "tab-migration")); Directory.CreateDirectory(store.DataDirectory);
        const string legacy = """
        {"Version":1,"Snap":true,"ScrollX":88,"ScrollY":144,
         "Items":[{"Id":"loose","Label":"Document","Target":"files/notes.txt","X":233,"Y":377}],
         "Groups":[{"Id":"group","Label":"Master data","X":67,"Y":109,"Width":299,"Height":177,"Rows":true,"Scroll":true,
          "Items":[{"Id":"website","Label":"Custom name","Target":"https://example.com","WebsiteIcon":"icons/cached.png","WebsiteTitle":"Original title","CustomLabel":true,"WebsiteMetadataFetched":true,"X":12,"Y":34}]}]}
        """;
        File.WriteAllText(store.SavePath, legacy);
        var migrated = store.Load();
        check(store.NeedsUpgrade && migrated.Version == 3 && migrated.ActivePage.Name == "Home" && migrated.Groups[0].ActiveTab.Name == "Main", "Version 1 migrates into Home and Main tabs");
        check(migrated.Snap && migrated.ScrollX == 88 && migrated.ScrollY == 144 && migrated.Items[0].X == 233 && migrated.Items[0].Y == 377 && migrated.Groups[0].X == 67 && migrated.Groups[0].Width == 299 && migrated.Groups[0].Rows && migrated.Groups[0].Scroll, "Migration preserves positions, base dimensions, scrolling and snapping");
        var oldWeb = migrated.AllItems().First(i => i.Id == "website");
        check(oldWeb.CustomLabel && oldWeb.WebsiteIcon == "icons/cached.png" && oldWeb.WebsiteTitle == "Original title" && oldWeb.WebsiteMetadataFetched && oldWeb.X == 12, "Migration preserves shortcut identities, custom labels and cached website metadata");
        store.Save(migrated); store.Save(migrated);
        check(File.ReadAllText(store.Version1BackupPath) == legacy && JsonDocument.Parse(File.ReadAllText(store.SavePath)).RootElement.GetProperty("Version").GetInt32() == 3, "Original version 1 save is preserved separately before atomic version 3 saves");
        var rejected = false; try { LayoutStore.Deserialize("{\"Version\":2,\"Pages\":[]}"); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Empty page collections cannot replace a valid layout");
        int invalidVersions = 0;
        foreach (var bad in new[] { "{}", "{\"Version\":\"2\"}", "{\"Version\":2,\"Pages\":[{}]}" })
        {
            try { LayoutStore.Deserialize(bad); } catch (Exception e) when (e is InvalidDataException or JsonException) { invalidVersions++; }
        }
        check(invalidVersions == 3, "Missing versions and incomplete page data produce recoverable errors");
        var originalPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "Homepage.data", "layout.json");
        if (File.Exists(originalPath))
        {
            var original = File.ReadAllText(originalPath); using var doc = JsonDocument.Parse(original);
            if (doc.RootElement.GetProperty("Version").GetInt32() == 1)
            {
                var actualStore = new LayoutStore(Path.Combine(output, "actual-layout-migration")); Directory.CreateDirectory(actualStore.DataDirectory);
                File.WriteAllText(actualStore.SavePath, original); var actual = actualStore.Load();
                var root = doc.RootElement;
                var expected = root.GetProperty("Items").EnumerateArray().Concat(root.GetProperty("Groups").EnumerateArray().SelectMany(g => g.GetProperty("Items").EnumerateArray())).Select(e => JsonSerializer.Deserialize<Shortcut>(e.GetRawText())!).ToArray();
                check(expected.Length == actual.AllItems().Count() && expected.All(i => actual.AllItems().Any(a => JsonSerializer.Serialize(i) == JsonSerializer.Serialize(a))), "Existing user layout migrates every shortcut and cached icon unchanged in an isolated copy");
                var oldGroups = root.GetProperty("Groups").EnumerateArray().ToArray();
                check(oldGroups.Length == actual.Groups.Count && oldGroups.All(g => actual.Groups.Any(a => a.Id == g.GetProperty("Id").GetString() && a.Label == g.GetProperty("Label").GetString() && a.X == g.GetProperty("X").GetDouble() && a.Y == g.GetProperty("Y").GetDouble() && a.Width == g.GetProperty("Width").GetDouble() && a.Height == g.GetProperty("Height").GetDouble() && a.Rows == g.GetProperty("Rows").GetBoolean() && a.Scroll == g.GetProperty("Scroll").GetBoolean())), "Existing user groups migrate geometry, names and display settings unchanged");
                actualStore.Save(actual); check(File.ReadAllText(actualStore.Version1BackupPath) == original, "Existing user save remains byte-for-byte intact in the migration backup");
            }
        }

        var uiStore = new LayoutStore(Path.Combine(output, "tab-ui"));
        var window = new MainWindow(uiStore, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show(); Pump();
        var state = new Layout();
        var home = state.ActivePage;
        var group = new Group { Label = "Resources", X = 52, Y = 72, Width = 310, Height = 190, Rows = true, Scroll = true,
            Items = Enumerable.Range(1, 25).Select(i => new Shortcut { Label = "Resource " + i, Target = "https://example.com/" + i }).ToList() };
        home.Groups.Add(group);
        var originalTab = group.ActiveTab;
        window.SetLayoutForTest(state); window.UpdateLayout(); Pump();
        check(MainWindow.FindDescendants<Button>(window).Any(b => (b.Content as string) == "+" && b.TranslatePoint(new Point(), (UIElement)window.Content).Y > 30), "Group plus remains available outside pencil mode");
        var inner = MainWindow.FindDescendants<ScrollViewer>(window).First(s => s.Tag as string == "group-scroll");
        inner.ScrollToVerticalOffset(144); window.UpdateLayout(); Pump();
        check(originalTab.ScrollY == 144, "Group tab records its own internal scroll offset");
        var second = window.AddGroupTab(group, "Applications"); window.UpdateLayout(); Pump();
        check(group.ActiveTab == second && !group.Rows && !group.Scroll && group.Items.Count == 0 && group.Width == 310 && group.Height == 190, "New group tab selects immediately with independent view settings and shared dimensions");
        MainWindow.FindDescendants<Button>(window).First(b => b.Tag as string == originalTab.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); Pump();
        inner = MainWindow.FindDescendants<ScrollViewer>(window).First(s => s.Tag as string == "group-scroll");
        check(group.Rows && group.Scroll && inner.VerticalOffset == 144, "Switching group tabs restores row mode and internal scrolling");
        window.SelectGroupTab(group, second); window.Undo(); Pump();
        state = window.CurrentLayout; home = state.ActivePage; group = home.Groups[0]; originalTab = group.ActiveTab;
        check(group.Tabs.Count == 1 && group.Items.Count == 25, "Tab navigation adds no undo entry and undo removes the newly created group tab");
        second = window.AddGroupTab(group, "Applications"); Pump();
        window.RenameGroupTab(second, "Apps"); window.Undo(); Pump();
        state = window.CurrentLayout; home = state.ActivePage; group = home.Groups[0]; originalTab = group.Tabs[0]; second = group.Tabs[1];
        check(second.Name == "Applications", "Group tab rename supports undo");
        window.ReorderGroupTab(group, second.Id, originalTab.Id);
        check(group.Tabs[0] == second, "Group tab reorder preserves contents and selection");
        window.RemoveGroupTab(group, originalTab); window.Undo(); Pump();
        state = window.CurrentLayout; home = state.ActivePage; group = home.Groups[0]; originalTab = group.Tabs.First(t => t.Name == "Main"); second = group.Tabs.First(t => t.Name == "Applications");
        check(originalTab.Items.Count == 25 && group.Tabs.Count == 2, "Removing a populated group tab and undo restores all shortcuts");
        window.SetEditingForTest(true); window.UpdateLayout(); Pump(); // Empty pages only have room to scroll while editing.
        window.Viewport.ScrollToHorizontalOffset(270); window.Viewport.ScrollToVerticalOffset(330); window.UpdateLayout(); Pump();
        var work = window.AddPage("Work"); window.UpdateLayout(); Pump();
        check(window.CurrentLayout.ActivePage == work && window.Viewport.HorizontalOffset == 0 && window.Viewport.VerticalOffset == 0 && home.ScrollX == 270 && home.ScrollY == 330, "New pages select immediately and keep the previous canvas offsets");
        window.Viewport.ScrollToHorizontalOffset(110); window.Viewport.ScrollToVerticalOffset(210); window.UpdateLayout(); Pump();
        MainWindow.FindDescendants<Button>(window).First(b => b.Tag as string == home.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); Pump();
        check(window.Viewport.HorizontalOffset == 270 && window.Viewport.VerticalOffset == 330 && work.ScrollX == 110 && work.ScrollY == 210, "Page switching restores both canvas scroll directions independently");
        window.SetEditingForTest(false); window.UpdateLayout(); Pump();
        window.SelectPage(work); window.Undo(); Pump();
        state = window.CurrentLayout; home = state.ActivePage; group = home.Groups[0]; originalTab = group.Tabs.First(t => t.Name == "Main"); second = group.Tabs.First(t => t.Name == "Applications");
        check(state.Pages.Count == 1 && state.AllItems().Count() == 25, "Page navigation adds no undo entry and undo removes page creation");
        work = window.AddPage("Work"); Pump();
        var item = originalTab.Items[0];
        var destination = new Group { Label = "Work links", X = 80, Y = 70 }; work.Groups.Add(destination);
        var destinationTab = window.AddGroupTab(destination, "More");
        window.TransferShortcut(item, work, destination, destinationTab); window.UpdateLayout(); Pump();
        check(destinationTab.Items.Contains(item) && !originalTab.Items.Contains(item) && state.AllItems().Count(i => i.Id == item.Id) == 1 && state.ActivePage == work && destination.ActiveTab == destinationTab, "Move to transfers across pages and group tabs once and selects the destination");
        window.TransferShortcut(item, home); Pump();
        check(home.Items.Contains(item) && !destinationTab.Items.Contains(item) && state.ActivePage == home, "Move to supports standalone canvas destinations across pages");
        window.Undo(); Pump();
        state = window.CurrentLayout; home = state.Pages.First(p => p.Name == "Home"); work = state.Pages.First(p => p.Name == "Work");
        check(state.ActivePage == work && work.Groups[0].ActiveTab.Items.Any(i => i.Id == item.Id) && state.AllItems().Count(i => i.Id == item.Id) == 1, "Undo restores transfers across hidden pages without duplication");
        window.RenamePage(work, "Office"); window.ReorderPage(work.Id, home.Id);
        check(state.Pages[0] == work && work.Name == "Office", "Page tabs can be renamed and reordered");
        window.RemovePage(home); window.Undo(); Pump();
        state = window.CurrentLayout; home = state.Pages.First(p => p.Name == "Home"); work = state.Pages.First(p => p.Name == "Office");
        check(home.Groups[0].Tabs.First(t => t.Name == "Main").Items.Count == 24, "Removing a populated page and undo restores hidden group tabs");
        var web1 = new Shortcut { Label = "thermofisher.sharepoint.com", Target = "https://thermofisher.sharepoint.com/sites/MaterialMasterTeam/Shared%20Documents/Forms/AllItems.aspx" };
        var web2 = new Shortcut { Label = "thermofisher.sharepoint.com", Target = web1.Target + "?view=another" };
        home.Groups[0].Tabs.First(t => t.Name == "Main").Items.Add(web1); home.Items.Add(web2);
        window.SelectPage(work); Pump();
        Wait(window.RefreshWebsiteAsync(web1)); Wait(window.RefreshWebsiteAsync(web2));
        check(web1.WebsiteTitle == "Material Master Team - Documents" && web1.WebsiteIcon == web2.WebsiteIcon && Directory.GetFiles(Path.Combine(uiStore.DataDirectory, "icons"), "*.png").Length == 1, "Website updates reach hidden pages and group tabs and share one cached icon");
        uiStore.Save(state); var reloaded = new LayoutStore(uiStore.BaseDirectory).Load();
        check(reloaded.Pages.Count == 2 && reloaded.ActivePage.Name == "Office" && reloaded.AllItems().Count() == state.AllItems().Count() && reloaded.AllItems().First(i => i.Id == web1.Id).WebsiteIcon == web1.WebsiteIcon && reloaded.Pages.First(p => p.Name == "Home").ScrollX == home.ScrollX, "Version 3 persists all pages, selected tabs, offsets and hidden website icons");
        window.SelectPage(home); group = home.Groups[0];
        window.SelectGroupTab(group, group.Tabs.First(t => t.Name == "Main")); Pump();
        for (int i = 0; i < 12; i++) window.AddPage("Page " + i + " with a long title");
        window.SelectPage(home);
        for (int i = 0; i < 10; i++) window.AddGroupTab(group, "Collection " + i);
        window.SelectGroupTab(group, group.Tabs.First(t => t.Name == "Main"));
        foreach (var loose in home.Items) { loose.X = 410; loose.Y = 72; }
        window.Render();
        window.Viewport.ScrollToHorizontalOffset(0); window.Viewport.ScrollToVerticalOffset(0); window.UpdateLayout(); Pump();
        window.Width = 640; window.UpdateLayout(); Pump();
        check(MainWindow.FindDescendants<ScrollViewer>(window).Count(s => s.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && s.ScrollableWidth > 0) >= 2, "Long page and group tab strips overflow horizontally");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) SelfTests.Render(window, Path.Combine(output, "homepage-tabs-" + (int)(scale * 100) + ".png"), scale);
        window.SetEditingForTest(true); Pump(); SelfTests.Render(window, Path.Combine(output, "homepage-tabs-edit.png"), 1);
        window.Close();
        var lastStore = new LayoutStore(Path.Combine(output, "last-tabs"));
        var last = new MainWindow(lastStore, true); last.RemovePage(last.CurrentLayout.ActivePage);
        var lastGroup = new Group(); last.CurrentLayout.Groups.Add(lastGroup); last.RemoveGroupTab(lastGroup, lastGroup.ActiveTab);
        check(last.CurrentLayout.Pages.Count == 1 && lastGroup.Tabs.Count == 1, "The final page and final group tab cannot be removed"); last.Close();
    }
}
