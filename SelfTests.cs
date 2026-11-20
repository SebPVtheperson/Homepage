using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Homepage;

public static class SelfTests
{
    public static int Run(string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = Path.GetFullPath(outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : Path.Combine(Path.GetTempPath(), "Homepage-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        var results = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); results.Add("PASS " + name); }
        try
        {
            var home = Path.Combine(output, "original"); Directory.CreateDirectory(home);
            var store = new LayoutStore(home);
            Check(store.Load().AllItems().Count() == 0, "New folder starts with an empty layout");
            Directory.CreateDirectory(Path.Combine(home, "files"));
            var file = Path.Combine(home, "files", "notes.txt"); File.WriteAllText(file, "A portable document.");
            Check(store.StoreTarget(file) == Path.Combine("files", "notes.txt"), "Contained files use relative paths");
            var external = Path.Combine(output, "external.txt"); File.WriteAllText(external, "External.");
            Check(Path.IsPathRooted(store.StoreTarget(external)), "External targets retain absolute paths");
            Check(store.StoreTarget("https://example.com") == "https://example.com", "Web addresses remain unchanged");
            Check(!LayoutStore.IsWebsite("javascript:alert(1)") && !LayoutStore.IsWebsite("file:///C:/temp"), "Only HTTP and HTTPS are classified as websites");
            var state = new Layout { Snap = true, ScrollX = 40, ScrollY = 70 };
            state.Items.Add(new Shortcut { Label = "Notes", Target = store.StoreTarget(file), X = 32, Y = 64 });
            state.Groups.Add(new Group { Label = "Links", Rows = true, Scroll = true, Items = [new Shortcut { Label = "Example", Target = "https://example.com" }] });
            store.Save(state); state.Items[0].Label = "Renamed notes"; store.Save(state);
            var reloaded = new LayoutStore(home).Load();
            Check(reloaded.Items[0].Label == "Renamed notes" && reloaded.Groups[0].Scroll && reloaded.Groups[0].Rows && reloaded.Snap && reloaded.ScrollY == 70, "Layout and preferences survive restart");
            Check(LayoutStore.Deserialize(File.ReadAllText(store.BackupPath)).Items[0].Label == "Notes", "Atomic save retains previous valid state");
            using (var locked = new FileStream(store.SavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool saveFailed = false; try { store.Save(state); } catch (IOException) { saveFailed = true; }
                Check(saveFailed, "A locked save reports failure instead of discarding changes");
            }
            store.Save(state);
            Check(new LayoutStore(home).Load().Items[0].Label == "Renamed notes", "Saving can retry after a file lock is released");
            var moved = Path.Combine(output, "relocated"); Directory.CreateDirectory(Path.Combine(moved, "files"));
            File.Copy(file, Path.Combine(moved, "files", "notes.txt"));
            var movedStore = new LayoutStore(moved); movedStore.Save(reloaded);
            Check(File.Exists(movedStore.ResolveTarget(movedStore.Load().Items[0].Target)), "Relative links survive moving the app folder and files together");
            File.WriteAllText(store.SavePath, "{broken"); var recovery = new LayoutStore(home); var recovered = recovery.Load();
            Check(recovery.CanSave && recovered.Items[0].Label == "Renamed notes" && recovery.LoadMessage is not null, "Damaged primary recovers the previous save");
            Check(Directory.GetFiles(store.DataDirectory, "*.damaged-*").Length == 1, "Recovery preserves the damaged file");
            recovery.Save(recovered);
            Check(LayoutStore.Deserialize(File.ReadAllText(store.BackupPath)).Items[0].Label == "Renamed notes", "Recovery does not overwrite a valid backup with corrupt data");
            File.WriteAllText(store.SavePath, "broken"); File.WriteAllText(store.BackupPath, "broken");
            var blockedStore = new LayoutStore(home); blockedStore.Load();
            Check(!blockedStore.CanSave, "Unreadable primary and backup pause saving");
            bool blocked = false; try { blockedStore.Save(new Layout()); } catch (IOException) { blocked = true; }
            Check(blocked && File.ReadAllText(store.SavePath) == "broken", "Paused saving protects unreadable user data");
            blockedStore.Recover(); blockedStore.Save(new Layout()); Check(blockedStore.CanSave, "Explicit recovery allows saving a new layout");
            var future = false; try { LayoutStore.Deserialize("{\"Version\":4}"); } catch (InvalidDataException) { future = true; }
            Check(future, "Unknown layout versions are rejected");
            var invalid = false; try { LayoutStore.Deserialize("{\"Version\":1,\"Items\":null}"); } catch (InvalidDataException) { invalid = true; }
            Check(invalid, "Malformed layout collections are rejected");
            var history = new UndoHistory(); history.Push(state); state.Items.Clear();
            Check(history.Pop()!.Items.Count == 1 && File.Exists(file), "Undo restores entries without touching linked files");
            var group = new Group { Width = 220, Height = 100, Items = Enumerable.Range(0, 12).Select(i => new Shortcut { Target = "https://example.com", Label = "Link " + i }).ToList() };
            Check(MainWindow.GroupHeight(group) > 100, "Non-scrolling groups grow to fit icon contents"); group.Scroll = true;
            Check(MainWindow.GroupHeight(group) == 100, "Scrolling groups keep a fixed height");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var uiStore = new LayoutStore(Path.Combine(output, "ui")); Directory.CreateDirectory(uiStore.BaseDirectory);
            var window = new MainWindow(uiStore, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
            window.Show(); window.UpdateLayout();
            var demo = new Layout();
            demo.Items.Add(new Shortcut { Label = "Documents", Target = home, X = 52, Y = 52 });
            demo.Items.Add(new Shortcut { Label = "My notes", Target = file, X = 148, Y = 52 });
            demo.Groups.Add(new Group { Label = "Everyday", X = 52, Y = 190, Width = 400, Height = 230, Items = [new Shortcut { Label = "Browser", Target = "https://example.com" }, new Shortcut { Label = "Homepage", Target = Environment.ProcessPath! }, new Shortcut { Label = "Notes", Target = file }, new Shortcut { Label = "Files", Target = home }] });
            demo.Groups.Add(new Group { Label = "Reading list", X = 488, Y = 52, Width = 310, Height = 215, Rows = true, Scroll = true, Items = Enumerable.Range(1, 15).Select(i => new Shortcut { Label = "Resource " + i, Target = "https://example.com/" + i }).ToList() });
            demo.Groups.Add(new Group { Label = "Projects", X = 488, Y = 300, Width = 310, Height = 180, Items = [new Shortcut { Label = "Project files", Target = home }, new Shortcut { Label = "Reference", Target = "https://example.com" }] });
            window.SetLayoutForTest(demo); window.UpdateLayout();
            var headerButtons = MainWindow.FindDescendants<Button>(window).Where(b => b.Width == 20 && b.TranslatePoint(new Point(), (UIElement)window.Content).Y < 30).ToArray();
            Check(headerButtons.Length == 6 && headerButtons.All(b => b.IsVisible && b.IsEnabled && b.TranslatePoint(new Point(), (UIElement)window.Content).X < 175), "Gear, four toolbar buttons and search remain visible outside edit mode");
            var gridButton = headerButtons.First(b => (string)b.Content == "⌗");
            var undoButton = headerButtons.First(b => (string)b.Content == "↶");
            gridButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.CurrentLayout.Snap && !MainWindow.FindDescendants<Thumb>(window).Any(t => t.Cursor == Cursors.SizeAll), "Grid button enables snapping without enabling layout editing");
            undoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!window.CurrentLayout.Snap, "Undo button works outside edit mode");
            demo = window.CurrentLayout;
            Check(Icons.For(file) is BitmapSource && Icons.For(home) is BitmapSource && Icons.For(Environment.ProcessPath!) is BitmapSource, "Windows shell supplies file, folder, and application icons");
            var movable = demo.Groups[0].Items[0];
            window.MoveShortcut(movable, demo.Groups[1], 1, new Point());
            Check(!demo.Groups[0].Items.Contains(movable) && demo.Groups[1].Items[1] == movable && demo.AllItems().Count(i => i.Id == movable.Id) == 1, "Dragging between groups moves rather than duplicates shortcuts");
            window.MoveShortcut(movable, demo.Groups[1], 4, new Point());
            Check(demo.Groups[1].Items[3] == movable, "Reorder adjusts insertion index after removal");
            window.MoveShortcut(movable, null, 0, new Point(920, 80));
            Check(demo.Items.Contains(movable) && !demo.Groups[1].Items.Contains(movable), "Shortcuts can move from groups onto the canvas");
            window.Undo(); Check(window.CurrentLayout.Groups[1].Items.Any(i => i.Id == movable.Id), "UI undo restores shortcut membership");
            window.SetLayoutForTest(demo); window.SetEditingForTest(true); window.UpdateLayout();
            Check(MainWindow.FindDescendants<Button>(window).Count(b => b.Width == 20 && b.FontSize == 12) >= 8, "Editing controls use 12 px glyphs and 20 px hit areas");
            var inner = MainWindow.FindDescendants<ScrollViewer>(window).First(s => s.Tag as string == "group-scroll" && s.ScrollableHeight > 0);
            inner.ScrollToVerticalOffset(100); window.UpdateLayout();
            Check(inner.VerticalOffset > 0 && window.Viewport.VerticalOffset == 0, "Group scrolling is independent of the canvas");
            var previousInner = inner.VerticalOffset;
            var nestedWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            inner.RaiseEvent(nestedWheel); window.UpdateLayout();
            Check(nestedWheel.Handled && inner.VerticalOffset > previousInner && window.Viewport.VerticalOffset == 0, "Wheel events over scrolling groups stay inside the group");
            var nonScrolling = MainWindow.FindDescendants<ScrollViewer>(window).First(s => s.Tag as string == "group-scroll" && s.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled);
            var canvasWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            nonScrolling.RaiseEvent(canvasWheel); window.UpdateLayout();
            Check(canvasWheel.Handled && window.Viewport.VerticalOffset > 0, "Wheel events over non-scrolling groups move the canvas");
            window.Viewport.ScrollToVerticalOffset(0); window.UpdateLayout();
            demo.Snap = true; window.Render(); window.UpdateLayout();
            var groupBorder = MainWindow.FindDescendants<Border>(window).First(b => b.Tag as string == demo.Groups[0].Id);
            var thumbs = MainWindow.FindDescendants<Thumb>(groupBorder).Where(t => t.Cursor == Cursors.SizeAll || t.Cursor == Cursors.SizeNWSE).ToArray();
            var moveThumb = thumbs.First(t => t.Cursor == Cursors.SizeAll);
            moveThumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            moveThumb.RaiseEvent(new DragDeltaEventArgs(17, 19) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(demo.Groups[0].X % 16 == 0 && demo.Groups[0].Y % 16 == 0, "Groups snap during dragging rather than only on release");
            for (int step = 0; step < 8; step++) moveThumb.RaiseEvent(new DragDeltaEventArgs(1, 1) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(demo.Groups[0].X == 80 && demo.Groups[0].Y == 224, "Small drag deltas accumulate correctly with snapping enabled");
            moveThumb.RaiseEvent(new DragCompletedEventArgs(17, 19, false) { RoutedEvent = Thumb.DragCompletedEvent }); window.UpdateLayout();
            Check(demo.Groups[0].X % 16 == 0 && demo.Groups[0].Y % 16 == 0, "Group movement gestures snap to the configured grid");
            groupBorder = MainWindow.FindDescendants<Border>(window).First(b => b.Tag as string == demo.Groups[0].Id);
            var resize = MainWindow.FindDescendants<Thumb>(groupBorder).First(t => t.Cursor == Cursors.SizeNWSE);
            resize.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            resize.RaiseEvent(new DragDeltaEventArgs(19, 19) { RoutedEvent = Thumb.DragDeltaEvent });
            Check(demo.Groups[0].Width % 16 == 0 && demo.Groups[0].Height % 16 == 0, "Groups snap during resizing rather than only on release");
            resize.RaiseEvent(new DragDeltaEventArgs(-1000, -1000) { RoutedEvent = Thumb.DragDeltaEvent });
            resize.RaiseEvent(new DragCompletedEventArgs(-1000, -1000, false) { RoutedEvent = Thumb.DragCompletedEvent }); window.UpdateLayout();
            Check(demo.Groups[0].Width >= 180 && demo.Groups[0].Height >= 100, "Resize gestures preserve minimum usable group dimensions");
            window.Undo(); window.Undo(); demo = window.CurrentLayout; demo.Snap = false; window.Render(); window.UpdateLayout();
            var freeItem = demo.Items[0];
            window.MoveShortcut(freeItem, null, 0, new Point(147, 113));
            Check(freeItem.X == 103 && freeItem.Y == 89, "With snapping off, canvas shortcuts retain exact free placement");
            window.Undo(); demo = window.CurrentLayout; demo.Snap = true; window.Render();
            freeItem = demo.Items[0]; window.MoveShortcut(freeItem, null, 0, new Point(147, 113));
            Check(freeItem.X == 96 && freeItem.Y == 96, "With snapping on, dropped shortcuts align to the 16 px grid");
            window.Undo(); demo = window.CurrentLayout; demo.Snap = false; window.Render(); window.UpdateLayout();
            inner = MainWindow.FindDescendants<ScrollViewer>(window).First(s => s.Tag as string == "group-scroll" && s.ScrollableHeight > 0); inner.ScrollToVerticalOffset(0); window.UpdateLayout();
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                Render(window, Path.Combine(output, "homepage-edit-" + (int)(scale * 100) + ".png"), scale);
            }
            window.SetEditingForTest(false); window.UpdateLayout();
            Render(window, Path.Combine(output, "homepage-normal.png"), 1);
            window.Close();
            WebsiteChecks.Run(Check, output);
            TabChecks.Run(Check, output);
            BookmarkChecks.Run(Check, output);
            GroupMoveChecks.Run(Check, output);
            CondensedChecks.Run(Check, output);
            DragPreviewChecks.Run(Check, output);
            app.Shutdown();
            var portable = Path.Combine(output, "portable-launch"); Directory.CreateDirectory(portable);
            var portableExe = Path.Combine(portable, "Homepage.exe"); File.Copy(Environment.ProcessPath!, portableExe);
            var launchInfo = new ProcessStartInfo(portableExe, "--smoke-launch") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = portable };
            launchInfo.Environment["DOTNET_ROOT"] = Path.Combine(portable, "no-installed-runtime");
            using var process = Process.Start(launchInfo)!;
            if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("Portable launch did not finish."); }
            Check(process.ExitCode == 0 && File.Exists(Path.Combine(portable, "Homepage.data", "layout.json")), "Copied EXE starts independently and saves beside itself without an installed runtime");
            var launcherStore = new LayoutStore(portable);
            var persisted = launcherStore.Load(); persisted.Items.Add(new Shortcut { Label = "Saved website", Target = "https://example.com" }); launcherStore.Save(persisted);
            using var restarted = Process.Start(launchInfo)!;
            if (!restarted.WaitForExit(20000)) { restarted.Kill(); throw new Exception("Portable restart did not finish."); }
            Check(restarted.ExitCode == 0 && launcherStore.Load().Items[0].Label == "Saved website", "Normal startup and shutdown preserve an existing layout");
            File.WriteAllLines(Path.Combine(output, "results.txt"), results.Append($"Passed {results.Count} checks.")); return 0;
        }
        catch (Exception e) { File.WriteAllLines(Path.Combine(output, "results.txt"), results.Append("FAIL " + e)); return 1; }
    }
    public static void Render(MainWindow window, string path, double scale)
    {
        var root = (FrameworkElement)window.Content; root.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); png.Save(stream);
    }
}
