using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homepage;

public sealed class Shortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Shortcut";
    public string Target { get; set; } = "";
    public string? WebsiteIcon { get; set; }
    public string? WebsiteTitle { get; set; }
    public bool CustomLabel { get; set; }
    public bool WebsiteMetadataFetched { get; set; }
    public double X { get; set; } = 64;
    public double Y { get; set; } = 64;
}

public sealed class Group
{
    public Group() { SelectedTabId = Tabs[0].Id; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "New group";
    public double X { get; set; } = 96;
    public double Y { get; set; } = 96;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 280;
    [JsonRequired] public List<GroupTab> Tabs { get; set; } = [new()];
    public string SelectedTabId { get; set; } = "";
    [JsonIgnore] public GroupTab RootTab => Tabs.FirstOrDefault(t => t.Id == SelectedTabId) ?? Tabs[0];
    [JsonIgnore] public GroupTab ActiveTab => ActivePath().Last();
    public IEnumerable<GroupTab> AllTabs() => Tabs.SelectMany(t => t.Descendants());
    public IEnumerable<GroupTab> ActivePath()
    {
        var tab = RootTab;
        while (true) { yield return tab; var child = tab.Children.FirstOrDefault(t => t.Id == tab.SelectedChildId); if (child is null) break; tab = child; }
    }
    public List<GroupTab> Siblings(GroupTab tab) => AllTabs().FirstOrDefault(t => t.Children.Contains(tab))?.Children ?? Tabs;
    public void Select(GroupTab tab, bool ownContents = false)
    {
        if (!AllTabs().Contains(tab)) return;
        var child = tab;
        while (AllTabs().FirstOrDefault(t => t.Children.Contains(child)) is { } parent) { parent.SelectedChildId = child.Id; child = parent; }
        SelectedTabId = child.Id;
        if (ownContents) tab.SelectedChildId = null;
    }
    [JsonIgnore] public bool Rows { get => ActiveTab.Rows; set => ActiveTab.Rows = value; }
    [JsonIgnore] public bool Condensed { get => ActiveTab.Condensed; set => ActiveTab.Condensed = value; }
    [JsonIgnore] public bool Scroll { get => ActiveTab.Scroll; set => ActiveTab.Scroll = value; }
    [JsonIgnore] public List<Shortcut> Items { get => ActiveTab.Items; set => ActiveTab.Items = value; }
}

public sealed class GroupTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Main";
    public bool Rows { get; set; }
    public bool Condensed { get; set; }
    public bool Scroll { get; set; }
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    [JsonRequired] public List<Shortcut> Items { get; set; } = [];
    public List<GroupTab> Children { get; set; } = [];
    public string? SelectedChildId { get; set; }
    public IEnumerable<GroupTab> Descendants() => new[] { this }.Concat(Children.SelectMany(t => t.Descendants()));
    public IEnumerable<Shortcut> AllItems() => Descendants().SelectMany(t => t.Items);
}
public sealed class CanvasPage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Home";
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    [JsonRequired] public List<Shortcut> Items { get; set; } = [];
    [JsonRequired] public List<Group> Groups { get; set; } = [];
}
public sealed class Layout
{
    public Layout() { SelectedPageId = Pages[0].Id; }
    public int Version { get; set; } = 3;
    public bool Snap { get; set; }
    [JsonRequired] public List<CanvasPage> Pages { get; set; } = [new()];
    public string SelectedPageId { get; set; } = "";
    [JsonIgnore] public CanvasPage ActivePage => Pages.FirstOrDefault(p => p.Id == SelectedPageId) ?? Pages[0];
    [JsonIgnore] public double ScrollX { get => ActivePage.ScrollX; set => ActivePage.ScrollX = value; }
    [JsonIgnore] public double ScrollY { get => ActivePage.ScrollY; set => ActivePage.ScrollY = value; }
    [JsonIgnore] public List<Shortcut> Items { get => ActivePage.Items; set => ActivePage.Items = value; }
    [JsonIgnore] public List<Group> Groups { get => ActivePage.Groups; set => ActivePage.Groups = value; }
    public IEnumerable<Group> AllGroups() => Pages.SelectMany(p => p.Groups);
    public IEnumerable<Shortcut> AllItems() => Pages.SelectMany(p => p.Items.Concat(p.Groups.SelectMany(g => g.AllTabs().SelectMany(t => t.Items))));
}

