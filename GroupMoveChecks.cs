using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Homepage;

public static class GroupMoveChecks
{
    static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    public static void Run(Action<bool, string> check, string output)
    {
        var store = new LayoutStore(Path.Combine(output, "group-move")); Directory.CreateDirectory(store.BaseDirectory);
        var file = Path.Combine(store.BaseDirectory, "notes.txt"); File.WriteAllText(file, "Keep this target.");
        var state = new Layout(); state.ActivePage.Name = "Work"; var source = state.ActivePage;
        var destination = new CanvasPage { Name = "Employee" }; state.Pages.Add(destination);
        var child = new GroupTab { Name = "Nested", Rows = true, Scroll = true, ScrollY = 72, Items = [new Shortcut { Label = "Notes", Target = "notes.txt" }, new Shortcut { Label = "Web", Target = "https://example.com", WebsiteIcon = "icons/existing.png" }] };
        var root = new GroupTab { Name = "Admin", Children = [child], SelectedChildId = child.Id };
        var group = new Group { Label = "TFS Admin", X = 420, Y = 920, Width = 320, Height = 250, Tabs = [root], SelectedTabId = root.Id };
        source.Groups.Add(group);
        var window = new MainWindow(store, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show(); window.SetLayoutForTest(state); window.UpdateLayout(); Pump();
        var original = JsonSerializer.Serialize(group);
        var border = MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == group.Id);
        var move = border.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header as string == "Move to page…");
        check(move.IsEnabled && !move.Items.OfType<MenuItem>().First(m => m.Header as string == "Work").IsEnabled, "Group menu offers other main pages and disables its current page");
        move.Items.OfType<MenuItem>().First(m => m.Header as string == "Employee").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        check(!source.Groups.Contains(group) && destination.Groups.Single() == group && state.ActivePage == destination && state.AllGroups().Count(g => g.Id == group.Id) == 1, "Group menu moves TFS Admin to Employee once and switches to the destination");
        check(JsonSerializer.Serialize(group) == original && state.AllItems().Count() == 2 && File.Exists(file), "Moving a group preserves geometry, nested selections, settings, icons and linked files");
        check(window.Viewport.VerticalOffset > 0 && MainWindow.FindDescendants<Border>(window.Surface).Any(b => b.Tag as string == group.Id), "Destination canvas reveals the moved group even below the initial viewport");
        store.Save(state); var saved = store.Load();
        check(saved.ActivePage.Name == "Employee" && saved.Pages.First(p => p.Name == "Employee").Groups.Single().Id == group.Id && saved.Pages.First(p => p.Name == "Work").Groups.Count == 0, "Moved group membership and destination selection persist after reload");
        window.Undo(); Pump();
        check(window.CurrentLayout.ActivePage.Name == "Work" && window.CurrentLayout.Pages.First(p => p.Name == "Work").Groups.Single().Id == group.Id && !window.CurrentLayout.Pages.First(p => p.Name == "Employee").Groups.Any() && window.CurrentLayout.AllItems().Count() == 2, "One undo restores the entire group to its original main page");
        window.Close();
    }
}
