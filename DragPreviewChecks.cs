using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Homepage;

public static class DragPreviewChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        var store = new LayoutStore(Path.Combine(output, "drag-preview"));
        var window = new MainWindow(store, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        var state = new Layout { Snap = true };
        var item = new Shortcut { Label = "Dragged shortcut", Target = "https://example.com", X = 40, Y = 50 }; state.Items.Add(item);
        List<Shortcut> Links(int count) => Enumerable.Range(1, count).Select(i => new Shortcut { Label = "Destination " + i, Target = "https://example.com/" + i }).ToList();
        var rows = new Group { Label = "Row landing", Rows = true, Condensed = true, Scroll = true, Width = 300, Height = 220, X = 190, Y = 50, Items = Links(8) };
        var icons = new Group { Label = "Icon landing", Condensed = true, Width = 300, Height = 210, X = 530, Y = 50, Items = Links(5) }; state.Groups.AddRange([rows, icons]);
        window.SetLayoutForTest(state); window.Show(); window.SetEditingForTest(true); window.UpdateLayout();
        Border Tile(string id) => MainWindow.FindDescendants<Border>(window.Surface).First(b => b.Tag as string == id);
        Border Marker() => MainWindow.FindDescendants<Border>(window).First(b => b.Tag as string == "shortcut-landing");
        Panel Items(Group group) => (Panel)MainWindow.FindDescendants<ScrollViewer>(Tile(group.Id)).First(s => s.Tag as string == "group-scroll").Content;
        var source = Tile(item.Id); var before = LayoutStore.Serialize(state);
        window.BeginShortcutDrag(item, source, new Point(20, 20));
        var ghost = MainWindow.FindDescendants<Border>(window).First(b => b.Tag as string == "shortcut-ghost");
        check(source.Opacity == .25 && ghost.Opacity == .6 && !ghost.IsHitTestVisible && ghost.Width == source.ActualWidth, "Shortcut dragging fades the source and shows a translucent non-interactive ghost");
        var bitmap = (System.Windows.Media.Imaging.BitmapSource)((Image)ghost.Child).Source;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        check(Enumerable.Range(0, bitmap.PixelWidth * (bitmap.PixelHeight / 2)).Any(i => pixels[i * 4 + 3] > 0) && Enumerable.Range(bitmap.PixelWidth * (bitmap.PixelHeight * 3 / 4), bitmap.PixelWidth * (bitmap.PixelHeight - bitmap.PixelHeight * 3 / 4)).Any(i => pixels[i * 4 + 3] > 0), "Ghost snapshot retains the full icon and label regardless of source canvas offsets");
        var point = new Point(147, 313); window.UpdateShortcutDragPreview(item, point); window.UpdateLayout();
        var marker = Marker(); var target = marker.TranslatePoint(new Point(), window.Surface);
        check(target.X == 96 && target.Y == 288 && marker.Width == 88 && marker.Height == 88, "Canvas landing marker uses the exact snapped position and final tile size");
        check(LayoutStore.Serialize(state) == before, "Drag previews do not change shortcut membership or create layout edits");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) SelfTests.Render(window, Path.Combine(output, "homepage-drag-canvas-" + (int)(scale * 100) + ".png"), scale);
        window.EndShortcutDrag(); check(source.Opacity == 1 && Marker().Visibility == Visibility.Collapsed && !MainWindow.FindDescendants<Border>(window).Any(b => b.Tag as string == "shortcut-ghost"), "Ending or cancelling a drag restores opacity and removes both previews");
        window.MoveShortcut(item, null, 0, point); check(item.X == target.X && item.Y == target.Y, "Releasing a shortcut on the canvas places it exactly at its preview");
        source = Tile(item.Id); window.BeginShortcutDrag(item, source, new Point(20, 20));
        var panel = Items(rows); var pointer = panel.TranslatePoint(new Point(80, 18 * 3 + 2), window.Surface);
        int index = window.UpdateShortcutDragPreview(item, pointer, rows, panel); window.UpdateLayout(); marker = Marker(); target = marker.TranslatePoint(new Point(), panel);
        check(index == 3 && marker.Height == 18 && target.Y == 54, "Condensed row destinations preview the correct insertion slot and row height");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) SelfTests.Render(window, Path.Combine(output, "homepage-drag-rows-" + (int)(scale * 100) + ".png"), scale);
        window.EndShortcutDrag(); window.MoveShortcut(item, rows, index, pointer); window.UpdateLayout();
        check(rows.Items[3] == item && state.AllItems().Count(i => i.Id == item.Id) == 1, "Cross-group release inserts at the previewed row without duplication");
        panel = Items(rows); source = Tile(item.Id); window.BeginShortcutDrag(item, source, new Point(30, 8));
        pointer = panel.TranslatePoint(new Point(80, 18 * 6 + 2), window.Surface); index = window.UpdateShortcutDragPreview(item, pointer, rows, panel); window.UpdateLayout(); target = Marker().TranslatePoint(new Point(), panel);
        check(index == 6 && target.Y == 5 * 18, "Same-group landing preview accounts for removing the source before insertion");
        window.EndShortcutDrag(); window.MoveShortcut(item, rows, index, pointer); window.UpdateLayout(); check(rows.Items[5] == item, "Same-group row reordering matches the previewed final index");
        source = Tile(item.Id); window.BeginShortcutDrag(item, source, new Point(30, 8));
        panel = Items(icons); pointer = panel.TranslatePoint(new Point(95, 2), window.Surface); index = window.UpdateShortcutDragPreview(item, pointer, icons, panel); window.UpdateLayout(); marker = Marker(); target = marker.TranslatePoint(new Point(), panel);
        check(index == 1 && target.X == 88 && target.Y == 0 && marker.Height == 44, "Icon-grid destinations show the exact column and condensed tile size");
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) SelfTests.Render(window, Path.Combine(output, "homepage-drag-icons-" + (int)(scale * 100) + ".png"), scale);
        window.EndShortcutDrag(); window.MoveShortcut(item, icons, index, pointer); check(icons.Items[1] == item && !rows.Items.Contains(item), "Row-to-icon transfers land at the previewed icon position");
        window.Close();
    }
}