public sealed class LayoutStore(string baseDirectory)
{
    sealed class LegacyLayout
    {
        public bool Snap { get; set; }
        public double ScrollX { get; set; }
        public double ScrollY { get; set; }
        public List<Shortcut> Items { get; set; } = [];
        public List<LegacyGroup> Groups { get; set; } = [];
    }
    sealed class LegacyGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Label { get; set; } = "New group";
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 380;
        public double Height { get; set; } = 280;
        public bool Rows { get; set; }
        public bool Scroll { get; set; }
        public List<Shortcut> Items { get; set; } = [];
    }
    public string BaseDirectory { get; } = Path.GetFullPath(baseDirectory);
    public string DataDirectory => Path.Combine(BaseDirectory, "Homepage.data");
    public string SavePath => Path.Combine(DataDirectory, "layout.json");
    public string BackupPath => SavePath + ".bak";
    public string Version1BackupPath => Path.Combine(DataDirectory, "layout.v1-backup.json");
    public string Version2BackupPath => Path.Combine(DataDirectory, "layout.v2-backup.json");
    public bool NeedsUpgrade { get; private set; }
    public string? LoadMessage { get; private set; }
    public bool CanSave { get; private set; } = true;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 256 };
    public static string Serialize(Layout value) => JsonSerializer.Serialize(value, JsonOptions);
    public static Layout Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("Version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version)) throw new InvalidDataException("Missing layout version.");
        Layout value;
        if (version == 1)
        {
            var old = JsonSerializer.Deserialize<LegacyLayout>(json) ?? throw new InvalidDataException("Empty layout.");
            if (old.Items is null || old.Groups is null || old.Groups.Any(g => g is null || g.Items is null)) throw new InvalidDataException("Incomplete layout.");
            value = new Layout { Snap = old.Snap, ScrollX = old.ScrollX, ScrollY = old.ScrollY, Items = old.Items };
            foreach (var g in old.Groups) value.Groups.Add(new Group { Id = g.Id, Label = g.Label, X = g.X, Y = g.Y, Width = g.Width, Height = g.Height, Rows = g.Rows, Scroll = g.Scroll, Items = g.Items });
        }
        else if (version is 2 or 3)
        {
            if (!document.RootElement.TryGetProperty("Pages", out _)) throw new InvalidDataException("Incomplete pages.");
            value = JsonSerializer.Deserialize<Layout>(json, JsonOptions) ?? throw new InvalidDataException("Empty layout.");
        }
        else throw new InvalidDataException("This layout version is not supported.");
        var ids = new HashSet<string>();
        void Identity(string id, string name) { if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Invalid tab or group."); }
        void ValidateTab(GroupTab t, int depth)
        {
            if (t is null || t.Items is null || t.Children is null || depth > 64) throw new InvalidDataException("Incomplete or excessively nested tab.");
            Identity(t.Id, t.Name); t.ScrollX = Position(t.ScrollX); t.ScrollY = Position(t.ScrollY);
            foreach (var child in t.Children) ValidateTab(child, depth + 1);
            if (!t.Children.Any(c => c.Id == t.SelectedChildId)) t.SelectedChildId = null;
        }
        if (value.Pages is null || value.Pages.Count == 0) throw new InvalidDataException("Incomplete pages.");
        foreach (var p in value.Pages)
        {
            if (p is null || p.Items is null || p.Groups is null) throw new InvalidDataException("Incomplete page.");
            Identity(p.Id, p.Name); p.ScrollX = Position(p.ScrollX); p.ScrollY = Position(p.ScrollY);
            foreach (var g in p.Groups)
            {
                if (g is null || g.Tabs is null || g.Tabs.Count == 0) throw new InvalidDataException("Incomplete group.");
                Identity(g.Id, g.Label);
                g.X = Position(g.X); g.Y = Position(g.Y);
                g.Width = Size(g.Width, 180, 10000); g.Height = Size(g.Height, 100, 10000);
                foreach (var t in g.Tabs) ValidateTab(t, 0);
                g.SelectedTabId = g.RootTab.Id;
            }
        }
        foreach (var item in value.AllItems())
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Label) || string.IsNullOrWhiteSpace(item.Target))
                throw new InvalidDataException("Invalid shortcut.");
            item.X = Position(item.X); item.Y = Position(item.Y);
        }
        value.SelectedPageId = value.ActivePage.Id;
        value.Version = 3;
        return value;
    }
    static double Position(double x) => Size(x, 0, 1000000);
    static double Size(double x, double min, double max) => double.IsFinite(x) ? Math.Clamp(x, min, max) : min;
    public Layout Load()
    {
        if (!File.Exists(SavePath)) return new Layout();
        try { var json = File.ReadAllText(SavePath); var value = Deserialize(json); using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 }); NeedsUpgrade = doc.RootElement.GetProperty("Version").GetInt32() < 3; return value; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            try
            {
                var backup = Deserialize(File.ReadAllText(BackupPath));
                // Preserve the damaged primary before any replacement.
                File.Copy(SavePath, SavePath + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
                LoadMessage = "Recovered the previous save. The damaged file was preserved in Homepage.data.";
                return backup;
            }
            catch
            {
                CanSave = false;
                LoadMessage = "Could not read your saved layout. Saving is paused to protect it. Right-click the canvas → Recover saved layout.";
                return new Layout();
            }
        }
    }
    public void Recover()
    {
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(SavePath)) File.Move(SavePath, SavePath + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        CanSave = true;
    }
    public void Save(Layout value)
    {
        if (!CanSave) throw new IOException("Saving is paused. Right-click the canvas → Recover saved layout.");
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(SavePath))
        {
            try { var json = File.ReadAllText(SavePath); Deserialize(json); using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 }); int oldVersion = doc.RootElement.GetProperty("Version").GetInt32(); var original = oldVersion == 1 ? Version1BackupPath : Version2BackupPath; if (oldVersion < 3 && !File.Exists(original)) File.Copy(SavePath, original); }
            catch (JsonException) { }
            catch (InvalidDataException) { }
        }
        var temp = SavePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush(true);
        }
        if (!File.Exists(SavePath)) File.Move(temp, SavePath);
        else
        {
            // Only replace the last-good backup with a valid primary.
            bool valid;
            try { Deserialize(File.ReadAllText(SavePath)); valid = true; } catch { valid = false; }
            try { File.Replace(temp, SavePath, valid ? BackupPath : null); }
            catch (UnauthorizedAccessException) when (File.Exists(temp))
            {
                // Some Windows folders reject ReplaceFile even though overwrite-by-rename
                // is permitted. Keep the previous valid save before trying that operation.
                if (valid) File.Copy(SavePath, BackupPath, true);
                File.Move(temp, SavePath, true);
            }
        }
        NeedsUpgrade = false;
    }
    public string StoreTarget(string target)
    {
        if (IsWebsite(target)) return target;
        var full = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(BaseDirectory, full);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative) ? relative : full;
    }
    public string ResolveTarget(string target) => IsWebsite(target) ? target : Path.GetFullPath(target, BaseDirectory);
    public static bool IsWebsite(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
}

public sealed class UndoHistory
{
    readonly Stack<string> states = new();
    public int Count => states.Count;
    public void Push(Layout value)
    {
        var serialized = LayoutStore.Serialize(value);
        if (states.Count == 0 || states.Peek() != serialized) states.Push(serialized);
        if (states.Count > 100)
        {
            var newest = states.Take(100).Reverse().ToArray(); states.Clear();
            foreach (var state in newest) states.Push(state);
        }
    }
    public Layout? Pop() => states.Count > 0 ? LayoutStore.Deserialize(states.Pop()) : null;
}
