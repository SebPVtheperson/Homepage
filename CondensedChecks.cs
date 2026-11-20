using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Homepage;

public static class CondensedChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        var store = new LayoutStore(Path.Combine(output, "condensed"));
        var window = new MainWindow(store, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        var state = new Layout();
        List<Shortcut> Items() => Enumerable.Range(1, 14).Select(i => new Shortcut { Label = "A readable shortcut label " + i, Target = "https://example.com/" + i }).ToList();
        var rows = new Group { Label = "Condensed rows", Rows = true, X = 50, Y = 60, Width = 330, Height = 100, Items = Items() };
        var icons = new Group { Label = "Condensed icons", X = 420, Y = 60, Width = 300, Height = 100, Items = Items() };
        state.Groups.AddRange([rows, icons]); window.SetLayoutForTest(state); window.Show(); window.UpdateLayout();
        check(MainWindow.FindDescendants<Button>(window.Surface).Count(b => b.Content as string == "⋯" && b.IsVisible) == 2, "Group ellipsis buttons remain visible and usable without pencil editing");
        var normalHeight = MainWindow.GroupHeight(rows);
        var border = MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == rows.Id);
        var options = border.ContextMenu.Items.OfType<MenuItem>().ToList();
        check(options.FindIndex(m => m.Header as string == "Condensed") == options.FindIndex(m => m.Header as string == "Show as rows") + 1, "Condensed option appears directly below Show as rows");
        options.First(m => m.Header as string == "Condensed").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); window.UpdateLayout();
        border = MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == rows.Id);
        var rowTile = MainWindow.FindDescendants<Border>(border).First(b => b.Tag as string == rows.Items[0].Id);
        var text = MainWindow.FindDescendants<TextBlock>(rowTile).Single();
        check(rowTile.Height == 18 && MainWindow.FindDescendants<Image>(rowTile).Single().Height == 12 && text.ActualHeight <= rowTile.ActualHeight - 2 && MainWindow.GroupHeight(rows) == normalHeight - 14 * 18, "Condensed rows halve occupied height and icons while labels remain within their rows");
        border = MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == icons.Id);
        border.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header as string == "Condensed").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); window.UpdateLayout();
        border = MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == icons.Id);
        var iconTile = MainWindow.FindDescendants<Border>(border).First(b => b.Tag as string == icons.Items[0].Id);
        check(iconTile.Height == 44 && MainWindow.FindDescendants<Image>(iconTile).Single().Height == 24 && MainWindow.FindDescendants<WrapPanel>(border).Single().ItemHeight == 44, "Condensed icon grids halve tile height and use matching wrap spacing");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) SelfTests.Render(window, Path.Combine(output, "homepage-condensed-" + (int)(scale * 100) + ".png"), scale);
        store.Save(state); var reloaded = store.Load();
        check(reloaded.Groups.All(g => g.Condensed), "Condensed settings persist after reload");
        window.Undo(); check(!window.CurrentLayout.Groups[1].Condensed && window.CurrentLayout.Groups[0].Condensed, "Condensed changes support undo independently per collection");
        var group = window.CurrentLayout.Groups[0]; var nested = window.AddNestedTab(group, group.ActiveTab, "Full size");
        check(!group.Condensed && group.Tabs[0].Condensed && nested == group.ActiveTab, "New nested collections retain their own independent density setting");
        window.Close();
    }
}
